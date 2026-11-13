using Linkbase.Core.Graph;
using Linkbase.Data;
using Microsoft.EntityFrameworkCore;

namespace Linkbase.Core.Paths;

// How two records are connected: the records on the way, from the first to the second, and the links between them. Truncated
// means the search gave up on a very busy record (one with thousands of links) before it had looked everywhere, so a shorter
// path may exist. No Nodes and not Truncated means there is no path within MaxHops.
public sealed record PathResult(IReadOnlyList<string> NodeIds, IReadOnlyList<GraphEdge> Edges, bool Truncated, int MaxHops)
{
    public bool Found => NodeIds.Count > 0;

    public int Hops => Edges.Count;
}

// Finds the shortest chain of links between two records, in either direction, whatever the relationships are. It searches
// from both ends at once, a level at a time, always growing the end with fewer records to look at, so the search stays small
// even when the graph is big.
public sealed class PathRepository(IDbContextFactory<LinkbaseDbContext> contextFactory)
{
    public const int DefaultMaxHops = 6;

    // The most records one side looks through at once, and the most links read for one level. Past these the search stops
    // and says it was cut short, rather than reading a hub's tens of thousands of links.
    private const int FrontierLimit = 5000;
    private const int LinkLimit = 50_000;

    private sealed record Step(string Previous, string SourceId, string TargetId, string RelType);

    public async Task<PathResult> ShortestPathAsync(string fromId, string toId, int maxHops = DefaultMaxHops, CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var found = await db.Nodes.AsNoTracking().Where(n => (n.Id == fromId || n.Id == toId) && n.UserId == AppUser.DefaultId).Select(n => n.Id).ToListAsync(cancellationToken);
        if (!found.Contains(fromId) || !found.Contains(toId))
        {
            throw new LinkbaseValidationException("One of the records no longer exists.");
        }

        if (fromId == toId)
        {
            return new PathResult([fromId], [], false, maxHops);
        }

        // Each side remembers how it reached every record it has seen, so a path can be walked back to its start.
        var reached = new[] { new Dictionary<string, Step?> { [fromId] = null }, new Dictionary<string, Step?> { [toId] = null } };
        var frontier = new[] { new List<string> { fromId }, new List<string> { toId } };
        var truncated = false;

        for (var depth = 0; depth < maxHops; depth++)
        {
            if (frontier[0].Count == 0 || frontier[1].Count == 0)
            {
                break;
            }

            var side = frontier[0].Count <= frontier[1].Count ? 0 : 1;
            var current = frontier[side];
            if (current.Count > FrontierLimit)
            {
                current = current.Take(FrontierLimit).ToList();
                truncated = true;
            }

            var links = await db.GraphEdges.AsNoTracking()
                .Where(e => current.Contains(e.SourceId) || current.Contains(e.TargetId))
                .Take(LinkLimit + 1)
                .Select(e => new { e.SourceId, e.TargetId, e.RelType })
                .ToListAsync(cancellationToken);
            if (links.Count > LinkLimit)
            {
                links.RemoveAt(links.Count - 1);
                truncated = true;
            }

            var inFrontier = current.ToHashSet();
            var next = new List<string>();
            string? meeting = null;
            foreach (var link in links)
            {
                foreach (var (from, to) in new[] { (link.SourceId, link.TargetId), (link.TargetId, link.SourceId) })
                {
                    if (!inFrontier.Contains(from) || reached[side].ContainsKey(to))
                    {
                        continue;
                    }

                    reached[side][to] = new Step(from, link.SourceId, link.TargetId, link.RelType);
                    next.Add(to);
                    if (meeting is null && reached[1 - side].ContainsKey(to))
                    {
                        meeting = to;
                    }
                }
            }

            if (meeting is not null)
            {
                return Walk(meeting, reached, maxHops, truncated);
            }

            frontier[side] = next;
        }

        return new PathResult([], [], truncated, maxHops);
    }

    // The path through the record where the two searches met: back to the start from there, and on to the end.
    private static PathResult Walk(string meeting, Dictionary<string, Step?>[] reached, int maxHops, bool truncated)
    {
        var edges = new List<GraphEdge>();
        var nodes = new List<string>();

        var toStart = new List<string> { meeting };
        for (var at = meeting; reached[0][at] is { } step; at = step.Previous)
        {
            edges.Insert(0, new GraphEdge(step.SourceId, step.TargetId, step.RelType));
            toStart.Insert(0, step.Previous);
        }

        nodes.AddRange(toStart);
        for (var at = meeting; reached[1][at] is { } step; at = step.Previous)
        {
            edges.Add(new GraphEdge(step.SourceId, step.TargetId, step.RelType));
            nodes.Add(step.Previous);
        }

        return new PathResult(nodes, edges, truncated, maxHops);
    }
}
