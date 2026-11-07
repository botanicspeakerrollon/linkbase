using Linkbase.Data;
using Linkbase.Domains.Climbing.Data;
using Linkbase.Core.Graph;
using Linkbase.Core.Explore;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace Linkbase.Tests;

[Trait("Category", "Database")]
[Collection(PostgresCollection.Name)]
public class GraphRepositoryTests(PostgresFixture fixture)
{
    private static string Unique(string name) => $"{name} {Guid.NewGuid():N}";

    private GraphRepository Graph() => new(fixture.ContextFactory());

    private AscentRepository Ascents()
    {
        var refresher = new TripRefresher(new TripService(fixture.ContextFactory()), () => "no-corrections-in-tests.json", NullLogger<TripRefresher>.Instance);
        return new AscentRepository(fixture.ContextFactory(), new AreaRepository(fixture.ContextFactory()), refresher);
    }

    private sealed record Scenario(string Country, string Crag, string Route, string Partner, long AscentId, long RouteId, long CragId, long AreaId, long PartnerId);

    private async Task<Scenario> CreateAsync(string date = "2020-05-02", Discipline discipline = Discipline.Trad)
    {
        var (country, crag, route, partner) = (Unique("Country"), Unique("Crag"), Unique("Route"), Unique("Partner"));
        var id = await Ascents().CreateAsync(new AscentInput
        {
            NewRoute = new NewRouteInput(route, "VS 4c", GradeSystem.UkTrad, discipline, 20, 1, null, new NewCragInput(crag, null, country)),
            Date = DateOnly.Parse(date),
            DatePrecision = DatePrecision.Day,
            Style = AscentStyle.Onsight,
            Partners = [partner],
            Notes = "A private note",
        });

        await using var db = fixture.CreateContext();
        var row = await db.Ascents.AsNoTracking().Where(a => a.Id == id)
            .Select(a => new { a.RouteId, a.Route.CragId, a.Route.Crag.AreaId, PartnerId = a.Partners.Select(p => p.PartnerId).Single() })
            .SingleAsync();
        return new Scenario(country, crag, route, partner, id, row.RouteId, row.CragId, row.AreaId, row.PartnerId);
    }

    private static readonly GraphFilter NoFilter = new();

    [Fact]
    public async Task Views_build_props_from_named_columns()
    {
        await using var db = fixture.CreateContext();
        var definitions = await db.Database
            .SqlQueryRaw<string>("SELECT definition AS \"Value\" FROM pg_views WHERE viewname IN ('graph_nodes', 'graph_edges')")
            .ToListAsync();
        Assert.Equal(2, definitions.Count);
        Assert.All(definitions, d => Assert.DoesNotContain("to_jsonb", d));
    }

    [Fact]
    public async Task Nodes_carry_a_label_and_their_named_properties()
    {
        var s = await CreateAsync();

        var graph = await Graph().SubgraphAsync([$"ascent:{s.AscentId}", $"route:{s.RouteId}", $"crag:{s.CragId}", $"area:{s.AreaId}", $"partner:{s.PartnerId}"]);

        var byId = graph.Nodes.ToDictionary(n => n.Id);
        var ascent = byId[$"ascent:{s.AscentId}"];
        Assert.Equal("Ascent", ascent.Label);
        Assert.Equal(s.Route, ascent.Name);
        Assert.Equal("2020-05-02", ascent.Property("date"));
        Assert.Equal("onsight", ascent.Property("style"));
        Assert.Equal("A private note", ascent.Property("notes"));
        Assert.Equal(s.AscentId, ascent.EntityId);

        Assert.Equal("VS 4c", byId[$"route:{s.RouteId}"].Property("grade"));
        Assert.Equal("Crag", byId[$"crag:{s.CragId}"].Label);
        Assert.Equal("Country", byId[$"area:{s.AreaId}"].Label);
        Assert.Equal(s.Partner, byId[$"partner:{s.PartnerId}"].Name);
    }

    [Fact]
    public async Task Subgraph_returns_every_relationship_between_the_nodes()
    {
        var s = await CreateAsync();

        var graph = await Graph().SubgraphAsync([$"ascent:{s.AscentId}", $"route:{s.RouteId}", $"crag:{s.CragId}", $"area:{s.AreaId}", $"partner:{s.PartnerId}"]);

        Assert.Contains(new GraphEdge($"ascent:{s.AscentId}", $"route:{s.RouteId}", "OF"), graph.Edges);
        Assert.Contains(new GraphEdge($"route:{s.RouteId}", $"crag:{s.CragId}", "AT"), graph.Edges);
        Assert.Contains(new GraphEdge($"crag:{s.CragId}", $"area:{s.AreaId}", "IN"), graph.Edges);
        Assert.Contains(new GraphEdge($"ascent:{s.AscentId}", $"partner:{s.PartnerId}", "WITH"), graph.Edges);
    }

