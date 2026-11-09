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
using Linkbase.Domains.Climbing.Import.Web;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace Linkbase.Tests;

public class RepoPathsTests
{
    [Fact]
    public void Finds_the_repo_root_from_inside_it()
    {
        var path = RepoPaths.Resolve("data/corrections.json");
        Assert.True(File.Exists(Path.Combine(Path.GetDirectoryName(Path.GetDirectoryName(path)!)!, "Linkbase.sln")));
    }

    [Fact]
    public void Says_so_when_started_outside_the_repo() =>
        Assert.Throws<DirectoryNotFoundException>(() => RepoPaths.Resolve("x", Path.GetTempPath()));
}

public class LabelsTests
{
    [Theory]
    [InlineData(AscentStyle.TopRope, "Top rope")]
    [InlineData(AscentStyle.Dnf, "DNF")]
    [InlineData(AscentStyle.Onsight, "Onsight")]
    public void Styles_read_naturally(AscentStyle style, string expected) => Assert.Equal(expected, Labels.Of(style));

    [Theory]
    [InlineData(null, "")]
    [InlineData(GradeSystem.UkTrad, "UK trad")]
    [InlineData(GradeSystem.UkTech, "UK tech")]
    [InlineData(GradeSystem.Yds, "YDS")]
    [InlineData(GradeSystem.V, "V grade")]
    [InlineData(GradeSystem.Uiaa, "UIAA")]
    [InlineData(GradeSystem.ViaFerrata, "Via ferrata")]
    [InlineData(GradeSystem.French, "French")]
    public void Grade_systems_read_naturally(GradeSystem? system, string expected) => Assert.Equal(expected, Labels.Of(system));

    [Fact]
    public void Disciplines_read_naturally()
    {
        Assert.Equal("", Labels.Of((Discipline?)null));
        Assert.Equal("Trad", Labels.Of(Discipline.Trad));
    }

    [Theory]
    [InlineData(DatePrecision.Day, "Exact day", "5 Apr 2012")]
    [InlineData(DatePrecision.Month, "Month only", "Apr 2012")]
    [InlineData(DatePrecision.Year, "Year only", "2012")]
    public void Dates_show_only_what_is_known(DatePrecision precision, string label, string date)
    {
        Assert.Equal(label, Labels.Of(precision));
        Assert.Equal(date, Labels.Date(new DateOnly(2012, 4, 5), precision));
    }

    [Fact]
    public void Counts_and_stars()
    {
        Assert.Equal("1 route", Labels.Count(1, "route"));
        Assert.Equal("2 routes", Labels.Count(2, "route"));
        Assert.Equal("★★", Labels.Stars(2));
        Assert.Equal("", Labels.Stars(0));
        Assert.Equal("", Labels.Stars(null));
    }
}

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

public class ExtractorHttpTests
{
    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public string? RequestBody { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestBody = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            return respond(request);
        }
    }

    private static IConfiguration Config() => new ConfigurationBuilder()
        .AddInMemoryCollection(new Dictionary<string, string?> { ["Anthropic:ApiKey"] = "test-key", ["Anthropic:Model"] = "claude-haiku-5-5" })
        .Build();

    private static HttpResponseMessage Json(HttpStatusCode status, string body) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private const string ToolResponse =
        """
        {
          "id": "msg_1", "type": "message", "role": "assistant", "model": "claude-haiku-5-5",
          "content": [
            { "type": "tool_use", "id": "tu_1", "name": "record_climbing_data", "input": {
              "areas": [], "crags": [{ "name": "Larkfield", "area": null, "rock_type": null, "lat": null, "lon": null }],
              "routes": [{ "name": "Slab Theory", "crag": "Larkfield", "grade": "7b+", "grade_system": "french", "discipline": "sport", "length_m": null, "pitches": null, "summary": null }]
            } }
          ],
          "stop_reason": "tool_use", "stop_sequence": null, "usage": { "input_tokens": 10, "output_tokens": 20 }
        }
        """;

    [Fact]
    public async Task A_real_response_is_read_through_the_sdk()
    {
        var handler = new StubHandler(_ => Json(HttpStatusCode.OK, ToolResponse));
        var extractor = new AnthropicClimbingExtractor(Config(), new HttpClient(handler));

        var page = await extractor.ExtractAsync("Slab Theory at Larkfield", ["Earlier"], CancellationToken.None);

        Assert.Equal("Slab Theory", page.Routes.Single().Name);
        Assert.Contains("claude-haiku-5-5", handler.RequestBody);
        Assert.Contains("Slab Theory at Larkfield", handler.RequestBody);
        Assert.Contains("Earlier", handler.RequestBody);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, "rejected")]
    [InlineData((HttpStatusCode)429, "rate limiting")]
    [InlineData(HttpStatusCode.BadRequest, "Check Anthropic:Model")]
    [InlineData(HttpStatusCode.InternalServerError, "server error")]
    [InlineData(HttpStatusCode.Forbidden, "returned an error")]
    public async Task Api_errors_become_clear_messages(HttpStatusCode status, string expected)
    {
        var handler = new StubHandler(_ => Json(status, """{"type":"error","error":{"type":"x","message":"nope"}}"""));
        var extractor = new AnthropicClimbingExtractor(Config(), new HttpClient(handler));

        var ex = await Assert.ThrowsAsync<ExtractionFailedException>(() => extractor.ExtractAsync("text", [], CancellationToken.None));

        Assert.Contains(expected, ex.Message);
    }

    [Fact]
    public async Task A_network_failure_is_a_clear_message()
    {
        var handler = new StubHandler(_ => throw new HttpRequestException("down"));
        var extractor = new AnthropicClimbingExtractor(Config(), new HttpClient(handler));

        var ex = await Assert.ThrowsAsync<ExtractionFailedException>(() => extractor.ExtractAsync("text", [], CancellationToken.None));

        Assert.Contains("Couldn't reach", ex.Message);
    }

    [Fact]
    public void Empty_results_are_a_clear_failure()
    {
        // JSON null deserialises to nothing at all, which isn't the same as empty lists.
        var input = new Dictionary<string, JsonElement> { ["areas"] = JsonSerializer.SerializeToElement(1) };
        Assert.Throws<ExtractionFailedException>(() => AnthropicClimbingExtractor.ReadResponse("tool_use", input));
    }
}

