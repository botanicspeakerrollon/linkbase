using System.Text.Json;
using System.Text.Json.Nodes;
using Linkbase.Core.Security;
using Linkbase.Data;
using Microsoft.EntityFrameworkCore;

namespace Linkbase.Core.History;

// One thing that changed, as the timeline shows it. OtherName is the record at the other end of a link, if it still exists.
public sealed record HistoryChange(
    ChangeEntityKind Kind, ChangeOp Op, string EntityId, string? FieldKey, string? RelType, string? RelatedId, string? OtherName,
    string? OldValue, string? NewValue);

// A change set with its reason, as the timeline shows it. Only the changes that touch the record are listed.
public sealed record HistoryEntry(
    long ChangeSetId, DateTimeOffset When, JustificationKind Kind, string? Note, string? SourceId, string? SourceKind, string? SourceComment,
    long? ImportBatchId, long? CauseChangeSetId, IReadOnlyList<HistoryChange> Changes);

// One change near a record, in one line, for "what changed near here since...". RecordName is the record the change is to, or for
// a link the end of it that is nearer.
public sealed record NearbyChange(long ChangeSetId, DateTimeOffset When, JustificationKind Kind, string? Note, string RecordId, string RecordName, string Description);

// Reads the audit trail. See docs/history/audit-history-plan.md.
public sealed class HistoryRepository(IDbContextFactory<LinkbaseDbContext> contextFactory, FieldPolicy? fieldPolicy = null)
{
    public const string MaskText = "••••";

    private async ValueTask<HiddenFields> HiddenAsync(LinkbaseDbContext db, CancellationToken cancellationToken) =>
        fieldPolicy is null ? HiddenFields.None : await fieldPolicy.HiddenAsync(db, cancellationToken);

    // With row security on, a change is shown only if what it touched can still be seen: the record, or the link and both its
    // ends. Anything deleted can't be checked, so it's hidden (docs/history/security-design.md, "History"). Values of hidden fields are
    // masked, including inside the JSON of a create or delete. With security off this returns the changes as they were.
    private static async Task<List<Change>> SecureAsync(LinkbaseDbContext db, HiddenFields hidden, List<Change> changes, CancellationToken cancellationToken)
    {
        if (!hidden.Secured || changes.Count == 0)
        {
            return changes;
        }

        var ids = changes.SelectMany(c => new[] { c.EntityId, c.RelatedId }).OfType<string>().Distinct().ToList();
        var types = await db.Nodes.AsNoTracking().Where(n => ids.Contains(n.Id)).Select(n => new { n.Id, n.Type }).ToDictionaryAsync(n => n.Id, n => n.Type, cancellationToken);

        var linked = changes.Where(c => c.RelType != null && c.RelatedId != null && types.ContainsKey(c.EntityId) && types.ContainsKey(c.RelatedId)).ToList();
        var sources = linked.Select(c => c.EntityId).Distinct().ToList();
        var targets = linked.Select(c => c.RelatedId!).Distinct().ToList();
        var links = linked.Count == 0
            ? []
            : (await db.NodeLinks.AsNoTracking().Where(l => sources.Contains(l.SourceId) && targets.Contains(l.TargetId)).Select(l => new { l.SourceId, l.RelType, l.TargetId }).ToListAsync(cancellationToken))
                .Select(l => (l.SourceId, l.RelType, l.TargetId)).ToHashSet();

        // A property a record's own label hides from this viewer is masked on that record, like one its field hides.
        hidden = await hidden.ForRecordsAsync(db, types.Keys, cancellationToken);
        var kept = new List<Change>();
        foreach (var change in changes)
        {
            if (change.RelType != null)
            {
                if (change.RelatedId == null || !links.Contains((change.EntityId, change.RelType, change.RelatedId)))
                {
                    continue;
                }

                kept.Add(change);
            }
            else if (types.TryGetValue(change.EntityId, out var type))
            {
                kept.Add(change.EntityKind is ChangeEntityKind.Node or ChangeEntityKind.PropertyLabel ? Masked(change, type, hidden) : change);
            }
        }

        return kept;
    }

