using Linkbase.Data;
using Microsoft.EntityFrameworkCore;

namespace Linkbase.Domains.Climbing.Data;

public sealed record AscentFilter
{
    public DateOnly? From { get; init; }
    public DateOnly? To { get; init; }
    public long? AreaId { get; init; }
    public long? CragId { get; init; }
    public long? RouteId { get; init; }
    public GradeSystem? GradeSystem { get; init; }
    public Discipline? Discipline { get; init; }
    public AscentStyle? Style { get; init; }
}

public enum AscentSort { Date, Route, Crag, Style }

public sealed record AscentRow(
    long Id,
    DateOnly Date,
    DatePrecision DatePrecision,
    long RouteId,
    string RouteName,
    string? Grade,
    GradeSystem? GradeSystem,
    Discipline? Discipline,
    long CragId,
    string CragName,
    AscentStyle Style,
    string? StyleRaw,
    IReadOnlyList<string> Partners,
    string? TripName,
    string? Notes,
    short? MyRating);

public sealed record AscentPage(IReadOnlyList<AscentRow> Rows, int Total);

public sealed record YearCount(int Year, int Ascents);

public sealed record DisciplineCount(Discipline? Discipline, int Ascents);

public sealed record HardestGrade(GradeSystem System, string Grade, string RouteName, long RouteId, int Ascents);

public sealed record AscentStats(
    int Total,
    IReadOnlyList<YearCount> PerYear,
    IReadOnlyList<DisciplineCount> ByDiscipline,
    IReadOnlyList<HardestGrade> Hardest);

public sealed record NewCragInput(string Name, long? AreaId, string? NewCountryName);

public sealed record NewRouteInput(
    string Name,
    string? Grade,
    GradeSystem? GradeSystem,
    Discipline? Discipline,
    int? LengthM,
    int? Pitches,
    long? CragId,
    NewCragInput? NewCrag);

public sealed record AscentInput
{
    public long? RouteId { get; init; }
    public NewRouteInput? NewRoute { get; init; }
    public required DateOnly Date { get; init; }
    public required DatePrecision DatePrecision { get; init; }
    public required AscentStyle Style { get; init; }
    public IReadOnlyList<string> Partners { get; init; } = [];
    public string? Notes { get; init; }
    public short? MyRating { get; init; }
}

public sealed record AscentEdit(
    long Id,
    long RouteId,
    string RouteName,
    string? Grade,
    long CragId,
    string CragName,
    DateOnly Date,
    DatePrecision DatePrecision,
    AscentStyle Style,
    string? StyleRaw,
    IReadOnlyList<string> Partners,
    string? Notes,
    short? MyRating);

