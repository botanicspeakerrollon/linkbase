using Linkbase.Data;
using Microsoft.EntityFrameworkCore;

namespace Linkbase.Domains.Climbing.Data;

public sealed record EntityCounts(string Entity, int Total, int Ukc, int MountainProject, int WithoutRef);

public sealed record ReconciliationReport(
    IReadOnlyList<EntityCounts> Entities,
    int Partners,
    int PartnerLinks,
    int Trips,
    int TripLinks,
    int InferredTripLinks,
    int YearOnlyAscents,
    int MonthOnlyAscents);

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

        return new ReconciliationReport(
            entities,
            await db.Partners.CountAsync(cancellationToken),
            await db.AscentPartners.CountAsync(cancellationToken),
            await db.Trips.CountAsync(cancellationToken),
            await db.Ascents.CountAsync(a => a.TripId != null, cancellationToken),
            await db.Ascents.CountAsync(a => a.TripInferred, cancellationToken),
            await db.Ascents.CountAsync(a => a.DatePrecision == DatePrecision.Year, cancellationToken),
            await db.Ascents.CountAsync(a => a.DatePrecision == DatePrecision.Month, cancellationToken));
    }
}
