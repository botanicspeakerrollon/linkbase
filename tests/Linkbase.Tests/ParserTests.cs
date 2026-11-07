using Linkbase.Data;
using Linkbase.Domains.Climbing.Data;
using Linkbase.Core.Graph;
using Linkbase.Domains.Climbing.Import;

namespace Linkbase.Tests;

public class UkcParserTests
{
    private static readonly List<ParsedAscent> Rows = Load();

    private static List<ParsedAscent> Load()
    {
        using var reader = new StreamReader(Path.Combine("Fixtures", "ukc-sample.csv"));
        return UkcParser.Parse(reader);
    }

    [Fact]
    public void Reads_every_row_including_multiline_notes() => Assert.Equal(12, Rows.Count);

    [Fact]
    public void Removes_stars_and_extra_spaces_from_grades()
    {
        Assert.Equal("VS 4c", Rows[0].Grade);
        Assert.Equal("HS", Rows[2].Grade);
    }

    [Fact]
    public void Trims_names_and_turns_blank_notes_into_null()
    {
        Assert.Equal("Spaced Climb", Rows[2].RouteName);
        Assert.Equal("Test Crag", Rows[2].CragName);
        Assert.Null(Rows[2].Notes);
    }

    [Fact]
    public void Parses_all_three_date_shapes()
    {
        Assert.Equal((new DateOnly(2026, 8, 20), DatePrecision.Day), (Rows[0].Date, Rows[0].DatePrecision));
        Assert.Equal((new DateOnly(2012, 4, 1), DatePrecision.Month), (Rows[2].Date, Rows[2].DatePrecision));
        Assert.Equal((new DateOnly(2014, 1, 1), DatePrecision.Year), (Rows[3].Date, Rows[3].DatePrecision));
    }

    [Fact]
    public void Rejects_an_unknown_date_format() =>
        Assert.Throws<FormatException>(() => UkcParser.ParseDate("Aug 2020"));

    [Theory]
    [InlineData("Lead O/S", AscentStyle.Onsight)]
    [InlineData("AltLd O/S", AscentStyle.Onsight)]
    [InlineData("Sent O/S", AscentStyle.Onsight)]
    [InlineData("Lead RP", AscentStyle.Redpoint)]
    [InlineData("Lead β", AscentStyle.Flash)]
    [InlineData("Lead", AscentStyle.Lead)]
    [InlineData("Lead rpt", AscentStyle.Lead)]
    [InlineData("Lead G/U", AscentStyle.Lead)]
    [InlineData("Lead dog", AscentStyle.Hangdog)]
    [InlineData("AltLd dog", AscentStyle.Hangdog)]
    [InlineData("Lead dnf", AscentStyle.Dnf)]
    [InlineData("Sent dnf", AscentStyle.Dnf)]
    [InlineData("Sent", AscentStyle.Send)]
    [InlineData("Sent x", AscentStyle.Send)]
    [InlineData("2nd O/S", AscentStyle.Second)]
    [InlineData("TR dog", AscentStyle.TopRope)]
    [InlineData("Solo O/S", AscentStyle.Solo)]
    [InlineData("DWS", AscentStyle.Solo)]
    [InlineData("-", AscentStyle.Other)]
    public void Maps_styles(string raw, AscentStyle expected) => Assert.Equal(expected, UkcParser.MapStyle(raw));

    [Fact]
    public void Decodes_entities_in_the_raw_style()
    {
        Assert.Equal("2nd β", Rows[2].StyleRaw);
        Assert.Equal(AscentStyle.Second, Rows[2].Style);
    }

    [Fact]
    public void Treats_v_grades_rows_as_font() => Assert.Equal(GradeSystem.Font, Rows[4].GradeSystem);

    [Fact]
    public void Splits_partners_on_commas_and_and() =>
        Assert.Equal(["Ann", "Bob", "Cy"], Rows[1].Partners);

    [Fact]
    public void Identical_rows_get_different_ascent_ids_but_the_same_route_id()
    {
        var soloed = Rows.Where(r => r.RouteName == "Twice Soloed").ToList();
        Assert.Equal(2, soloed.Count);
        Assert.NotEqual(soloed[0].AscentExternalId, soloed[1].AscentExternalId);
        Assert.Equal(soloed[0].RouteExternalId, soloed[1].RouteExternalId);
    }

    [Fact]
    public void Maps_ice_to_winter() => Assert.Equal(Discipline.Winter, Rows[^1].Discipline);
}

public class MountainProjectParserTests
{
    private static readonly List<ParsedAscent> Rows = Load();

