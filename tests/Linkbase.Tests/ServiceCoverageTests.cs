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
    public void Property_titles_read_naturally() => Assert.Equal("Rock type", GraphStyle.PropertyTitle("rock_type"));

    [Fact]
    public void Area_links_go_to_browse() => Assert.Equal("/browse/area/3", GraphStyle.Link(new GraphNode("area:3", "Area", "A", [])));
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
[Collection(PostgresCollection.Name)]
public class RepositoryCoverageTests(PostgresFixture fixture)
{
    private static string Unique(string name) => $"{name} {Guid.NewGuid():N}";

    private TripRefresher Refresher(string? path = null) => new(
        new TripService(fixture.ContextFactory()),
        () => path ?? "no-corrections-in-tests.json",
        NullLogger<TripRefresher>.Instance);

    private AreaRepository Areas() => new(fixture.ContextFactory());
    private AscentRepository Ascents() => new(fixture.ContextFactory(), Areas(), Refresher());
    private CragRepository Crags() => new(fixture.ContextFactory(), Refresher());
    private RouteRepository Routes() => new(fixture.ContextFactory(), Refresher());
    private PartnerRepository Partners() => new(fixture.ContextFactory());
    private ReconciliationService Reconciliation() => new(fixture.ContextFactory());

    private async Task<(long AscentId, long RouteId, long CragId, long AreaId, string Crag, string Route, string Country)> CreateAsync(string? partner = null)
    {
        var (route, crag, country) = (Unique("Route"), Unique("Crag"), Unique("Country"));
        var id = await Ascents().CreateAsync(new AscentInput
        {
            NewRoute = new NewRouteInput(route, "VS 4c", GradeSystem.UkTrad, Discipline.Trad, 20, 1, null, new NewCragInput(crag, null, country)),
            Date = new DateOnly(2020, 5, 2),
            DatePrecision = DatePrecision.Day,
            Style = AscentStyle.Lead,
            Partners = partner is null ? [] : [partner],
        });
        await using var db = fixture.CreateContext();
        var row = await db.Ascents.AsNoTracking().Where(a => a.Id == id).Select(a => new { a.RouteId, a.Route.CragId, a.Route.Crag.AreaId }).SingleAsync();
        return (id, row.RouteId, row.CragId, row.AreaId, crag, route, country);
    }

    [Fact]
    public async Task Partner_search_finds_names_and_lists_without_a_term()
    {
        var partner = Unique("Partner");
        await CreateAsync(partner);

        Assert.Contains(partner, await Partners().SearchNamesAsync(partner));
        Assert.NotEmpty(await Partners().SearchNamesAsync(null));
        Assert.DoesNotContain(partner, await Partners().SearchNamesAsync(Unique("Nobody")));
    }

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

    [Fact]
    public async Task Route_search_get_update_and_delete()
    {
        var s = await CreateAsync();
        Assert.Contains(await Routes().SearchAsync(s.CragId, null), r => r.Id == s.RouteId);
        Assert.Contains(await Routes().SearchAsync(null, s.Route), r => r.Id == s.RouteId);
        Assert.Contains(await Routes().SearchAsync(s.CragId, s.Route.Replace("Route", "Rute")), r => r.Id == s.RouteId);

        var detail = await Routes().GetAsync(s.RouteId);
        Assert.Equal(s.Crag, detail!.CragName);
        Assert.Null(await Routes().GetAsync(-1));

        var renamed = Unique("Renamed");
        await Routes().UpdateAsync(s.RouteId, new RouteInput($" {renamed} ", " E1 5b ", GradeSystem.UkTrad, Discipline.Sport, 30, 2, " Mine ", s.CragId));
        var updated = await Routes().GetAsync(s.RouteId);
        Assert.Equal(renamed, updated!.Name);
        Assert.Equal("E1 5b", updated.Grade);
        Assert.Equal("Mine", updated.Description);

        await Routes().UpdateAsync(s.RouteId, new RouteInput(renamed, " ", null, null, null, null, " ", s.CragId));
        var cleared = await Routes().GetAsync(s.RouteId);
        Assert.Null(cleared!.Grade);
        Assert.Null(cleared.Description);
    }

    [Fact]
    public async Task Route_update_validates_and_moving_crag_refreshes_trips()
    {
        var s = await CreateAsync();
        var other = await CreateAsync();

        await Assert.ThrowsAsync<LinkbaseValidationException>(() => Routes().UpdateAsync(s.RouteId, new RouteInput(" ", null, null, null, null, null, null, s.CragId)));
        await Assert.ThrowsAsync<LinkbaseValidationException>(() => Routes().UpdateAsync(s.RouteId, new RouteInput("X", null, null, null, -1, null, null, s.CragId)));
        await Assert.ThrowsAsync<LinkbaseValidationException>(() => Routes().UpdateAsync(s.RouteId, new RouteInput("X", null, null, null, null, -1, null, s.CragId)));
        await Assert.ThrowsAsync<LinkbaseValidationException>(() => Routes().UpdateAsync(-1, new RouteInput("X", null, null, null, null, null, null, s.CragId)));

        await Routes().UpdateAsync(s.RouteId, new RouteInput(s.Route, null, null, null, null, null, null, other.CragId));
        Assert.Equal(other.CragId, (await Routes().GetAsync(s.RouteId))!.CragId);
    }