public class PageTextFetcherFailureTests
{
    private sealed class StubHandler(Func<HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(respond());
    }

    private static PageTextFetcher Fetcher(Func<HttpResponseMessage> respond) => new(new HttpClient(new StubHandler(respond)));

    [Fact]
    public async Task A_page_too_large_suggests_pasting()
    {
        var ex = await Assert.ThrowsAsync<PageFetchFailedException>(() => Fetcher(() =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("x", Encoding.UTF8, "text/html") };
            response.Content.Headers.ContentLength = 50_000_000;
            return response;
        }).FetchAsync("https://example.com/big", CancellationToken.None));
        Assert.Contains("too large", ex.Message);
    }

    [Fact]
    public async Task A_network_failure_suggests_pasting()
    {
        var ex = await Assert.ThrowsAsync<PageFetchFailedException>(() =>
            Fetcher(() => throw new HttpRequestException("no route")).FetchAsync("https://example.com/x", CancellationToken.None));
        Assert.Contains("no route", ex.Message);
        Assert.Contains("paste", ex.Message);
    }

    [Fact]
    public async Task A_timeout_suggests_pasting()
    {
        var ex = await Assert.ThrowsAsync<PageFetchFailedException>(() =>
            Fetcher(() => throw new TaskCanceledException()).FetchAsync("https://example.com/x", CancellationToken.None));
        Assert.Contains("too long", ex.Message);
    }

    [Fact]
    public void A_page_with_no_main_text_suggests_pasting() =>
        Assert.Throws<PageFetchFailedException>(() =>
            PageTextFetcher.ExtractMainText(new Uri("https://example.com"), "<html><body></body></html>"));
}

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

public class GradeRankingEdgeCaseTests
{
    [Theory]
    [InlineData(GradeSystem.UkTrad, "E2")]
    [InlineData(GradeSystem.UkTrad, "VS xx")]
    [InlineData(GradeSystem.Font, "f7A+")]
    [InlineData(GradeSystem.Yds, "5.10")]
    [InlineData(GradeSystem.V, "V3-")]
    [InlineData(GradeSystem.Australian, "22")]
    public void Ranks_grades_in_every_form(GradeSystem system, string grade) => Assert.NotNull(GradeRanking.Rank(system, grade));

    [Theory]
    [InlineData(GradeSystem.French, "easy")]
    [InlineData(GradeSystem.Yds, "hard")]
    [InlineData(GradeSystem.V, "hard")]
    [InlineData(GradeSystem.Uiaa, "abc")]
    [InlineData(GradeSystem.Uiaa, "XIII")]
    [InlineData(GradeSystem.Alpine, "nope")]
    [InlineData(GradeSystem.Alpine, "XX")]
    public void Leaves_unrecognised_grades_unranked(GradeSystem system, string grade) => Assert.Null(GradeRanking.Rank(system, grade));
}

