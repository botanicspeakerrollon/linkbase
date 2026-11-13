using System.Text.RegularExpressions;
using Linkbase.Core.Explore;
using Linkbase.Core.History;
using Linkbase.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;
using NpgsqlTypes;

namespace Linkbase.Core.Models;

public sealed partial class ModelEditor
{
    [GeneratedRegex("^#[0-9A-Fa-f]{6}$")]
    private static partial Regex HexColor();

    public async Task AddTypeAsync(string name, string? color = null, string? icon = null, ChangeReason? reason = null, CancellationToken cancellationToken = default)
    {
        name = name.Trim();
        if (!ModelFile.TypeName().IsMatch(name))
        {
            throw new LinkbaseValidationException("A type's name is letters and digits, starting with a capital, such as Supplier.");
        }

        CheckLook(color, icon);
        await WriteAsync(reason, async (db, changes) =>
        {
            if (await db.NodeTypes.AnyAsync(t => t.Name == name, cancellationToken))
            {
                throw new LinkbaseValidationException($"There is already a type called {name}.");
            }

            await ExecuteAsync(db,
                "INSERT INTO node_type (name, color, icon, sort_order) SELECT @name, @color, @icon, coalesce(max(sort_order), 0) + 1 FROM node_type",
                [Text("name", name), Text("color", Clean(color)), Text("icon", Clean(icon))], cancellationToken);
            changes.Add(Defined($"type:{name}", ChangeOp.Create, to: Json(new { name, color = Clean(color), icon = Clean(icon) })));
        }, cancellationToken);
    }

    // Changes how a type looks and how its records are named. A name template only names records saved from then on.
    public async Task UpdateTypeAsync(string name, string? color, string? icon, string? nameTemplate, ChangeReason? reason = null, CancellationToken cancellationToken = default)
    {
        CheckLook(color, icon);
        await WriteAsync(reason, async (db, changes) =>
        {
            var type = await EditableTypeAsync(db, name, cancellationToken);
            var template = Clean(nameTemplate);
            if (template is not null)
            {
                var fields = await db.FieldDefinitions.AsNoTracking().Where(f => f.NodeTypeName == name).ToListAsync(cancellationToken);
                CheckTemplate(name, template, fields);
            }

            Change(changes, $"type:{name}", "color", type.Color, Clean(color));
            Change(changes, $"type:{name}", "icon", type.Icon, Clean(icon));
            Change(changes, $"type:{name}", "name_template", type.NameTemplate, template);
            await ExecuteAsync(db, "UPDATE node_type SET color = @color, icon = @icon, name_template = @template WHERE name = @name",
                [Text("name", name), Text("color", Clean(color)), Text("icon", Clean(icon)), Text("template", template)], cancellationToken);
        }, cancellationToken);
    }

    // Removes a type that has no records and that nothing refers to.
    public async Task DeleteTypeAsync(string name, ChangeReason? reason = null, CancellationToken cancellationToken = default)
    {
        await WriteAsync(reason, async (db, changes) =>
        {
            await EditableTypeAsync(db, name, cancellationToken);
            RefuseWatched(name, $"delete {name}");
            if (await db.Nodes.AnyAsync(n => n.Type == name, cancellationToken))
            {
                throw new LinkbaseValidationException($"{name} still has records. Delete them first.");
            }

            var related = await db.LinkDefinitions.AsNoTracking().Where(l => l.SourceType == name || l.TargetType == name)
                .OrderBy(l => l.RelType).Select(l => l.SourceType + " " + l.RelType + " " + l.TargetType).ToListAsync(cancellationToken);
            if (related.Count > 0)
            {
                throw new LinkbaseValidationException($"{name} is in {string.Join(", ", related)}. Delete {(related.Count == 1 ? "that relationship" : "those relationships")} first.");
            }

            await ExecuteAsync(db, "DELETE FROM hierarchy_level WHERE node_type_name = @name", [Text("name", name)], cancellationToken);
            await ExecuteAsync(db, "DELETE FROM field_definition WHERE node_type_name = @name", [Text("name", name)], cancellationToken);
            await ExecuteAsync(db, "DELETE FROM node_type WHERE name = @name", [Text("name", name)], cancellationToken);
            changes.Add(Defined($"type:{name}", ChangeOp.Delete, from: Json(new { name })));
        }, cancellationToken);
    }