    [Fact]
    public async Task Crag_get_routes_update_and_search()
    {
        var s = await CreateAsync();
        Assert.Equal(s.Crag, (await Crags().GetAsync(s.CragId))!.Name);
        Assert.Null(await Crags().GetAsync(-1));

        var route = (await Crags().RoutesAsync(s.CragId)).Single();
        Assert.Equal(1, route.Ascents);
        Assert.Equal(new DateOnly(2020, 5, 2), route.LastClimbed);

        Assert.NotEmpty(await Crags().FindCragCandidatesAsync(null));

        await Crags().UpdateAsync(s.CragId, new CragInput(s.Crag, s.AreaId, " ", null, null, [" a ", " "], " "));
        var detail = await Crags().GetAsync(s.CragId);
        Assert.Null(detail!.RockType);
        Assert.Null(detail.Notes);
        Assert.Equal(["a"], detail.Aliases);

        var other = await CreateAsync();
        await Crags().UpdateAsync(s.CragId, new CragInput(s.Crag, other.AreaId, "Grit", 1, 2, [], "Note"));
        Assert.Equal(other.AreaId, (await Crags().GetAsync(s.CragId))!.AreaId);
    }

    [Fact]
    public async Task Crag_update_validates()
    {
        var s = await CreateAsync();
        await Assert.ThrowsAsync<LinkbaseValidationException>(() => Crags().UpdateAsync(s.CragId, new CragInput(" ", s.AreaId, null, null, null, [], null)));
        await Assert.ThrowsAsync<LinkbaseValidationException>(() => Crags().UpdateAsync(s.CragId, new CragInput("X", s.AreaId, null, null, 181, [], null)));
        await Assert.ThrowsAsync<LinkbaseValidationException>(() => Crags().UpdateAsync(-1, new CragInput("X", s.AreaId, null, null, null, [], null)));
    }

    [Fact]
    public async Task Area_search_and_path()
    {
        var s = await CreateAsync();
        Assert.Contains(await Areas().SearchAsync(s.Country), a => a.Id == s.AreaId);
        Assert.NotEmpty(await Areas().SearchAsync(null));
        Assert.Equal(s.Country, (await Areas().PathAsync(s.AreaId)).Single().Name);

        var contents = await Areas().GetContentsAsync(s.AreaId);
        Assert.Equal(s.Country, contents!.Name);
        Assert.Equal(1, contents.Crags.Single().Routes);
    }

