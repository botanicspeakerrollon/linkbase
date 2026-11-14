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
public sealed class HistoryRepository(IDbContextFactory<LinkbaseDbContext> contextFactory)
{
    public const int PageSize = 30;

    // How many neighbours "near" takes in, and how many changes come back.
    public const int NearLimit = 500;
    public const int NearbyShown = 200;

    // The history of one link, newest first: when it was made, edited or removed. Links an import added aren't logged one
    // by one, so the import is the first entry.
    public async Task<IReadOnlyList<HistoryEntry>> ForLinkAsync(string sourceId, string relType, string targetId, CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
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
        var changes = await touching.Where(c => ids.Contains(c.ChangeSetId)).OrderBy(c => c.Id).ToListAsync(cancellationToken);

        var result = entries.Select(e => new HistoryEntry(
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
        var near = await db.NodeLinks.AsNoTracking()
            .Where(l => l.SourceId == nodeId || l.TargetId == nodeId)
            .Select(l => l.SourceId == nodeId ? l.TargetId : l.SourceId)
            .Distinct()
            .Take(NearLimit)
            .ToListAsync(cancellationToken);
        near.Add(nodeId);

        var changes = await db.Changes.AsNoTracking()
            .Where(c => c.ChangeSet.CreatedAt >= since
                && ((c.EntityKind == ChangeEntityKind.Node && near.Contains(c.EntityId))
                    || (c.EntityKind == ChangeEntityKind.Link && (near.Contains(c.EntityId) || near.Contains(c.RelatedId!)))
                    || (c.EntityKind == ChangeEntityKind.ExternalRef && near.Contains(c.EntityId))))
            .OrderByDescending(c => c.Id)
            .Take(NearbyShown)
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
            .ToListAsync(cancellationToken);

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
        var touching = db.Changes.AsNoTracking().Where(c =>
            ((c.EntityKind == ChangeEntityKind.Node || c.EntityKind == ChangeEntityKind.Label) && c.RelType == null && c.EntityId == nodeId)
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
        var changes = await touching.Where(c => ids.Contains(c.ChangeSetId)).OrderBy(c => c.Id).ToListAsync(cancellationToken);

        var others = changes.Select(c => c.EntityId == nodeId ? c.RelatedId : c.EntityId).OfType<string>().Distinct().ToList();
        var names = await db.Nodes.AsNoTracking().Where(n => others.Contains(n.Id)).Select(n => new { n.Id, n.Name }).ToDictionaryAsync(n => n.Id, n => n.Name, cancellationToken);

        var result = entries.Select(e => new HistoryEntry(
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