public class PatternBuilderEdgeCaseTests
{
    private static readonly GraphSchema Schema = new(
        [new GraphConnection("Route", "AT", "Crag")],
        [
            new GraphPropertyInfo("Crag", "name", GraphValueType.Text),
            new GraphPropertyInfo("Ascent", "date", GraphValueType.Date),
            new GraphPropertyInfo("Trip", "abroad", GraphValueType.Boolean),
            new GraphPropertyInfo("Route", "length_m", GraphValueType.Number),
            new GraphPropertyInfo("Route", "style", GraphValueType.Choice) { Options = ["sport", "trad", "boulder"] },
            new GraphPropertyInfo("Crag", "aliases", GraphValueType.List),
        ]);

    private static PatternBuilder Start(string label)
    {
        var builder = new PatternBuilder(Schema);
        builder.Apply(new PatternSuggestion(SuggestionKind.Label, label) { Label = label });
        return builder;
    }

    private static void Choose(PatternBuilder builder, string key, FilterOperator op)
    {
        builder.Apply(new PatternSuggestion(SuggestionKind.Property, key) { Key = key });
        builder.Apply(new PatternSuggestion(SuggestionKind.Operator, "") { Operator = op });
    }

    [Theory]
    [InlineData(FilterOperator.Equals, "=")]
    [InlineData(FilterOperator.Contains, "contains")]
    [InlineData(FilterOperator.StartsWith, "starts with")]
    [InlineData(FilterOperator.EndsWith, "ends with")]
    [InlineData(FilterOperator.GreaterThan, ">")]
    [InlineData(FilterOperator.GreaterOrEqual, ">=")]
    [InlineData(FilterOperator.LessThan, "<")]
    [InlineData(FilterOperator.LessOrEqual, "<=")]
    public void Operators_have_readable_text(FilterOperator op, string text) => Assert.Equal(text, PatternBuilder.OperatorText(op));

    [Fact]
    public void Booleans_only_compare_with_equals_and_accept_only_true_or_false()
    {
        var builder = Start("Trip");
        builder.Apply(new PatternSuggestion(SuggestionKind.Property, "abroad") { Key = "abroad" });
        Assert.Equal(["="], builder.Suggest("", [], []).Select(s => s.Text));

        builder.Apply(new PatternSuggestion(SuggestionKind.Operator, "=") { Operator = FilterOperator.Equals });
        Assert.Empty(builder.Suggest("maybe", [], []));
        Assert.Single(builder.Suggest("true", [], []));
    }

    [Fact]
    public void A_list_compares_its_values_as_text()
    {
        var builder = Start("Crag");
        builder.Apply(new PatternSuggestion(SuggestionKind.Property, "aliases") { Key = "aliases" });
        Assert.Equal(["contains", "="], builder.Suggest("", [], []).Select(s => s.Text));
    }

    [Fact]
    public void A_choice_compares_with_equals_and_offers_only_its_options()
    {
        var builder = Start("Route");
        builder.Apply(new PatternSuggestion(SuggestionKind.Property, "style") { Key = "style" });
        Assert.Equal(["="], builder.Suggest("", [], []).Select(s => s.Text));

        builder.Apply(new PatternSuggestion(SuggestionKind.Operator, "=") { Operator = FilterOperator.Equals });
        Assert.Equal(["sport", "trad", "boulder"], builder.Suggest("", [], ["sport"]).Select(s => s.Value));
        Assert.Equal(["trad"], builder.Suggest("TR", [], []).Select(s => s.Value));
        Assert.Empty(builder.Suggest("aid", [], []));
    }

    [Fact]
    public void Dates_must_be_valid()
    {
        var builder = Start("Ascent");
        Choose(builder, "date", FilterOperator.GreaterThan);
        Assert.Empty(builder.Suggest("soon", [], []));
        Assert.Single(builder.Suggest("2020-01-01", [], []));
    }

    [Fact]
    public void Clear_empties_everything()
    {
        var builder = Start("Crag");
        Choose(builder, "name", FilterOperator.Contains);
        builder.Clear();
        Assert.True(builder.IsEmpty);
        Assert.Empty(builder.Chips);
        Assert.Null(builder.ToPattern());
    }

    [Fact]
    public void A_match_after_a_label_adds_to_it_rather_than_starting_again()
    {
        var builder = Start("Crag");
        builder.Apply(new PatternSuggestion(SuggestionKind.Match, "") { Label = "Crag", Key = "name", Value = "Larkfield" });
        var node = builder.ToPattern()!.Nodes.Single();
        Assert.Equal("name", node.Filters.Single().Key);
    }

    [Fact]
    public void An_any_node_shows_as_any_and_goes_when_its_relationship_does()
    {
        var builder = new PatternBuilder(Schema);
        builder.Apply(builder.Suggest("AT", [], []).First(s => s.Kind == SuggestionKind.Relationship));
        Assert.Null(builder.Chips[0].Label);

        builder.RemoveLast();
        Assert.True(builder.IsEmpty);
    }

