using Linkbase.Core.History;
using Microsoft.EntityFrameworkCore;

namespace Linkbase.Core.Records;

public sealed partial class RecordRepository
{
    // Whether this model keeps an audit trail and asks for a reason with each change, for the forms to show.
    public async Task<HistorySettings> HistorySettingsAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        return await ChangeRecorder.SettingsAsync(db, cancellationToken);
    }
}
