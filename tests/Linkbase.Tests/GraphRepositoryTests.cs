using System.Text.Json.Nodes;
using Linkbase.Data;
using Linkbase.Domains.Climbing.Data;
using Linkbase.Core.Graph;
using Linkbase.Core.Explore;
using Microsoft.EntityFrameworkCore;

namespace Linkbase.Tests;

// The graph over climbing records in the generic store. Each test builds what it needs in a database of its own.
[Trait("Category", "Database")]
public class GraphRepositoryTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    private static readonly Linkbase.Core.Explore.GraphStyles Styles = new(Linkbase.Domains.RegisteredDomains.Registry);
    private static string Unique(string name) => $"{name} {Guid.NewGuid():N}";

    private sealed record Scenario(
        ModelFactory Store, string Country, string Crag, string Route, string Partner, string AscentId, string RouteId, string CragId, string AreaId, string PartnerId);

    private static GraphRepository Graph(Scenario s) => new(s.Store, Linkbase.Domains.RegisteredDomains.Registry, Linkbase.Core.Security.FieldPolicy.Unrestricted);

    private async Task<Scenario> CreateAsync(string date = "2020-05-02", string discipline = "trad")
    {
        var store = await ClimbingStore.CreateAsync(fixture);
        var (country, crag, route, partner) = (Unique("Country"), Unique("Crag"), Unique("Route"), Unique("Partner"));
        var area = await ClimbingStore.AddCountryAsync(store, country);
        var cragId = await ClimbingStore.AddAsync(store, "Crag", crag, null, "IN", area);
        var routeId = await ClimbingStore.AddAsync(
            store, "Route", route, new JsonObject { ["grade"] = "VS 4c", ["grade_system"] = "uk_trad", ["discipline"] = discipline }, "AT", cragId);
        var partnerId = await ClimbingStore.AddAsync(store, "Partner", partner);
        var ascent = await ClimbingStore.AddAsync(store, "Ascent", route, Ascent(date, "onsight", "A private note"), "OF", routeId);
        await ClimbingStore.LinkAsync(store, ascent, "WITH", partnerId);
        return new Scenario(store, country, crag, route, partner, ascent, routeId, cragId, area, partnerId);
    }

    private static JsonObject Ascent(string date, string style, string? notes = null)
    {
        var props = new JsonObject { ["date"] = date, ["date_precision"] = "day", ["style"] = style, ["source"] = "manual" };
        if (notes is not null)
        {
            props["notes"] = notes;
        }

        return props;
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

        var graph = await Graph(s).SubgraphAsync([s.AscentId, s.RouteId, s.CragId, s.AreaId, s.PartnerId]);

        var byId = graph.Nodes.ToDictionary(n => n.Id);
        var ascent = byId[s.AscentId];
        Assert.Equal("Ascent", ascent.Label);
        Assert.Equal(s.Route, ascent.Name);
        Assert.Equal("2020-05-02", ascent.Property("date"));
        Assert.Equal("onsight", ascent.Property("style"));
        Assert.Equal("A private note", ascent.Property("notes"));

        Assert.Equal("VS 4c", byId[s.RouteId].Property("grade"));
        Assert.Equal("Crag", byId[s.CragId].Label);
        Assert.Equal("Area", byId[s.AreaId].Label);
        Assert.Equal(s.Partner, byId[s.PartnerId].Name);
    }

    [Fact]
    public async Task Subgraph_returns_every_relationship_between_the_nodes()
    {
        var s = await CreateAsync();

        var graph = await Graph(s).SubgraphAsync([s.AscentId, s.RouteId, s.CragId, s.AreaId, s.PartnerId]);

        Assert.Contains(new GraphEdge(s.AscentId, s.RouteId, "OF"), graph.Edges);
        Assert.Contains(new GraphEdge(s.RouteId, s.CragId, "AT"), graph.Edges);
        Assert.Contains(new GraphEdge(s.CragId, s.AreaId, "IN"), graph.Edges);
        Assert.Contains(new GraphEdge(s.AscentId, s.PartnerId, "WITH"), graph.Edges);
    }

    [Fact]
    public async Task Trips_link_to_ascents_and_the_countries_they_visited()
    {
        var s = await CreateAsync();
        await new GenericTripService(s.Store).RebuildAsync([]);
        await using var db = s.Store.CreateDbContext();
        var tripId = await db.NodeLinks.AsNoTracking().Where(l => l.SourceId == s.AscentId && l.RelType == "ON_TRIP").Select(l => l.TargetId).SingleAsync();

        var neighbours = await Graph(s).NeighboursAsync(tripId, NoFilter, [], 10);

        Assert.Contains(neighbours.Nodes, n => n.Id == s.AscentId);
        Assert.Contains(neighbours.Nodes, n => n.Id == s.AreaId);
    }

    [Fact]
    public async Task Neighbours_follow_relationships_in_both_directions()
    {
        var s = await CreateAsync();

        var neighbours = await Graph(s).NeighboursAsync(s.RouteId, NoFilter, [], 10);

        Assert.Contains(neighbours.Nodes, n => n.Id == s.CragId);
        Assert.Contains(neighbours.Nodes, n => n.Id == s.AscentId);
        Assert.Equal(0, neighbours.Remaining);
    }

    [Fact]
    public async Task Neighbours_can_be_limited_to_relationship_types()
    {
        var s = await CreateAsync();

        var neighbours = await Graph(s).NeighboursAsync(s.AscentId, NoFilter with { RelTypes = ["WITH"] }, [], 10);

        Assert.Equal(s.PartnerId, neighbours.Nodes.Single().Id);
    }

    [Fact]
    public async Task Property_filters_only_narrow_types_that_have_the_property()
    {
        var s = await CreateAsync(date: "2020-05-02", discipline: "sport");

        var outOfRange = await Graph(s).NeighboursAsync(s.RouteId, NoFilter with { Properties = [new PatternFilter("date", FilterOperator.GreaterOrEqual, "2021-01-01")] }, [], 10);
        Assert.DoesNotContain(outOfRange.Nodes, n => n.Label == "Ascent");
        Assert.Contains(outOfRange.Nodes, n => n.Label == "Crag");

        var inRange = await Graph(s).NeighboursAsync(
            s.RouteId,
            NoFilter with { Properties = [new PatternFilter("date", FilterOperator.GreaterOrEqual, "2020-01-01"), new PatternFilter("date", FilterOperator.LessOrEqual, "2020-12-31")] },
            [],
            10);
        Assert.Contains(inRange.Nodes, n => n.Label == "Ascent");

        var wrongDiscipline = await Graph(s).NeighboursAsync(s.CragId, NoFilter with { Properties = [new PatternFilter("discipline", FilterOperator.Equals, "boulder")] }, [], 10);
        Assert.DoesNotContain(wrongDiscipline.Nodes, n => n.Label == "Route");
        Assert.Contains(wrongDiscipline.Nodes, n => n.Label == "Area");

        var rightDiscipline = await Graph(s).NeighboursAsync(s.CragId, NoFilter with { Properties = [new PatternFilter("discipline", FilterOperator.Equals, "sport")] }, [], 10);
        Assert.Contains(rightDiscipline.Nodes, n => n.Label == "Route");
    }

    [Fact]
    public async Task Property_filters_reject_unknown_properties_and_bad_values()
    {
        var s = await CreateAsync();

        await Assert.ThrowsAsync<LinkbaseValidationException>(() => Graph(s).NeighboursAsync(s.RouteId, NoFilter with { Properties = [new PatternFilter("nope'; --", FilterOperator.Equals, "x")] }, [], 10));
        await Assert.ThrowsAsync<LinkbaseValidationException>(() => Graph(s).NeighboursAsync(s.RouteId, NoFilter with { Properties = [new PatternFilter("date", FilterOperator.Equals, "soon")] }, [], 10));
    }

    [Fact]
    public async Task Limit_reports_what_remains_and_excluding_shown_nodes_gets_the_next_batch()
    {
        var s = await CreateAsync();
        for (var i = 0; i < 4; i++)
        {
            await ClimbingStore.AddAsync(s.Store, "Ascent", s.Route, Ascent($"2021-01-0{1 + i}", "lead"), "OF", s.RouteId);
        }

        var first = await Graph(s).NeighboursAsync(s.RouteId, NoFilter, [], 3);
        Assert.Equal(3, first.Nodes.Count);
        Assert.Equal(3, first.Remaining);

        var second = await Graph(s).NeighboursAsync(s.RouteId, NoFilter, first.Nodes.Select(n => n.Id).ToList(), 3);
        Assert.Equal(3, second.Nodes.Count);
        Assert.Equal(0, second.Remaining);
        Assert.Empty(first.Nodes.Select(n => n.Id).Intersect(second.Nodes.Select(n => n.Id)));
    }

    [Fact]
    public async Task Search_finds_by_part_of_a_name_or_similar_spelling_and_filters_by_label()
    {
        var store = await ClimbingStore.CreateAsync(fixture);
        var suffix = Guid.NewGuid().ToString("N")[..12];
        var area = await ClimbingStore.AddCountryAsync(store, Unique("Country"));
        var crag = await ClimbingStore.AddAsync(store, "Crag", $"Crag {suffix}", null, "IN", area);
        var route = await ClimbingStore.AddAsync(store, "Route", $"Stanage {suffix}", null, "AT", crag);
        var ascent = await ClimbingStore.AddAsync(store, "Ascent", $"Stanage {suffix}", Ascent("2020-01-01", "lead"), "OF", route);
        var graph = new GraphRepository(store, Linkbase.Domains.RegisteredDomains.Registry, Linkbase.Core.Security.FieldPolicy.Unrestricted);

        var all = await graph.SearchNodesAsync(suffix, null);
        Assert.Contains(all, n => n.Label == "Route");
        Assert.Contains(all, n => n.Label == "Crag");
        Assert.Contains(all, n => n.Id == ascent);

        var routesOnly = await graph.SearchNodesAsync(suffix, ["Route"]);
        Assert.All(routesOnly, n => Assert.Equal("Route", n.Label));
        Assert.NotEmpty(routesOnly);

        Assert.Contains(await graph.SearchNodesAsync($"Stannage {suffix}", ["Route"]), n => n.Name == $"Stanage {suffix}");
        Assert.Empty(await graph.SearchNodesAsync("  ", null));
    }

    [Fact]
    public void Route_captions_include_the_grade_and_ascents_show_date_and_style()
    {
        var route = new GraphNode("route:1", "Route", "Slab", [new GraphProperty("grade", "VS 4c")]);
        Assert.Equal("Slab VS 4c", Styles.Caption(route));
        Assert.Equal("Slab", Styles.Caption(new GraphNode("route:2", "Route", "Slab", [])));

        var ascent = new GraphNode("ascent:1", "Ascent", "Slab", [
            new GraphProperty("date", "2012-04-01"),
            new GraphProperty("date_precision", "month"),
            new GraphProperty("style", "top_rope"),
        ]);
        Assert.Equal("Apr 2012 top rope", Styles.Caption(ascent));
    }

    [Fact]
    public void Links_point_at_the_right_page()
    {
        Assert.Equal("/data/Ascent/7", Styles.Link(new GraphNode("ascent:7", "Ascent", "X", [])));
        Assert.Equal("/data/Partner/7", Styles.Link(new GraphNode("partner:7", "Partner", "X", [])));
        Assert.Equal("/data/Trip/7", Styles.Link(new GraphNode("trip:7", "Trip", "X", [])));
    }
}
