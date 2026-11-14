using Linkbase.Data;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using NpgsqlTypes;

namespace Linkbase.Core.Paths;

// Which way a trace follows links. Downstream is the way a link points (from its source to its target), upstream is back
// against it, and both follows each link either way.
public enum TraceDirection
{
    Downstream,
    Upstream,
    Both,
}

// One record reached from a parent, and the link that led there. Outgoing is true when the link points from the parent to
// this record. Further is how many links the record has to follow on, and stops counting past FurtherCap, so a busy record
// doesn't make the page slow.
public sealed record TraceChild(string ParentId, string NodeId, string Label, string Name, string RelType, bool Outgoing, int Further)
{
    public bool HasMore => Further > 0;

    public string FurtherText => Further > TraceRepository.FurtherCap ? $"{TraceRepository.FurtherCap}+" : Further.ToString(System.Globalization.CultureInfo.InvariantCulture);
}

// The records a trace dialog hands back to Explore to draw, the record it started from first.
public sealed record TraceResult(IReadOnlyList<string> NodeIds);

// The records one step on from some parents, and for each parent how many more matched than were returned.
public sealed record TraceStep(IReadOnlyList<TraceChild> Children, IReadOnlyDictionary<string, int> Remaining);

// Follows the links out of records, a step at a time, for the trace in Explore. It reads one step at a time, so the page
// decides how deep to go, and a busy record never makes it read tens of thousands of links.
public sealed class TraceRepository(IDbContextFactory<LinkbaseDbContext> contextFactory)
{
    // Counting a record's onward links stops here.
    public const int FurtherCap = 100;

    // The most parents one call looks at. The page expands a level at a time, and a level with more records than this
    // leaves the rest collapsed, for the person to open.
    public const int MaxParents = 200;

    // The step from the given records, in either direction, ordered by type then name within each parent. Skip and limit
    // count the children of each parent, so Show more can ask for the next page. RelTypes narrows the links followed.
    public async Task<TraceStep> StepAsync(
        IReadOnlyCollection<string> parentIds,
        TraceDirection direction,
        IReadOnlyCollection<string>? relTypes,
        int skip,
        int limit,
        CancellationToken cancellationToken = default)
    {
        if (parentIds.Count == 0)
        {
            return new TraceStep([], new Dictionary<string, int>());
        }

        if (parentIds.Count > MaxParents)
        {
            throw new LinkbaseValidationException($"A trace expands at most {MaxParents} records at once.");
        }

        var parents = new NpgsqlParameter("parents", NpgsqlDbType.Array | NpgsqlDbType.Text) { Value = parentIds.ToArray() };
        var rels = new NpgsqlParameter("rels", NpgsqlDbType.Array | NpgsqlDbType.Text)
        {
            Value = relTypes is { Count: > 0 } ? relTypes.ToArray() : (object)DBNull.Value,
        };
        const string Rels = "(@rels IS NULL OR e.rel_type = ANY(@rels))";

        var hits = new List<string>();
        if (direction != TraceDirection.Upstream)
        {
            hits.Add($"SELECT e.source_id AS parent, e.target_id AS child, e.rel_type, true AS outgoing FROM graph_edges e WHERE e.source_id = ANY(@parents) AND {Rels}");
        }

        if (direction != TraceDirection.Downstream)
        {
            hits.Add($"SELECT e.target_id AS parent, e.source_id AS child, e.rel_type, false AS outgoing FROM graph_edges e WHERE e.target_id = ANY(@parents) AND {Rels}");
        }

        var onward = direction switch
        {
            TraceDirection.Downstream => "e.source_id = j.child",
            TraceDirection.Upstream => "e.target_id = j.child",
            _ => "(e.source_id = j.child OR e.target_id = j.child)",
        };

        // Materialized so the sort happens after the links are found, as in the neighbour query, or the planner may walk
        // every node in label order hoping to reach the limit early.
        var sql =
            $"""
            WITH hits AS MATERIALIZED (
                SELECT DISTINCT parent, child, rel_type, outgoing FROM ({string.Join(" UNION ALL ", hits)}) x
                WHERE parent <> child
            ),
            joined AS MATERIALIZED (
                SELECT h.parent, h.child, h.rel_type, h.outgoing, n.label, n.name,
                       row_number() OVER (PARTITION BY h.parent ORDER BY n.label, n.name, h.child, h.rel_type, h.outgoing) AS rn,
                       count(*) OVER (PARTITION BY h.parent) AS total
                FROM hits h
                JOIN graph_nodes n ON n.node_id = h.child
                WHERE n.user_id = @user
            )
            SELECT j.parent AS "ParentId", j.child AS "NodeId", j.label AS "Label", j.name AS "Name", j.rel_type AS "RelType",
                   j.outgoing AS "Outgoing", j.total AS "Total", f.cnt AS "Further"
            FROM joined j
            CROSS JOIN LATERAL (
                SELECT count(*)::int AS cnt
                FROM (SELECT 1 FROM graph_edges e WHERE {onward} AND {Rels} LIMIT {FurtherCap + 1}) c
            ) f
            WHERE j.rn > @skip AND j.rn <= @skip + @limit
            ORDER BY j.parent, j.rn
            """;

        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var rows = await db.Database.SqlQueryRaw<TraceRow>(
            sql,
            parents,
            rels,
            new NpgsqlParameter("user", NpgsqlDbType.Bigint) { Value = AppUser.DefaultId },
            new NpgsqlParameter("skip", NpgsqlDbType.Integer) { Value = Math.Max(0, skip) },
            new NpgsqlParameter("limit", NpgsqlDbType.Integer) { Value = Math.Max(1, limit) }).ToListAsync(cancellationToken);

        var remaining = rows
            .GroupBy(r => r.ParentId)
            .Select(g => (Parent: g.Key, Left: (int)g.First().Total - Math.Max(0, skip) - g.Count()))
            .Where(g => g.Left > 0)
            .ToDictionary(g => g.Parent, g => g.Left);
        return new TraceStep(
            rows.Select(r => new TraceChild(r.ParentId, r.NodeId, r.Label, r.Name, r.RelType, r.Outgoing, r.Further)).ToList(),
            remaining);
    }

    private sealed record TraceRow(string ParentId, string NodeId, string Label, string Name, string RelType, bool Outgoing, long Total, int Further);
}
