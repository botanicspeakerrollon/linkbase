using System.Text.Json;
using Linkbase.Data;
using Npgsql;
using NpgsqlTypes;

namespace Linkbase.Core.Models;

// One level of a hierarchy: a root has no parent, any other level lists the levels it may be in.
public sealed record HierarchyLevelRule(string Kind, bool Root, IReadOnlyList<string> Under);

// How a type's records form a tree: Field says which level a record is on, Rel is the relationship from a record to its
// parent, and Levels say what may sit under what. See docs/models.md.
public sealed record HierarchyRules(string TypeName, string FieldKey, string RelType, IReadOnlyList<HierarchyLevelRule> Levels)
{
    public HierarchyLevelRule? Level(string? kind) => Levels.FirstOrDefault(l => l.Kind == kind);

    // Why a record at one level can't be in a record at another, or null if it can. Used for each link to a parent.
    public string? CheckParent(string? childKind, string? parentKind)
    {
        if (Level(childKind) is not { } level)
        {
            return $"{TypeName} needs a {FieldKey} that is one of: {string.Join(", ", Levels.Select(l => l.Kind))}.";
        }

        if (level.Root)
        {
            return $"A {level.Kind} is at the top, so it can't be in another {TypeName}.";
        }

        return parentKind is not null && level.Under.Contains(parentKind)
            ? null
            : $"A {level.Kind} can only be in a {Either(level.Under)}, not a {parentKind ?? "record with no " + FieldKey}.";
    }

    // The same as one language: "country", "country or region".
    internal static string Either(IReadOnlyList<string> kinds) =>
        kinds.Count < 2 ? string.Join("", kinds) : string.Join(", ", kinds.Take(kinds.Count - 1)) + " or " + kinds[^1];

    internal string LevelsJson() => JsonSerializer.Serialize(
        Levels.Select(l => new { kind = l.Kind, root = l.Root, under = l.Under }), JsonSerializerOptions.Web);
}

// Checks the hierarchies of a whole store with SQL, for the loader and for committing an import. Records are only touched
// by reading, so a problem throws and the caller's transaction rolls everything back.
public static class HierarchyChecks
{
    private const int Examples = 5;

