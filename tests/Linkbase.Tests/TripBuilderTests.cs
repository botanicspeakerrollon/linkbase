using Linkbase.Data;
using Linkbase.Domains.Climbing.Data;
using Linkbase.Core.Graph;
using Linkbase.Domains.Climbing.Import;

namespace Linkbase.Tests;

public class TripBuilderTests
{
    private static long _nextId;

    private static TripAscent Day(string country, string date) =>
        new(++_nextId, country, DateOnly.Parse(date), DatePrecision.Day);

    private static TripAscent Month(string country, string date) =>
        new(++_nextId, country, DateOnly.Parse(date), DatePrecision.Month);

    private static List<PlannedTrip> Build(params TripAscent[] ascents) => TripBuilder.Build(ascents, []);

    [Fact]
    public void Uk_trip_ends_after_a_gap_of_more_than_three_days()
    {
        var trips = Build(Day("Wales", "2020-05-01"), Day("Wales", "2020-05-04"), Day("Wales", "2020-05-08"));
        Assert.Equal(2, trips.Count);
        Assert.Equal(new DateOnly(2020, 5, 4), trips[0].EndDate);
    }

    [Fact]
    public void Abroad_trip_allows_a_gap_of_seven_days_but_not_eight()
    {
        var trips = Build(Day("Spain", "2020-05-01"), Day("Spain", "2020-05-08"), Day("Spain", "2020-05-16"));
        Assert.Equal(2, trips.Count);
        Assert.Equal(new DateOnly(2020, 5, 8), trips[0].EndDate);
    }

    [Fact]
    public void Single_days_in_england_are_not_trips_but_elsewhere_they_are()
    {
        var trips = Build(Day("England", "2020-05-01"), Day("Wales", "2020-06-01"), Day("Spain", "2020-07-01"));
        Assert.Equal(["Wales", "Spain"], trips.Select(t => t.Country));
    }

    [Fact]
    public void A_day_in_another_country_breaks_a_trip()
    {
        var trips = Build(Day("Wales", "2020-05-01"), Day("Scotland", "2020-05-02"), Day("Wales", "2020-05-03"));
        Assert.Equal(3, trips.Count);
    }

    [Fact]
    public void Names_and_flags_follow_the_country()
    {
        var home = Build(Day("England", "2016-07-01"), Day("England", "2016-07-02")).Single();
        Assert.Equal("England 2016-07-01", home.Key);
        Assert.Equal("England · Jul 2016", home.Name);
        Assert.True(home.Home);
        Assert.False(home.Abroad);

        var wales = Build(Day("Wales", "2012-09-01")).Single();
        Assert.False(wales.Home);
        Assert.False(wales.Abroad);

        Assert.True(Build(Day("Spain", "2012-09-01")).Single().Abroad);
    }

    [Fact]
    public void Links_every_ascent_on_a_trip_day()
    {
        var first = Day("Spain", "2020-05-01");
        var second = Day("Spain", "2020-05-01");
        Assert.Equal([first.Id, second.Id], Build(first, second).Single().AscentIds);
    }

    [Fact]
    public void Month_only_ascent_joins_the_one_overlapping_trip_as_inferred()
    {
        var undated = Month("Spain", "2020-05-01");
        var trip = Build(Day("Spain", "2020-05-20"), undated).Single();
        Assert.Contains(undated.Id, trip.AscentIds);
        Assert.Equal([undated.Id], trip.InferredAscentIds);
    }

    [Fact]
    public void Month_only_ascent_with_two_overlapping_trips_gets_its_own_unknown_dates_trip()
    {
        var undated = Month("Spain", "2020-05-01");
        var trips = Build(Day("Spain", "2020-05-01"), Day("Spain", "2020-05-20"), undated);
        var unknown = trips.Single(t => t.Approximate);
        Assert.Equal("Spain 2020-05-01 (dates unknown)", unknown.Key);
        Assert.Equal("Spain · May 2020 (dates unknown)", unknown.Name);
        Assert.Null(unknown.StartDate);
        Assert.Equal(new DateOnly(2020, 5, 1), unknown.SortDate);
        Assert.Equal([undated.Id], unknown.InferredAscentIds);
    }

    [Fact]
    public void Month_only_ascent_in_england_with_no_overlap_joins_nothing() =>
        Assert.Empty(Build(Month("England", "2020-05-01")));

    [Fact]
    public void Year_only_ascents_join_nothing()
    {
        var yearOnly = new TripAscent(++_nextId, "Spain", new DateOnly(2020, 1, 1), DatePrecision.Year);
        Assert.Empty(Build(yearOnly));
    }

    [Fact]
    public void Merge_combines_trips_in_its_countries_and_dates()
    {
        var merge = new TripMerge("Asia", ["Thailand", "China"], new DateOnly(2013, 7, 1), new DateOnly(2013, 9, 30), "Notes");
        var a = Day("Thailand", "2013-07-05");
        var b = Day("China", "2013-08-05");
        var undated = Month("China", "2013-09-01");
        var outside = Day("Thailand", "2014-01-01");

        var trips = TripBuilder.Build([a, b, undated, outside], [merge]);

        Assert.Equal(2, trips.Count);
        var merged = trips.Single(t => t.Manual);
        Assert.Equal("manual: Asia", merged.Key);
        Assert.Equal("Asia", merged.Name);
        Assert.Equal("Notes", merged.Notes);
        Assert.Equal(new DateOnly(2013, 7, 5), merged.StartDate);
        Assert.Equal(new DateOnly(2013, 8, 5), merged.EndDate);
        Assert.True(merged.Abroad);
        Assert.True(merged.DatesPartlyUnknown);
        Assert.Equal(3, merged.AscentIds.Count);
        Assert.Equal([undated.Id], merged.InferredAscentIds);
    }

    [Fact]
    public void Merge_with_one_matching_trip_changes_nothing()
    {
        var merge = new TripMerge("Asia", ["Thailand", "China"], new DateOnly(2013, 7, 1), new DateOnly(2013, 9, 30), null);
        var trip = TripBuilder.Build([Day("Thailand", "2013-07-05")], [merge]).Single();
        Assert.False(trip.Manual);
    }
}
