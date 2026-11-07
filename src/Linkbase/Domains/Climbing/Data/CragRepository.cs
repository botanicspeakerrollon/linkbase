using Linkbase.Data;
using Microsoft.EntityFrameworkCore;

namespace Linkbase.Domains.Climbing.Data;

public sealed record CragOption(long Id, string Name, string? Place);

public sealed record CragDetail(
    long Id,
    string Name,
    long AreaId,
    string? RockType,
    double? Lat,
    double? Lon,
    IReadOnlyList<string> Aliases,
    string? Notes);

public sealed record CragInput(
    string Name,
    long AreaId,
    string? RockType,
    double? Lat,
    double? Lon,
    IReadOnlyList<string> Aliases,
    string? Notes);

public sealed record RouteSummary(
    long Id,
    string Name,
    string? Grade,
    GradeSystem? GradeSystem,
    Discipline? Discipline,
    int? LengthM,
    int? Pitches,
    int Ascents,
    DateOnly? LastClimbed);

public sealed class CragRepository(IDbContextFactory<LinkbaseDbContext> contextFactory, TripRefresher tripRefresher)
{
    private const double SearchSimilarity = 0.3;
    private const int SearchLimit = 20;

    // Matches on part of the name or aliases, or a similar spelling, best match first.
    public async Task<IReadOnlyList<CragOption>> FindCragCandidatesAsync(string? text, CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var query = db.Crags.AsNoTracking();
        if (string.IsNullOrWhiteSpace(text))
        {
            return await query.OrderBy(c => c.Name).Take(SearchLimit)
                .Select(c => new CragOption(c.Id, c.Name, c.Area.Name))
                .ToListAsync(cancellationToken);
        }

        var term = text.Trim();
        var pattern = $"%{term}%";
        return await query
            .Where(c => EF.Functions.ILike(c.Name, pattern)
                || c.Aliases.Any(a => EF.Functions.ILike(a, pattern))
                || EF.Functions.TrigramsSimilarity(c.Name, term) >= SearchSimilarity)
            .OrderByDescending(c => EF.Functions.ILike(c.Name, pattern))
            .ThenByDescending(c => EF.Functions.TrigramsSimilarity(c.Name, term))
            .Take(SearchLimit)
            .Select(c => new CragOption(c.Id, c.Name, c.Area.Name))
            .ToListAsync(cancellationToken);
    }

    public async Task<CragDetail?> GetAsync(long id, CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        return await db.Crags.AsNoTracking()
            .Where(c => c.Id == id)
            .Select(c => new CragDetail(c.Id, c.Name, c.AreaId, c.RockType, c.Lat, c.Lon, c.Aliases, c.Notes))
            .SingleOrDefaultAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<RouteSummary>> RoutesAsync(long cragId, CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        return await db.Routes.AsNoTracking()
            .Where(r => r.CragId == cragId)
            .OrderBy(r => r.Name)
            .Select(r => new RouteSummary(
                r.Id,
                r.Name,
                r.Grade,
                r.GradeSystem,
                r.Discipline,
                r.LengthM,
                r.Pitches,
                r.Ascents.Count,
                r.Ascents.Max(a => (DateOnly?)a.Date)))
            .ToListAsync(cancellationToken);
    }

    public async Task UpdateAsync(long id, CragInput input, CancellationToken cancellationToken = default)
    {
        Validate(input);
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var crag = await db.Crags.SingleOrDefaultAsync(c => c.Id == id, cancellationToken)
            ?? throw new LinkbaseValidationException("This crag no longer exists.");
        var movedArea = crag.AreaId != input.AreaId;

        crag.Name = input.Name.Trim();
        crag.AreaId = input.AreaId;
        crag.RockType = string.IsNullOrWhiteSpace(input.RockType) ? null : input.RockType.Trim();
        crag.Lat = input.Lat;
        crag.Lon = input.Lon;
        crag.Aliases = input.Aliases.Select(a => a.Trim()).Where(a => a.Length > 0).ToList();
        crag.Notes = string.IsNullOrWhiteSpace(input.Notes) ? null : input.Notes.Trim();
        await db.SaveChangesAsync(cancellationToken);

        // Trips depend on a crag's country.
        if (movedArea)
        {
            await tripRefresher.RefreshAsync(cancellationToken);
        }
    }

    public async Task DeleteAsync(long id, CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var routes = await db.Routes.CountAsync(r => r.CragId == id, cancellationToken);
        if (routes > 0)
        {
            throw new LinkbaseValidationException($"This crag still has {routes} route(s). Delete or move them first.");
        }

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        await EntityLinks.RemoveAsync(db, ExternalEntityType.Crag, id, cancellationToken);
        await db.Crags.Where(c => c.Id == id).ExecuteDeleteAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    private static void Validate(CragInput input)
    {
        if (string.IsNullOrWhiteSpace(input.Name))
        {
            throw new LinkbaseValidationException("A crag needs a name.");
        }

        if (input.Lat is < -90 or > 90)
        {
            throw new LinkbaseValidationException("Latitude must be between -90 and 90.");
        }

        if (input.Lon is < -180 or > 180)
        {
            throw new LinkbaseValidationException("Longitude must be between -180 and 180.");
        }
    }
}