public sealed class AscentRepository(
    IDbContextFactory<LinkbaseDbContext> contextFactory,
    AreaRepository areas,
    TripRefresher tripRefresher)
{
    // Styles that count as climbing the route cleanly, for "hardest grade" stats.
    private static readonly AscentStyle[] CleanStyles =
        [AscentStyle.Onsight, AscentStyle.Flash, AscentStyle.Redpoint, AscentStyle.Send, AscentStyle.Lead, AscentStyle.Solo];

    public async Task<AscentPage> SearchAsync(
        AscentFilter filter,
        AscentSort sort,
        bool descending,
        int page,
        int pageSize,
        CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var query = await ApplyAsync(db.Ascents.AsNoTracking(), filter, cancellationToken);

        var total = await query.CountAsync(cancellationToken);
        var sorted = (sort, descending) switch
        {
            (AscentSort.Route, false) => query.OrderBy(a => a.Route.Name),
            (AscentSort.Route, true) => query.OrderByDescending(a => a.Route.Name),
            (AscentSort.Crag, false) => query.OrderBy(a => a.Route.Crag.Name),
            (AscentSort.Crag, true) => query.OrderByDescending(a => a.Route.Crag.Name),
            (AscentSort.Style, false) => query.OrderBy(a => a.Style),
            (AscentSort.Style, true) => query.OrderByDescending(a => a.Style),
            (_, false) => query.OrderBy(a => a.Date),
            (_, true) => query.OrderByDescending(a => a.Date),
        };

        var rows = await sorted
            .ThenByDescending(a => a.Id)
            .Skip(page * pageSize)
            .Take(pageSize)
            .Select(a => new AscentRow(
                a.Id,
                a.Date,
                a.DatePrecision,
                a.RouteId,
                a.Route.Name,
                a.Route.Grade,
                a.Route.GradeSystem,
                a.Route.Discipline,
                a.Route.CragId,
                a.Route.Crag.Name,
                a.Style,
                a.StyleRaw,
                a.Partners.Select(p => p.Partner.Name).OrderBy(n => n).ToList(),
                a.Trip == null ? null : a.Trip.Name,
                a.Notes,
                a.MyRating))
            .ToListAsync(cancellationToken);

        return new AscentPage(rows, total);
    }

    public async Task<AscentStats> StatsAsync(AscentFilter filter, CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var query = await ApplyAsync(db.Ascents.AsNoTracking(), filter, cancellationToken);

        var total = await query.CountAsync(cancellationToken);
        var perYear = await query
            .GroupBy(a => a.Date.Year)
            .OrderByDescending(g => g.Key)
            .Select(g => new YearCount(g.Key, g.Count()))
            .ToListAsync(cancellationToken);
        var byDiscipline = await query
            .GroupBy(a => a.Route.Discipline)
            .OrderByDescending(g => g.Count())
            .Select(g => new DisciplineCount(g.Key, g.Count()))
            .ToListAsync(cancellationToken);

        var climbed = await query
            .Where(a => CleanStyles.Contains(a.Style) && a.Route.GradeSystem != null && a.Route.Grade != null)
            .Select(a => new { System = a.Route.GradeSystem, a.Route.Grade, a.RouteId, a.Route.Name })
            .ToListAsync(cancellationToken);

        var hardest = climbed
            .GroupBy(c => c.RouteId)
            .Select(route => (Route: route.First(), Ascents: route.Count(), Rank: GradeRanking.Rank(route.First().System, route.First().Grade)))
            .Where(x => x.Rank is not null)
            .GroupBy(x => x.Route.System!.Value)
            .Select(system => system.MaxBy(x => x.Rank))
            .Select(x => new HardestGrade(x.Route.System!.Value, x.Route.Grade!, x.Route.Name, x.Route.RouteId, x.Ascents))
            .OrderBy(h => h.System)
            .ToList();

        return new AscentStats(total, perYear, byDiscipline, hardest);
    }

    public async Task<AscentEdit?> GetForEditAsync(long id, CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        return await db.Ascents.AsNoTracking()
            .Where(a => a.Id == id)
            .Select(a => new AscentEdit(
                a.Id,
                a.RouteId,
                a.Route.Name,
                a.Route.Grade,
                a.Route.CragId,
                a.Route.Crag.Name,
                a.Date,
                a.DatePrecision,
                a.Style,
                a.StyleRaw,
                a.Partners.Select(p => p.Partner.Name).OrderBy(n => n).ToList(),
                a.Notes,
                a.MyRating))
            .SingleOrDefaultAsync(cancellationToken);
    }

    public async Task<long> CreateAsync(AscentInput input, CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

        var ascent = new Ascent { UserId = AppUser.DefaultId, Source = DataSource.Manual };
        await ApplyInputAsync(db, ascent, input, cancellationToken);
        db.Ascents.Add(ascent);
        await db.SaveChangesAsync(cancellationToken);

        await transaction.CommitAsync(cancellationToken);
        await tripRefresher.RefreshAsync(cancellationToken);
        return ascent.Id;
    }

    public async Task UpdateAsync(long id, AscentInput input, CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

        var ascent = await db.Ascents.Include(a => a.Partners).SingleOrDefaultAsync(a => a.Id == id, cancellationToken)
            ?? throw new LinkbaseValidationException("This ascent no longer exists.");
        await ApplyInputAsync(db, ascent, input, cancellationToken);
        await db.SaveChangesAsync(cancellationToken);

        await transaction.CommitAsync(cancellationToken);
        await tripRefresher.RefreshAsync(cancellationToken);
    }

    public async Task DeleteAsync(long id, CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

        await db.AscentPartners.Where(p => p.AscentId == id).ExecuteDeleteAsync(cancellationToken);
        await EntityLinks.RemoveAsync(db, ExternalEntityType.Ascent, id, cancellationToken);
        await db.Ascents.Where(a => a.Id == id).ExecuteDeleteAsync(cancellationToken);

        await transaction.CommitAsync(cancellationToken);
        await tripRefresher.RefreshAsync(cancellationToken);
    }

    private async Task<IQueryable<Ascent>> ApplyAsync(IQueryable<Ascent> query, AscentFilter filter, CancellationToken cancellationToken)
    {
        query = query.Where(a => a.UserId == AppUser.DefaultId);
        if (filter.From is { } from)
        {
            query = query.Where(a => a.Date >= from);
        }

        if (filter.To is { } to)
        {
            query = query.Where(a => a.Date <= to);
        }

        if (filter.AreaId is { } areaId)
        {
            var areaIds = await areas.DescendantIdsAsync(areaId, cancellationToken);
            query = query.Where(a => areaIds.Contains(a.Route.Crag.AreaId));
        }

        if (filter.CragId is { } cragId)
        {
            query = query.Where(a => a.Route.CragId == cragId);
        }

        if (filter.RouteId is { } routeId)
        {
            query = query.Where(a => a.RouteId == routeId);
        }

        if (filter.GradeSystem is { } gradeSystem)
        {
            query = query.Where(a => a.Route.GradeSystem == gradeSystem);
        }

        if (filter.Discipline is { } discipline)
        {
            query = query.Where(a => a.Route.Discipline == discipline);
        }

        if (filter.Style is { } style)
        {
            query = query.Where(a => a.Style == style);
        }

        return query;
    }

    private static async Task ApplyInputAsync(LinkbaseDbContext db, Ascent ascent, AscentInput input, CancellationToken cancellationToken)
    {
        if (input.MyRating is < 0 or > 3)
        {
            throw new LinkbaseValidationException("A rating must be between 0 and 3 stars.");
        }

        ascent.RouteId = await ResolveRouteAsync(db, input, cancellationToken);
        ascent.DatePrecision = input.DatePrecision;
        ascent.Date = input.DatePrecision switch
        {
            DatePrecision.Month => new DateOnly(input.Date.Year, input.Date.Month, 1),
            DatePrecision.Year => new DateOnly(input.Date.Year, 1, 1),
            _ => input.Date,
        };
        ascent.Style = input.Style;
        ascent.Notes = string.IsNullOrWhiteSpace(input.Notes) ? null : input.Notes.Trim();
        ascent.MyRating = input.MyRating;

        var names = input.Partners.Select(p => p.Trim()).Where(p => p.Length > 0).Distinct(StringComparer.Ordinal).ToList();
        var existing = await db.Partners
            .Where(p => p.UserId == AppUser.DefaultId && names.Contains(p.Name))
            .ToDictionaryAsync(p => p.Name, cancellationToken);
        ascent.Partners.Clear();
        foreach (var name in names)
        {
            if (!existing.TryGetValue(name, out var partner))
            {
                partner = new Partner { UserId = AppUser.DefaultId, Name = name };
                db.Partners.Add(partner);
                await db.SaveChangesAsync(cancellationToken);
            }

            ascent.Partners.Add(new AscentPartner { PartnerId = partner.Id });
        }
    }

    private static async Task<long> ResolveRouteAsync(LinkbaseDbContext db, AscentInput input, CancellationToken cancellationToken)
    {
        if (input.RouteId is { } routeId)
        {
            return routeId;
        }

        var newRoute = input.NewRoute ?? throw new LinkbaseValidationException("Choose a route.");
        if (string.IsNullOrWhiteSpace(newRoute.Name))
        {
            throw new LinkbaseValidationException("A route needs a name.");
        }

        var route = new Route
        {
            UserId = AppUser.DefaultId,
            CragId = await ResolveCragAsync(db, newRoute, cancellationToken),
            Name = newRoute.Name.Trim(),
            Grade = string.IsNullOrWhiteSpace(newRoute.Grade) ? null : newRoute.Grade.Trim(),
            GradeSystem = newRoute.GradeSystem,
            Discipline = newRoute.Discipline,
            LengthM = newRoute.LengthM,
            Pitches = newRoute.Pitches,
        };
        db.Routes.Add(route);
        await db.SaveChangesAsync(cancellationToken);
        return route.Id;
    }

    private static async Task<long> ResolveCragAsync(LinkbaseDbContext db, NewRouteInput newRoute, CancellationToken cancellationToken)
    {
        if (newRoute.CragId is { } cragId)
        {
            return cragId;
        }

        var newCrag = newRoute.NewCrag ?? throw new LinkbaseValidationException("Choose a crag.");
        if (string.IsNullOrWhiteSpace(newCrag.Name))
        {
            throw new LinkbaseValidationException("A crag needs a name.");
        }

        var crag = new Crag
        {
            UserId = AppUser.DefaultId,
            AreaId = await ResolveAreaAsync(db, newCrag, cancellationToken),
            Name = newCrag.Name.Trim(),
        };
        db.Crags.Add(crag);
        await db.SaveChangesAsync(cancellationToken);
        return crag.Id;
    }

    private static async Task<long> ResolveAreaAsync(LinkbaseDbContext db, NewCragInput newCrag, CancellationToken cancellationToken)
    {
        if (newCrag.AreaId is { } areaId)
        {
            return areaId;
        }

        if (string.IsNullOrWhiteSpace(newCrag.NewCountryName))
        {
            throw new LinkbaseValidationException("Choose an area for the new crag.");
        }

        var name = newCrag.NewCountryName.Trim();
        var existingId = await db.Areas
            .Where(a => a.ParentId == null && a.Name == name)
            .Select(a => (long?)a.Id)
            .SingleOrDefaultAsync(cancellationToken);
        if (existingId is { } id)
        {
            return id;
        }

        var country = new Area { UserId = AppUser.DefaultId, Name = name, Kind = AreaKind.Country };
        db.Areas.Add(country);
        await db.SaveChangesAsync(cancellationToken);
        return country.Id;
    }
}

internal static class EntityLinks
{
    // external_ref is polymorphic and has no foreign key, so deletes clear it by hand.
    public static Task RemoveAsync(LinkbaseDbContext db, ExternalEntityType type, long id, CancellationToken cancellationToken) =>
        db.ExternalRefs.Where(r => r.EntityType == type && r.EntityId == id).ExecuteDeleteAsync(cancellationToken);
}