    // Sets how a type's records form a tree, or with null takes the tree away. The levels are checked against the type's
    // choice field and its parent relationship, and against the records already there.
    public async Task SetHierarchyAsync(string type, HierarchySpec? hierarchy, ChangeReason? reason = null, CancellationToken cancellationToken = default)
    {
        await WriteAsync(reason, async (db, changes) =>
        {
            var current = await EditableTypeAsync(db, type, cancellationToken);
            RefuseWatched(type, $"change {type}'s hierarchy");
            var before = current.HierarchyField is null
                ? null
                : Json(new { by = current.HierarchyField, via = current.HierarchyRel, levels = await Levels(db, type, cancellationToken) });
            await ExecuteAsync(db, "DELETE FROM hierarchy_level WHERE node_type_name = @name", [Text("name", type)], cancellationToken);
            if (hierarchy is null)
            {
                await ExecuteAsync(db, "UPDATE node_type SET hierarchy_field = NULL, hierarchy_rel = NULL WHERE name = @name", [Text("name", type)], cancellationToken);
                if (before is not null)
                {
                    changes.Add(Defined($"type:{type}", ChangeOp.Update, "hierarchy", before, null));
                }

                return;
            }

            await CheckHierarchyAsync(db, type, hierarchy, cancellationToken);
            await ExecuteAsync(db, "UPDATE node_type SET hierarchy_field = @by, hierarchy_rel = @via WHERE name = @name",
                [Text("name", type), Text("by", hierarchy.By), Text("via", hierarchy.Via)], cancellationToken);
            foreach (var level in hierarchy.Levels)
            {
                await ExecuteAsync(db, "INSERT INTO hierarchy_level (node_type_name, kind, is_root, under) VALUES (@name, @kind, @root, @under)",
                    [Text("name", type), Text("kind", level.Kind), new Param("root", level.Root, NpgsqlDbType.Boolean), TextArray("under", level.Root ? [] : level.Under)], cancellationToken);
            }

            // The records already there have to fit the levels. A problem throws, and the whole edit is rolled back.
            await HierarchyChecks.CheckAsync(
                (NpgsqlConnection)db.Database.GetDbConnection(), (NpgsqlTransaction)db.Database.CurrentTransaction!.GetDbTransaction(), null, requireParent: false, cancellationToken);
            changes.Add(Defined($"type:{type}", ChangeOp.Update, "hierarchy", before, Json(new { by = hierarchy.By, via = hierarchy.Via, levels = hierarchy.Levels })));
        }, cancellationToken);
    }

    private static async Task<List<object>> Levels(LinkbaseDbContext db, string type, CancellationToken cancellationToken) =>
        (await db.HierarchyLevels.AsNoTracking().Where(l => l.NodeTypeName == type).OrderBy(l => l.Id).ToListAsync(cancellationToken))
        .Select(l => (object)new { kind = l.Kind, root = l.IsRoot, under = l.Under }).ToList();

