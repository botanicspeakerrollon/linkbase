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

public class GraphStyleTests
{
    [Fact]
    public void Captions_cover_every_kind_of_node()
    {
        Assert.Equal("Trip name", GraphStyle.Caption(new GraphNode("trip:1", "Trip", "Trip name", [])));
        Assert.Equal("5 Apr 2012 lead", GraphStyle.Caption(new GraphNode("ascent:1", "Ascent", "R", [new GraphProperty("date", "2012-04-05"), new GraphProperty("style", "lead")])));
        Assert.Equal("2012", GraphStyle.Caption(new GraphNode("ascent:2", "Ascent", "R", [new GraphProperty("date", "2012-01-01"), new GraphProperty("date_precision", "year")])));
        Assert.Equal("lead", GraphStyle.Caption(new GraphNode("ascent:3", "Ascent", "R", [new GraphProperty("date", "soon"), new GraphProperty("style", "lead")])));
    }

    [Fact]
    public void Countries_are_areas_drawn_in_their_own_style()
    {
        var country = new GraphNode("area:1", "Area", "France", [new GraphProperty("kind", "country")]);
        var region = new GraphNode("area:2", "Area", "Jura", [new GraphProperty("kind", "region")]);

        Assert.NotEqual(GraphStyle.For(region).Color, GraphStyle.For(country).Color);
        Assert.Equal(GraphStyle.For("Area").Color, GraphStyle.For(region).Color);
        Assert.Single(GraphStyle.KindStylesOf("Area"));
        Assert.Empty(GraphStyle.KindStylesOf("Crag"));
    }

    [Fact]
    public void Property_titles_read_naturally() => Assert.Equal("Rock type", GraphStyle.PropertyTitle("rock_type"));
}