    [Fact]
    public async Task Trip_refresher_rebuilds_when_the_file_exists_and_skips_when_it_does_not()
    {
        var s = await CreateAsync();
        await Refresher("missing-file.json").RefreshAsync();
        await using var db = fixture.CreateContext();
        Assert.Null(await db.Ascents.AsNoTracking().Where(a => a.Id == s.AscentId).Select(a => a.TripId).SingleAsync());

        var path = Path.GetTempFileName();
        try
        {
            File.WriteAllText(path, """{ "partnerRenames": [], "mountainProjectPartners": [], "tripMerges": [] }""");
            await Refresher(path).RefreshAsync();
            Assert.NotNull(await db.Ascents.AsNoTracking().Where(a => a.Id == s.AscentId).Select(a => a.TripId).SingleAsync());
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task Trip_rebuild_removes_trips_that_no_longer_apply()
    {
        var s = await CreateAsync();
        var service = new TripService(fixture.ContextFactory());
        await service.RebuildAsync([]);
        await using var db = fixture.CreateContext();
        var tripId = (await db.Ascents.AsNoTracking().Where(a => a.Id == s.AscentId).Select(a => a.TripId).SingleAsync())!.Value;

        await Ascents().DeleteAsync(s.AscentId);
        await service.RebuildAsync([]);

        Assert.False(await db.Trips.AnyAsync(t => t.Id == tripId));
    }

    [Fact]
    public async Task Ascent_update_of_a_missing_row_and_partner_reuse()
    {
        var partner = Unique("Partner");
        var s = await CreateAsync(partner);
        var second = await Ascents().CreateAsync(new AscentInput
        {
            RouteId = s.RouteId,
            Date = new DateOnly(2020, 5, 3),
            DatePrecision = DatePrecision.Day,
            Style = AscentStyle.Lead,
            Partners = [partner],
        });
        Assert.Equal([partner], (await Ascents().GetForEditAsync(second))!.Partners);
        Assert.Null(await Ascents().GetForEditAsync(-1));

        await Assert.ThrowsAsync<LinkbaseValidationException>(() => Ascents().UpdateAsync(-1, new AscentInput
        {
            RouteId = s.RouteId,
            Date = new DateOnly(2020, 5, 3),
            DatePrecision = DatePrecision.Day,
            Style = AscentStyle.Lead,
        }));
    }

    [Fact]
    public async Task Ascent_create_validates_new_crag_and_area_and_reuses_a_country()
    {
        Task<long> Create(NewCragInput crag) => Ascents().CreateAsync(new AscentInput
        {
            NewRoute = new NewRouteInput(Unique("R"), null, null, null, null, null, null, crag),
            Date = new DateOnly(2020, 5, 3),
            DatePrecision = DatePrecision.Day,
            Style = AscentStyle.Lead,
        });

        await Assert.ThrowsAsync<LinkbaseValidationException>(() => Create(new NewCragInput(" ", null, "X")));
        await Assert.ThrowsAsync<LinkbaseValidationException>(() => Create(new NewCragInput(Unique("C"), null, null)));
        await Assert.ThrowsAsync<LinkbaseValidationException>(() => Ascents().CreateAsync(new AscentInput
        {
            NewRoute = new NewRouteInput(Unique("R"), null, null, null, null, null, null, null),
            Date = new DateOnly(2020, 5, 3),
            DatePrecision = DatePrecision.Day,
            Style = AscentStyle.Lead,
        }));

        var country = Unique("Country");
        var first = await Create(new NewCragInput(Unique("C"), null, country));
        var second = await Create(new NewCragInput(Unique("C"), null, country));
        await using var db = fixture.CreateContext();
        var areas = await db.Ascents.AsNoTracking().Where(a => a.Id == first || a.Id == second).Select(a => a.Route.Crag.AreaId).Distinct().ToListAsync();
        Assert.Single(areas);

        var byArea = await Create(new NewCragInput(Unique("C"), areas[0], null));
        Assert.Equal(areas[0], await db.Ascents.AsNoTracking().Where(a => a.Id == byArea).Select(a => a.Route.Crag.AreaId).SingleAsync());
    }

    [Theory]
    [InlineData(AscentSort.Route, false)]
    [InlineData(AscentSort.Route, true)]
    [InlineData(AscentSort.Crag, false)]
    [InlineData(AscentSort.Crag, true)]
    [InlineData(AscentSort.Style, false)]
    [InlineData(AscentSort.Style, true)]
    [InlineData(AscentSort.Date, false)]
    public async Task Search_sorts_every_way(AscentSort sort, bool descending)
    {
        var s = await CreateAsync();
        var page = await Ascents().SearchAsync(new AscentFilter { AreaId = s.AreaId }, sort, descending, 0, 5);
        Assert.Equal(s.AscentId, page.Rows.Single().Id);
    }

    [Fact]
    public async Task Search_filters_by_route_system_discipline_and_end_date()
    {
        var s = await CreateAsync();
        var filter = new AscentFilter { AreaId = s.AreaId };
        Assert.Single((await Ascents().SearchAsync(filter with { RouteId = s.RouteId }, AscentSort.Date, true, 0, 5)).Rows);
        Assert.Single((await Ascents().SearchAsync(filter with { GradeSystem = GradeSystem.UkTrad }, AscentSort.Date, true, 0, 5)).Rows);
        Assert.Empty((await Ascents().SearchAsync(filter with { GradeSystem = GradeSystem.French }, AscentSort.Date, true, 0, 5)).Rows);
        Assert.Single((await Ascents().SearchAsync(filter with { Discipline = Discipline.Trad }, AscentSort.Date, true, 0, 5)).Rows);
        Assert.Empty((await Ascents().SearchAsync(filter with { Discipline = Discipline.Sport }, AscentSort.Date, true, 0, 5)).Rows);
        Assert.Empty((await Ascents().SearchAsync(filter with { To = new DateOnly(2000, 1, 1) }, AscentSort.Date, true, 0, 5)).Rows);
    }

    [Fact]
    public async Task Reconciliation_report_runs_on_real_rows()
    {
        var partner = Unique("Partner");
        var s = await CreateAsync(partner);
        await using (var db = fixture.CreateContext())
        {
            db.ExternalRefs.Add(new ExternalRef { EntityType = ExternalEntityType.Route, EntityId = s.RouteId, Source = DataSource.Ukc, ExternalId = Unique("r") });
            await db.SaveChangesAsync();
        }

        await new TripService(fixture.ContextFactory()).RebuildAsync([]);

        var report = await Reconciliation().BuildReportAsync();
        Assert.True(report.Partners > 0);
        Assert.True(report.Trips > 0);
        Assert.Contains(report.Entities, e => e.Entity == "Routes" && e.Total > 0 && e.Ukc > 0);
    }
}
