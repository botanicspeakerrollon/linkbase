using Linkbase.Data;
using Microsoft.EntityFrameworkCore;

namespace Linkbase.Core.Home;

// A record someone added or edited lately.
public sealed record RecentRecord(string Id, string Type, string Key, string Name, DateTimeOffset At, bool Added);

public sealed record RecentImport(long Id, DataSource Source, string? SourceUrl, ImportStatus Status, DateTimeOffset At);

// One saved change as the home page summarises it: what kind, why, and how many things it touched.
public sealed record RecentChange(long ChangeSetId, DateTimeOffset At, JustificationKind Kind, string? Note, int Changes);

// How many records were created in a month, which is the first day of it.
public sealed record MonthCount(DateOnly Month, int Count);

// What the home page shows beyond the counts. Recent records and changes come from the audit trail, which is indexed by
// id, rather than from sorting every record, so they stay quick on a model with millions. Growth does read every record,
// so the page asks for it after it has drawn.
public sealed class HomeRepository(IDbContextFactory<LinkbaseDbContext> contextFactory)
{
    // Panels a model's configuration can hide with Models:<key>:Home:Hide.
    public const string RecentRecords = "records";
    public const string Imports = "imports";
    public const string Changes = "changes";
    public const string Growth = "growth";
    public static readonly IReadOnlyList<string> Panels = [RecentRecords, Imports, Changes, Growth];

    // Records changed by hand, newest first. A record shows once, at its latest change. Records an import added aren't logged
    // one by one, so they're in the imports panel instead.
    public async Task<IReadOnlyList<RecentRecord>> RecentRecordsAsync(int take, CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var latest = await db.Changes.AsNoTracking()
            .Where(c => c.EntityKind == ChangeEntityKind.Node)
            .OrderByDescending(c => c.Id)
            .Take(take * 8)
            .Select(c => new { c.EntityId, c.Op, c.ChangeSet.CreatedAt })
            .ToListAsync(cancellationToken);

        var ids = latest.Select(c => c.EntityId).Distinct().ToList();
        var nodes = await db.Nodes.AsNoTracking()
            .Where(n => ids.Contains(n.Id) && n.UserId == AppUser.DefaultId)
            .Select(n => new { n.Id, n.Type, n.Key, n.Name })
            .ToDictionaryAsync(n => n.Id, cancellationToken);

        // A record created and then edited still counts as added.
        var created = latest.Where(c => c.Op == ChangeOp.Create).Select(c => c.EntityId).ToHashSet();
        return latest
            .Where(c => nodes.ContainsKey(c.EntityId))
            .DistinctBy(c => c.EntityId)
            .Take(take)
            .Select(c => new RecentRecord(c.EntityId, nodes[c.EntityId].Type, nodes[c.EntityId].Key, nodes[c.EntityId].Name, c.CreatedAt, created.Contains(c.EntityId)))
            .ToList();
    }

    public async Task<IReadOnlyList<RecentImport>> RecentImportsAsync(int take, CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        return await db.ImportBatches.AsNoTracking()
            .Where(b => b.UserId == AppUser.DefaultId)
            .OrderByDescending(b => b.CreatedAt).ThenByDescending(b => b.Id)
            .Take(take)
            .Select(b => new RecentImport(b.Id, b.Source, b.SourceUrl, b.Status, b.CreatedAt))
            .ToListAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<RecentChange>> RecentChangesAsync(int take, CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        return await db.ChangeSets.AsNoTracking()
            .OrderByDescending(s => s.Id)
            .Take(take)
            .Select(s => new RecentChange(s.Id, s.CreatedAt, s.Justification.Kind, s.Justification.Note, db.Changes.Count(c => c.ChangeSetId == s.Id)))
            .ToListAsync(cancellationToken);
    }

    // Records created in each of the last months, oldest first, with the months nothing was created in counted as 0.
    public async Task<IReadOnlyList<MonthCount>> GrowthAsync(int months, DateTimeOffset now, CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var utc = now.UtcDateTime;
        var first = new DateTime(utc.Year, utc.Month, 1, 0, 0, 0, DateTimeKind.Utc).AddMonths(1 - months);
        var from = new DateTimeOffset(first, TimeSpan.Zero);
        var user = AppUser.DefaultId;
        var rows = await db.Database.SqlQuery<MonthRow>(
            $"""
            SELECT date_trunc('month', created_at AT TIME ZONE 'UTC') AS "Month", count(*)::int AS "Count"
            FROM node
            WHERE user_id = {user} AND created_at >= {from}
            GROUP BY 1
            """).ToListAsync(cancellationToken);
        var counts = rows.ToDictionary(r => (r.Month.Year, r.Month.Month), r => r.Count);

        return Enumerable.Range(0, months)
            .Select(i => first.AddMonths(i))
            .Select(m => new MonthCount(DateOnly.FromDateTime(m), counts.GetValueOrDefault((m.Year, m.Month))))
            .ToList();
    }

    private sealed record MonthRow(DateTime Month, int Count);
}
