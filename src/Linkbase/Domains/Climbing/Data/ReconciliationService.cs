using Linkbase.Data;
using Linkbase.Domains.Climbing.Import;
using Microsoft.EntityFrameworkCore;

namespace Linkbase.Domains.Climbing.Data;

public sealed record EntityCounts(string Entity, int Total, int Ukc, int MountainProject, int WithoutRef);

public sealed record ReviewRow(string EntityType, string Reason, double Similarity);

public sealed record ReconciliationReport(
    IReadOnlyList<EntityCounts> Entities,
    int Partners,
    int PartnerLinks,
    int Trips,
    int TripLinks,
    int InferredTripLinks,
    int YearOnlyAscents,
    int MonthOnlyAscents,
    IReadOnlyList<ReviewRow> OpenReviews);

public sealed class ReconciliationService(IDbContextFactory<LinkbaseDbContext> contextFactory)
{
    public async Task<ReconciliationReport> BuildReportAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);

        var refs = await db.ExternalRefs.AsNoTracking()
            .GroupBy(r => new { r.EntityType, r.Source })
            .Select(g => new { g.Key.EntityType, g.Key.Source, Count = g.Count() })
            .ToListAsync(cancellationToken);

        async Task<EntityCounts> CountsAsync<T>(string name, ExternalEntityType type, IQueryable<T> rows, IQueryable<long> ids)
        {
            var refIds = db.ExternalRefs.Where(r => r.EntityType == type).Select(r => r.EntityId);
            return new EntityCounts(
                name,
                await rows.CountAsync(cancellationToken),
                refs.Where(r => r.EntityType == type && r.Source == DataSource.Ukc).Sum(r => r.Count),
                refs.Where(r => r.EntityType == type && r.Source == DataSource.MountainProject).Sum(r => r.Count),
                await ids.CountAsync(id => !refIds.Contains(id), cancellationToken));
        }

        var entities = new List<EntityCounts>
        {
            await CountsAsync("Areas", ExternalEntityType.Area, db.Areas, db.Areas.Select(a => a.Id)),
            await CountsAsync("Crags", ExternalEntityType.Crag, db.Crags, db.Crags.Select(c => c.Id)),
            await CountsAsync("Routes", ExternalEntityType.Route, db.Routes, db.Routes.Select(r => r.Id)),
            await CountsAsync("Ascents", ExternalEntityType.Ascent, db.Ascents, db.Ascents.Select(a => a.Id)),
        };

        var reviews = await db.ReviewItems.AsNoTracking()
            .Where(i => i.Status == ReviewStatus.Open)
            .OrderBy(i => i.EntityType).ThenByDescending(i => i.Similarity)
            .Select(i => new { i.EntityType, i.Reason, i.Similarity })
            .ToListAsync(cancellationToken);

        return new ReconciliationReport(
            entities,
            await db.Partners.CountAsync(cancellationToken),
            await db.AscentPartners.CountAsync(cancellationToken),
            await db.Trips.CountAsync(cancellationToken),
            await db.Ascents.CountAsync(a => a.TripId != null, cancellationToken),
            await db.Ascents.CountAsync(a => a.TripInferred, cancellationToken),
            await db.Ascents.CountAsync(a => a.DatePrecision == DatePrecision.Year, cancellationToken),
            await db.Ascents.CountAsync(a => a.DatePrecision == DatePrecision.Month, cancellationToken),
            reviews.Select(r => new ReviewRow(r.EntityType.ToString(), r.Reason, r.Similarity)).ToList());
    }

    public async Task<ComparisonResult> CompareWithNeo4jAsync(Neo4jExport export, CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);

        var ascents = await db.Ascents.AsNoTracking()
            .Select(a => new ComparisonAscent(
                a.Source,
                a.Route.Crag.Name,
                a.Route.Name,
                a.Date,
                a.DatePrecision,
                a.StyleRaw,
                a.Notes,
                a.Route.Grade,
                a.Route.LengthM,
                a.Partners.Select(p => p.Partner.Name).ToList(),
                a.Trip == null ? null : a.Trip.Name,
                a.TripInferred))
            .ToListAsync(cancellationToken);

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

        var tripAreas = await db.Ascents.AsNoTracking()
            .Where(a => a.TripId != null)
            .Select(a => new { TripId = a.TripId!.Value, a.Route.Crag.AreaId })
            .Distinct()
            .ToListAsync(cancellationToken);
        var countries = tripAreas.ToLookup(t => t.TripId, t => CountryOf(t.AreaId));

        var trips = await db.Trips.AsNoTracking()
            .Select(t => new { t.Id, t.Key, t.Name, t.StartDate, t.EndDate })
            .ToListAsync(cancellationToken);

        return Neo4jComparison.Compare(
            export,
            ascents,
            trips.Select(t => new ComparisonTrip(t.Key, t.Name, t.StartDate, t.EndDate, countries[t.Id].Distinct().ToList())).ToList());
    }
}
