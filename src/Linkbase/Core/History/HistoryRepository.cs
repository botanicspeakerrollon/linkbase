using Linkbase.Data;
using Microsoft.EntityFrameworkCore;

namespace Linkbase.Core.History;

// One thing that changed, as the timeline shows it. OtherName is the record at the other end of a link, if it still exists.
public sealed record HistoryChange(
    ChangeEntityKind Kind, ChangeOp Op, string EntityId, string? FieldKey, string? RelType, string? RelatedId, string? OtherName,
    string? OldValue, string? NewValue);

// A change set with its reason, as the timeline shows it. Only the changes that touch the record are listed.
public sealed record HistoryEntry(
    long ChangeSetId, DateTimeOffset When, JustificationKind Kind, string? Note, string? SourceUrl, long? ImportBatchId, long? CauseChangeSetId,
    IReadOnlyList<HistoryChange> Changes);

// Reads the audit trail. See docs/audit-history-plan.md.
public sealed class HistoryRepository(IDbContextFactory<LinkbaseDbContext> contextFactory)
{
    public const int PageSize = 30;

    // The history of one record, newest first: changes to it, to links on it, and the import that added it. Pass the When of
    // the last entry to get the next page.
    public async Task<IReadOnlyList<HistoryEntry>> ForRecordAsync(string nodeId, DateTimeOffset? before = null, CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var touching = db.Changes.AsNoTracking().Where(c =>
            (c.EntityKind == ChangeEntityKind.Node && c.EntityId == nodeId)
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
                s.Id, s.CreatedAt, s.Justification.Kind, s.Justification.Note, s.Justification.SourceUrl, s.Justification.ImportBatchId, s.CauseChangeSetId,
            })
            .ToListAsync(cancellationToken);
        var ids = entries.Select(e => e.Id).ToList();
        var changes = await touching.Where(c => ids.Contains(c.ChangeSetId)).OrderBy(c => c.Id).ToListAsync(cancellationToken);

        var others = changes.Select(c => c.EntityId == nodeId ? c.RelatedId : c.EntityId).OfType<string>().Distinct().ToList();
        var names = await db.Nodes.AsNoTracking().Where(n => others.Contains(n.Id)).Select(n => new { n.Id, n.Name }).ToDictionaryAsync(n => n.Id, n => n.Name, cancellationToken);

        var result = entries.Select(e => new HistoryEntry(
            e.Id, e.CreatedAt, e.Kind, e.Note, e.SourceUrl, e.ImportBatchId, e.CauseChangeSetId,
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
                result.Add(new HistoryEntry(0, added.CreatedAt, JustificationKind.Import, $"Added by import #{added.ImportBatchId}", added.SourceUrl, added.ImportBatchId, null, []));
            }
        }

        return result;
    }
}