    private static List<ParsedAscent> Load()
    {
        using var reader = new StreamReader(Path.Combine("Fixtures", "mp-sample.csv"));
        return MountainProjectParser.Parse(reader);
    }

    [Fact]
    public void Adds_usa_above_the_state_and_takes_the_last_level_as_the_crag()
    {
        Assert.Equal(["USA", "California", "Test Range", "Test Area"], Rows[0].AreaPath);
        Assert.Equal("Test Rock", Rows[0].CragName);
    }

    [Fact]
    public void Uses_the_route_id_from_the_url() => Assert.Equal("111", Rows[0].RouteExternalId);

    [Fact]
    public void Picks_the_v_grade_only_for_a_bouldering_tick()
    {
        Assert.Equal("V5 R", Rows[1].Grade);
        Assert.Equal(GradeSystem.V, Rows[1].GradeSystem);
        Assert.Equal(Discipline.Boulder, Rows[1].Discipline);

        Assert.Equal("5.12a R", Rows[2].Grade);
        Assert.Equal(GradeSystem.Yds, Rows[2].GradeSystem);
        Assert.Equal(Discipline.Trad, Rows[2].Discipline);
    }

    [Fact]
    public void Converts_feet_to_metres()
    {
        Assert.Equal(247, Rows[3].LengthM);
        Assert.Null(Rows[5].LengthM);
    }

    [Fact]
    public void Gives_a_route_the_most_pitches_seen()
    {
        Assert.Equal(5, Rows[3].Pitches);
        Assert.Equal(5, Rows[4].Pitches);
    }

    [Fact]
    public void Decodes_notes() => Assert.Equal("Couldn't do it", Rows[1].Notes);

    [Fact]
    public void Same_route_on_one_date_gets_different_ids() =>
        Assert.NotEqual(Rows[3].AscentExternalId, Rows[4].AscentExternalId);

    [Theory]
    [InlineData("Send", "", AscentStyle.Send)]
    [InlineData("Flash", "", AscentStyle.Flash)]
    [InlineData("Attempt", "", AscentStyle.Dnf)]
    [InlineData("Follow", "", AscentStyle.Second)]
    [InlineData("TR", "", AscentStyle.TopRope)]
    [InlineData("Solo", "", AscentStyle.Solo)]
    [InlineData("Lead", "Onsight", AscentStyle.Onsight)]
    [InlineData("Lead", "Flash", AscentStyle.Flash)]
    [InlineData("Lead", "Redpoint", AscentStyle.Redpoint)]
    [InlineData("Lead", "Pinkpoint", AscentStyle.Redpoint)]
    [InlineData("Lead", "Fell/Hung", AscentStyle.Hangdog)]
    [InlineData("Lead", "", AscentStyle.Lead)]
    public void Maps_styles(string style, string leadStyle, AscentStyle expected) =>
        Assert.Equal(expected, MountainProjectParser.MapStyle(style, leadStyle));

    [Fact]
    public void Keeps_the_raw_style()
    {
        Assert.Equal("Attempt", Rows[0].StyleRaw);
        Assert.Equal("Lead / Fell/Hung", Rows[2].StyleRaw);
    }
}

public class CorrectionsTests
{
    private static readonly Corrections Rules = new(
        [new PartnerRename("Sam", "Sam K")],
        [new MountainProjectPartners(["Sam K"], []), new MountainProjectPartners(["Ann"], ["Test Canyon"])],
        []);

    private static ParsedAscent Row(DataSource source, string[] path, string[] partners) => new()
    {
        Source = source,
        AreaPath = path,
        CragName = "Crag",
        RouteName = "Route",
        RouteExternalId = "r",
        AscentExternalId = "a",
        Date = new DateOnly(2023, 1, 1),
        DatePrecision = DatePrecision.Day,
        Style = AscentStyle.Lead,
        Partners = partners,
    };

    [Fact]
    public void Renames_partners_exactly()
    {
        var result = Rules.Apply(Row(DataSource.Ukc, ["Wales"], ["Sam", "Sammy"]));
        Assert.Equal(["Sam K", "Sammy"], result.Partners);
    }

    [Fact]
    public void Adds_partners_to_mountain_project_ticks_by_area()
    {
        Assert.Equal(["Sam K"], Rules.Apply(Row(DataSource.MountainProject, ["USA", "Utah"], [])).Partners);
        Assert.Equal(["Sam K", "Ann"], Rules.Apply(Row(DataSource.MountainProject, ["USA", "Utah", "Test Canyon"], [])).Partners);
    }

    [Fact]
    public void Leaves_ukc_rows_without_added_partners() =>
        Assert.Empty(Rules.Apply(Row(DataSource.Ukc, ["Test Canyon"], [])).Partners);
}
