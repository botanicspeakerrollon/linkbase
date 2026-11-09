using System.Text.Json.Nodes;
using Linkbase.Data;
using Microsoft.EntityFrameworkCore;

namespace Linkbase.Domains.Climbing.Data;

// Checks a generic store against the typed tables it was copied from: the same records of each type, each with the same
// fields, name and dates, the same links and the same external ids. Reports what differs, such as "ascent:7: date is
// 2012-07-01, expected 2012-07-02".
public sealed class GenericVerifier(IDbContextFactory<LinkbaseDbContext> source, IDbContextFactory<LinkbaseDbContext> target)
{
    private const int MostReported = 20;

    // A copy keeps its rows' timestamps. Two separate imports of the same files can't, so they pass compareTimestamps: false.
    public async Task<IReadOnlyList<string>> VerifyAsync(bool compareTimestamps = true, CancellationToken cancellationToken = default)
    {
        var expected = await ClimbingGraph.ReadAsync(source, cancellationToken);
        await using var db = await target.CreateDbContextAsync(cancellationToken);
        var problems = new List<string>();

        var nodes = await db.Nodes.AsNoTracking()
            .Select(n => new { n.Id, n.Type, n.Key, n.Name, n.Props, n.CreatedAt, n.UpdatedAt })
            .ToDictionaryAsync(n => n.Id, cancellationToken);
        foreach (var type in expected.Nodes.Select(n => n.Type).Concat(nodes.Values.Select(n => n.Type)).Distinct().Order())
        {
            var want = expected.Nodes.Count(n => n.Type == type);
            var have = nodes.Values.Count(n => n.Type == type);
            if (want != have)
            {
                problems.Add($"{type}: {have:N0} records, expected {want:N0}");
            }
        }

        foreach (var node in expected.Nodes)
        {
            if (!nodes.TryGetValue(node.Id, out var actual))
            {
                problems.Add($"{node.Id}: missing");
                continue;
            }

            Compare(problems, node.Id, "type", node.Type, actual.Type);
            Compare(problems, node.Id, "key", node.Key, actual.Key);
            Compare(problems, node.Id, "name", node.Name, actual.Name);
            if (compareTimestamps)
            {
                Compare(problems, node.Id, "created", node.CreatedAt.ToUniversalTime().ToString("O"), actual.CreatedAt.ToUniversalTime().ToString("O"));
                Compare(problems, node.Id, "updated", node.UpdatedAt.ToUniversalTime().ToString("O"), actual.UpdatedAt.ToUniversalTime().ToString("O"));
            }

            var props = JsonNode.Parse(actual.Props) as JsonObject ?? [];
            foreach (var key in node.Props.Select(p => p.Key).Concat(props.Select(p => p.Key)).Distinct().Order())
            {
                if (!JsonNode.DeepEquals(node.Props[key], props[key]))
                {
                    problems.Add($"{node.Id}: {key} is {Show(props[key])}, expected {Show(node.Props[key])}");
                }
            }
        }

        var links = await db.NodeLinks.AsNoTracking().Select(l => new { l.SourceId, l.RelType, l.TargetId, l.Props }).ToListAsync(cancellationToken);
        var actualLinks = links.GroupBy(l => $"{l.SourceId} {l.RelType} {l.TargetId}").ToDictionary(g => g.Key, g => g.ToList());
        foreach (var link in expected.Links)
        {
            if (!actualLinks.TryGetValue(link.Signature, out var found))
            {
                problems.Add($"{link.Signature}: missing");
            }
            else if (found.Count > 1)
            {
                problems.Add($"{link.Signature}: {found.Count} times");
            }
            else if (!JsonNode.DeepEquals(link.Props, JsonNode.Parse(found[0].Props)))
            {
                problems.Add($"{link.Signature}: props are {found[0].Props}, expected {link.Props.ToJsonString()}");
            }
        }

        var wanted = expected.Links.Select(l => l.Signature).ToHashSet();
        problems.AddRange(actualLinks.Keys.Where(k => !wanted.Contains(k)).Order().Select(k => $"{k}: not in the typed tables"));

        var refs = await db.ExternalRefs.AsNoTracking().Where(r => r.NodeId != null)
            .Select(r => new { NodeId = r.NodeId!, NodeType = r.NodeType!, r.Source, r.ExternalId, r.Url })
            .ToListAsync(cancellationToken);
        var actualRefs = refs.ToDictionary(r => $"{r.NodeId} {r.Source} {r.ExternalId}");
        foreach (var r in expected.Refs)
        {
            var signature = $"{r.NodeId} {r.Source} {r.ExternalId}";
            if (!actualRefs.TryGetValue(signature, out var found))
            {
                problems.Add($"{r.NodeId}: {r.Source} id {r.ExternalId} is missing");
            }
            else if (found.Url != r.Url || found.NodeType != r.NodeType)
            {
                problems.Add($"{r.NodeId}: {r.Source} id {r.ExternalId} has url {found.Url ?? "none"}, expected {r.Url ?? "none"}");
            }
        }

        var wantedRefs = expected.Refs.Select(r => $"{r.NodeId} {r.Source} {r.ExternalId}").ToHashSet();
        problems.AddRange(actualRefs.Keys.Where(k => !wantedRefs.Contains(k)).Order().Select(k => $"{k}: external id not in the typed tables"));

        // The counts kept with the definitions are what the home page shows.
        var kept = await db.LinkDefinitions.AsNoTracking().ToListAsync(cancellationToken);
        var types = nodes.ToDictionary(n => n.Key, n => n.Value.Type);
        foreach (var definition in kept)
        {
            var real = links.Count(l => l.RelType == definition.RelType && types[l.SourceId] == definition.SourceType && types[l.TargetId] == definition.TargetType);
            if (definition.Links != real)
            {
                problems.Add($"{definition.SourceType} {definition.RelType} {definition.TargetType}: the kept count is {definition.Links:N0}, but there are {real:N0}");
            }
        }

        return problems.Count <= MostReported ? problems : [.. problems.Take(MostReported), $"and {problems.Count - MostReported:N0} more"];
    }

    private static void Compare(List<string> problems, string id, string what, string expected, string actual)
    {
        if (expected != actual)
        {
            problems.Add($"{id}: {what} is {actual}, expected {expected}");
        }
    }

    private static string Show(JsonNode? value) => value?.ToJsonString() ?? "none";
}