    [Fact]
    public async Task Trips_link_to_ascents_and_the_countries_they_visited()
    {
        var s = await CreateAsync();
        await new TripService(fixture.ContextFactory()).RebuildAsync([]);
        await using var db = fixture.CreateContext();
        var tripId = await db.Ascents.AsNoTracking().Where(a => a.Id == s.AscentId).Select(a => a.TripId).SingleAsync();

        var neighbours = await Graph().NeighboursAsync($"trip:{tripId}", NoFilter, [], 10);

        Assert.Contains(neighbours.Nodes, n => n.Id == $"ascent:{s.AscentId}");
        Assert.Contains(neighbours.Nodes, n => n.Id == $"area:{s.AreaId}" && n.Label == "Country");
    }

    [Fact]
    public async Task Neighbours_follow_relationships_in_both_directions()
    {
        var s = await CreateAsync();

        var neighbours = await Graph().NeighboursAsync($"route:{s.RouteId}", NoFilter, [], 10);

        Assert.Contains(neighbours.Nodes, n => n.Id == $"crag:{s.CragId}");
        Assert.Contains(neighbours.Nodes, n => n.Id == $"ascent:{s.AscentId}");
        Assert.Equal(0, neighbours.Remaining);
    }

    [Fact]
    public async Task Neighbours_can_be_limited_to_relationship_types()
    {
        var s = await CreateAsync();

        var neighbours = await Graph().NeighboursAsync($"ascent:{s.AscentId}", NoFilter with { RelTypes = ["WITH"] }, [], 10);

        Assert.Equal($"partner:{s.PartnerId}", neighbours.Nodes.Single().Id);
    }

    [Fact]
    public async Task Property_filters_only_narrow_types_that_have_the_property()
    {
        var s = await CreateAsync(date: "2020-05-02", discipline: Discipline.Sport);

        var outOfRange = await Graph().NeighboursAsync($"route:{s.RouteId}", NoFilter with { Properties = [new PatternFilter("date", FilterOperator.GreaterOrEqual, "2021-01-01")] }, [], 10);
        Assert.DoesNotContain(outOfRange.Nodes, n => n.Label == "Ascent");
        Assert.Contains(outOfRange.Nodes, n => n.Label == "Crag");

        var inRange = await Graph().NeighboursAsync(
            $"route:{s.RouteId}",
            NoFilter with { Properties = [new PatternFilter("date", FilterOperator.GreaterOrEqual, "2020-01-01"), new PatternFilter("date", FilterOperator.LessOrEqual, "2020-12-31")] },
            [],
            10);
        Assert.Contains(inRange.Nodes, n => n.Label == "Ascent");

        var wrongDiscipline = await Graph().NeighboursAsync($"crag:{s.CragId}", NoFilter with { Properties = [new PatternFilter("discipline", FilterOperator.Equals, "boulder")] }, [], 10);
        Assert.DoesNotContain(wrongDiscipline.Nodes, n => n.Label == "Route");
        Assert.Contains(wrongDiscipline.Nodes, n => n.Label == "Country");

        var rightDiscipline = await Graph().NeighboursAsync($"crag:{s.CragId}", NoFilter with { Properties = [new PatternFilter("discipline", FilterOperator.Equals, "sport")] }, [], 10);
        Assert.Contains(rightDiscipline.Nodes, n => n.Label == "Route");
    }

    [Fact]
    public async Task Property_filters_reject_unknown_properties_and_bad_values()
    {
        var s = await CreateAsync();

        await Assert.ThrowsAsync<LinkbaseValidationException>(() => Graph().NeighboursAsync($"route:{s.RouteId}", NoFilter with { Properties = [new PatternFilter("nope'; --", FilterOperator.Equals, "x")] }, [], 10));
        await Assert.ThrowsAsync<LinkbaseValidationException>(() => Graph().NeighboursAsync($"route:{s.RouteId}", NoFilter with { Properties = [new PatternFilter("date", FilterOperator.Equals, "soon")] }, [], 10));
    }

