using Linkbase.Core.Models;
using Linkbase.Data;
using Microsoft.EntityFrameworkCore;

namespace Linkbase.Core.Hooks;

// Hands committed change events to hooks. Each hook has its own position in each model's events (hook_position), and moves it on
// only after the hook has taken a batch, so a hook that throws is given the same events again and one that is slow or broken
// never holds up another. A hook added later starts at the newest event.
//
// The batch is read, handed over and the position moved in one transaction that holds an advisory lock for the hook, so with
// several instances running only one serves a hook at a time, and if it stops, its connection and the lock go with it. The
// cost is that the hook is called while the transaction is open, so a slow hook holds a connection. See
// docs/history/change-hooks-plan.md.
public sealed class ChangeDispatcher(
    IReadOnlyList<ConfiguredHook> hooks, Func<IReadOnlyList<string>> modelKeys, Func<string, LinkbaseDbContext> open, ILogger logger)
{
    // How many events were handed to hooks.
    public async Task<int> DispatchOnceAsync(CancellationToken cancellationToken)
    {
        var handed = 0;
        foreach (var hook in hooks)
        {
            foreach (var model in modelKeys().Where(m => hook.Models.Count == 0 || hook.Models.Contains(m)))
            {
                try
                {
                    handed += await DispatchAsync(hook, model, cancellationToken);
                }
                catch (Exception e) when (e is not OperationCanceledException)
                {
                    // Nothing was moved on, so the next pass gives the hook the same events. It never skips one on its own.
                    logger.LogWarning(e, "Change hook {Hook} failed on model {Model}; it will be given the same events again.", hook.Key, model);
                }
            }
        }

        return handed;
    }

    private async Task<int> DispatchAsync(ConfiguredHook hook, string model, CancellationToken cancellationToken)
    {
        await using var db = open(model);

        // Kept apart from the transaction below, so a hook that fails on its first events doesn't start again from newer ones.
        await db.Database.ExecuteSqlAsync(
            $"INSERT INTO hook_position (hook_key, last_event_id) SELECT {hook.Key}::text, coalesce(max(id), 0) FROM change_event ON CONFLICT (hook_key) DO NOTHING",
            cancellationToken);

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var locked = await db.Database.SqlQuery<bool>($"SELECT pg_try_advisory_xact_lock(hashtext({hook.Key})) AS \"Value\"").SingleAsync(cancellationToken);
        if (!locked)
        {
            return 0;
        }

        var last = await db.HookPositions.AsNoTracking().Where(p => p.HookKey == hook.Key).Select(p => p.LastEventId).SingleAsync(cancellationToken);
        var notices = await db.ChangeEvents.AsNoTracking()
            .Where(e => e.Id > last)
            .OrderBy(e => e.Id)
            .Take(hook.BatchSize)
            .Select(e => new ChangeNotice(model, e.Id, e.ChangeSetId, e.Kind, e.ImportBatchId, e.CauseChangeSetId, e.Entities, e.EntityCount, e.CreatedAt))
            .ToListAsync(cancellationToken);
        if (notices.Count == 0)
        {
            return 0;
        }

        await hook.Hook.HandleAsync(notices, cancellationToken);

        var newest = notices[^1].Id;
        await db.HookPositions.Where(p => p.HookKey == hook.Key)
            .ExecuteUpdateAsync(s => s.SetProperty(p => p.LastEventId, newest).SetProperty(p => p.UpdatedAt, DateTimeOffset.UtcNow), cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return notices.Count;
    }
}

// Runs the dispatcher in the background of the app, looking for new events every few seconds. Every instance runs one, and the
// lock in the dispatcher means they take turns.
public sealed class ChangeDispatcherService(IReadOnlyList<ConfiguredHook> hooks, ModelCatalog catalog, ILogger<ChangeDispatcherService> logger) : BackgroundService
{
    public static readonly TimeSpan Interval = TimeSpan.FromSeconds(5);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // A hook naming a model that doesn't exist is a mistake in configuration, so stop with the reason.
        foreach (var hook in hooks)
        {
            if (hook.Models.FirstOrDefault(m => catalog.Find(m) is null) is { } unknown)
            {
                throw new InvalidOperationException($"Hooks:{hook.Key}:Models names '{unknown}', which isn't one of: {string.Join(", ", catalog.Models.Select(m => m.Key))}.");
            }
        }

        var dispatcher = new ChangeDispatcher(
            hooks, () => catalog.Models.Select(m => m.Key).ToList(),
            key => new LinkbaseDbContext(new DbContextOptionsBuilder<LinkbaseDbContext>().UseNpgsql(catalog.ConnectionString(catalog.Find(key)!)).Options),
            logger);
        using var timer = new PeriodicTimer(Interval);
        do
        {
            await dispatcher.DispatchOnceAsync(stoppingToken);
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