    private static Change Masked(Change change, string type, HiddenFields hidden)
    {
        var keys = hidden.For(type, change.EntityId);
        if (keys.Count == 0)
        {
            return change;
        }

        if (change.FieldKey is { } key)
        {
            if (keys.Contains(key))
            {
                change.OldValue = change.OldValue is null ? null : MaskText;
                change.NewValue = change.NewValue is null ? null : MaskText;
            }

            return change;
        }

        change.OldValue = Strip(change.OldValue, keys);
        change.NewValue = Strip(change.NewValue, keys);
        return change;
    }

    // A create or delete of a record is JSON like {"name":..,"values":{key:value}}.
    private static string? Strip(string? json, IReadOnlySet<string> keys)
    {
        if (string.IsNullOrEmpty(json))
        {
            return json;
        }

        try
        {
            if (JsonNode.Parse(json) is JsonObject { } root && root["values"] is JsonObject values)
            {
                foreach (var key in keys)
                {
                    values.Remove(key);
                }

                return root.ToJsonString();
            }
        }
        catch (JsonException)
        {
        }

        return MaskText;
    }
    public const int PageSize = 30;

    // How many neighbours "near" takes in, and how many changes come back.
    public const int NearLimit = 500;
    public const int NearbyShown = 200;

    // The history of one link, newest first: when it was made, edited or removed. Links an import added aren't logged one
    // by one, so the import is the first entry.
    public async Task<IReadOnlyList<HistoryEntry>> ForLinkAsync(string sourceId, string relType, string targetId, CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var hidden = await HiddenAsync(db, cancellationToken);
        var touching = db.Changes.AsNoTracking().Where(c =>
            (c.EntityKind == ChangeEntityKind.Link || c.EntityKind == ChangeEntityKind.Label) && c.EntityId == sourceId && c.RelatedId == targetId && c.RelType == relType);
        var entries = await db.ChangeSets.AsNoTracking()
            .Where(s => touching.Any(c => c.ChangeSetId == s.Id))
            .OrderByDescending(s => s.CreatedAt).ThenByDescending(s => s.Id)
            .Take(PageSize)
            .Select(s => new
            {
                s.Id,
                s.CreatedAt,
                s.Justification.Kind,
                s.Justification.Note,
                s.Justification.SourceId,
                s.Justification.SourceKind,
                s.Justification.SourceComment,
                s.Justification.ImportBatchId,
                s.CauseChangeSetId,
            })
            .ToListAsync(cancellationToken);
        var ids = entries.Select(e => e.Id).ToList();
        var changes = await SecureAsync(db, hidden, await touching.Where(c => ids.Contains(c.ChangeSetId)).OrderBy(c => c.Id).ToListAsync(cancellationToken), cancellationToken);

        var result = entries.Where(e => !hidden.Secured || changes.Any(c => c.ChangeSetId == e.Id)).Select(e => new HistoryEntry(
            e.Id, e.CreatedAt, e.Kind, e.Note, e.SourceId, e.SourceKind, e.SourceComment, e.ImportBatchId, e.CauseChangeSetId,
            changes.Where(c => c.ChangeSetId == e.Id)
                .Select(c => new HistoryChange(c.EntityKind, c.Op, c.EntityId, c.FieldKey, c.RelType, c.RelatedId, null, c.OldValue, c.NewValue))
                .ToList())).ToList();

        if (entries.Count < PageSize && !changes.Any(c => c.Op == ChangeOp.Create))
        {
            var added = await db.NodeLinks.AsNoTracking()
                .Where(l => l.SourceId == sourceId && l.RelType == relType && l.TargetId == targetId && l.ImportBatchId != null)
                .Select(l => new { l.ImportBatchId, l.ImportBatch!.CreatedAt, l.ImportBatch.SourceUrl })
                .FirstOrDefaultAsync(cancellationToken);
            if (added is not null)
            {
                result.Add(new HistoryEntry(0, added.CreatedAt, JustificationKind.Import, $"Added by import #{added.ImportBatchId}", added.SourceUrl, null, null, added.ImportBatchId, null, []));
            }
        }

        return result;
    }

