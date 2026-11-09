using System.Globalization;
using System.Text.Json.Nodes;
using Linkbase.Data;
using Microsoft.EntityFrameworkCore;

namespace Linkbase.Domains.Climbing.Data;

public enum AscentSort { Date, Route, Crag, Style }

public sealed record YearCount(int Year, int Ascents);

public sealed record DisciplineCount(Discipline? Discipline, int Ascents);

// The Logbook page's queries over the generic store (see docs/history/climbing-to-generic-plan.md). Ids are node ids, such as
// crag:12.
public sealed record LogbookFilter
{
    public DateOnly? From { get; init; }
    public DateOnly? To { get; init; }

    // An area includes everything below it.
    public string? AreaId { get; init; }
    public string? CragId { get; init; }
    public GradeSystem? GradeSystem { get; init; }
    public Discipline? Discipline { get; init; }
    public AscentStyle? Style { get; init; }
}

public sealed record LogbookRow(
    string Id,
    string Key,
    DateOnly Date,
    DatePrecision DatePrecision,
    string RouteName,
    string? Grade,
    GradeSystem? GradeSystem,
    Discipline? Discipline,
    string CragName,
    AscentStyle Style,
    string? StyleRaw,
    IReadOnlyList<string> Partners,
    string? TripName,
    string? Notes,
    int? MyRating);

public sealed record LogbookResults(IReadOnlyList<LogbookRow> Rows, int Total);

public sealed record HardestClimb(GradeSystem System, string Grade, string RouteName, string RouteId, int Ascents);

public sealed record LogbookStats(
    int Total,
    IReadOnlyList<YearCount> PerYear,
    IReadOnlyList<DisciplineCount> ByDiscipline,
    IReadOnlyList<HardestClimb> Hardest);

// Reads the whole logbook and filters it in memory. That's nothing at a personal logbook's size, and it keeps the filters
// (which follow links up through crag and area) simple. A filter that gets slow would move into SQL.
public sealed class LogbookRepository(IDbContextFactory<LinkbaseDbContext> contextFactory)
{
    // Styles that count as climbing the route cleanly, for "hardest grade" stats.
    private static readonly AscentStyle[] CleanStyles =
        [AscentStyle.Onsight, AscentStyle.Flash, AscentStyle.Redpoint, AscentStyle.Send, AscentStyle.Lead, AscentStyle.Solo];

    private static readonly string[] LinkTypes = ["OF", "AT", "IN", "WITH", "ON_TRIP"];
    private static readonly string[] NodeTypes = ["Route", "Crag", "Area", "Partner", "Trip"];

    private sealed record Entry(LogbookRow Row, string RouteId, string CragId, IReadOnlySet<string> AreaIds);

    public async Task<LogbookResults> SearchAsync(
        LogbookFilter filter, AscentSort sort, bool descending, int page, int pageSize, CancellationToken cancellationToken = default)
    {
        var matching = Filter(await ReadAsync(cancellationToken), filter);
        var ordered = (sort, descending) switch
        {
            (AscentSort.Route, false) => matching.OrderBy(e => e.Row.RouteName, StringComparer.OrdinalIgnoreCase),
            (AscentSort.Route, true) => matching.OrderByDescending(e => e.Row.RouteName, StringComparer.OrdinalIgnoreCase),
            (AscentSort.Crag, false) => matching.OrderBy(e => e.Row.CragName, StringComparer.OrdinalIgnoreCase),
            (AscentSort.Crag, true) => matching.OrderByDescending(e => e.Row.CragName, StringComparer.OrdinalIgnoreCase),
            (AscentSort.Style, false) => matching.OrderBy(e => e.Row.Style.ToString(), StringComparer.Ordinal),
            (AscentSort.Style, true) => matching.OrderByDescending(e => e.Row.Style.ToString(), StringComparer.Ordinal),
            (_, false) => matching.OrderBy(e => e.Row.Date),
            (_, true) => matching.OrderByDescending(e => e.Row.Date),
        };

        var rows = ordered.ThenByDescending(e => e.Row.Id, StringComparer.Ordinal).Skip(page * pageSize).Take(pageSize).Select(e => e.Row).ToList();
        return new LogbookResults(rows, matching.Count);
    }

