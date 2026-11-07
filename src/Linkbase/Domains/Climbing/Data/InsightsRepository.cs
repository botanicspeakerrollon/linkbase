using Linkbase.Data;
using Microsoft.EntityFrameworkCore;

namespace Linkbase.Domains.Climbing.Data;

public sealed record TripAbroadRow(string Name, DateOnly? StartDate, DateOnly? EndDate, long DatedDays, long Ascents, string[] Crags, string[] Partners);

public sealed record BiggestTripRow(string Name, long Days, long Ascents, decimal AscentsPerDay);

public sealed record TripsPerYearRow(int Year, long Trips, long Days, string[] Names);

public sealed record CountryReturnRow(string Country, long Trips, int FirstYear, int LastYear);

public sealed record CragLoyaltyRow(string Crag, string Country, long Years, int FirstYear, int LastYear, long Days, long Climbs, long Repeats);

public sealed record CragVisitsRow(string Country, long Crags, long OneVisit, long Regulars);

public sealed record CragRepeatsRow(string Crag, long Ascents, long Climbs, long Repeats, decimal RepeatPercent);

public sealed record NotVisitedRow(string Crag, string Country, long Days, int LastYear);

public sealed record CountryMonthsRow(string Country, long Total, long[] Months);

public sealed record YearMonthsRow(int Year, long[] Months, long Total);

public sealed record SeasonRow(string Season, string Discipline, long Ascents);

public sealed record DisciplineMixRow(int Year, long Total, string[] Mix);

public sealed record PartnerTripsRow(string Partner, long Trips, string[] SomeTrips, string[] Countries);

public sealed record PartnerAscentsRow(string Partner, long Ascents);

public sealed record TwoDayTripRow(string Name, DateOnly? StartDate, DateOnly? EndDate);

public sealed record OnThisDayRow(int Year, long RouteId, string Route, string? Grade, long CragId, string Crag, string Style, string[] Partners, string? Notes);

// Ported from queries.cypher. Built on the ascent_facts view, which adds each ascent's country and season.
public sealed class InsightsRepository(IDbContextFactory<LinkbaseDbContext> contextFactory)
{
    private const long User = AppUser.DefaultId;

