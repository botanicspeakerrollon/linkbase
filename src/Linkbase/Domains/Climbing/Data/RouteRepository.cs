using Linkbase.Data;
using Microsoft.EntityFrameworkCore;

namespace Linkbase.Domains.Climbing.Data;

public sealed record RouteOption(long Id, string Name, string? Grade, long CragId, string CragName);

public sealed record RouteDetail(
    long Id,
    string Name,
    string? Grade,
    GradeSystem? GradeSystem,
    Discipline? Discipline,
    int? LengthM,
    int? Pitches,
    string? Description,
    long CragId,
    string CragName,
    long AreaId);

public sealed record RouteInput(
    string Name,
    string? Grade,
    GradeSystem? GradeSystem,
    Discipline? Discipline,
    int? LengthM,
    int? Pitches,
    string? Description,
    long CragId);

public sealed class RouteRepository(IDbContextFactory<LinkbaseDbContext> contextFactory, TripRefresher tripRefresher)
{
    private const double SearchSimilarity = 0.3;
    private const int SearchLimit = 20;

    public async Task<IReadOnlyList<RouteOption>> SearchAsync(long? cragId, string? text, CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var query = db.Routes.AsNoTracking();
        if (cragId is { } id)
        {
            query = query.Where(r => r.CragId == id);
        }

        if (string.IsNullOrWhiteSpace(text))
        {
            return await query.OrderBy(r => r.Name).Take(SearchLimit)
                .Select(r => new RouteOption(r.Id, r.Name, r.Grade, r.CragId, r.Crag.Name))
                .ToListAsync(cancellationToken);
        }

        var term = text.Trim();
        var pattern = $"%{term}%";
        return await query
            .Where(r => EF.Functions.ILike(r.Name, pattern) || EF.Functions.TrigramsSimilarity(r.Name, term) >= SearchSimilarity)
            .OrderByDescending(r => EF.Functions.ILike(r.Name, pattern))
            .ThenByDescending(r => EF.Functions.TrigramsSimilarity(r.Name, term))
            .Take(SearchLimit)
            .Select(r => new RouteOption(r.Id, r.Name, r.Grade, r.CragId, r.Crag.Name))
            .ToListAsync(cancellationToken);
    }

    public async Task<RouteDetail?> GetAsync(long id, CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        return await db.Routes.AsNoTracking()
            .Where(r => r.Id == id)
            .Select(r => new RouteDetail(
                r.Id,
                r.Name,
                r.Grade,
                r.GradeSystem,
                r.Discipline,
                r.LengthM,
                r.Pitches,
                r.Description,
                r.CragId,
                r.Crag.Name,
                r.Crag.AreaId))
            .SingleOrDefaultAsync(cancellationToken);
    }

    public async Task UpdateAsync(long id, RouteInput input, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(input.Name))
        {
            throw new LinkbaseValidationException("A route needs a name.");
        }

        if (input.LengthM < 0 || input.Pitches < 0)
        {
            throw new LinkbaseValidationException("Length and pitches can't be negative.");
        }

        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var route = await db.Routes.SingleOrDefaultAsync(r => r.Id == id, cancellationToken)
            ?? throw new LinkbaseValidationException("This route no longer exists.");
        var movedCrag = route.CragId != input.CragId;

        route.Name = input.Name.Trim();
        route.Grade = string.IsNullOrWhiteSpace(input.Grade) ? null : input.Grade.Trim();
        route.GradeSystem = input.GradeSystem;
        route.Discipline = input.Discipline;
        route.LengthM = input.LengthM;
        route.Pitches = input.Pitches;
        route.Description = string.IsNullOrWhiteSpace(input.Description) ? null : input.Description.Trim();
        route.CragId = input.CragId;
        await db.SaveChangesAsync(cancellationToken);

        // Trips depend on a route's country.
        if (movedCrag)
        {
            await tripRefresher.RefreshAsync(cancellationToken);
        }
    }

    public async Task DeleteAsync(long id, CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var ascents = await db.Ascents.CountAsync(a => a.RouteId == id, cancellationToken);
        if (ascents > 0)
        {
            throw new LinkbaseValidationException($"This route still has {ascents} ascent(s). Delete them first.");
        }

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        await EntityLinks.RemoveAsync(db, ExternalEntityType.Route, id, cancellationToken);
        await db.Routes.Where(r => r.Id == id).ExecuteDeleteAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }
}