    public async Task<LogbookStats> StatsAsync(LogbookFilter filter, CancellationToken cancellationToken = default)
    {
        var matching = Filter(await ReadAsync(cancellationToken), filter);

        var perYear = matching.GroupBy(e => e.Row.Date.Year).OrderByDescending(g => g.Key).Select(g => new YearCount(g.Key, g.Count())).ToList();
        var byDiscipline = matching.GroupBy(e => e.Row.Discipline).OrderByDescending(g => g.Count()).Select(g => new DisciplineCount(g.Key, g.Count())).ToList();

        var hardest = matching
            .Where(e => CleanStyles.Contains(e.Row.Style) && e.Row.GradeSystem is not null && e.Row.Grade is not null)
            .GroupBy(e => e.RouteId)
            .Select(route => (First: route.First(), Ascents: route.Count(), Rank: GradeRanking.Rank(route.First().Row.GradeSystem, route.First().Row.Grade)))
            .Where(x => x.Rank is not null)
            .GroupBy(x => x.First.Row.GradeSystem!.Value)
            .Select(system => system.MaxBy(x => x.Rank))
            .Select(x => new HardestClimb(x.First.Row.GradeSystem!.Value, x.First.Row.Grade!, x.First.Row.RouteName, x.First.RouteId, x.Ascents))
            .OrderBy(h => h.System)
            .ToList();

        return new LogbookStats(matching.Count, perYear, byDiscipline, hardest);
    }

    private static List<Entry> Filter(List<Entry> entries, LogbookFilter filter) => entries
        .Where(e => (filter.From is null || e.Row.Date >= filter.From)
            && (filter.To is null || e.Row.Date <= filter.To)
            && (filter.AreaId is null || e.AreaIds.Contains(filter.AreaId))
            && (filter.CragId is null || e.CragId == filter.CragId)
            && (filter.GradeSystem is null || e.Row.GradeSystem == filter.GradeSystem)
            && (filter.Discipline is null || e.Row.Discipline == filter.Discipline)
            && (filter.Style is null || e.Row.Style == filter.Style))
        .ToList();

    private async Task<List<Entry>> ReadAsync(CancellationToken cancellationToken)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);

        var nodes = await db.Nodes.AsNoTracking()
            .Where(n => NodeTypes.Contains(n.Type))
            .Select(n => new { n.Id, n.Name, n.Props, n.Type })
            .ToDictionaryAsync(n => n.Id, cancellationToken);
        var links = (await db.NodeLinks.AsNoTracking()
                .Where(l => LinkTypes.Contains(l.RelType))
                .Select(l => new { l.SourceId, l.RelType, l.TargetId })
                .ToListAsync(cancellationToken))
            .ToLookup(l => (l.SourceId, l.RelType), l => l.TargetId);
        var ascents = await db.Nodes.AsNoTracking()
            .Where(n => n.Type == "Ascent")
            .Select(n => new { n.Id, n.Key, n.Props })
            .ToListAsync(cancellationToken);

        string? Target(string id, string rel) => links[(id, rel)].OrderBy(t => t, StringComparer.Ordinal).FirstOrDefault();

        // Every area above a crag, nearest first. The set guards against a loop in bad data.
        IReadOnlySet<string> AreasAbove(string cragId)
        {
            var areas = new HashSet<string>();
            for (var area = Target(cragId, "IN"); area is not null && areas.Add(area); area = Target(area, "IN"))
            {
            }

            return areas;
        }

        var areaCache = new Dictionary<string, IReadOnlySet<string>>();
        var entries = new List<Entry>();
        foreach (var ascent in ascents)
        {
            var props = JsonNode.Parse(ascent.Props) as JsonObject ?? [];
            if (!DateOnly.TryParseExact((string?)props["date"], "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)
                || Target(ascent.Id, "OF") is not { } routeId || !nodes.TryGetValue(routeId, out var route)
                || Target(routeId, "AT") is not { } cragId || !nodes.TryGetValue(cragId, out var crag))
            {
                continue;
            }

            var routeProps = JsonNode.Parse(route.Props) as JsonObject ?? [];
            var partners = links[(ascent.Id, "WITH")].Where(nodes.ContainsKey).Select(p => nodes[p].Name).Order(StringComparer.OrdinalIgnoreCase).ToList();
            var trip = Target(ascent.Id, "ON_TRIP") is { } tripId && nodes.TryGetValue(tripId, out var found) ? found.Name : null;
            if (!areaCache.TryGetValue(cragId, out var areaIds))
            {
                areaCache[cragId] = areaIds = AreasAbove(cragId);
            }

            entries.Add(new Entry(
                new LogbookRow(
                    ascent.Id,
                    ascent.Key,
                    date,
                    Parse<DatePrecision>((string?)props["date_precision"]) ?? DatePrecision.Day,
                    route.Name,
                    (string?)routeProps["grade"],
                    Parse<GradeSystem>((string?)routeProps["grade_system"]),
                    Parse<Discipline>((string?)routeProps["discipline"]),
                    crag.Name,
                    Parse<AscentStyle>((string?)props["style"]) ?? AscentStyle.Other,
                    (string?)props["style_raw"],
                    partners,
                    trip,
                    (string?)props["notes"],
                    props["my_rating"] is { } rating ? (int)Math.Round((double)rating) : null),
                routeId,
                cragId,
                areaIds));
        }

        return entries;
    }

    private static T? Parse<T>(string? stored)
        where T : struct, Enum =>
        SnakeCaseEnumConverter<T>.TryFromStore(stored, out var value) ? value : null;
}