    [Fact]
    public async Task Limit_reports_what_remains_and_excluding_shown_nodes_gets_the_next_batch()
    {
        var s = await CreateAsync();
        for (var i = 0; i < 4; i++)
        {
            await Ascents().CreateAsync(new AscentInput
            {
                RouteId = s.RouteId,
                Date = new DateOnly(2021, 1, 1 + i),
                DatePrecision = DatePrecision.Day,
                Style = AscentStyle.Lead,
            });
        }

        var first = await Graph().NeighboursAsync($"route:{s.RouteId}", NoFilter, [], 3);
        Assert.Equal(3, first.Nodes.Count);
        Assert.Equal(3, first.Remaining);

        var second = await Graph().NeighboursAsync($"route:{s.RouteId}", NoFilter, first.Nodes.Select(n => n.Id).ToList(), 3);
        Assert.Equal(3, second.Nodes.Count);
        Assert.Equal(0, second.Remaining);
        Assert.Empty(first.Nodes.Select(n => n.Id).Intersect(second.Nodes.Select(n => n.Id)));
    }

    [Fact]
    public async Task Search_finds_by_part_of_a_name_or_similar_spelling_and_filters_by_label()
    {
        var suffix = Guid.NewGuid().ToString("N")[..12];
        var id = await Ascents().CreateAsync(new AscentInput
        {
            NewRoute = new NewRouteInput($"Stanage {suffix}", null, null, null, null, null, null, new NewCragInput($"Crag {suffix}", null, Unique("Country"))),
            Date = new DateOnly(2020, 1, 1),
            DatePrecision = DatePrecision.Day,
            Style = AscentStyle.Lead,
        });

        var all = await Graph().SearchNodesAsync(suffix, null);
        Assert.Contains(all, n => n.Label == "Route");
        Assert.Contains(all, n => n.Label == "Crag");
        Assert.Contains(all, n => n.Id == $"ascent:{id}");

        var routesOnly = await Graph().SearchNodesAsync(suffix, ["Route"]);
        Assert.All(routesOnly, n => Assert.Equal("Route", n.Label));
        Assert.NotEmpty(routesOnly);

        Assert.Contains(await Graph().SearchNodesAsync($"Stannage {suffix}", ["Route"]), n => n.Name == $"Stanage {suffix}");
        Assert.Empty(await Graph().SearchNodesAsync("  ", null));
    }

    [Fact]
    public async Task Scenes_save_replace_load_and_delete()
    {
        var name = Unique("Scene");
        await Graph().SaveSceneAsync(name, [new ScenePosition("crag:1", 1.5, 2.5)]);
        await Graph().SaveSceneAsync(name, [new ScenePosition("crag:2", 3, 4), new ScenePosition("crag:3", 5, 6)]);

        Assert.Contains(name, await Graph().SceneNamesAsync());
        var scene = await Graph().LoadSceneAsync(name);
        Assert.Equal(2, scene!.Positions.Count);
        Assert.Equal(new ScenePosition("crag:2", 3, 4), scene.Positions[0]);

        await Graph().DeleteSceneAsync(name);
        Assert.Null(await Graph().LoadSceneAsync(name));
        await Assert.ThrowsAsync<LinkbaseValidationException>(() => Graph().SaveSceneAsync(" ", []));
    }

    [Fact]
    public void Route_captions_include_the_grade_and_ascents_show_date_and_style()
    {
        var route = new GraphNode("route:1", "Route", "Slab", [new GraphProperty("grade", "VS 4c")]);
        Assert.Equal("Slab VS 4c", GraphStyle.Caption(route));
        Assert.Equal("Slab", GraphStyle.Caption(new GraphNode("route:2", "Route", "Slab", [])));

        var ascent = new GraphNode("ascent:1", "Ascent", "Slab", [
            new GraphProperty("date", "2012-04-01"),
            new GraphProperty("date_precision", "month"),
            new GraphProperty("style", "top_rope"),
        ]);
        Assert.Equal("Apr 2012 top rope", GraphStyle.Caption(ascent));
    }

    [Fact]
    public void Links_point_at_the_right_page()
    {
        Assert.Equal("/browse/area/7", GraphStyle.Link(new GraphNode("area:7", "Country", "X", [])));
        Assert.Equal("/browse/crag/7", GraphStyle.Link(new GraphNode("crag:7", "Crag", "X", [])));
        Assert.Equal("/browse/route/7", GraphStyle.Link(new GraphNode("route:7", "Route", "X", [])));
        Assert.Equal("/ascents/7", GraphStyle.Link(new GraphNode("ascent:7", "Ascent", "X", [])));
        Assert.Null(GraphStyle.Link(new GraphNode("partner:7", "Partner", "X", [])));
        Assert.Null(GraphStyle.Link(new GraphNode("trip:7", "Trip", "X", [])));
    }
}