    // The levels have to cover the choice field's options exactly, with somewhere for the tree to start and a way down
    // from it, and the parent relationship has to be one the type has with itself that isn't required.
    private static async Task CheckHierarchyAsync(LinkbaseDbContext db, string type, HierarchySpec hierarchy, CancellationToken cancellationToken)
    {
        var by = await db.FieldDefinitions.AsNoTracking().SingleOrDefaultAsync(f => f.NodeTypeName == type && f.Key == hierarchy.By, cancellationToken);
        if (by is null || by.Type != FieldType.Choice || by.Multiple)
        {
            throw new LinkbaseValidationException($"A hierarchy is by a choice field of {type} with one value, and {type} has no such field called {hierarchy.By}.");
        }

        if (!by.Required)
        {
            throw new LinkbaseValidationException($"{Title(hierarchy.By)} needs to be required, so every {type} has a level.");
        }

        var via = await db.LinkDefinitions.AsNoTracking().SingleOrDefaultAsync(l => l.SourceType == type && l.TargetType == type && l.RelType == hierarchy.Via, cancellationToken)
            ?? throw new LinkbaseValidationException($"{type} has no relationship {type} {hierarchy.Via} {type} to go up the tree by. Add it first.");
        if (via.Required)
        {
            throw new LinkbaseValidationException($"{type} {hierarchy.Via} {type} can't be required, because the top level has no parent.");
        }

        if (hierarchy.Levels.Count == 0)
        {
            throw new LinkbaseValidationException("A hierarchy needs levels.");
        }

        var kinds = hierarchy.Levels.Select(l => l.Kind).ToList();
        if (kinds.GroupBy(k => k).FirstOrDefault(g => g.Count() > 1) is { } repeated)
        {
            throw new LinkbaseValidationException($"The level {repeated.Key} is there more than once.");
        }

        var options = (by.Options ?? []).Select(o => o.Trim()).ToList();
        if (kinds.FirstOrDefault(k => !options.Contains(k)) is { } unknown)
        {
            throw new LinkbaseValidationException($"{unknown} isn't one of {Title(hierarchy.By)}'s options: {FieldValues.Listed(options)}.");
        }

        if (options.FirstOrDefault(o => !kinds.Contains(o)) is { } missing)
        {
            throw new LinkbaseValidationException($"{Title(hierarchy.By)}'s option {missing} has no level. Give it one, or take the option away.");
        }

        if (!hierarchy.Levels.Any(l => l.Root))
        {
            throw new LinkbaseValidationException("One level has to be at the top (a root), or nothing could start the tree.");
        }

        foreach (var level in hierarchy.Levels)
        {
            if (level.Root && level.Under.Count > 0)
            {
                throw new LinkbaseValidationException($"{level.Kind} is at the top, so it can't be under anything.");
            }

            if (!level.Root && level.Under.Count == 0)
            {
                throw new LinkbaseValidationException($"{level.Kind} needs the levels it may be in, or to be at the top.");
            }

            if (level.Under.FirstOrDefault(u => !kinds.Contains(u)) is { } other)
            {
                throw new LinkbaseValidationException($"{level.Kind} is under {other}, which isn't a level.");
            }
        }

        var reached = hierarchy.Levels.Where(l => l.Root).Select(l => l.Kind).ToHashSet();
        for (var grew = true; grew;)
        {
            grew = false;
            foreach (var level in hierarchy.Levels.Where(l => !reached.Contains(l.Kind) && l.Under.Any(reached.Contains)))
            {
                reached.Add(level.Kind);
                grew = true;
            }
        }

        if (hierarchy.Levels.FirstOrDefault(l => !reached.Contains(l.Kind)) is { } lost)
        {
            throw new LinkbaseValidationException($"{lost.Kind} can't be reached from a top level, so no record could be at it.");
        }
    }

    // A name template is text with {field} in it, each naming a text-like field of the type that has one value.
    internal static void CheckTemplate(string type, string template, IReadOnlyList<FieldDefinition> fields)
    {
        var tokens = NameTemplates.Tokens(template);
        if (tokens.Count == 0 || !NameTemplates.IsBalanced(template))
        {
            throw new LinkbaseValidationException("A name template is text with fields in braces, such as {name_first} {name_last}.");
        }

        foreach (var token in tokens.Distinct())
        {
            var field = fields.FirstOrDefault(f => f.Key == token)
                ?? throw new LinkbaseValidationException($"The name template uses {{{token}}}, which isn't a field of {type}.");
            if (field.Multiple || field.Type == FieldType.List || field.Owner is not null)
            {
                throw new LinkbaseValidationException($"The name template uses {{{token}}}, which needs to be a field with one value that people fill in.");
            }
        }
    }

    private static void CheckLook(string? color, string? icon)
    {
        if (Clean(color) is { } c && !HexColor().IsMatch(c))
        {
            throw new LinkbaseValidationException("A colour is written like #3a7bd5.");
        }

        if (Clean(icon) is { } i && GraphStyle.IconNamed(i) is null)
        {
            throw new LinkbaseValidationException($"{i} isn't one of the icons. Try a name such as Train, Person or Folder.");
        }
    }

    // The type as a tracked-free row, refusing one a domain module derives.
    private static async Task<NodeType> EditableTypeAsync(LinkbaseDbContext db, string name, CancellationToken cancellationToken)
    {
        var type = await db.NodeTypes.AsNoTracking().SingleOrDefaultAsync(t => t.Name == name, cancellationToken)
            ?? throw new LinkbaseValidationException($"There is no type called {name}.");
        return type.Owner is { } owner
            ? throw new LinkbaseValidationException($"{name} records are derived by {owner} from other records, so the type can't be changed here.")
            : type;
    }

    // A change that takes away or renames what a module may rely on.
    private void RefuseWatched(string type, string what)
    {
        if (_hooks.Watch(type))
        {
            throw new LinkbaseValidationException($"A domain module reads and writes {type}, so you can't {what}. Adding to it is fine.");
        }
    }

    private static string? Clean(string? text) => string.IsNullOrWhiteSpace(text) ? null : text.Trim();

    private static string Title(string key) => Records.RecordRepository.Title(key);

    private static void Change(List<PendingChange> changes, string entity, string field, string? from, string? to)
    {
        if (from != to)
        {
            changes.Add(Defined(entity, ChangeOp.Update, field, from, to));
        }
    }
}
