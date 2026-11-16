using System.Text.Json.Nodes;
using Linkbase.Core.Models;
using Linkbase.Core.Records;
using Linkbase.Core.Staging;
using Linkbase.Data;
using Microsoft.EntityFrameworkCore;

namespace Linkbase.Core.Extraction;

// Reads text for any model: fetches a page or takes pasted text, has the extractor read it part by part against the model's
// own types, checks the answer against the text, and stages what's new for review and commit by ImportStagingService. Unless
// the caller asks to update, records already there are left as they are, so undoing only ever removes what the import added.
public sealed class ModelExtractionService(
    IRecordRepository records,
    IModelExtractor extractor,
    PageTextFetcher fetcher,
    IDbContextFactory<LinkbaseDbContext> contextFactory,
    ImportStagingService staging,
    RecordHooks? hooks = null,
    IConfiguration? configuration = null)
{
    private readonly bool _keepWording = configuration?.GetValue("Extraction:KeepWording", false) ?? false;
    private const int NotesShown = 20;
    private readonly RecordHooks _hooks = hooks ?? RecordHooks.None;

    // With updateExisting, a record in the text that matches one already here by its exact name has the text's values laid over
    // it, where they differ, instead of being left as it is. Links and records matched only by a similar name are never updated.
    public async Task<long> ExtractFromUrlAsync(string url, IProgress<ExtractionProgress>? progress, CancellationToken cancellationToken, bool updateExisting = false)
    {
        await CheckCanUpdateAsync(updateExisting, cancellationToken);
        progress?.Report(new ExtractionProgress("Fetching the page", 0, 1));
        var page = await fetcher.FetchAsync(url, cancellationToken);
        return await ExtractAsync(page.Text, url.Trim(), progress, updateExisting, cancellationToken);
    }

    public async Task<long> ExtractFromTextAsync(string text, string? sourceUrl, IProgress<ExtractionProgress>? progress, CancellationToken cancellationToken, bool updateExisting = false)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            throw new ExtractionFailedException("There's no text to import.");
        }

        await CheckCanUpdateAsync(updateExisting, cancellationToken);
        return await ExtractAsync(text, string.IsNullOrWhiteSpace(sourceUrl) ? null : sourceUrl.Trim(), progress, updateExisting, cancellationToken);
    }

    // Checked before the text is read, so a model that can't take updates doesn't cost a call to the AI first.
    private async Task CheckCanUpdateAsync(bool updateExisting, CancellationToken cancellationToken)
    {
        if (!updateExisting)
        {
            return;
        }

        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        if (!(await Linkbase.Core.History.ChangeRecorder.SettingsAsync(db, cancellationToken)).Record)
        {
            throw new ExtractionFailedException("Updating records needs the model to keep a history, so the changes can be put back. This model doesn't, so untick Update records that are already here.");
        }
    }

    private async Task<long> ExtractAsync(string text, string? source, IProgress<ExtractionProgress>? progress, bool updateExisting, CancellationToken cancellationToken)
    {
        var types = await records.TypesAsync(cancellationToken);
        var relationships = await records.RelationshipsAsync(cancellationToken);
        var linkAttributes = new Dictionary<(string, string, string), IReadOnlyList<RecordField>>();
        foreach (var relationship in relationships)
        {
            linkAttributes[(relationship.FromType, relationship.RelType, relationship.ToType)] =
                await records.LinkAttributesAsync(relationship.FromType, relationship.RelType, relationship.ToType, cancellationToken);
        }

        var schema = ExtractionSchema.From(types, relationships, _hooks, linkAttributes) with { KeepWording = _keepWording };

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
            schema, new ExtractedData(found.SelectMany(d => d.Records).ToList(), found.SelectMany(d => d.Links).ToList()), text, _hooks);
        var batch = await StageAsync(schema, types, checkedData, source ?? "Pasted text", updateExisting, cancellationToken);
        progress?.Report(new ExtractionProgress("Done", chunks.Count, chunks.Count));
        return batch;
    }

    private async Task<long> StageAsync(
        ExtractionSchema schema, IReadOnlyList<RecordType> types, ModelExtractionValidator.Result data, string source, bool updateExisting, CancellationToken cancellationToken)
    {
        var notes = data.Warnings.ToList();
        var dropped = notes.Count;

        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);

        // Records already in the model, matched by name, are left as they are (unless updating) and can still be linked to. A
        // similar name matches when there is only one, and a record that must be linked, such as a route, only matches one linked
        // the same way.
        var (existing, taken) = await ExistingAsync(
            db, data.Records.Select(r => (r.Type, r.Key, r.Name)).Concat(data.Links.SelectMany(l => new[] { (l.FromType, Key(l.FromName), l.FromName), (l.ToType, Key(l.ToName), l.ToName) })),
            cancellationToken);
        existing = await ExistingRecordMatcher.MatchAsync(db, schema, data, existing, notes, cancellationToken);
        var updates = updateExisting ? await PlanUpdatesAsync(db, types, data, existing, notes, cancellationToken) : [];

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
                links.Add(new StagedLink(link.FromType, link.RelType, from, link.ToType, to, link.Props));
            }
        }

        // A type that requires links is only kept with them, because staging doesn't add them afterwards.
        // Dropping a record also drops the links to it, which can leave a record that depended on it without its own, so
        // this repeats until nothing more goes. Otherwise a route would stay, unlinked, after its crag was dropped.
        var kept = rekeyed.ToList();
        for (var changed = true; changed;)
        {
            changed = false;
            foreach (var record in kept.ToList())
            {
                if (existing.ContainsKey((record.Type, ModelExtractionValidator.Normalise(record.Name))))
                {
                    continue;
                }

                var missing = schema.Type(record.Type)!.RequiredLinks.FirstOrDefault(
                    req => !links.Any(l => l.SourceId == record.Id && l.RelType == req.RelType && l.TargetType == req.OtherType));
                if (missing is null)
                {
                    continue;
                }

                notes.Add($"Dropped {record.Type} '{record.Name}': the text doesn't link it with {missing.RelType} to a {missing.OtherType}.");
                kept.Remove(record);
                links = links.Where(l => l.SourceId != record.Id && l.TargetId != record.Id).ToList();
                changed = true;
            }
        }

        links = links.Distinct().ToList();

        // A link between records that are already linked that way is left out, so the review shows only what would be new.
        var linkEnds = links.SelectMany(l => new[] { l.SourceId, l.TargetId }).Distinct().ToList();
        var linkedAlready = (await db.NodeLinks.AsNoTracking()
            .Where(l => linkEnds.Contains(l.SourceId) && linkEnds.Contains(l.TargetId))
            .Select(l => new { l.SourceId, l.RelType, l.TargetId })
            .ToListAsync(cancellationToken))
            .Select(l => (l.SourceId, l.RelType, l.TargetId))
            .ToHashSet();
        var linksThere = links.Count(l => linkedAlready.Contains((l.SourceId, l.RelType, l.TargetId)));
        links = links.Where(l => !linkedAlready.Contains((l.SourceId, l.RelType, l.TargetId))).ToList();

        var added = kept.Where(r => !existing.ContainsKey((r.Type, ModelExtractionValidator.Normalise(r.Name)))).ToList();
        var alreadyThere = kept.Count - added.Count - updates.Count;
        added = added.Select(r => Hooked(types, r)).ToList();
        if (added.Count == 0 && links.Count == 0 && updates.Count == 0)
        {
            throw new ExtractionFailedException(alreadyThere + linksThere > 0
                ? "Everything found in the text is already here."
                : $"Nothing in the text fits this model. {notes.FirstOrDefault()}".Trim());
        }

        var what = string.Join(" and ", new[]
        {
            added.Count > 0 ? $"{added.Count:N0} records" : null,
            links.Count > 0 ? $"{links.Count:N0} links" : null,
            updates.Count > 0 ? $"{updates.Count:N0} updates" : null,
        }.Where(s => s is not null)) + " read from text";
        var shown = notes.Take(NotesShown).ToList();
        if (notes.Count > NotesShown)
        {
            shown.Add($"And {notes.Count - NotesShown:N0} more things left out.");
        }

        // Where each record was read from, when that is a web page.
        var refs = !Uri.TryCreate(source, UriKind.Absolute, out var address) || address.Scheme is not ("http" or "https")
            ? []
            : kept.Select(r => new StagedRef(Resolve(r.Type, r.Name) ?? r.Id, r.Type, ExternalId(address, r), source)).ToList();

        return await staging.StageAsync(
            new StagingRequest(DataSource.Ai, source, what, ModelChanges.None, alreadyThere, notes.Count, shown)
            {
                Refs = refs,
                UpdateExisting = updateExisting,
                Updating = updates.Count,
                UpdateLinks = false,
            },
            added.Concat(updates), links, cancellationToken);
    }

    // The records in the text that are the same as one already here, by an exact name, and whose values differ. Each is staged
    // under the record's own key and name with only the values that change, so the review shows just those and a commit leaves
    // the rest alone. A name that only looks similar is not updated, because a wrong match would overwrite another record. A type
    // a module watches is updated too: its hooks set the fields it keeps (a route's grade_rank) on the new values, those are
    // staged with the rest, and a commit tells the module afterwards, as for any other change.
    private async Task<List<StagedRecord>> PlanUpdatesAsync(
        LinkbaseDbContext db, IReadOnlyList<RecordType> types,
        ModelExtractionValidator.Result data,
        Dictionary<(string Type, string Name), string> existing,
        List<string> notes,
        CancellationToken cancellationToken)
    {
        var matched = data.Records
            .Select(r => (Record: r, Id: existing.GetValueOrDefault((r.Type, ModelExtractionValidator.Normalise(r.Name)))))
            .Where(m => m.Id is not null)
            .ToList();
        var ids = matched.Select(m => m.Id!).Distinct().ToList();
        var nodes = ids.Count == 0
            ? []
            : (await db.Nodes.AsNoTracking().Where(n => ids.Contains(n.Id)).Select(n => new { n.Id, n.Key, n.Name, n.Props }).ToListAsync(cancellationToken))
                .ToDictionary(n => n.Id);

        var updates = new List<StagedRecord>();
        var done = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (record, id) in matched)
        {
            if (!nodes.TryGetValue(id!, out var node) || !done.Add(node.Id))
            {
                continue;
            }

            if (ModelExtractionValidator.Normalise(node.Name) != ModelExtractionValidator.Normalise(record.Name))
            {
                notes.Add($"{record.Type} '{record.Name}' is like '{node.Name}' already here, but only an exact name is updated, so it was left as it is.");
                continue;
            }

            var now = RecordRepository.Values(node.Props);
            var changed = RecordRepository.Values(record.Props).Where(v => now.GetValueOrDefault(v.Key) != v.Value).Select(v => v.Key).ToHashSet();
            if (changed.Count == 0)
            {
                continue;
            }

            var props = JsonNode.Parse(record.Props) as JsonObject ?? [];

            // Notes are added to, not replaced, so what is already written stays. Text already there is not added again.
            if (changed.Contains("notes") && now.GetValueOrDefault("notes") is { Length: > 0 } written && (string?)props["notes"] is { } extra)
            {
                if (written.Contains(extra.Trim(), StringComparison.OrdinalIgnoreCase))
                {
                    changed.Remove("notes");
                    props.Remove("notes");
                }
                else
                {
                    props["notes"] = $"{written}\n\n{extra.Trim()}";
                }

                if (changed.Count == 0)
                {
                    continue;
                }
            }

            // What the module keeps is worked out from the record as it will be, then staged where it differs from now.
            if (_hooks.Watch(record.Type) && types.FirstOrDefault(t => t.Name == record.Type) is { } type)
            {
                var merged = JsonNode.Parse(node.Props) as JsonObject ?? [];
                foreach (var (key, value) in props.ToList())
                {
                    merged[key] = value?.DeepClone();
                }

                _hooks.BeforeSave(type, node.Name, merged);
                var after = RecordRepository.Values(merged.ToJsonString());
                foreach (var key in after.Keys.Where(k => !props.ContainsKey(k) && now.GetValueOrDefault(k) != after[k]).ToList())
                {
                    props[key] = merged[key]?.DeepClone();
                    changed.Add(key);
                }
            }

            foreach (var key in props.Select(p => p.Key).Where(k => !changed.Contains(k)).ToList())
            {
                props.Remove(key);
            }

            updates.Add(new StagedRecord(record.Type, node.Key, node.Name, props.ToJsonString()));
        }

        return updates;
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

    // A page's id for a record: the page, without its fragment, and what the record is called there.
    private static string ExternalId(Uri page, StagedRecord record) =>
        $"{page.GetLeftPart(UriPartial.Path).ToLowerInvariant().TrimEnd('/')}{page.Query}|{record.Type}|{ModelExtractionValidator.Normalise(record.Name)}";

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
