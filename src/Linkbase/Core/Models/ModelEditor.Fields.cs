using Linkbase.Core.History;
using Linkbase.Data;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using NpgsqlTypes;

namespace Linkbase.Core.Models;

// Fields of a type and the values on a relationship's links are edited by the same code, because they are the same thing
// kept in two places: a FieldHome says which.
public sealed partial class ModelEditor
{
    public async Task AddFieldAsync(FieldHome home, FieldSpec spec, ChangeReason? reason = null, CancellationToken cancellationToken = default)
    {
        spec = Normalised(spec);
        CheckSpec(home, spec);
        await WriteAsync(reason, async (db, changes) =>
        {
            await GuardAsync(db, home, destructive: false, "", cancellationToken);
            if ((await FieldsOfAsync(db, home, cancellationToken)).Any(f => f.Key == spec.Key))
            {
                throw new LinkbaseValidationException($"{home.Label} already has a {home.Noun} called {spec.Key}.");
            }

            if (spec.Required)
            {
                await RequireFilledAsync(db, home, spec, cancellationToken);
            }

            await InsertAsync(db, home, spec, cancellationToken);
            changes.Add(Defined(EntityId(home, spec.Key), ChangeOp.Create, to: Json(Describe(spec))));
        }, cancellationToken);
    }

