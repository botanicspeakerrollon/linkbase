using Linkbase.Data;
using Linkbase.Domains.Climbing.Data;
using Linkbase.Core.Graph;
using Linkbase.Domains.Climbing.Import;

namespace Linkbase.Tests;

public class Neo4jComparisonTests
{
    private static Neo4jAscent Graph(string? notes = null, string[]? partners = null, string? style = "Lead") => new(
        "ukc-1", "UKC", style, null, null, notes, "20/Aug/26", "Slab", "Crag", "VS 4c", null, null, partners ?? [], []);

    private static ComparisonAscent App(string? notes = null, string[]? partners = null, string? style = "Lead") => new(
        DataSource.Ukc, "Crag", "Slab", new DateOnly(2026, 8, 20), DatePrecision.Day, style, notes, "VS 4c", null, partners ?? [], null, false);

    private static ComparisonResult Compare(Neo4jAscent[] graph, ComparisonAscent[] app) =>
        Neo4jComparison.Compare(new Neo4jExport(graph, []), app, []);

    [Fact]
    public void Identical_rows_have_no_differences()
    {
        var result = Compare([Graph(partners: ["B", "A"])], [App(partners: ["A", "B"])]);
        Assert.Equal(1, result.AscentsIdentical);
        Assert.Empty(result.Differences);
    }

    [Fact]
    public void A_dash_style_matches_no_style()
    {
        var result = Compare([Graph(style: null)], [App(style: "-")]);
        Assert.Empty(result.Differences);
    }

    [Fact]
    public void Reports_an_edited_note()
    {
        var result = Compare([Graph(notes: "Edited")], [App(notes: "Original")]);
        Assert.Equal(0, result.AscentsIdentical);
        Assert.Contains(result.Differences, d => d.Contains("notes differ"));
    }

    [Fact]
    public void Reports_an_ascent_on_only_one_side() =>
        Assert.Contains(Compare([Graph()], []).Differences, d => d.Contains("1 in the graph, 0 in the import"));

    [Fact]
    public void Reports_a_trip_whose_visited_countries_differ_from_its_ascents()
    {
        var export = new Neo4jExport([], [new Neo4jTrip("T", "Trip", null, null, ["Spain"])]);
        var result = Neo4jComparison.Compare(export, [], [new ComparisonTrip("T", "Trip", null, null, ["France"])]);
        Assert.Contains(result.Differences, d => d.Contains("countries differ"));
    }
}
