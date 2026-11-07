using Linkbase.Data;
using Microsoft.EntityFrameworkCore;

namespace Linkbase.Domains.Climbing.Data;

public sealed record ReviewEntry(
    long Id,
    ExternalEntityType EntityType,
    long EntityId,
    string EntityName,
    int EntityChildren,
    long CandidateId,
    string CandidateName,
    int CandidateChildren,
    string Place,
    double Similarity,
    string Reason);

public sealed class ReviewRepository(IDbContextFactory<LinkbaseDbContext> contextFactory)
{
    public async Task<IReadOnlyList<ReviewEntry>> OpenItemsAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var items = await db.ReviewItems.AsNoTracking()
            .Where(i => i.Status == ReviewStatus.Open)
            .OrderByDescending(i => i.Similarity)
            .Select(i => new { i.Id, i.EntityType, i.EntityId, i.CandidateId, i.Similarity, i.Reason })
            .ToListAsync(cancellationToken);

        var routeIds = items.Where(i => i.EntityType == ExternalEntityType.Route).SelectMany(i => new[] { i.EntityId, i.CandidateId }).ToList();
        var routes = await db.Routes.AsNoTracking()
            .Where(r => routeIds.Contains(r.Id))
            .Select(r => new { r.Id, r.Name, Children = r.Ascents.Count, Place = r.Crag.Name })
            .ToDictionaryAsync(r => r.Id, cancellationToken);

        var cragIds = items.Where(i => i.EntityType == ExternalEntityType.Crag).SelectMany(i => new[] { i.EntityId, i.CandidateId }).ToList();
        var crags = await db.Crags.AsNoTracking()
            .Where(c => cragIds.Contains(c.Id))
            .Select(c => new { c.Id, c.Name, Children = c.Routes.Count, Place = c.Area.Name })
            .ToDictionaryAsync(c => c.Id, cancellationToken);

        var entries = new List<ReviewEntry>();
        foreach (var i in items)
        {
            if (i.EntityType == ExternalEntityType.Route && routes.TryGetValue(i.EntityId, out var route) && routes.TryGetValue(i.CandidateId, out var routeCandidate))
            {
                entries.Add(new ReviewEntry(i.Id, i.EntityType, i.EntityId, route.Name, route.Children, i.CandidateId, routeCandidate.Name, routeCandidate.Children, route.Place, i.Similarity, i.Reason));
            }
            else if (i.EntityType == ExternalEntityType.Crag && crags.TryGetValue(i.EntityId, out var crag) && crags.TryGetValue(i.CandidateId, out var cragCandidate))
            {
                entries.Add(new ReviewEntry(i.Id, i.EntityType, i.EntityId, crag.Name, crag.Children, i.CandidateId, cragCandidate.Name, cragCandidate.Children, crag.Place, i.Similarity, i.Reason));
            }
            else if (i.EntityType == ExternalEntityType.Ascent)
            {
                entries.Add(new ReviewEntry(i.Id, i.EntityType, i.EntityId, i.Reason, 0, i.CandidateId, "", 0, "", i.Similarity, i.Reason));
            }
        }

        return entries;
    }

    public async Task MarkDifferentAsync(long itemId, CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        await db.ReviewItems.Where(i => i.Id == itemId)
            .ExecuteUpdateAsync(
                s => s.SetProperty(i => i.Status, ReviewStatus.Resolved).SetProperty(i => i.UpdatedAt, DateTimeOffset.UtcNow),
                cancellationToken);
    }

    // Moves everything from one row to the other, then deletes it. Keeps the kept row's own fields.
    public async Task MergeAsync(long itemId, long keepId, CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var item = await db.ReviewItems.AsNoTracking().SingleOrDefaultAsync(i => i.Id == itemId, cancellationToken)
            ?? throw new LinkbaseValidationException("This review item no longer exists.");
        if (keepId != item.EntityId && keepId != item.CandidateId)
        {
            throw new LinkbaseValidationException("Choose one of the two rows to keep.");
        }

        var removeId = keepId == item.EntityId ? item.CandidateId : item.EntityId;
        var now = DateTimeOffset.UtcNow;

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        switch (item.EntityType)
        {
            case ExternalEntityType.Route:
                await db.Ascents.Where(a => a.RouteId == removeId)
                    .ExecuteUpdateAsync(s => s.SetProperty(a => a.RouteId, keepId).SetProperty(a => a.UpdatedAt, now), cancellationToken);
                await MoveRefsAsync(db, ExternalEntityType.Route, removeId, keepId, now, cancellationToken);
                await EntityLinks.RemoveAsync(db, ExternalEntityType.Route, removeId, cancellationToken);
                await db.Routes.Where(r => r.Id == removeId).ExecuteDeleteAsync(cancellationToken);
                break;
            case ExternalEntityType.Crag:
                await db.Routes.Where(r => r.CragId == removeId)
                    .ExecuteUpdateAsync(s => s.SetProperty(r => r.CragId, keepId).SetProperty(r => r.UpdatedAt, now), cancellationToken);
                await MoveRefsAsync(db, ExternalEntityType.Crag, removeId, keepId, now, cancellationToken);
                await EntityLinks.RemoveAsync(db, ExternalEntityType.Crag, removeId, cancellationToken);
                await db.Crags.Where(c => c.Id == removeId).ExecuteDeleteAsync(cancellationToken);
                break;
            default:
                throw new LinkbaseValidationException("Only crags and routes can be merged.");
        }

        await transaction.CommitAsync(cancellationToken);
    }

    // The next import still recognises the merged row by its old external id.
    private static Task MoveRefsAsync(
        LinkbaseDbContext db,
        ExternalEntityType type,
        long fromId,
        long toId,
        DateTimeOffset now,
        CancellationToken cancellationToken) =>
        db.ExternalRefs.Where(r => r.EntityType == type && r.EntityId == fromId)
            .ExecuteUpdateAsync(s => s.SetProperty(r => r.EntityId, toId).SetProperty(r => r.UpdatedAt, now), cancellationToken);
}