    // Route, then AT Crag, each with a condition, as someone would build it.
    private static PatternBuilder RouteAtCrag()
    {
        var builder = Start("Route");
        Choose(builder, "length_m", FilterOperator.GreaterThan);
        builder.Apply(new PatternSuggestion(SuggestionKind.Value, "") { Value = "20" });
        builder.Apply(builder.Suggest("AT", [], []).First(s => s.Kind == SuggestionKind.Relationship));
        Choose(builder, "name", FilterOperator.Contains);
        builder.Apply(new PatternSuggestion(SuggestionKind.Value, "") { Value = "Ridge" });
        return builder;
    }

    [Fact]
    public void The_arrows_move_to_an_earlier_node_and_conditions_go_there_without_losing_the_rest()
    {
        var builder = RouteAtCrag();
        Assert.True(builder.AtEnd);
        Assert.False(builder.FocusNext());

        Assert.True(builder.FocusPrevious());
        Assert.False(builder.FocusPrevious());
        Assert.False(builder.AtEnd);
        Assert.Equal("Route", builder.CurrentLabel);
        Assert.Equal([true, false], builder.Chips.Select(c => c.Focused));

        // Only the earlier node's own properties are offered, and no relationships, which link from the end.
        var suggestions = builder.Suggest("", [], []);
        Assert.Contains(suggestions, s => s is { Kind: SuggestionKind.Property, Key: "length_m" });
        Assert.DoesNotContain(suggestions, s => s.Kind == SuggestionKind.Relationship);

        builder.Apply(new PatternSuggestion(SuggestionKind.FreeText, "") { Value = "slab" });
        Assert.Equal("Route", builder.CurrentLabel);

        var pattern = builder.ToPattern()!;
        Assert.Equal(2, pattern.Nodes[0].Filters.Count);
        Assert.Equal("slab", pattern.Nodes[0].Filters[1].Value);
        Assert.Equal("Ridge", pattern.Nodes[1].Filters.Single().Value);
        Assert.Single(pattern.Rels);

        Assert.True(builder.FocusNext());
        Assert.True(builder.AtEnd);
        Assert.Equal("Crag", builder.CurrentLabel);
        Assert.All(builder.Chips, c => Assert.False(c.Focused));
    }

    [Fact]
    public void A_half_made_condition_shows_on_the_focused_node_and_moving_away_drops_it()
    {
        var builder = RouteAtCrag();
        builder.FocusOn(0);
        builder.Apply(new PatternSuggestion(SuggestionKind.Property, "length_m") { Key = "length_m" });
        Assert.Equal(["length m", null], builder.Chips.Select(c => c.Pending));
        Assert.False(builder.FocusNext());

        builder.FocusOn(1);
        Assert.Null(builder.PendingKey);
        Assert.True(builder.AtEnd);
        builder.FocusOn(9);
        Assert.True(builder.AtEnd);
    }

    [Fact]
    public void Backspace_on_an_earlier_node_takes_only_its_conditions()
    {
        var builder = RouteAtCrag();
        builder.FocusOn(0);

        builder.RemoveLast();
        builder.RemoveLast();

        var pattern = builder.ToPattern()!;
        Assert.Equal(2, pattern.Nodes.Count);
        Assert.Empty(pattern.Nodes[0].Filters);
        Assert.Single(pattern.Nodes[1].Filters);
    }

    [Fact]
    public void One_condition_can_come_off_any_node()
    {
        var builder = RouteAtCrag();

        builder.RemoveFilter(0, 0);
        builder.RemoveFilter(0, 5);
        builder.RemoveFilter(7, 0);

        Assert.Equal([0, 1], builder.ToPattern()!.Nodes.Select(n => n.Filters.Count));
    }
}

[Trait("Category", "Database")]
public class DatabaseStatusTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    [Fact]
    public async Task Database_status_reports_connected_and_up_to_date()
    {
        var status = await new DatabaseStatusService(fixture.ContextFactory(), NullLogger<DatabaseStatusService>.Instance).GetStatusAsync();
        Assert.True(status.Connected);
        Assert.Equal(0, status.PendingMigrations);
        Assert.True(status.AppliedMigrations > 0);
        Assert.Null(status.Error);
    }

    private sealed class BrokenFactory : IDbContextFactory<LinkbaseDbContext>
    {
        public LinkbaseDbContext CreateDbContext() => new(new DbContextOptionsBuilder<LinkbaseDbContext>()
            .UseNpgsql("Host=127.0.0.1;Port=1;Database=x;Username=x;Password=x;Timeout=1").Options);
    }

    [Fact]
    public async Task Database_status_reports_a_failed_connection_without_throwing()
    {
        var status = await new DatabaseStatusService(new BrokenFactory(), NullLogger<DatabaseStatusService>.Instance).GetStatusAsync();
        Assert.False(status.Connected);
        Assert.NotNull(status.Error);
    }
}
