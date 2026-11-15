using System.Text.Json;
using Linkbase.Core.Models;
using Linkbase.Data;
using Microsoft.EntityFrameworkCore;

namespace Linkbase.Core.Records;

// The rules of a type's hierarchy, where records are created, edited and linked. The loader and imports check the same
// rules in SQL (HierarchyChecks). A record below the top level can lose its parent, so that it can be moved by removing
// the link and adding another, and a type's records may arrive without one from an import.
internal static class RecordHierarchy
{
    // The record's level, from its values.
    private static string? KindOf(string props, string field)
    {
        using var document = JsonDocument.Parse(props);
        return document.RootElement.TryGetProperty(field, out var kind) && kind.ValueKind == JsonValueKind.String ? kind.GetString() : null;
    }

    private static async Task<HierarchyRules?> HierarchyOfAsync(LinkbaseDbContext db, string type, CancellationToken cancellationToken)
    {
        var found = await db.NodeTypes.AsNoTracking()
            .Where(t => t.Name == type && t.HierarchyField != null && t.HierarchyRel != null)
            .Select(t => new
            {
                t.HierarchyField,
                t.HierarchyRel,
                Levels = t.HierarchyLevels.OrderBy(l => l.Id).Select(l => new { l.Kind, l.IsRoot, l.Under }).ToList(),
            })
            .SingleOrDefaultAsync(cancellationToken);
        return found is null
            ? null
            : new HierarchyRules(type, found.HierarchyField!, found.HierarchyRel!, found.Levels.Select(l => new HierarchyLevelRule(l.Kind, l.IsRoot, l.Under)).ToList());
    }

    // A new record: below the top level it needs one parent of a level it may be in, and at the top it has none.
    internal static async Task CheckNewHierarchyAsync(
        LinkbaseDbContext db, RecordType type, string props, IReadOnlyList<(string RelType, string TargetId, string TargetType)> links, CancellationToken cancellationToken)
    {
        if (type.Hierarchy is not { } rules)
        {
            return;
        }

        var kind = KindOf(props, rules.FieldKey);
        var level = rules.Level(kind) ?? throw new LinkbaseValidationException(rules.CheckParent(kind, null)!);
        var parents = links.Where(l => l.RelType == rules.RelType && l.TargetType == type.Name).ToList();
        if (parents.Count > 1)
        {
            throw new LinkbaseValidationException($"A {type.Name} can be in only one {type.Name}.");
        }

        if (parents.Count == 0)
        {
            if (!level.Root)
            {
                throw new LinkbaseValidationException($"A {level.Kind} needs a {HierarchyRules.Either(level.Under)} to be in.");
            }

            return;
        }

        var parentProps = await db.Nodes.AsNoTracking().Where(n => n.Id == parents[0].TargetId).Select(n => n.Props).SingleAsync(cancellationToken);
        if (rules.CheckParent(level.Kind, KindOf(parentProps, rules.FieldKey)) is { } why)
        {
            throw new LinkbaseValidationException(why);
        }
    }

    // Changing a record's level has to suit the record it's in and the records in it.
    internal static async Task CheckKindChangeAsync(
        LinkbaseDbContext db, RecordType type, string id, string oldProps, string newProps, CancellationToken cancellationToken)
    {
        if (type.Hierarchy is not { } rules)
        {
            return;
        }

        var kind = KindOf(newProps, rules.FieldKey);
        if (kind == KindOf(oldProps, rules.FieldKey))
        {
            return;
        }

        if (rules.Level(kind) is null)
        {
            throw new LinkbaseValidationException(rules.CheckParent(kind, null)!);
        }

        var parents = await db.NodeLinks.AsNoTracking()
            .Where(l => l.SourceId == id && l.RelType == rules.RelType && l.Target.Type == type.Name)
            .Select(l => l.Target.Props)
            .ToListAsync(cancellationToken);
        foreach (var parent in parents)
        {
            if (rules.CheckParent(kind, KindOf(parent, rules.FieldKey)) is { } why)
            {
                throw new LinkbaseValidationException($"It can't become a {kind}, because it's in a {KindOf(parent, rules.FieldKey)}. {why}");
            }
        }

        var children = await db.NodeLinks.AsNoTracking()
            .Where(l => l.TargetId == id && l.RelType == rules.RelType && l.Source.Type == type.Name)
            .Select(l => l.Source.Props)
            .ToListAsync(cancellationToken);
        foreach (var childKind in children.Select(c => KindOf(c, rules.FieldKey)).Distinct())
        {
            if (rules.CheckParent(childKind, kind) is { } why)
            {
                throw new LinkbaseValidationException($"It can't become a {kind}, because there are {childKind} records in it. {why}");
            }
        }
    }

    // A link from a record to its parent has to suit both levels, the record can have only one parent, and the tree can't
    // go round in a circle.
    internal static async Task CheckAddedParentAsync(
        LinkbaseDbContext db, string sourceId, string sourceType, string relType, string targetId, string targetType, CancellationToken cancellationToken)
    {
        if (sourceType != targetType || await HierarchyOfAsync(db, sourceType, cancellationToken) is not { } rules || rules.RelType != relType)
        {
            return;
        }

        if (sourceId == targetId)
        {
            throw new LinkbaseValidationException("A record can't be in itself.");
        }

        var ends = await db.Nodes.AsNoTracking()
            .Where(n => n.Id == sourceId || n.Id == targetId)
            .Select(n => new { n.Id, n.Name, n.Props })
            .ToListAsync(cancellationToken);
        var child = ends.First(e => e.Id == sourceId);
        var parent = ends.First(e => e.Id == targetId);
        if (rules.CheckParent(KindOf(child.Props, rules.FieldKey), KindOf(parent.Props, rules.FieldKey)) is { } why)
        {
            throw new LinkbaseValidationException(why);
        }

        if (await db.NodeLinks.AnyAsync(l => l.SourceId == sourceId && l.RelType == relType && l.Target.Type == sourceType, cancellationToken))
        {
            throw new LinkbaseValidationException(
                $"{child.Name} is in a {sourceType} already, and can be in only one. To move it, remove that link first.");
        }

        // Going up from the new parent reaches the record only if the link would close a circle.
        var above = await db.Database.SqlQuery<string>(
            $"""
            WITH RECURSIVE up (id) AS (
                SELECT {targetId}::text
                UNION
                SELECT l.target_id FROM up JOIN node_link l ON l.source_id = up.id AND l.rel_type = {relType}
            )
            SELECT id AS "Value" FROM up WHERE id = {sourceId}
            """).ToListAsync(cancellationToken);
        if (above.Count > 0)
        {
            throw new LinkbaseValidationException($"{parent.Name} is in {child.Name}, directly or further up, so that link would make a circle.");
        }
    }
}
