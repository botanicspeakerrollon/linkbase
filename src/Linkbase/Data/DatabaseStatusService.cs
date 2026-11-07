using Microsoft.EntityFrameworkCore;

namespace Linkbase.Data;

public sealed record DatabaseStatus(bool Connected, int AppliedMigrations, int PendingMigrations, string? Error);

public sealed class DatabaseStatusService(
    IDbContextFactory<LinkbaseDbContext> contextFactory,
    ILogger<DatabaseStatusService> logger)
{
    public async Task<DatabaseStatus> GetStatusAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
            var applied = await db.Database.GetAppliedMigrationsAsync(cancellationToken);
            var pending = await db.Database.GetPendingMigrationsAsync(cancellationToken);
            return new DatabaseStatus(true, applied.Count(), pending.Count(), null);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Could not reach the database");
            return new DatabaseStatus(false, 0, 0, ex.Message);
        }
    }
}
