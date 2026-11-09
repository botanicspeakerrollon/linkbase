using Linkbase.Core;
using Linkbase.Domains.Climbing.Data;

namespace Linkbase.Tests;

// The Insights reports over the generic store, on the made-up logbook from GenericConversionTests: two ascents on a home trip
// in Wessex in May 2020, a July 2021 ascent in Gaule on a trip abroad, and a repeat of the first route in 2022 with only
// the year known. Sam Example climbed the first and the third.
[Trait("Category", "Database")]
public sealed class InsightsRepositoryTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    private async Task<InsightsRepository> ConvertedAsync()
    {
        var target = await MadeUpLogbook.CreateAsync(fixture);
        return new InsightsRepository(target);
    }

    [Fact]
    public async Task Trip_reports_count_days_ascents_crags_and_partners()
    {
        var insights = await ConvertedAsync();

        var abroad = Assert.Single(await insights.TripsAbroadAsync());
        Assert.Equal(("Gaule, July 2021", null, 0L, 1L), (abroad.Name, abroad.StartDate, abroad.DatedDays, abroad.Ascents));
        Assert.Equal(["Les Falaises"], abroad.Crags);
        Assert.Equal(["Sam Example"], abroad.Partners);

        var biggest = Assert.Single(await insights.BiggestTripsAsync());
        Assert.Equal(("Wessex, May 2020", 2L, 2L, 1.0m), (biggest.Name, biggest.Days, biggest.Ascents, biggest.AscentsPerDay));

        var year = Assert.Single(await insights.TripsAbroadPerYearAsync());
        Assert.Equal((2021, 1L, 0L), (year.Year, year.Trips, year.Days));
        Assert.Equal(["Gaule, July 2021"], year.Names);

        var country = Assert.Single(await insights.CountriesRevisitedAsync());
        Assert.Equal(("Gaule", 1L, 2021, 2021), (country.Country, country.Trips, country.FirstYear, country.LastYear));

        Assert.Equal("Wessex, May 2020", Assert.Single(await insights.TwoDayTripsAsync()).Name);
    }

    [Fact]
    public async Task Place_reports_count_visits_and_repeats()
    {
        var insights = await ConvertedAsync();

        var visits = (await insights.CragVisitsByCountryAsync()).ToDictionary(v => v.Country);
        Assert.Equal((1L, 0L, 0L), (visits["Wessex"].Crags, visits["Wessex"].OneVisit, visits["Wessex"].Regulars));
        Assert.Equal((1L, 1L), (visits["Gaule"].Crags, visits["Gaule"].OneVisit));

        var repeated = Assert.Single(await insights.MostRepeatedCragsAsync());
        Assert.Equal(("Hollow Tor", 3L, 2L, 1L, 33m), (repeated.Crag, repeated.Ascents, repeated.Climbs, repeated.Repeats, repeated.RepeatPercent));

        Assert.Empty(await insights.CragLoyaltyAsync());
        Assert.Empty(await insights.NotVisitedLatelyAsync(2040));
    }

    [Fact]
    public async Task Season_reports_leave_out_partial_dates()
    {
        var insights = await ConvertedAsync();

        var wessex = (await insights.AscentsByMonthAndCountryAsync()).Single(c => c.Country == "Wessex");
        Assert.Equal(2, wessex.Total);
        Assert.Equal(12, wessex.Months.Length);
        Assert.Equal(2, wessex.Months[4]);

        // The 2022 repeat knows only its year, so it has no month.
        var days = await insights.ClimbingDaysByMonthAsync();
        Assert.Equal([2020, 2021], days.Select(y => y.Year));
        Assert.Equal(2, days[0].Months[4]);

        var seasons = await insights.SeasonsByDisciplineAsync();
        Assert.Contains(seasons, s => s is { Season: "Spring", Discipline: "trad", Ascents: 2 });
        Assert.Contains(seasons, s => s is { Season: "Summer", Discipline: "sport", Ascents: 1 });

        var mix = await insights.DisciplineMixByYearAsync();
        Assert.Equal([2020, 2021, 2022], mix.Select(m => m.Year));
        Assert.Equal(["trad 100%"], mix[0].Mix);
    }

    [Fact]
    public async Task Partner_reports_count_trips_and_ascents()
    {
        var insights = await ConvertedAsync();

        var byTrips = Assert.Single(await insights.PartnersByTripsAsync());
        Assert.Equal(("Sam Example", 2L), (byTrips.Partner, byTrips.Trips));
        Assert.Equal(["Gaule", "Wessex"], byTrips.Countries.Order());

        var byAscents = Assert.Single(await insights.PartnersByAscentsAsync());
        Assert.Equal(("Sam Example", 2L), (byAscents.Partner, byAscents.Ascents));
    }

    [Fact]
    public async Task On_this_day_lists_exact_dates_with_their_details()
    {
        var insights = await ConvertedAsync();

        var row = Assert.Single(await insights.OnThisDayAsync(5, 2));
        Assert.Equal((2020, "Crack of Dawn", "E1 5b", "Hollow Tor", "onsight", "Cold hands"), (row.Year, row.Route, row.Grade, row.Crag, row.Style, row.Notes));
        Assert.Equal(["Sam Example"], row.Partners);

        // July 2021 is stored as the first of the month but only knows its month.
        Assert.Empty(await insights.OnThisDayAsync(7, 1));
    }
}