    // What changed in the neighbourhood of a record since a time: to the record, to the records it is linked to, and to the
    // links of any of them. Newest first. The neighbourhood is the record and up to NearLimit of its neighbours.
    public async Task<IReadOnlyList<NearbyChange>> NearbyAsync(string nodeId, DateTimeOffset since, CancellationToken cancellationToken = default)
    {
        // Npgsql only accepts offset 0 for timestamptz, and callers pass local-offset times.
        since = since.ToUniversalTime();
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var hidden = await HiddenAsync(db, cancellationToken);
        var near = await db.NodeLinks.AsNoTracking()
            .Where(l => l.SourceId == nodeId || l.TargetId == nodeId)
            .Select(l => l.SourceId == nodeId ? l.TargetId : l.SourceId)
            .Distinct()
            .Take(NearLimit)
            .ToListAsync(cancellationToken);
        near.Add(nodeId);

        var found = await db.Changes.AsNoTracking()
            .Include(c => c.ChangeSet).ThenInclude(s => s.Justification)
            .Where(c => c.ChangeSet.CreatedAt >= since
                && ((c.EntityKind == ChangeEntityKind.Node && near.Contains(c.EntityId))
                    || (c.EntityKind == ChangeEntityKind.Link && (near.Contains(c.EntityId) || near.Contains(c.RelatedId!)))
                    || (c.EntityKind == ChangeEntityKind.ExternalRef && near.Contains(c.EntityId))))
            .OrderByDescending(c => c.Id)
            .Take(NearbyShown)
            .ToListAsync(cancellationToken);
        var changes = (await SecureAsync(db, hidden, found, cancellationToken))
            .Select(c => new
            {
                c.ChangeSetId,
                c.ChangeSet.CreatedAt,
                c.ChangeSet.Justification.Kind,
                c.ChangeSet.Justification.Note,
                c.EntityKind,
                c.EntityId,
                c.Op,
                c.FieldKey,
                c.RelType,
                c.RelatedId,
                c.OldValue,
                c.NewValue,
            })
            .ToList();

        // Records an import added aren't logged one by one, so they come from the records themselves.
        var imported = await db.Nodes.AsNoTracking()
            .Where(n => near.Contains(n.Id) && n.ImportBatchId != null && n.CreatedAt >= since)
            .Select(n => new { n.Id, n.Name, n.CreatedAt, n.ImportBatchId })
            .Take(NearbyShown)
            .ToListAsync(cancellationToken);

        var wanted = changes.SelectMany(c => new[] { c.EntityId, c.RelatedId }).OfType<string>().Distinct().ToList();
        var names = await db.Nodes.AsNoTracking().Where(n => wanted.Contains(n.Id)).Select(n => new { n.Id, n.Name }).ToDictionaryAsync(n => n.Id, n => n.Name, cancellationToken);
        string Name(string id) => names.GetValueOrDefault(id) ?? id;

        var nearSet = near.ToHashSet();
        var lines = changes.Select(c =>
        {
            var record = c.EntityKind == ChangeEntityKind.Link && !nearSet.Contains(c.EntityId) ? c.RelatedId! : c.EntityId;
            var link = c.EntityKind == ChangeEntityKind.Link ? $"{Name(c.EntityId)} {c.RelType} {Name(c.RelatedId!)}" : "";
            var description = c.EntityKind switch
            {
                ChangeEntityKind.Node => c.Op switch
                {
                    ChangeOp.Create => "Added",
                    ChangeOp.Delete => "Deleted",
                    _ => $"{Records.RecordRepository.Title(c.FieldKey ?? "name")}: {Show(c.OldValue)} → {Show(c.NewValue)}",
                },
                ChangeEntityKind.Link => c.Op switch
                {
                    ChangeOp.Create => $"Linked: {link}",
                    ChangeOp.Delete => $"Unlinked: {link}",
                    _ => $"{link}, {Records.RecordRepository.Title(c.FieldKey ?? "")}: {Show(c.OldValue)} → {Show(c.NewValue)}",
                },
                _ => c.Op == ChangeOp.Create ? $"Added the {c.FieldKey} id {c.NewValue}" : $"Removed the {c.FieldKey} id {c.OldValue}",
            };
            return new NearbyChange(c.ChangeSetId, c.CreatedAt, c.Kind, c.Note, record, Name(record), description);
        });

        return lines
            .Concat(imported.Select(n => new NearbyChange(0, n.CreatedAt, JustificationKind.Import, $"Import #{n.ImportBatchId}", n.Id, n.Name, "Added by import")))
            .OrderByDescending(l => l.When).ThenByDescending(l => l.ChangeSetId)
            .Take(NearbyShown)
            .ToList();
    }

