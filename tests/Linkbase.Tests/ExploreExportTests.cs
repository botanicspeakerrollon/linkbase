using System.Text;
using System.Text.Json;
using Linkbase.Core.Explore;
using Linkbase.Core.Graph;

namespace Linkbase.Tests;

public class ExploreExportTests
{
    private static readonly GraphNode Ada = new("person:1", "Person", "Ada Quill", [new("role", "Engineer"), new("grade", "7")]);
    private static readonly GraphNode Bo = new("person:2", "Person", "Bo, \"the\" Tester", [new("team", "Platform")]);
    private static readonly GraphNode Office = new("place:9", "Office", "North Yard", []);

    private static readonly IReadOnlyList<GraphEdge> Edges =
    [
        new("person:1", "place:9", "WORKS_AT"),
        new("person:1", "place:9", "WORKS_AT", "WORKS_AT (2)"),
        new("person:2", "place:9", "WORKS_AT"),
        new("person:1", "person:404", "KNOWS"),
    ];

    private static string[] Lines(string csv) => csv.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);

    [Fact]
    public void Nodes_get_a_column_for_every_property_any_of_them_has()
    {
        var lines = Lines(ExploreExport.NodesCsv([Ada, Bo, Office]));

        Assert.Equal("id,type,name,role,grade,team", lines[0]);
        Assert.Equal("person:1,Person,Ada Quill,Engineer,7,", lines[1]);
        Assert.Equal("person:2,Person,\"Bo, \"\"the\"\" Tester\",,,Platform", lines[2]);
        Assert.Equal("place:9,Office,North Yard,,,", lines[3]);
    }

    [Fact]
    public void Every_field_the_model_defines_for_the_types_shown_is_a_column_even_when_nobody_has_a_value()
    {
        var schema = new GraphSchema([],
        [
            new("Person", "name", GraphValueType.Text), new("Person", "role", GraphValueType.Text),
            new("Person", "nickname", GraphValueType.Text), new("Person", "grade", GraphValueType.Text),
            new("Office", "name", GraphValueType.Text), new("Office", "address", GraphValueType.Text),
            new("Crag", "area", GraphValueType.Text),
        ]);

        var lines = Lines(ExploreExport.NodesCsv([Ada, Office], schema));

        // Crag isn't shown, so its field isn't a column. A property a node has that the model doesn't define still is.
        Assert.Equal("id,type,name,role,nickname,grade,address", lines[0]);
        Assert.Equal("person:1,Person,Ada Quill,Engineer,,7,", lines[1]);
        Assert.Equal("place:9,Office,North Yard,,,,", lines[2]);

        var stray = new GraphNode("person:3", "Person", "Cy", [new("pet", "Cat")]);
        Assert.Equal("id,type,name,role,nickname,grade,address,pet", Lines(ExploreExport.NodesCsv([Ada, Office, stray], schema))[0]);
    }

    [Fact]
    public void A_cell_a_spreadsheet_would_run_as_a_formula_is_escaped()
    {
        var sneaky = new GraphNode("x:1", "Note", "=HYPERLINK(\"http://example.test\")", [new("sum", "+1+2"), new("at", "@cmd")]);

        var row = Lines(ExploreExport.NodesCsv([sneaky]))[1];

        Assert.DoesNotContain(",=", row, StringComparison.Ordinal);
        Assert.DoesNotContain(",+", row, StringComparison.Ordinal);
        Assert.DoesNotContain(",@", row, StringComparison.Ordinal);
        Assert.Contains("'=HYPERLINK", row, StringComparison.Ordinal);
    }

    [Fact]
    public void Links_are_those_between_exported_nodes_once_each()
    {
        var lines = Lines(ExploreExport.LinksCsv([Ada, Bo, Office], Edges));

        Assert.Equal("source_id,source_type,source_name,relationship,target_id,target_type,target_name,values", lines[0]);
        Assert.Equal(3, lines.Length);
        Assert.Equal("person:1,Person,Ada Quill,WORKS_AT,place:9,Office,North Yard,", lines[1]);
        Assert.DoesNotContain(lines, l => l.Contains("KNOWS", StringComparison.Ordinal));
    }

    [Fact]
    public void Json_has_nodes_with_properties_and_links_by_id()
    {
        using var json = JsonDocument.Parse(ExploreExport.AsJson([Ada, Office], Edges));

        var nodes = json.RootElement.GetProperty("nodes");
        Assert.Equal(2, nodes.GetArrayLength());
        Assert.Equal("Ada Quill", nodes[0].GetProperty("name").GetString());
        Assert.Equal("Person", nodes[0].GetProperty("type").GetString());
        Assert.Equal("Engineer", nodes[0].GetProperty("properties").GetProperty("role").GetString());

        var link = Assert.Single(json.RootElement.GetProperty("links").EnumerateArray());
        Assert.Equal("person:1", link.GetProperty("source").GetString());
        Assert.Equal("WORKS_AT", link.GetProperty("relationship").GetString());
        Assert.Equal("place:9", link.GetProperty("target").GetString());
    }

    [Fact]
    public void Links_with_different_values_are_separate_and_carry_them()
    {
        IReadOnlyList<GraphEdge> edges =
        [
            new("person:1", "place:9", "WORKS_AT") { Values = new Dictionary<string, string> { ["role"] = "Lead" } },
            new("person:1", "place:9", "WORKS_AT") { Values = new Dictionary<string, string> { ["role"] = "Cover" } },
            new("person:1", "place:9", "WORKS_AT") { Values = new Dictionary<string, string> { ["role"] = "Cover" } },
        ];

        var lines = Lines(ExploreExport.LinksCsv([Ada, Office], edges));
        Assert.Equal(3, lines.Length);
        Assert.EndsWith(",role: Lead", lines[1], StringComparison.Ordinal);
        Assert.EndsWith(",role: Cover", lines[2], StringComparison.Ordinal);

        using var json = JsonDocument.Parse(ExploreExport.AsJson([Ada, Office], edges));
        var links = json.RootElement.GetProperty("links").EnumerateArray().ToList();
        Assert.Equal(2, links.Count);
        Assert.Equal("Lead", links[0].GetProperty("values").GetProperty("role").GetString());
        Assert.Equal("WORKS_AT · Lead", edges[0].Label);
        Assert.NotEqual(edges[0].Key, edges[1].Key);
    }

    [Fact]
    public void Nothing_shown_still_gives_a_header()
    {
        Assert.Equal("id,type,name", Lines(ExploreExport.NodesCsv([])).Single());
        Assert.Single(Lines(ExploreExport.LinksCsv([], Edges)));
    }

    [Fact]
    public void Files_are_named_for_what_and_when_and_start_with_a_byte_order_mark()
    {
        Assert.Equal("linkbase-nodes-2031-03-04-0705.csv", ExploreExport.FileName("nodes", "csv", new DateTimeOffset(2031, 3, 4, 7, 5, 0, TimeSpan.Zero)));

        var bytes = ExploreExport.Utf8("é");
        Assert.Equal(Encoding.UTF8.GetPreamble(), bytes[..3]);
        Assert.Equal("é", Encoding.UTF8.GetString(bytes[3..]));
    }
}
