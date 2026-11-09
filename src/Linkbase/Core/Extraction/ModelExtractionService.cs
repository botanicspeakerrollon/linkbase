using System.Text.Json.Nodes;
using Linkbase.Core.Models;
using Linkbase.Core.Records;
using Linkbase.Core.Staging;
using Linkbase.Data;
using Microsoft.EntityFrameworkCore;

namespace Linkbase.Core.Extraction;

// Reads text for any model: fetches a page or takes pasted text, has the extractor read it part by part against the model's
// own types, checks the answer against the text, and stages what's new for review and commit by ImportStagingService. Like
// a CSV import, records already there are left as they are, so undoing only ever removes what the import added.
public sealed class ModelExtractionService(
    RecordRepository records,
    IModelExtractor extractor,
    PageTextFetcher fetcher,
    IDbContextFactory<LinkbaseDbContext> contextFactory,
    ImportStagingService staging,
    RecordHooks? hooks = null)
{
    private const int NotesShown = 20;
    private readonly RecordHooks _hooks = hooks ?? RecordHooks.None;

    public async Task<long> ExtractFromUrlAsync(string url, IProgress<ExtractionProgress>? progress, CancellationToken cancellationToken)
    {
        progress?.Report(new ExtractionProgress("Fetching the page", 0, 1));
        var page = await fetcher.FetchAsync(url, cancellationToken);
        return await ExtractAsync(page.Text, url.Trim(), progress, cancellationToken);
    }

    public Task<long> ExtractFromTextAsync(string text, string? sourceUrl, IProgress<ExtractionProgress>? progress, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            throw new ExtractionFailedException("There's no text to import.");
        }

        return ExtractAsync(text, string.IsNullOrWhiteSpace(sourceUrl) ? null : sourceUrl.Trim(), progress, cancellationToken);
    }

    private async Task<long> ExtractAsync(string text, string? source, IProgress<ExtractionProgress>? progress, CancellationToken cancellationToken)
    {
        var types = await records.TypesAsync(cancellationToken);
        var schema = ExtractionSchema.From(types, await records.RelationshipsAsync(cancellationToken));

        var chunks = TextChunker.Split(text);
        var found = new List<ExtractedData>();
        var known = new List<string>();
        for (var i = 0; i < chunks.Count; i++)
        {
            progress?.Report(new ExtractionProgress($"Reading part {i + 1} of {chunks.Count}", i, chunks.Count));
            var part = await extractor.ExtractAsync(schema, chunks[i], known, cancellationToken);
            found.Add(part);
            known.AddRange(part.Records.Select(r => r.Name).Except(known, StringComparer.OrdinalIgnoreCase));
        }

        progress?.Report(new ExtractionProgress("Checking the results", chunks.Count, chunks.Count));
        var checkedData = ModelExtractionValidator.Validate(
            schema, new ExtractedData(found.SelectMany(d => d.Records).ToList(), found.SelectMany(d => d.Links).ToList()), text);
        var batch = await StageAsync(schema, types, checkedData, source ?? "Pasted text", cancellationToken);
        progress?.Report(new ExtractionProgress("Done", chunks.Count, chunks.Count));
        return batch;
    }

    private async Task<long> StageAsync(
        ExtractionSchema schema, IReadOnlyList<RecordType> types, ModelExtractionValidator.Result data, string source, CancellationToken cancellationToken)
    {
        var notes = data.Warnings.ToList();
        var dropped = notes.Count;

        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);

        // Records already in the model, matched by key or by name, are left as they are and can still be linked to.
        var (existing, taken) = await ExistingAsync(
            db, data.Records.Select(r => (r.Type, r.Key, r.Name)).Concat(data.Links.SelectMany(l => new[] { (l.FromType, Key(l.FromName), l.FromName), (l.ToType, Key(l.ToName), l.ToName) })),
            cancellationToken);

        // A new record whose key is taken by a different record gets the next free one.
        var rekeyed = new List<StagedRecord>();
        foreach (var record in data.Records)
        {
            var candidate = record;
            if (!existing.ContainsKey((record.Type, ModelExtractionValidator.Normalise(record.Name))))
            {
                var stem = record.Key;
                for (var n = 2; taken.Contains(candidate.Id); n++)
                {
                    candidate = record with { Key = $"{stem}-{n}" };
                }

                taken.Add(candidate.Id);
            }

            rekeyed.Add(candidate);
        }

        var extracted = rekeyed.ToDictionary(r => (r.Type, ModelExtractionValidator.Normalise(r.Name)), r => r.Id);

        string? Resolve(string type, string name) =>
            existing.TryGetValue((type, ModelExtractionValidator.Normalise(name)), out var id) ? id
            : extracted.TryGetValue((type, ModelExtractionValidator.Normalise(name)), out id) ? id
            : null;

        var links = new List<StagedLink>();
        foreach (var link in data.Links)
        {
            var (from, to) = (Resolve(link.FromType, link.FromName), Resolve(link.ToType, link.ToName));
            if (from is null || to is null)
            {
                notes.Add($"Dropped the link {link.FromName} {link.RelType} {link.ToName}: {(from is null ? link.FromName : link.ToName)} isn't in the text as a {(from is null ? link.FromType : link.ToType)}.");
            }
            else
            {
                links.Add(new StagedLink(link.FromType, link.RelType, from, link.ToType, to));
            }
        }

        // A type that requires links is only kept with them, because staging doesn't add them afterwards.
        var kept = new List<StagedRecord>();
        foreach (var record in rekeyed)
        {
            if (existing.ContainsKey((record.Type, ModelExtractionValidator.Normalise(record.Name))))
            {
                kept.Add(record);
                continue;
            }

            var missing = schema.Type(record.Type)!.RequiredLinks.FirstOrDefault(
                req => !links.Any(l => l.SourceId == record.Id && l.RelType == req.RelType && l.TargetType == req.OtherType));
            if (missing is null)
            {
                kept.Add(record);
            }
            else
            {
                notes.Add($"Dropped {record.Type} '{record.Name}': the text doesn't link it with {missing.RelType} to a {missing.OtherType}.");
            }
        }

        var keptIds = kept.Select(r => r.Id).ToHashSet(StringComparer.Ordinal);
        var droppedIds = rekeyed.Select(r => r.Id).Where(id => !keptIds.Contains(id)).ToHashSet(StringComparer.Ordinal);
        links = links.Where(l => !droppedIds.Contains(l.SourceId) && !droppedIds.Contains(l.TargetId)).Distinct().ToList();

        var added = kept.Where(r => !existing.ContainsKey((r.Type, ModelExtractionValidator.Normalise(r.Name)))).ToList();
        var alreadyThere = kept.Count - added.Count;
        added = added.Select(r => Hooked(types, r)).ToList();
        if (added.Count == 0 && links.Count == 0)
        {
            throw new ExtractionFailedException(alreadyThere > 0
                ? "Everything found in the text is already here."
                : $"Nothing in the text fits this model. {notes.FirstOrDefault()}".Trim());
        }

        var what = string.Join(" and ", new[]
        {
            added.Count > 0 ? $"{added.Count:N0} records" : null,
            links.Count > 0 ? $"{links.Count:N0} links" : null,
        }.Where(s => s is not null)) + " read from text";
        var shown = notes.Take(NotesShown).ToList();
        if (notes.Count > NotesShown)
        {
            shown.Add($"And {notes.Count - NotesShown:N0} more things left out.");
        }

        return await staging.StageAsync(
            new StagingRequest(DataSource.Ai, source, what, ModelChanges.None, alreadyThere, notes.Count, shown), added, links, cancellationToken);
    }

    // A module's hooks set the fields it keeps, so they're staged with the record.
    private StagedRecord Hooked(IReadOnlyList<RecordType> types, StagedRecord record)
    {
        if (!_hooks.Watch(record.Type) || types.FirstOrDefault(t => t.Name == record.Type) is not { } type)
        {
            return record;
        }

        var props = JsonNode.Parse(record.Props) as JsonObject ?? [];
        _hooks.BeforeSave(type, record.Name, props);
        return record with { Props = props.ToJsonString() };
    }

    private static string Key(string name) => RecordRepository.Slug(name);

    // The node id of each (type, normalised name) that's already in the model, and the keys of those types that are taken. A
    // node is the same record when its name matches, whatever its key. One with only the key is a different record.
    private static async Task<(Dictionary<(string Type, string Name), string> Found, HashSet<string> Taken)> ExistingAsync(
        LinkbaseDbContext db, IEnumerable<(string Type, string Key, string Name)> wanted, CancellationToken cancellationToken)
    {
        var found = new Dictionary<(string, string), string>();
        var taken = new HashSet<string>(StringComparer.Ordinal);
        foreach (var group in wanted.GroupBy(w => w.Type))
        {
            var stems = group.Select(w => w.Key).Distinct().ToList();
            var names = group.Select(w => w.Name).Distinct().ToList();
            var nodes = await db.Nodes.AsNoTracking()
                .Where(n => n.Type == group.Key && (names.Contains(n.Name) || stems.Contains(n.Key)))
                .Select(n => new { n.Id, n.Key, n.Name })
                .ToListAsync(cancellationToken);
            taken.UnionWith(nodes.Select(n => n.Id));

            // A key that's taken gets a suffix, such as atlas-2, so the keys like it are needed to find a free one.
            foreach (var stem in nodes.Select(n => n.Key).Where(stems.Contains).Distinct())
            {
                taken.UnionWith(await db.Nodes.AsNoTracking().Where(n => n.Type == group.Key && n.Key.StartsWith(stem + "-")).Select(n => n.Id).ToListAsync(cancellationToken));
            }

            foreach (var w in group)
            {
                var wantedName = ModelExtractionValidator.Normalise(w.Name);
                if (nodes.FirstOrDefault(n => ModelExtractionValidator.Normalise(n.Name) == wantedName) is { } node)
                {
                    found[(w.Type, wantedName)] = node.Id;
                }
            }
        }

        return (found, taken);
    }
}