    private static string Show(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return "(empty)";
        }

        var line = value.Replace('\n', ',').Trim();
        return line.Length > 80 ? line[..80] + "…" : line;
    }

    // The history of one record, newest first: changes to it, to links on it, and the import that added it. Pass the When of
    // the last entry to get the next page.
    public async Task<IReadOnlyList<HistoryEntry>> ForRecordAsync(string nodeId, DateTimeOffset? before = null, CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var hidden = await HiddenAsync(db, cancellationToken);
        var touching = db.Changes.AsNoTracking().Where(c =>
            ((c.EntityKind == ChangeEntityKind.Node || c.EntityKind == ChangeEntityKind.Label || c.EntityKind == ChangeEntityKind.PropertyLabel) && c.RelType == null && c.EntityId == nodeId)
            || (c.EntityKind == ChangeEntityKind.Label && c.RelType != null && (c.EntityId == nodeId || c.RelatedId == nodeId))
            || (c.EntityKind == ChangeEntityKind.Link && (c.EntityId == nodeId || c.RelatedId == nodeId))
            || (c.EntityKind == ChangeEntityKind.ExternalRef && c.EntityId == nodeId));
        var sets = db.ChangeSets.AsNoTracking().Where(s => touching.Any(c => c.ChangeSetId == s.Id));
        if (before is { } cutoff)
        {
            sets = sets.Where(s => s.CreatedAt < cutoff);
        }

        var entries = await sets
            .OrderByDescending(s => s.CreatedAt).ThenByDescending(s => s.Id)
            .Take(PageSize)
            .Select(s => new
            {
                s.Id,
                s.CreatedAt,
                s.Justification.Kind,
                s.Justification.Note,
                s.Justification.SourceId,
                s.Justification.SourceKind,
                s.Justification.SourceComment,
                s.Justification.ImportBatchId,
                s.CauseChangeSetId,
            })
            .ToListAsync(cancellationToken);
        var ids = entries.Select(e => e.Id).ToList();
        var changes = await SecureAsync(db, hidden, await touching.Where(c => ids.Contains(c.ChangeSetId)).OrderBy(c => c.Id).ToListAsync(cancellationToken), cancellationToken);

        var others = changes.Select(c => c.EntityId == nodeId ? c.RelatedId : c.EntityId).OfType<string>().Distinct().ToList();
        var names = await db.Nodes.AsNoTracking().Where(n => others.Contains(n.Id)).Select(n => new { n.Id, n.Name }).ToDictionaryAsync(n => n.Id, n => n.Name, cancellationToken);

        var result = entries.Where(e => !hidden.Secured || changes.Any(c => c.ChangeSetId == e.Id)).Select(e => new HistoryEntry(
            e.Id, e.CreatedAt, e.Kind, e.Note, e.SourceId, e.SourceKind, e.SourceComment, e.ImportBatchId, e.CauseChangeSetId,
            changes.Where(c => c.ChangeSetId == e.Id).Select(c =>
            {
                var other = c.EntityKind == ChangeEntityKind.Link ? (c.EntityId == nodeId ? c.RelatedId : c.EntityId) : null;
                return new HistoryChange(c.EntityKind, c.Op, c.EntityId, c.FieldKey, c.RelType, c.RelatedId,
                    other is null ? null : names.GetValueOrDefault(other) ?? other, c.OldValue, c.NewValue);
            }).ToList())).ToList();

        // A record an import added has no changes of its own, because creates aren't logged row by row. Its first entry comes
        // from the batch, at the end of the history.
        if (entries.Count < PageSize)
        {
            var added = await db.Nodes.AsNoTracking()
                .Where(n => n.Id == nodeId && n.ImportBatchId != null)
                .Select(n => new { n.ImportBatchId, n.CreatedAt, n.ImportBatch!.SourceUrl })
                .SingleOrDefaultAsync(cancellationToken);
            if (added is not null && !changes.Any(c => c.EntityKind == ChangeEntityKind.Node && c.EntityId == nodeId && c.Op == ChangeOp.Create))
            {
                result.Add(new HistoryEntry(0, added.CreatedAt, JustificationKind.Import, $"Added by import #{added.ImportBatchId}", added.SourceUrl, null, null, added.ImportBatchId, null, []));
            }
        }

        return result;
    }
}
