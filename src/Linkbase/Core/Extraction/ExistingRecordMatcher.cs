using Linkbase.Core.Records;
using Linkbase.Core.Staging;
using Linkbase.Data;
using Microsoft.EntityFrameworkCore;

namespace Linkbase.Core.Extraction;

// Decides which records read from text are already in the model. A name that matches exactly is the same record, and so is
// the one similar name there is ("Stanage" for "Stanage Edge"), unless it differs only by a number, as in a series. A
// record whose type requires a link, such as a route at a crag, only matches one with that link to the record the text
// puts it at, so a second "Hope" at another crag is a new route. Similar names that can't be told apart are added as
// new, with a note, because a wrong match would lose the record while a duplicate is easy to merge.
internal static class ExistingRecordMatcher
{
    private const double Threshold = 0.5;
    private const int MaxCandidates = 10;

    private sealed record Candidate(string Id, string Name, bool Exact);

    // Exact is what a plain look-up by name found, for the records and for the ends of links. Returns the same, decided again
    // for each record.
    public static async Task<Dictionary<(string Type, string Name), string>> MatchAsync(
        LinkbaseDbContext db,
        ExtractionSchema schema,
        ModelExtractionValidator.Result data,
        Dictionary<(string Type, string Name), string> exact,
        List<string> notes,
        CancellationToken cancellationToken)
    {
        var result = new Dictionary<(string Type, string Name), string>(exact);
        var extracted = new HashSet<(string Type, string Name)>();
        var candidates = new Dictionary<(string Type, string Name), List<Candidate>>();
        foreach (var record in data.Records)
        {
            var key = (record.Type, ModelExtractionValidator.Normalise(record.Name));
            extracted.Add(key);
            result.Remove(key);
            candidates[key] = await CandidatesAsync(db, record.Type, record.Name, cancellationToken);
        }

        var linked = await LinksOfCandidatesAsync(db, schema, data, candidates, cancellationToken);
        var decided = new HashSet<(string Type, string Name)>();
        var pending = data.Records.ToList();

        // Parents are decided before their children, so each pass settles one more level.
        for (var pass = 0; pass <= schema.Types.Count && pending.Count > 0; pass++)
        {
            var last = pass == schema.Types.Count;
            var next = new List<StagedRecord>();
            foreach (var record in pending)
            {
                var key = (record.Type, ModelExtractionValidator.Normalise(record.Name));
                var pool = candidates[key].AsEnumerable();
                var waiting = false;
                foreach (var required in schema.Type(record.Type)?.RequiredLinks ?? [])
                {
                    var targets = TargetNames(data, record, required.RelType, required.OtherType);
                    if (targets.Count == 0)
                    {
                        continue;
                    }

                    var ids = new List<string>();
                    foreach (var target in targets)
                    {
                        var targetKey = (required.OtherType, ModelExtractionValidator.Normalise(target));
                        if (result.TryGetValue(targetKey, out var id))
                        {
                            ids.Add(id);
                        }
                        else if (extracted.Contains(targetKey) && !decided.Contains(targetKey) && !last)
                        {
                            waiting = true;
                        }
                    }

                    // A new record can't already have a link to a record that's new too.
                    pool = pool.Where(c => ids.Any(id => linked.Contains((required.RelType, c.Id, id)))).ToList();
                }

                if (waiting)
                {
                    next.Add(record);
                    continue;
                }

                decided.Add(key);
                Decide(record, pool.ToList(), result, key, notes);
            }

            pending = next;
        }

        return result;
    }

    private static void Decide(
        StagedRecord record, List<Candidate> pool, Dictionary<(string Type, string Name), string> result, (string Type, string Name) key, List<string> notes)
    {
        if (pool.FirstOrDefault(c => c.Exact) is { } same)
        {
            result[key] = same.Id;
            return;
        }

        var similar = pool.Where(c => !NameMatching.OnlyNumberingDiffers(record.Name, c.Name)).ToList();
        if (similar.Count == 1)
        {
            result[key] = similar[0].Id;
            notes.Add($"Treated {record.Type} '{record.Name}' as the {record.Type} already here, '{similar[0].Name}'.");
        }
        else if (similar.Count > 1)
        {
            notes.Add($"{record.Type} '{record.Name}' is like several already here ({string.Join(", ", similar.Take(3).Select(c => $"'{c.Name}'"))}), so it was added as new.");
        }
    }

    // The names a record is linked to by a relationship, in the links the text states.
    private static List<string> TargetNames(ModelExtractionValidator.Result data, StagedRecord record, string relType, string otherType) =>
        data.Links
            .Where(l => l.RelType == relType && l.FromType == record.Type && l.ToType == otherType
                && ModelExtractionValidator.Normalise(l.FromName) == ModelExtractionValidator.Normalise(record.Name))
            .Select(l => l.ToName)
            .Distinct()
            .ToList();

    private static async Task<List<Candidate>> CandidatesAsync(LinkbaseDbContext db, string type, string name, CancellationToken cancellationToken)
    {
        var found = await db.Nodes.AsNoTracking()
            .Where(n => n.Type == type && (n.Name == name || EF.Functions.TrigramsSimilarity(n.Name, name) >= Threshold))
            .OrderByDescending(n => EF.Functions.TrigramsSimilarity(n.Name, name))
            .Take(MaxCandidates)
            .Select(n => new { n.Id, n.Name })
            .ToListAsync(cancellationToken);
        var wanted = ModelExtractionValidator.Normalise(name);
        return found.Select(n => new Candidate(n.Id, n.Name, ModelExtractionValidator.Normalise(n.Name) == wanted)).ToList();
    }

    // The links the candidates have for the relationships their type requires: (relationship, source id, target id).
    private static async Task<HashSet<(string RelType, string SourceId, string TargetId)>> LinksOfCandidatesAsync(
        LinkbaseDbContext db,
        ExtractionSchema schema,
        ModelExtractionValidator.Result data,
        Dictionary<(string Type, string Name), List<Candidate>> candidates,
        CancellationToken cancellationToken)
    {
        var linked = new HashSet<(string, string, string)>();
        foreach (var type in schema.Types.Where(t => t.RequiredLinks.Count > 0))
        {
            var ids = data.Records.Where(r => r.Type == type.Name)
                .SelectMany(r => candidates[(r.Type, ModelExtractionValidator.Normalise(r.Name))])
                .Select(c => c.Id)
                .Distinct()
                .ToList();
            if (ids.Count == 0)
            {
                continue;
            }

            var rels = type.RequiredLinks.Select(r => r.RelType).Distinct().ToList();
            var links = await db.NodeLinks.AsNoTracking()
                .Where(l => ids.Contains(l.SourceId) && rels.Contains(l.RelType))
                .Select(l => new { l.RelType, l.SourceId, l.TargetId })
                .ToListAsync(cancellationToken);
            linked.UnionWith(links.Select(l => (l.RelType, l.SourceId, l.TargetId)));
        }

        return linked;
    }
}
