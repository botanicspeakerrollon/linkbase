using Linkbase.Core.History;

namespace Linkbase.Core.Records;

public sealed partial class RecordRepository
{
    private readonly RecordMerger merger = new(contextFactory, hooks, new RecordReader(contextFactory, fieldPolicy));

    // Folds one record into another of the same type: the one kept keeps its name and its own values, gains any value it
    // lacked (and any item of a list it lacked), and takes over every link and external id of the one dropped, which is then
    // deleted. A link the kept record already has, or that would join it to itself, is dropped rather than doubled. All of it
    // is saved together, or none of it, and the audit trail says what went where.
    public Task<MergeResult> MergeAsync(string keepId, string dropId, ChangeReason? reason = null, CancellationToken cancellationToken = default) =>
        merger.MergeAsync(keepId, dropId, reason, cancellationToken);
}