    // Changes a field's description, group, whether it is required and a choice's options. Its type, key and whether it holds
    // several values are changed by the other methods, because they rewrite the records.
    public async Task UpdateFieldAsync(FieldHome home, FieldSpec spec, ChangeReason? reason = null, CancellationToken cancellationToken = default)
    {
        spec = Normalised(spec);
        CheckSpec(home, spec);
        await WriteAsync(reason, async (db, changes) =>
        {
            await GuardAsync(db, home, destructive: false, "", cancellationToken);
            var old = await FieldAsync(db, home, spec.Key, cancellationToken);
            if (old.Type != spec.Type || old.Multiple != spec.Multiple)
            {
                throw new LinkbaseValidationException($"Use Change type to change {spec.Key}'s type, or whether it holds several values.");
            }

            var entity = EntityId(home, spec.Key);
            var oldOptions = old.OptionList.Select(o => o.Trim()).ToList();
            var newOptions = spec.OptionList.Select(o => o.Trim()).ToList();
            if (await HierarchyFieldAsync(db, home, cancellationToken) == spec.Key && (!spec.Required || !oldOptions.ToHashSet().SetEquals(newOptions)))
            {
                throw new LinkbaseValidationException($"{Title(spec.Key)} sets the levels of {home.Type}'s hierarchy, so it stays required and keeps its options. Change the hierarchy first.");
            }

            var scope = new Scope(home);
            var removed = oldOptions.Where(o => !newOptions.Contains(o)).ToList();
            if (removed.Count > 0)
            {
                var inUse = spec.Multiple
                    ? $"jsonb_typeof({scope.P} -> @key) = 'array' AND EXISTS (SELECT 1 FROM jsonb_array_elements_text({scope.P} -> @key) v WHERE v = ANY(@removed))"
                    : $"({scope.P} ->> @key) = ANY(@removed)";
                if (await scope.CountAsync(db, inUse, cancellationToken, Text("key", spec.Key), TextArray("removed", removed)) is > 0 and var used)
                {
                    var examples = await scope.ExamplesAsync(db, spec.Multiple ? "v.v" : $"{scope.P} ->> @key", inUse, Examples, cancellationToken, Text("key", spec.Key), TextArray("removed", removed));
                    throw new LinkbaseValidationException(
                        $"{used:N0} {Plural(home)} still use {string.Join(", ", examples.Select(e => $"\"{e}\""))}, which you are taking away. Change their value first.");
                }
            }

            if (spec.Required && !old.Required)
            {
                await RequireFilledAsync(db, home, spec, cancellationToken);
            }

            Change(changes, entity, "required", old.Required.ToString().ToLowerInvariant(), spec.Required.ToString().ToLowerInvariant());
            Change(changes, entity, "options", oldOptions.Count == 0 ? null : Json(oldOptions), newOptions.Count == 0 ? null : Json(newOptions));
            Change(changes, entity, "description", old.Description, spec.Description);
            Change(changes, entity, "group", old.Group, spec.Group);
            await ReplaceAsync(db, home, spec.Key, spec, cancellationToken);
        }, cancellationToken);
    }

    // Converts a field's values to another type, or refuses with the values that don't fit. From text a field can become
    // a number, a date, a yes/no or a choice (whose options default to the values found); from any of those back to text; and
    // a choice becomes text. A field with several values keeps its type.
    public async Task ChangeFieldTypeAsync(
        FieldHome home, string key, FieldType to, IReadOnlyList<string>? options = null, ChangeReason? reason = null, CancellationToken cancellationToken = default)
    {
        await WriteAsync(reason, async (db, changes) =>
        {
            await GuardAsync(db, home, destructive: true, $"change the type of {key}", cancellationToken);
            var old = await FieldAsync(db, home, key, cancellationToken);
            if (old.Multiple)
            {
                throw new LinkbaseValidationException($"{Title(key)} holds several values, so its type can't be changed. Add a new field and move the values over.");
            }

            if (old.Type == to)
            {
                throw new LinkbaseValidationException($"{Title(key)} is already {Describe(to)}.");
            }

            if (await HierarchyFieldAsync(db, home, cancellationToken) == key)
            {
                throw new LinkbaseValidationException($"{Title(key)} sets the levels of {home.Type}'s hierarchy, so it has to stay a choice.");
            }

            var allowed = to == FieldType.Text ? old.Type is FieldType.Number or FieldType.Date or FieldType.Boolean or FieldType.Choice
                : old.Type == FieldType.Text && to is FieldType.Number or FieldType.Date or FieldType.Boolean or FieldType.Choice;
            if (!allowed)
            {
                throw new LinkbaseValidationException($"{Title(key)} can't go straight from {Describe(old.Type)} to {Describe(to)}. Make it text first, then {Describe(to)}.");
            }

            var scope = new Scope(home);
            var value = $"({scope.P} ->> @key)";
            var has = "jsonb_exists(" + scope.P + ", @key)";
            var keyParam = Text("key", key);
            IReadOnlyList<string>? newOptions = null;
            string? set = null;
            string? misfit = null;
            var extra = new List<Param> { keyParam };
            switch (to)
            {
                case FieldType.Text:
                    set = $"jsonb_set({scope.P}, ARRAY[@key], to_jsonb({value}))";
                    break;
                case FieldType.Number:
                    misfit = $"{has} AND NOT ({value} ~ '^\\s*-?[0-9]+(\\.[0-9]+)?\\s*$')";
                    set = $"jsonb_set({scope.P}, ARRAY[@key], to_jsonb(btrim({value})::numeric))";
                    break;
                case FieldType.Date:
                    misfit = $"{has} AND NOT ({value} ~ '^[0-9]{{4}}-[0-9]{{2}}-[0-9]{{2}}$')";
                    set = $"jsonb_set({scope.P}, ARRAY[@key], to_jsonb(({value})::date::text))";
                    break;
                case FieldType.Boolean:
                    misfit = $"{has} AND NOT (lower(btrim({value})) IN ('true', 'false'))";
                    set = $"jsonb_set({scope.P}, ARRAY[@key], to_jsonb(lower(btrim({value}))::boolean))";
                    break;
                case FieldType.Choice:
                    newOptions = options is { Count: > 0 }
                        ? options.Select(o => o.Trim()).Where(o => o.Length > 0).ToList()
                        : await scope.ExamplesAsync(db, value, has, FieldValues.MaxOptions + 1, cancellationToken, keyParam);
                    CheckOptions(key, newOptions);
                    extra.Add(TextArray("options", newOptions));
                    extra.Add(TextArray("lowered", newOptions.Select(o => o.ToLowerInvariant())));
                    misfit = $"{has} AND NOT (lower(btrim({value})) = ANY(@lowered))";
                    set = $"jsonb_set({scope.P}, ARRAY[@key], to_jsonb((SELECT o FROM unnest(@options) o WHERE lower(o) = lower(btrim({value})) LIMIT 1)))";
                    break;
            }

            if (misfit is not null && await scope.CountAsync(db, misfit, cancellationToken, [.. extra]) is > 0 and var wrong)
            {
                var examples = await scope.ExamplesAsync(db, value, misfit, Examples, cancellationToken, [.. extra]);
                throw new LinkbaseValidationException(
                    $"{wrong:N0} {Plural(home)} have a value that isn't {Describe(to)}, such as {string.Join(", ", examples.Select(e => $"\"{e}\""))}. Fix them first, or leave {key} as {Describe(old.Type)}.");
            }

            try
            {
                await scope.UpdateAsync(db, set!, has, cancellationToken, [.. extra]);
            }
            catch (PostgresException ex) when (ex.SqlState.StartsWith("22", StringComparison.Ordinal))
            {
                throw new LinkbaseValidationException($"A value of {key} isn't {Describe(to)}: {ex.MessageText}.");
            }

            var spec = old with { Type = to, Options = newOptions };
            Change(changes, EntityId(home, key), "type", old.Type.ToString().ToLowerInvariant(), to.ToString().ToLowerInvariant());
            await ReplaceAsync(db, home, key, spec, cancellationToken);
        }, cancellationToken);
    }

    public async Task RenameFieldAsync(FieldHome home, string key, string newKey, ChangeReason? reason = null, CancellationToken cancellationToken = default)
    {
        newKey = newKey.Trim();
        CheckKey(home, newKey);
        await WriteAsync(reason, async (db, changes) =>
        {
            await GuardAsync(db, home, destructive: true, $"rename {key}", cancellationToken);
            var old = await FieldAsync(db, home, key, cancellationToken);
            if (newKey == key)
            {
                throw new LinkbaseValidationException($"{Title(key)} already has that name.");
            }

            if ((await FieldsOfAsync(db, home, cancellationToken)).Any(f => f.Key == newKey))
            {
                throw new LinkbaseValidationException($"{home.Label} already has a {home.Noun} called {newKey}.");
            }

            var scope = new Scope(home);
            await scope.UpdateAsync(db, $"({scope.P} - @key) || jsonb_build_object(@new, {scope.P} -> @key)", $"jsonb_exists({scope.P}, @key)", cancellationToken,
                Text("key", key), Text("new", newKey));
            if (!home.IsLink)
            {
                // The type's name template and hierarchy follow the new name.
                var type = await db.NodeTypes.AsNoTracking().SingleAsync(t => t.Name == home.Type, cancellationToken);
                var template = type.NameTemplate?.Replace("{" + key + "}", "{" + newKey + "}", StringComparison.Ordinal);
                await ExecuteAsync(db,
                    "UPDATE node_type SET name_template = @template, hierarchy_field = CASE WHEN hierarchy_field = @key THEN @new ELSE hierarchy_field END WHERE name = @type",
                    [Text("template", template), Text("key", key), Text("new", newKey), Text("type", home.Type)], cancellationToken);
            }

            await ReplaceAsync(db, home, key, old with { Key = newKey }, cancellationToken);
            changes.Add(Defined(EntityId(home, key), ChangeOp.Update, "key", key, newKey));
        }, cancellationToken);
    }

    // Takes a field away. Values it holds are lost, so that needs saying: it returns how many records or links had one.
    public async Task<long> DeleteFieldAsync(FieldHome home, string key, bool discardValues = false, ChangeReason? reason = null, CancellationToken cancellationToken = default) =>
        await WriteAsync(reason, async (db, changes) =>
        {
            await GuardAsync(db, home, destructive: true, $"delete {key}", cancellationToken);
            var old = await FieldAsync(db, home, key, cancellationToken);
            if (!home.IsLink)
            {
                var type = await db.NodeTypes.AsNoTracking().SingleAsync(t => t.Name == home.Type, cancellationToken);
                if (type.HierarchyField == key)
                {
                    throw new LinkbaseValidationException($"{Title(key)} sets the levels of {home.Type}'s hierarchy. Take the hierarchy away first.");
                }

                if (type.NameTemplate is { } template && NameTemplates.Tokens(template).Contains(key))
                {
                    throw new LinkbaseValidationException($"{home.Type}'s name template uses {{{key}}}. Change the template first.");
                }
            }

            var scope = new Scope(home);
            var has = $"jsonb_exists({scope.P}, @key)";
            var held = await scope.CountAsync(db, has, cancellationToken, Text("key", key));
            if (held > 0 && !discardValues)
            {
                throw new LinkbaseValidationException(
                    $"{held:N0} {Plural(home)} have a value for {key}, and deleting it loses them. Delete it anyway to discard them.");
            }

            if (held > 0)
            {
                await scope.UpdateAsync(db, $"{scope.P} - @key", has, cancellationToken, Text("key", key));
            }

            await RemoveAsync(db, home, key, cancellationToken);
            changes.Add(Defined(EntityId(home, key), ChangeOp.Delete, from: Json(Describe(old)), to: held > 0 ? Json(new { discarded = held }) : null));
            return held;
        }, cancellationToken);

    // ---- Checks

    private static string Plural(FieldHome home) => home.IsLink ? "links" : "records";

    private static string Describe(FieldType type) => type switch
    {
        FieldType.Number => "a number",
        FieldType.Date => "a date",
        FieldType.Boolean => "a yes/no",
        FieldType.Choice => "a choice",
        _ => "text",
    };

    // A text list is a text field with several values.
    private static FieldSpec Normalised(FieldSpec spec) => spec with
    {
        Type = spec.Type == FieldType.List ? FieldType.Text : spec.Type,
        Multiple = spec.Multiple || spec.Type == FieldType.List,
        Key = spec.Key.Trim(),
        Description = Clean(spec.Description),
        Group = Clean(spec.Group),
        Options = spec.Type == FieldType.Choice ? spec.OptionList.Select(o => o.Trim()).ToList() : null,
    };

    private static void CheckKey(FieldHome home, string key)
    {
        if (!ModelFile.FieldKey().IsMatch(key) || (key == "name" && !home.IsLink))
        {
            throw new LinkbaseValidationException($"A {home.Noun}'s name is lower case letters, digits and underscores, starting with a letter{(home.IsLink ? "" : ", and not name")}.");
        }
    }

    private static void CheckOptions(string key, IReadOnlyList<string> options)
    {
        if (options.Count is 0 or > FieldValues.MaxOptions)
        {
            throw new LinkbaseValidationException($"{Title(key)} is a choice, so it needs between 1 and {FieldValues.MaxOptions} options.");
        }

        if (options.Any(string.IsNullOrWhiteSpace))
        {
            throw new LinkbaseValidationException($"{Title(key)} has an empty option.");
        }

        if (options.GroupBy(o => o.Trim(), StringComparer.OrdinalIgnoreCase).FirstOrDefault(g => g.Count() > 1) is { } repeated)
        {
            throw new LinkbaseValidationException($"{Title(key)} has the option {repeated.Key} more than once.");
        }

        if (options.Any(o => o.Length > 100 || o.Any(char.IsControl)))
        {
            throw new LinkbaseValidationException($"{Title(key)}'s options are short text, up to 100 characters.");
        }
    }

    private static void CheckSpec(FieldHome home, FieldSpec spec)
    {
        CheckKey(home, spec.Key);
        if (spec.Description is { } description && (description.Length > MaxDescription || description.Any(char.IsControl)))
        {
            throw new LinkbaseValidationException($"A description is up to {MaxDescription} characters of text.");
        }

        if (spec.Group is { } group && (group.Length > MaxGroup || group.Any(char.IsControl)))
        {
            throw new LinkbaseValidationException($"A group is up to {MaxGroup} characters of text.");
        }

        if (home.IsLink && spec.Group is not null)
        {
            throw new LinkbaseValidationException("A value on a link can't have a group.");
        }

        if (spec.Multiple && spec.Type == FieldType.Boolean)
        {
            throw new LinkbaseValidationException("A yes/no can't have several values.");
        }

        if (spec.Type == FieldType.Choice)
        {
            CheckOptions(spec.Key, spec.OptionList);
        }
        else if (spec.OptionList.Count > 0)
        {
            throw new LinkbaseValidationException("Only a choice has options.");
        }
    }

    // Every record or link needs a value before the field can be required, because a required field can't be left empty.
    private async Task RequireFilledAsync(LinkbaseDbContext db, FieldHome home, FieldSpec spec, CancellationToken cancellationToken)
    {
        var scope = new Scope(home);
        var missing = $"NOT jsonb_exists({scope.P}, @key)" + (spec.Multiple ? $" OR jsonb_array_length({scope.P} -> @key) = 0" : "");
        if (await scope.CountAsync(db, missing, cancellationToken, Text("key", spec.Key)) is > 0 and var count)
        {
            throw new LinkbaseValidationException(
                $"{count:N0} {Plural(home)} have no {spec.Key}, so it can't be required yet. Fill it in for them, or keep it optional.");
        }
    }

    // ---- Where a field is kept

    private static string EntityId(FieldHome home, string key) => home.IsLink ? $"attribute:{home.Label}.{key}" : $"field:{home.Type}.{key}";

    private async Task GuardAsync(LinkbaseDbContext db, FieldHome home, bool destructive, string what, CancellationToken cancellationToken)
    {
        if (!home.IsLink)
        {
            await EditableTypeAsync(db, home.Type, cancellationToken);
            if (destructive)
            {
                RefuseWatched(home.Type, what);
            }

            return;
        }

        var exists = await db.LinkDefinitions.AsNoTracking().AnyAsync(l => l.SourceType == home.Type && l.RelType == home.Rel && l.TargetType == home.To, cancellationToken);
        if (!exists)
        {
            throw new LinkbaseValidationException($"There is no relationship {home.Label}.");
        }

        foreach (var end in new[] { home.Type, home.To! }.Distinct())
        {
            await EditableTypeAsync(db, end, cancellationToken);
            if (destructive)
            {
                RefuseWatched(end, what);
            }
        }
    }

    private static async Task<string?> HierarchyFieldAsync(LinkbaseDbContext db, FieldHome home, CancellationToken cancellationToken) =>
        home.IsLink ? null : await db.NodeTypes.AsNoTracking().Where(t => t.Name == home.Type).Select(t => t.HierarchyField).SingleOrDefaultAsync(cancellationToken);

    private static async Task<List<FieldSpec>> FieldsOfAsync(LinkbaseDbContext db, FieldHome home, CancellationToken cancellationToken)
    {
        if (!home.IsLink)
        {
            return (await db.FieldDefinitions.AsNoTracking().Where(f => f.NodeTypeName == home.Type).OrderBy(f => f.Id).ToListAsync(cancellationToken))
                .Select(f => Spec(f.Key, f.Type, f.Options, f.Required, f.Multiple, f.Description, f.GroupName, f.Owner)).ToList();
        }

        return (await db.LinkAttributes.AsNoTracking()
                .Where(a => a.LinkDefinition.SourceType == home.Type && a.LinkDefinition.RelType == home.Rel && a.LinkDefinition.TargetType == home.To)
                .OrderBy(a => a.Id).ToListAsync(cancellationToken))
            .Select(a => Spec(a.Key, a.Type, a.Options, a.Required, a.Multiple, a.Description, null, null)).ToList();
    }

    private static async Task<FieldSpec> FieldAsync(LinkbaseDbContext db, FieldHome home, string key, CancellationToken cancellationToken)
    {
        var field = (await FieldsOfAsync(db, home, cancellationToken)).FirstOrDefault(f => f.Key == key)
            ?? throw new LinkbaseValidationException($"{home.Label} has no {home.Noun} called {key}.");
        return field.Owner is { } owner
            ? throw new LinkbaseValidationException($"{Title(key)} is kept by {owner}, so it can't be changed here.")
            : field;
    }

    private static Param[] Columns(FieldHome home, FieldSpec spec, string key) =>
    [
        Text("home_type", home.Type), Text("home_rel", home.Rel), Text("home_to", home.To), Text("key", key), Text("new_key", spec.Key),
        new Param("field_type", SnakeCaseEnumConverter<FieldType>.ToStore(Stored(spec)), NpgsqlDbType.Text),
        spec.Options is { Count: > 0 } ? TextArray("options", spec.Options) : new Param("options", null, NpgsqlDbType.Array | NpgsqlDbType.Text),
        new Param("required", spec.Required, NpgsqlDbType.Boolean), Text("description", spec.Description), Text("group", spec.Group),
        new Param("multiple", spec.Multiple, NpgsqlDbType.Boolean),
    ];

    private static Task<long> InsertAsync(LinkbaseDbContext db, FieldHome home, FieldSpec spec, CancellationToken cancellationToken) =>
        ExecuteAsync(db,
            home.IsLink
                ? """
                  INSERT INTO link_attribute (link_definition_id, key, type, options, required, description, multiple)
                  SELECT id, @new_key, @field_type, @options, @required, @description, @multiple FROM link_definition
                  WHERE source_type = @home_type AND rel_type = @home_rel AND target_type = @home_to
                  """
                : "INSERT INTO field_definition (node_type_name, key, type, options, required, description, group_name, multiple) VALUES (@home_type, @new_key, @field_type, @options, @required, @description, @group, @multiple)",
            Columns(home, spec, spec.Key), cancellationToken);

    private static Task<long> ReplaceAsync(LinkbaseDbContext db, FieldHome home, string key, FieldSpec spec, CancellationToken cancellationToken) =>
        ExecuteAsync(db,
            home.IsLink
                ? """
                  UPDATE link_attribute SET key = @new_key, type = @field_type, options = @options, required = @required, description = @description, multiple = @multiple
                  WHERE key = @key AND link_definition_id = (SELECT id FROM link_definition WHERE source_type = @home_type AND rel_type = @home_rel AND target_type = @home_to)
                  """
                : "UPDATE field_definition SET key = @new_key, type = @field_type, options = @options, required = @required, description = @description, group_name = @group, multiple = @multiple WHERE node_type_name = @home_type AND key = @key",
            Columns(home, spec, key), cancellationToken);

    private static Task<long> RemoveAsync(LinkbaseDbContext db, FieldHome home, string key, CancellationToken cancellationToken) =>
        ExecuteAsync(db,
            home.IsLink
                ? "DELETE FROM link_attribute WHERE key = @key AND link_definition_id = (SELECT id FROM link_definition WHERE source_type = @home_type AND rel_type = @home_rel AND target_type = @home_to)"
                : "DELETE FROM field_definition WHERE node_type_name = @home_type AND key = @key",
            [Text("home_type", home.Type), Text("home_rel", home.Rel), Text("home_to", home.To), Text("key", key)], cancellationToken);
}
