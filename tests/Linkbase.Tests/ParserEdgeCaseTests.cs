using Linkbase.Core.Extraction;
using Linkbase.Core;
using System.Net;
using System.Text;
using System.Text.Json;
using Linkbase.Data;
using Linkbase.Domains.Climbing.Data;
using Linkbase.Core.Graph;
using Linkbase.Domains.Climbing.Features;
using Linkbase.Core.Explore;
using Linkbase.Domains.Climbing.Import;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace Linkbase.Tests;

public class ParserEdgeCaseTests
{
    private const string UkcHeader = "Climb name,Grade,Style,Partner(s),Notes,Date,Crag name,Grade Type,Grade System,Country\n";
    private const string MpHeader = "Date,Route,Rating,Notes,URL,Pitches,Location,\"Avg Stars\",\"Your Stars\",Style,\"Lead Style\",\"Route Type\",\"Your Rating\",Length,\"Rating Code\"\n";

    private static ParsedAscent Ukc(string row) => UkcParser.Parse(new StringReader(UkcHeader + row + "\n")).Single();

    private static ParsedAscent Mp(string row) => MountainProjectParser.Parse(new StringReader(MpHeader + row + "\n")).Single();

    [Fact]
    public void Blank_styles_and_grades_are_empty() =>
        Assert.Multiple(
            () => Assert.Null(Ukc("R,,,,,01/Jan/20,C,Trad,British,Wales").Grade),
            () => Assert.Equal(AscentStyle.Other, UkcParser.MapStyle(null)),
            () => Assert.Equal(AscentStyle.Other, UkcParser.MapStyle("Something")));

    [Theory]
    [InlineData("French", GradeSystem.French)]
    [InlineData("USA", GradeSystem.Yds)]
    [InlineData("UIAA", GradeSystem.Uiaa)]
    [InlineData("Australian", GradeSystem.Australian)]
    [InlineData("Nordic", GradeSystem.Nordic)]
    [InlineData("Alpine", GradeSystem.Alpine)]
    [InlineData("Scrambling", GradeSystem.Scrambling)]
    [InlineData("Via Ferrata", GradeSystem.ViaFerrata)]
    [InlineData("Ice", GradeSystem.Ice)]
    [InlineData("Summits", GradeSystem.Other)]
    public void Maps_every_grade_system(string name, GradeSystem expected) => Assert.Equal(expected, UkcParser.MapGradeSystem(name));

    [Theory]
    [InlineData("Trad", Discipline.Trad)]
    [InlineData("Sport", Discipline.Sport)]
    [InlineData("Bouldering", Discipline.Boulder)]
    [InlineData("Alpine", Discipline.Alpine)]
    [InlineData("Scrambling", Discipline.Other)]
    public void Maps_every_discipline(string name, Discipline expected) => Assert.Equal(expected, UkcParser.MapDiscipline(name));

    [Theory]
    [InlineData("Sent β", AscentStyle.Flash)]
    [InlineData("AltLd RP", AscentStyle.Redpoint)]
    public void Maps_remaining_styles(string raw, AscentStyle expected) => Assert.Equal(expected, UkcParser.MapStyle(raw));

    [Fact]
    public void A_bad_mountain_project_url_is_rejected() =>
        Assert.Throws<FormatException>(() => Mp("2023-01-01,R,V1,,https://example.com/x,1,\"Utah > Crag\",1,-1,Send,,Boulder,,,1"));

    [Theory]
    [InlineData("Sport", Discipline.Sport)]
    [InlineData("Alpine", Discipline.Alpine)]
    [InlineData("Ice", Discipline.Winter)]
    [InlineData("Mixed", Discipline.Winter)]
    [InlineData("TR", Discipline.Other)]
    public void Maps_every_route_type(string type, Discipline expected) =>
        Assert.Equal(expected, Mp($"2023-01-01,R,5.9,,https://www.mountainproject.com/route/1/r,1,\"Utah > Crag\",1,-1,Lead,,{type},,,1").Discipline);

    [Fact]
    public void An_unknown_mountain_project_style_is_other() =>
        Assert.Equal(AscentStyle.Other, MountainProjectParser.MapStyle("Something", ""));

    [Fact]
    public void A_rating_with_no_grade_has_none()
    {
        Assert.Null(MountainProjectParser.ChooseGrade("Easy", false));
        var row = Mp("2023-01-01,R,Easy,,https://www.mountainproject.com/route/1/r,x,\"Utah > Crag\",1,-1,Lead,,Trad,,,1");
        Assert.Null(row.GradeSystem);
        Assert.Null(row.Pitches);
    }

    [Fact]
    public void An_empty_csv_has_no_rows() => Assert.Empty(UkcParser.Parse(new StringReader(UkcHeader)));

    [Fact]
    public void Corrections_load_from_a_file_and_reject_an_empty_one()
    {
        var path = Path.GetTempFileName();
        try
        {
            File.WriteAllText(path, """{ "partnerRenames": [], "mountainProjectPartners": [], "tripMerges": [{ "name": "A", "countries": ["X"], "from": "2020-01-01", "to": "2020-02-01", "notes": null }] }""");
            Assert.Equal("A", Corrections.Load(path).TripMerges.Single().Name);

            File.WriteAllText(path, "null");
            Assert.Throws<FormatException>(() => Corrections.Load(path));
        }
        finally
        {
            File.Delete(path);
        }
    }
}