    // requireParent also wants every record that isn't a root to have a parent, which a loaded model must, but an import
    // of records can't promise, since it adds their links separately. With a batch, only the records that batch gave a
    // parent to are checked, so something wrong that was there already doesn't stop an unrelated import.
    public static async Task CheckAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, long? batchId, bool requireParent, CancellationToken cancellationToken)
    {
        foreach (var rules in await ReadAsync(connection, transaction, cancellationToken))
        {
            await CheckAsync(connection, transaction, rules, batchId, requireParent, cancellationToken);
        }
    }

    private static async Task<List<HierarchyRules>> ReadAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, CancellationToken cancellationToken)
    {
        var rows = new List<(string Type, string Field, string Rel, HierarchyLevelRule Level)>();
        await using (var command = new NpgsqlCommand(
            """
            SELECT t.name, t.hierarchy_field, t.hierarchy_rel, l.kind, l.is_root, l.under
            FROM node_type t JOIN hierarchy_level l ON l.node_type_name = t.name
            WHERE t.hierarchy_field IS NOT NULL AND t.hierarchy_rel IS NOT NULL
            ORDER BY t.name, l.id
            """, connection, transaction))
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                rows.Add((reader.GetString(0), reader.GetString(1), reader.GetString(2),
                    new HierarchyLevelRule(reader.GetString(3), reader.GetBoolean(4), reader.GetFieldValue<string[]>(5))));
            }
        }

        return rows.GroupBy(r => r.Type)
            .Select(g => new HierarchyRules(g.Key, g.First().Field, g.First().Rel, g.Select(r => r.Level).ToList()))
            .ToList();
    }

    private static async Task CheckAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, HierarchyRules rules, long? batchId, bool requireParent, CancellationToken cancellationToken)
    {
        // Field and relationship names were checked when the model file was read, but go in as parameters all the same.
        await using var command = new NpgsqlCommand(
            """
            WITH RECURSIVE level AS (
                SELECT l ->> 'kind' AS kind, (l ->> 'root')::boolean AS root, l -> 'under' AS under
                FROM jsonb_array_elements(@levels::jsonb) l
            ),
            child AS (
                SELECT n.id, n.key, n.props ->> @field AS kind FROM node n
                WHERE n.type = @type
                  AND (@batch::bigint IS NULL OR EXISTS (
                      SELECT 1 FROM node_link b WHERE b.source_id = n.id AND b.rel_type = @rel AND b.import_batch_id = @batch))
            ),
            edge AS (
                SELECT l.source_id AS child, l.target_id AS parent, p.props ->> @field AS parent_kind
                FROM node_link l JOIN node p ON p.id = l.target_id AND p.type = @type
                WHERE l.rel_type = @rel AND EXISTS (SELECT 1 FROM node s WHERE s.id = l.source_id AND s.type = @type)
            ),
            up (start, cur) AS (
                SELECT e.child, e.parent FROM edge e WHERE e.child IN (SELECT id FROM child)
                UNION
                SELECT up.start, e.parent FROM up JOIN edge e ON e.child = up.cur
            ),
            problem AS (
                SELECT 'top' AS p, c.key FROM child c JOIN level lv ON lv.kind = c.kind AND lv.root
                WHERE EXISTS (SELECT 1 FROM edge e WHERE e.child = c.id)
                UNION ALL
                SELECT 'kind', c.key FROM child c JOIN level lv ON lv.kind = c.kind AND NOT lv.root
                JOIN edge e ON e.child = c.id
                WHERE NOT coalesce(jsonb_exists(lv.under, e.parent_kind), false)
                UNION ALL
                SELECT 'many', c.key FROM child c WHERE (SELECT count(*) FROM edge e WHERE e.child = c.id) > 1
                UNION ALL
                SELECT 'none', c.key FROM child c JOIN level lv ON lv.kind = c.kind AND NOT lv.root
                WHERE @requireParent AND NOT EXISTS (SELECT 1 FROM edge e WHERE e.child = c.id)
                UNION ALL
                SELECT 'unknown', c.key FROM child c WHERE NOT EXISTS (SELECT 1 FROM level lv WHERE lv.kind = c.kind)
                UNION ALL
                SELECT 'cycle', c.key FROM child c WHERE EXISTS (SELECT 1 FROM up WHERE up.start = c.id AND up.cur = c.id)
            )
            SELECT p, count(*) OVER (PARTITION BY p), key FROM (
                SELECT p, key, row_number() OVER (PARTITION BY p ORDER BY key) AS n FROM problem
            ) numbered WHERE n <= @examples
            ORDER BY p, key
            """, connection, transaction)
        { CommandTimeout = 0 };
        command.Parameters.Add(new NpgsqlParameter("levels", NpgsqlDbType.Text) { Value = rules.LevelsJson() });
        command.Parameters.Add(new NpgsqlParameter("field", NpgsqlDbType.Text) { Value = rules.FieldKey });
        command.Parameters.Add(new NpgsqlParameter("type", NpgsqlDbType.Text) { Value = rules.TypeName });
        command.Parameters.Add(new NpgsqlParameter("rel", NpgsqlDbType.Text) { Value = rules.RelType });
        command.Parameters.Add(new NpgsqlParameter("batch", NpgsqlDbType.Bigint) { Value = (object?)batchId ?? DBNull.Value });
        command.Parameters.Add(new NpgsqlParameter("requireParent", NpgsqlDbType.Boolean) { Value = requireParent });
        command.Parameters.Add(new NpgsqlParameter("examples", NpgsqlDbType.Integer) { Value = Examples });

        var found = new List<(string Problem, long Total, string Key)>();
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                found.Add((reader.GetString(0), reader.GetInt64(1), reader.GetString(2)));
            }
        }

        if (found.Count == 0)
        {
            return;
        }

        var type = rules.TypeName;
        var lines = found.GroupBy(f => f.Problem).Select(g =>
        {
            var total = g.First().Total;
            var keys = string.Join(", ", g.Select(f => f.Key));
            return g.Key switch
            {
                "top" => $"{total:N0} {type} record(s) at the top level are in another {type}, such as {keys}.",
                "kind" => $"{total:N0} {type} record(s) are in a {type} of a level they can't be in, such as {keys}.",
                "many" => $"{total:N0} {type} record(s) are in more than one {type}, such as {keys}.",
                "none" => $"{total:N0} {type} record(s) below the top level aren't in any {type}, such as {keys}.",
                "unknown" => $"{total:N0} {type} record(s) have no {rules.FieldKey} that is one of {string.Join(", ", rules.Levels.Select(l => l.Kind))}, such as {keys}.",
                _ => $"{total:N0} {type} record(s) are in themselves through the {rules.RelType} links, such as {keys}.",
            };
        });
        throw new LinkbaseValidationException(
            $"The {type} hierarchy isn't followed: {string.Join(" ", lines)}"
            + (batchId is null ? "" : " Untick the links involved in the review, then commit again."));
    }
}