    private async Task<List<T>> QueryAsync<T>(FormattableString sql, CancellationToken cancellationToken)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        return await db.Database.SqlQuery<T>(sql).ToListAsync(cancellationToken);
    }

    // Newest first. A partial date is stored as the first of its month or year, so only exact days count here.
    public Task<List<OnThisDayRow>> OnThisDayAsync(int month, int day, CancellationToken cancellationToken = default) => QueryAsync<OnThisDayRow>(
        $"""
        SELECT extract(year FROM a.date)::int AS "Year",
               r.id AS "RouteId", r.name AS "Route", r.grade AS "Grade",
               c.id AS "CragId", c.name AS "Crag",
               a.style AS "Style",
               coalesce((SELECT array_agg(p.name ORDER BY p.name)
                         FROM ascent_partner ap
                         JOIN partner p ON p.id = ap.partner_id
                         WHERE ap.ascent_id = a.id), ARRAY[]::text[]) AS "Partners",
               a.notes AS "Notes"
        FROM ascent a
        JOIN route r ON r.id = a.route_id
        JOIN crag c ON c.id = r.crag_id
        WHERE a.user_id = {User}
          AND a.date_precision = 'day'
          AND extract(month FROM a.date) = {month}
          AND extract(day FROM a.date) = {day}
        ORDER BY a.date DESC, a.id DESC
        """, cancellationToken);

    public Task<List<TripAbroadRow>> TripsAbroadAsync(CancellationToken cancellationToken = default) => QueryAsync<TripAbroadRow>(
        $"""
        SELECT t.name AS "Name", t.start_date AS "StartDate", t.end_date AS "EndDate",
               count(DISTINCT f.date) FILTER (WHERE f.date_precision = 'day') AS "DatedDays",
               count(*) AS "Ascents",
               (array_agg(DISTINCT f.crag_name))[1:5] AS "Crags",
               coalesce((SELECT array_agg(DISTINCT p.name)
                         FROM ascent a
                         JOIN ascent_partner ap ON ap.ascent_id = a.id
                         JOIN partner p ON p.id = ap.partner_id
                         WHERE a.trip_id = t.id), ARRAY[]::text[]) AS "Partners"
        FROM trip t
        JOIN ascent_facts f ON f.trip_id = t.id
        WHERE t.abroad AND t.user_id = {User}
        GROUP BY t.id
        ORDER BY t.sort_date DESC
        """, cancellationToken);

    public Task<List<BiggestTripRow>> BiggestTripsAsync(CancellationToken cancellationToken = default) => QueryAsync<BiggestTripRow>(
        $"""
        SELECT t.name AS "Name",
               count(DISTINCT f.date) FILTER (WHERE f.date_precision = 'day') AS "Days",
               count(*) AS "Ascents",
               round(count(*) FILTER (WHERE f.date_precision = 'day')::numeric
                     / count(DISTINCT f.date) FILTER (WHERE f.date_precision = 'day'), 1) AS "AscentsPerDay"
        FROM trip t
        JOIN ascent_facts f ON f.trip_id = t.id
        WHERE t.user_id = {User}
        GROUP BY t.id
        HAVING count(DISTINCT f.date) FILTER (WHERE f.date_precision = 'day') > 0
        ORDER BY "Days" DESC
        LIMIT 15
        """, cancellationToken);

    public Task<List<TripsPerYearRow>> TripsAbroadPerYearAsync(CancellationToken cancellationToken = default) => QueryAsync<TripsPerYearRow>(
        $"""
        SELECT y.year AS "Year", count(*) AS "Trips", sum(y.days)::bigint AS "Days", array_agg(y.name ORDER BY y.sort_date) AS "Names"
        FROM (
            SELECT t.id, t.name, t.sort_date, extract(year FROM t.sort_date)::int AS year,
                   count(DISTINCT f.date) FILTER (WHERE f.date_precision = 'day') AS days
            FROM trip t
            JOIN ascent_facts f ON f.trip_id = t.id
            WHERE t.abroad AND t.user_id = {User}
            GROUP BY t.id
        ) y
        GROUP BY y.year
        ORDER BY y.year
        """, cancellationToken);

    public Task<List<CountryReturnRow>> CountriesRevisitedAsync(CancellationToken cancellationToken = default) => QueryAsync<CountryReturnRow>(
        $"""
        SELECT f.country AS "Country", count(DISTINCT t.id) AS "Trips",
               min(extract(year FROM t.sort_date))::int AS "FirstYear",
               max(extract(year FROM t.sort_date))::int AS "LastYear"
        FROM trip t
        JOIN ascent_facts f ON f.trip_id = t.id
        WHERE NOT t.home AND t.user_id = {User}
        GROUP BY f.country
        ORDER BY "Trips" DESC, "Country"
        """, cancellationToken);

    public Task<List<CragLoyaltyRow>> CragLoyaltyAsync(CancellationToken cancellationToken = default) => QueryAsync<CragLoyaltyRow>(
        $"""
        SELECT f.crag_name AS "Crag", f.country AS "Country", count(DISTINCT f.year) AS "Years",
               min(f.year) AS "FirstYear", max(f.year) AS "LastYear", count(DISTINCT f.day_key) AS "Days",
               count(DISTINCT f.route_id) AS "Climbs", count(*) - count(DISTINCT f.route_id) AS "Repeats"
        FROM ascent_facts f
        WHERE f.user_id = {User}
        GROUP BY f.crag_id, f.crag_name, f.country
        HAVING count(DISTINCT f.year) >= 3
        ORDER BY "Years" DESC, "Days" DESC
        """, cancellationToken);

    public Task<List<CragVisitsRow>> CragVisitsByCountryAsync(CancellationToken cancellationToken = default) => QueryAsync<CragVisitsRow>(
        $"""
        SELECT c.country AS "Country", count(*) AS "Crags",
               count(*) FILTER (WHERE c.days = 1) AS "OneVisit",
               count(*) FILTER (WHERE c.days >= 5) AS "Regulars"
        FROM (
            SELECT f.crag_id, f.country, count(DISTINCT f.day_key) AS days
            FROM ascent_facts f
            WHERE f.user_id = {User}
            GROUP BY f.crag_id, f.country
        ) c
        GROUP BY c.country
        ORDER BY "Crags" DESC
        """, cancellationToken);

    public Task<List<CragRepeatsRow>> MostRepeatedCragsAsync(CancellationToken cancellationToken = default) => QueryAsync<CragRepeatsRow>(
        $"""
        SELECT f.crag_name AS "Crag", count(*) AS "Ascents", count(DISTINCT f.route_id) AS "Climbs",
               count(*) - count(DISTINCT f.route_id) AS "Repeats",
               round(100.0 * (count(*) - count(DISTINCT f.route_id)) / count(*)) AS "RepeatPercent"
        FROM ascent_facts f
        WHERE f.user_id = {User}
        GROUP BY f.crag_id, f.crag_name
        HAVING count(*) > count(DISTINCT f.route_id)
        ORDER BY "Repeats" DESC
        LIMIT 15
        """, cancellationToken);

    public Task<List<NotVisitedRow>> NotVisitedLatelyAsync(int currentYear, CancellationToken cancellationToken = default) => QueryAsync<NotVisitedRow>(
        $"""
        SELECT f.crag_name AS "Crag", f.country AS "Country", count(DISTINCT f.day_key) AS "Days", max(f.year) AS "LastYear"
        FROM ascent_facts f
        WHERE f.user_id = {User}
        GROUP BY f.crag_id, f.crag_name, f.country
        HAVING count(DISTINCT f.day_key) >= 5 AND max(f.year) <= {currentYear} - 5
        ORDER BY "Days" DESC
        """, cancellationToken);

    public Task<List<CountryMonthsRow>> AscentsByMonthAndCountryAsync(CancellationToken cancellationToken = default) => QueryAsync<CountryMonthsRow>(
        $"""
        SELECT f.country AS "Country", count(*) AS "Total",
               ARRAY(SELECT count(*) FILTER (WHERE g.month = m)
                     FROM generate_series(1, 12) m
                     LEFT JOIN ascent_facts g ON g.country = f.country AND g.user_id = f.user_id AND g.month = m
                     GROUP BY m ORDER BY m) AS "Months"
        FROM ascent_facts f
        WHERE f.month IS NOT NULL AND f.user_id = {User}
        GROUP BY f.country, f.user_id
        ORDER BY "Total" DESC
        """, cancellationToken);

    public Task<List<YearMonthsRow>> ClimbingDaysByMonthAsync(CancellationToken cancellationToken = default) => QueryAsync<YearMonthsRow>(
        $"""
        SELECT f.year AS "Year",
               ARRAY(SELECT count(DISTINCT g.day_key)
                     FROM generate_series(1, 12) m
                     LEFT JOIN ascent_facts g ON g.year = f.year AND g.user_id = f.user_id AND g.month = m
                     GROUP BY m ORDER BY m) AS "Months",
               count(DISTINCT f.day_key) AS "Total"
        FROM ascent_facts f
        WHERE f.month IS NOT NULL AND f.user_id = {User}
        GROUP BY f.year, f.user_id
        ORDER BY f.year
        """, cancellationToken);

    public Task<List<SeasonRow>> SeasonsByDisciplineAsync(CancellationToken cancellationToken = default) => QueryAsync<SeasonRow>(
        $"""
        SELECT f.season AS "Season", coalesce(f.discipline, 'none') AS "Discipline", count(*) AS "Ascents"
        FROM ascent_facts f
        WHERE f.season IS NOT NULL AND f.user_id = {User}
        GROUP BY f.season, f.discipline
        ORDER BY array_position(ARRAY['Spring', 'Summer', 'Autumn', 'Winter'], f.season), "Ascents" DESC
        """, cancellationToken);

    public Task<List<DisciplineMixRow>> DisciplineMixByYearAsync(CancellationToken cancellationToken = default) => QueryAsync<DisciplineMixRow>(
        $"""
        SELECT d.year AS "Year", sum(d.n)::bigint AS "Total",
               array_agg(d.discipline || ' ' || round(100.0 * d.n / d.total) || '%' ORDER BY d.n DESC) AS "Mix"
        FROM (
            SELECT f.year, coalesce(f.discipline, 'none') AS discipline, count(*) AS n,
                   sum(count(*)) OVER (PARTITION BY f.year) AS total
            FROM ascent_facts f
            WHERE f.user_id = {User}
            GROUP BY f.year, f.discipline
        ) d
        GROUP BY d.year
        ORDER BY d.year
        """, cancellationToken);

    public Task<List<PartnerTripsRow>> PartnersByTripsAsync(CancellationToken cancellationToken = default) => QueryAsync<PartnerTripsRow>(
        $"""
        SELECT p.name AS "Partner", count(DISTINCT t.id) AS "Trips",
               (array_agg(DISTINCT t.name))[1:8] AS "SomeTrips",
               array_agg(DISTINCT f.country) AS "Countries"
        FROM partner p
        JOIN ascent_partner ap ON ap.partner_id = p.id
        JOIN ascent_facts f ON f.ascent_id = ap.ascent_id
        JOIN trip t ON t.id = f.trip_id
        WHERE p.user_id = {User}
        GROUP BY p.id
        ORDER BY "Trips" DESC, "Partner"
        LIMIT 30
        """, cancellationToken);

    public Task<List<PartnerAscentsRow>> PartnersByAscentsAsync(CancellationToken cancellationToken = default) => QueryAsync<PartnerAscentsRow>(
        $"""
        SELECT p.name AS "Partner", count(*) AS "Ascents"
        FROM partner p
        JOIN ascent_partner ap ON ap.partner_id = p.id
        WHERE p.user_id = {User}
        GROUP BY p.id
        ORDER BY "Ascents" DESC, "Partner"
        LIMIT 15
        """, cancellationToken);

    public Task<List<TwoDayTripRow>> TwoDayTripsAsync(CancellationToken cancellationToken = default) => QueryAsync<TwoDayTripRow>(
        $"""
        SELECT t.name AS "Name", t.start_date AS "StartDate", t.end_date AS "EndDate"
        FROM trip t
        JOIN ascent_facts f ON f.trip_id = t.id
        WHERE t.user_id = {User}
        GROUP BY t.id
        HAVING count(DISTINCT f.date) FILTER (WHERE f.date_precision = 'day') = 2
        ORDER BY t.start_date
        """, cancellationToken);
}
