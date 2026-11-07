using Linkbase.Data;
using Linkbase.Domains.Climbing.Import;
using Microsoft.EntityFrameworkCore;

namespace Linkbase.Domains.Climbing.Data;

public sealed record TripRebuildSummary(int Trips, int LinkedAscents, int InferredLinks);

public sealed class TripService(IDbContextFactory<LinkbaseDbContext> contextFactory)
{
    // Trips are derived from ascents, so this can run as often as needed. Keys keep trip ids stable.
    public async Task<TripRebuildSummary> RebuildAsync(IReadOnlyList<TripMerge> merges, CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

        var plan = TripBuilder.Build(await LoadTripAscentsAsync(db, cancellationToken), merges);
        var existing = await db.Trips.ToDictionaryAsync(t => t.Key, cancellationToken);

        // Trip links are derived, so these updates deliberately leave ascent.updated_at alone:
        // rebuilding trips isn't an edit to the ascent.
        await db.Ascents.Where(a => a.TripId != null)
            .ExecuteUpdateAsync(s => s.SetProperty(a => a.TripId, (long?)null).SetProperty(a => a.TripInferred, false), cancellationToken);

        foreach (var stale in existing.Values.Where(t => plan.All(p => p.Key != t.Key)))
        {
            db.Trips.Remove(stale);
        }

        var saved = new List<(PlannedTrip Plan, Trip Trip)>();
        foreach (var planned in plan)
        {
            if (!existing.TryGetValue(planned.Key, out var trip))
            {
                trip = new Trip { UserId = AppUser.DefaultId, Key = planned.Key, Name = planned.Name };
                db.Trips.Add(trip);
            }

            trip.Name = planned.Name;
            trip.StartDate = planned.StartDate;
            trip.EndDate = planned.EndDate;
            trip.SortDate = planned.SortDate;
            trip.Notes = planned.Notes;
            trip.Abroad = planned.Abroad;
            trip.Home = planned.Home;
            trip.Approximate = planned.Approximate;
            trip.DatesPartlyUnknown = planned.DatesPartlyUnknown;
            trip.Manual = planned.Manual;
            saved.Add((planned, trip));
        }

        await db.SaveChangesAsync(cancellationToken);

        foreach (var (planned, trip) in saved)
        {
            var tripId = trip.Id;
            await db.Ascents.Where(a => planned.AscentIds.Contains(a.Id))
                .ExecuteUpdateAsync(s => s.SetProperty(a => a.TripId, tripId), cancellationToken);
            await db.Ascents.Where(a => planned.InferredAscentIds.Contains(a.Id))
                .ExecuteUpdateAsync(s => s.SetProperty(a => a.TripInferred, true), cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
        return new TripRebuildSummary(plan.Count, plan.Sum(p => p.AscentIds.Count), plan.Sum(p => p.InferredAscentIds.Count));
    }

    private static async Task<List<TripAscent>> LoadTripAscentsAsync(LinkbaseDbContext db, CancellationToken cancellationToken)
    {
        var areas = await db.Areas.AsNoTracking()
            .Select(a => new { a.Id, a.ParentId, a.Name })
            .ToDictionaryAsync(a => a.Id, cancellationToken);

        string CountryOf(long areaId)
        {
            var area = areas[areaId];
            while (area.ParentId is { } parentId)
            {
                area = areas[parentId];
            }

            return area.Name;
        }

        var ascents = await db.Ascents.AsNoTracking()
            .Select(a => new { a.Id, a.Route.Crag.AreaId, a.Date, a.DatePrecision })
            .ToListAsync(cancellationToken);

        return ascents.Select(a => new TripAscent(a.Id, CountryOf(a.AreaId), a.Date, a.DatePrecision)).ToList();
    }
}
