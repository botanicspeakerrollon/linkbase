using System.Text.Json;
using System.Text.Json.Nodes;
using Bunit;
using Linkbase.Core.Ask;
using Linkbase.Core.Explore;
using Linkbase.Core.Extraction;
using Linkbase.Core.Graph;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor.Services;

namespace Linkbase.Tests;

internal sealed class FakeQuestionCompiler(Func<string, GraphPattern> answer) : IQuestionCompiler
{
    public string? LastQuestion { get; private set; }

    public Task<GraphPattern> CompileAsync(GraphSchema schema, string question, DateOnly today, CancellationToken cancellationToken)
    {
        LastQuestion = question;
        return Task.FromResult(answer(question));
    }
}

public class PatternCheckerTests
{
    private static readonly GraphSchema Schema = new(
        [
            new GraphConnection("Crag", "IN", "Country"),
            new GraphConnection("Route", "AT", "Crag"),
            new GraphConnection("Person", "WORKS_ON", "Route"),
        ],
        [
            new GraphPropertyInfo("Crag", "name", GraphValueType.Text),
            new GraphPropertyInfo("Country", "name", GraphValueType.Text),
            new GraphPropertyInfo("Route", "name", GraphValueType.Text),
            new GraphPropertyInfo("Route", "grade", GraphValueType.Text),
            new GraphPropertyInfo("Route", "length_m", GraphValueType.Number),
            new GraphPropertyInfo("Route", "style", GraphValueType.Choice) { Options = ["Sport", "Trad"] },
            new GraphPropertyInfo("Route", "first_climbed", GraphValueType.Date),
            new GraphPropertyInfo("Person", "name", GraphValueType.Text),
        ])
    {
        LinkProperties = [new GraphLinkPropertyInfo("WORKS_ON", "role", GraphValueType.Choice) { Options = ["Lead", "Support"] }],
    };

    private static GraphPattern Route(params PatternFilter[] filters) => new([new PatternNode("Route", filters)], []);

    private static QuestionFailedException Fails(GraphPattern pattern, string question) =>
        Assert.Throws<QuestionFailedException>(() => PatternChecker.Check(pattern, Schema, question));

    [Fact]
    public void A_pattern_that_fits_the_model_passes()
    {
        var pattern = new GraphPattern(
            [new PatternNode("Country", [new PatternFilter("name", FilterOperator.Equals, "Spain")]), new PatternNode("Crag", []), new PatternNode("Route", [new PatternFilter("length_m", FilterOperator.GreaterThan, "20")])],
            [new PatternRel("IN", false), new PatternRel("AT", false)]);

        var result = PatternChecker.Check(pattern, Schema, "routes over 20 in Spain");

        Assert.Equal(3, result.Nodes.Count);
        Assert.Equal("Spain", result.Nodes[0].Filters[0].Value);
    }

    [Fact]
    public void An_unknown_type_is_refused()
    {
        var ex = Fails(new GraphPattern([new PatternNode("Spaceship", [])], []), "spaceships");
        Assert.Contains("Spaceship", ex.Message);
    }

    [Fact]
    public void An_unknown_field_is_refused()
    {
        Fails(Route(new PatternFilter("secret", FilterOperator.Equals, "x")), "x");
    }

    [Fact]
    public void A_field_of_another_type_is_refused()
    {
        Fails(new GraphPattern([new PatternNode("Crag", [new PatternFilter("grade", FilterOperator.Equals, "7a")])], []), "7a");
    }

    [Fact]
    public void An_operator_the_field_cant_take_is_refused()
    {
        Fails(Route(new PatternFilter("grade", FilterOperator.GreaterThan, "7a")), "above 7a");
    }

    [Fact]
    public void A_number_must_be_a_number_and_a_date_a_date()
    {
        Fails(Route(new PatternFilter("length_m", FilterOperator.GreaterThan, "long")), "long routes");
        Fails(Route(new PatternFilter("first_climbed", FilterOperator.GreaterThan, "last year")), "last year");
    }

    [Fact]
    public void A_choice_is_matched_to_its_option_and_an_unknown_one_refused()
    {
        var result = PatternChecker.Check(Route(new PatternFilter("style", FilterOperator.Equals, "sport")), Schema, "sport routes");
        Assert.Equal("Sport", result.Nodes[0].Filters[0].Value);

        Fails(Route(new PatternFilter("style", FilterOperator.Equals, "Aid")), "aid routes");
    }

    [Fact]
    public void Text_the_question_didnt_say_is_refused_as_invented()
    {
        var ex = Fails(Route(new PatternFilter("grade", FilterOperator.Equals, "8c")), "my hardest route");
        Assert.Contains("8c", ex.Message);
    }

    [Fact]
    public void Free_text_must_come_from_the_question()
    {
        var any = new GraphPattern([new PatternNode(null, [new PatternFilter(PatternFilter.AnyField, FilterOperator.Equals, "Spain")])], []);

        var result = PatternChecker.Check(any, Schema, "anything about spain");
        Assert.Equal(FilterOperator.Contains, result.Nodes[0].Filters[0].Operator);
        Fails(any, "anything about France");
    }

    [Fact]
    public void A_link_has_to_exist_in_that_direction()
    {
        var backwards = new GraphPattern([new PatternNode("Route", []), new PatternNode("Crag", [])], [new PatternRel("AT", false)]);
        Fails(backwards, "routes");

        var unknown = new GraphPattern([new PatternNode("Route", []), new PatternNode("Crag", [])], [new PatternRel("LIKES", true)]);
        Fails(unknown, "routes");
    }

    [Fact]
    public void An_untyped_end_matches_any_link_that_exists()
    {
        var pattern = new GraphPattern([new PatternNode("Route", []), new PatternNode(null, [])], [new PatternRel("AT", true)]);
        Assert.Equal(2, PatternChecker.Check(pattern, Schema, "routes").Nodes.Count);
    }

    [Fact]
    public void A_filter_on_a_links_own_value_is_matched_to_its_choice()
    {
        var pattern = new GraphPattern(
            [new PatternNode("Person", []), new PatternNode("Route", [])],
            [new PatternRel("WORKS_ON", true) { Filters = [new PatternFilter("role", FilterOperator.Equals, "lead")] }]);

        var result = PatternChecker.Check(pattern, Schema, "leads");

        Assert.Equal("Lead", result.Rels[0].Filters[0].Value);
    }

    [Fact]
    public void A_links_own_filter_naming_a_value_it_doesnt_carry_is_refused()
    {
        var pattern = new GraphPattern(
            [new PatternNode("Person", []), new PatternNode("Route", [])],
            [new PatternRel("WORKS_ON", true) { Filters = [new PatternFilter("days_a_week", FilterOperator.Equals, "3")] }]);
        Assert.Throws<QuestionFailedException>(() => PatternChecker.Check(pattern, Schema, "3 days a week"));
    }

    [Fact]
    public void A_links_own_filter_cant_be_free_text()
    {
        var pattern = new GraphPattern(
            [new PatternNode("Person", []), new PatternNode("Route", [])],
            [new PatternRel("WORKS_ON", true) { Filters = [new PatternFilter(PatternFilter.AnyField, FilterOperator.Contains, "x")] }]);
        Assert.Throws<QuestionFailedException>(() => PatternChecker.Check(pattern, Schema, "x"));
    }

    [Fact]
    public void A_pattern_that_isnt_a_chain_is_refused()
    {
        Fails(new GraphPattern([], []), "x");
        Fails(new GraphPattern([new PatternNode("Route", [])], [new PatternRel("AT", true)]), "x");
    }

    private static GraphPattern RouteChain(PatternRank? rank) =>
        new([new PatternNode("Crag", []), new PatternNode("Route", []), new PatternNode("Person", [])], [new PatternRel("AT", false), new PatternRel("WORKS_ON", false)]) { Rank = rank };

    [Fact]
    public void A_ranking_between_two_positions_in_the_chain_is_kept_and_its_size_trimmed()
    {
        var result = PatternChecker.Check(RouteChain(new PatternRank(0, 2, 500)), Schema, "which crag");

        Assert.Equal(new PatternRank(0, 2, PatternRank.MaxTop), result.Rank);
        Assert.Null(PatternChecker.Check(RouteChain(null), Schema, "crags").Rank);
    }

    [Theory]
    [InlineData(0, 3)]
    [InlineData(-1, 2)]
    [InlineData(1, 1)]
    public void A_ranking_of_positions_the_chain_doesnt_have_is_refused(int group, int count) =>
        Fails(RouteChain(new PatternRank(group, count, 5)), "which crag");

    [Fact]
    public void Too_long_a_chain_or_too_long_a_value_is_refused()
    {
        var chain = new GraphPattern(Enumerable.Repeat(new PatternNode("Route", []), PatternChecker.MaxNodes + 1).ToList(), Enumerable.Repeat(new PatternRel("AT", true), PatternChecker.MaxNodes).ToList());
        Fails(chain, "x");

        var value = new string('a', PatternChecker.MaxValueLength + 1);
        Fails(Route(new PatternFilter("name", FilterOperator.Contains, value)), value);
    }
}

public class AnthropicQuestionCompilerTests
{
    private static readonly GraphSchema Schema = new(
        [new GraphConnection("Route", "AT", "Crag")],
        [new GraphPropertyInfo("Route", "grade", GraphValueType.Text), new GraphPropertyInfo("Crag", "name", GraphValueType.Text)])
    {
        LinkProperties = [new GraphLinkPropertyInfo("AT", "pitch", GraphValueType.Number)],
    };

    private static Dictionary<string, JsonElement> Input(string json) =>
        JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json)!;

    [Fact]
    public void The_tool_schema_only_names_what_the_model_has()
    {
        var schema = JsonNode.Parse(AnthropicQuestionCompiler.InputSchemaJson(Schema))!;
        var node = schema["properties"]!["records"]!["items"]!["properties"]!;

        Assert.Equal(["Route", "Crag", "Anything"], node["type"]!["enum"]!.AsArray().Select(n => n!.GetValue<string>()));
        Assert.Equal(["grade", "name", "*"], node["filters"]!["items"]!["properties"]!["field"]!["enum"]!.AsArray().Select(n => n!.GetValue<string>()));
        Assert.Equal(["AT"], schema["properties"]!["links"]!["items"]!["properties"]!["rel_type"]!["enum"]!.AsArray().Select(n => n!.GetValue<string>()));
        Assert.Equal(["pitch"], schema["properties"]!["links"]!["items"]!["properties"]!["filters"]!["items"]!["properties"]!["field"]!["enum"]!.AsArray().Select(n => n!.GetValue<string>()));
    }

    [Fact]
    public void The_tool_schema_lets_the_model_rank_or_leave_it_null()
    {
        var rank = JsonNode.Parse(AnthropicQuestionCompiler.InputSchemaJson(Schema))!["properties"]!["rank"]!["anyOf"]!.AsArray();

        Assert.Equal("null", rank[0]!["type"]!.GetValue<string>());
        Assert.Equal(["group", "count", "top"], rank[1]!["required"]!.AsArray().Select(n => n!.GetValue<string>()));
        Assert.Equal(PatternChecker.MaxNodes, rank[1]!["properties"]!["group"]!["enum"]!.AsArray().Count);
        Assert.Contains("rank", JsonNode.Parse(AnthropicQuestionCompiler.InputSchemaJson(Schema))!["required"]!.AsArray().Select(n => n!.GetValue<string>()));
    }

    [Fact]
    public void A_ranking_in_the_answer_becomes_part_of_the_pattern()
    {
        var pattern = AnthropicQuestionCompiler.ReadResponse("tool_use", Input(
            """
            {"records":[{"type":"Route","filters":[]},{"type":"Crag","filters":[]}],"links":[{"rel_type":"AT","direction":"outgoing","filters":[]}],
             "rank":{"group":1,"count":0,"top":5}}
            """));
        Assert.Equal(new PatternRank(1, 0, 5), pattern.Rank);

        var plain = AnthropicQuestionCompiler.ReadResponse("tool_use", Input(
            """{"records":[{"type":"Route","filters":[]}],"links":[],"rank":null}"""));
        Assert.Null(plain.Rank);
    }

    [Fact]
    public void The_prompt_lists_the_model_and_treats_the_question_as_data()
    {
        var prompt = AnthropicQuestionCompiler.SystemPrompt(Schema, new DateOnly(2026, 5, 1));

        Assert.Contains("Route: grade (text)", prompt);
        Assert.Contains("Route AT Crag, carrying: pitch (a number)", prompt);
        Assert.Contains("Today is 2026-05-01", prompt);
        Assert.Contains("ignore them", prompt);
        Assert.Contains("\"rank\"", prompt);
    }

    [Fact]
    public void A_links_filters_become_part_of_the_pattern()
    {
        var pattern = AnthropicQuestionCompiler.ReadResponse("tool_use", Input(
            """
            {"records":[{"type":"Route","filters":[]},{"type":"Crag","filters":[]}],
             "links":[{"rel_type":"AT","direction":"outgoing","filters":[{"field":"pitch","operator":"equals","value":"2"}]}]}
            """));

        Assert.Equal("pitch", pattern.Rels[0].Filters[0].Key);
        Assert.Equal("2", pattern.Rels[0].Filters[0].Value);
    }

    [Fact]
    public void A_link_with_no_filters_key_is_read_as_having_none()
    {
        var pattern = AnthropicQuestionCompiler.ReadResponse("tool_use", Input(
            """{"records":[{"type":"Route","filters":[]},{"type":"Crag","filters":[]}],"links":[{"rel_type":"AT","direction":"outgoing"}]}"""));

        Assert.Empty(pattern.Rels[0].Filters);
    }

    [Fact]
    public void An_answer_becomes_a_pattern()
    {
        var pattern = AnthropicQuestionCompiler.ReadResponse("tool_use", Input(
            """
            {"records":[{"type":"Route","filters":[{"field":"grade","operator":"greaterThan","value":"7a"}]},{"type":"Anything","filters":[]}],
             "links":[{"rel_type":"AT","direction":"outgoing"}]}
            """));

        Assert.Equal("Route", pattern.Nodes[0].Label);
        Assert.Equal(FilterOperator.GreaterThan, pattern.Nodes[0].Filters[0].Operator);
        Assert.Null(pattern.Nodes[1].Label);
        Assert.True(pattern.Rels[0].Outgoing);
    }

    [Fact]
    public void A_refusal_a_cut_off_or_a_missing_call_is_a_failure()
    {
        Assert.Throws<QuestionFailedException>(() => AnthropicQuestionCompiler.ReadResponse("refusal", null));
        Assert.Throws<QuestionFailedException>(() => AnthropicQuestionCompiler.ReadResponse("max_tokens", null));
        Assert.Throws<QuestionFailedException>(() => AnthropicQuestionCompiler.ReadResponse("end_turn", null));
        Assert.Throws<QuestionFailedException>(() => AnthropicQuestionCompiler.ReadResponse("tool_use", Input("""{"records":[{"type":"Route"}],"links":[]}""")));
    }

    [Fact]
    public async Task A_missing_key_is_reported_not_thrown_as_something_else()
    {
        var compiler = new AnthropicQuestionCompiler(new ConfigurationBuilder().Build());
        var service = new QuestionService(compiler, TimeProvider.System);

        var ex = await Assert.ThrowsAsync<QuestionFailedException>(() => service.AskAsync(Schema, "routes", CancellationToken.None));
        Assert.Contains("Anthropic:ApiKey", ex.Message);
    }
}

public class QuestionServiceTests
{
    private static readonly GraphSchema Schema = new(
        [new GraphConnection("Route", "AT", "Crag")],
        [new GraphPropertyInfo("Route", "grade", GraphValueType.Text), new GraphPropertyInfo("Crag", "name", GraphValueType.Text)]);

    [Fact]
    public async Task A_good_answer_is_returned_after_the_check()
    {
        var compiler = new FakeQuestionCompiler(_ => new GraphPattern([new PatternNode("Route", [new PatternFilter("grade", FilterOperator.Equals, " 7a ")])], []));

        var pattern = await new QuestionService(compiler, TimeProvider.System).AskAsync(Schema, "  routes graded 7a ", CancellationToken.None);

        Assert.Equal("routes graded 7a", compiler.LastQuestion);
        Assert.Equal("7a", pattern.Nodes[0].Filters[0].Value);
    }

    [Fact]
    public async Task A_bad_answer_never_gets_out()
    {
        var compiler = new FakeQuestionCompiler(_ => new GraphPattern([new PatternNode("Secrets", [])], []));

        await Assert.ThrowsAsync<QuestionFailedException>(() => new QuestionService(compiler, TimeProvider.System).AskAsync(Schema, "secrets", CancellationToken.None));
    }

    [Fact]
    public async Task An_empty_or_huge_question_is_refused_without_asking()
    {
        var compiler = new FakeQuestionCompiler(_ => throw new InvalidOperationException("Should not be asked."));
        var service = new QuestionService(compiler, TimeProvider.System);

        await Assert.ThrowsAsync<QuestionFailedException>(() => service.AskAsync(Schema, "   ", CancellationToken.None));
        await Assert.ThrowsAsync<QuestionFailedException>(() => service.AskAsync(Schema, new string('x', QuestionService.MaxQuestionLength + 1), CancellationToken.None));
        Assert.Null(compiler.LastQuestion);
    }

    [Fact]
    public async Task The_api_failures_come_out_as_question_failures()
    {
        var service = new QuestionService(new FailingCompiler(), TimeProvider.System);

        var ex = await Assert.ThrowsAsync<QuestionFailedException>(() => service.AskAsync(Schema, "routes", CancellationToken.None));
        Assert.Equal("rate limited", ex.Message);
    }

    private sealed class FailingCompiler : IQuestionCompiler
    {
        public Task<GraphPattern> CompileAsync(GraphSchema schema, string question, DateOnly today, CancellationToken cancellationToken) =>
            throw new ExtractionFailedException("rate limited");
    }
}

public class AskBoxTests : BunitContext
{
    private static readonly GraphSchema Schema = new(
        [new GraphConnection("Route", "AT", "Crag")],
        [new GraphPropertyInfo("Route", "grade", GraphValueType.Text), new GraphPropertyInfo("Crag", "name", GraphValueType.Text)]);

    private IRenderedComponent<AskBox> Render(Func<string, GraphPattern> answer, GraphPattern? received = null, Action<GraphPattern>? onAnswer = null, bool enabled = true)
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddMudServices();
        Services.AddSingleton<IConfiguration>(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Extraction:Enabled"] = enabled ? "true" : "false" }).Build());
        Services.AddSingleton<IQuestionCompiler>(new FakeQuestionCompiler(answer));
        Services.AddSingleton(TimeProvider.System);
        Services.AddSingleton<QuestionService>();
        return Render<AskBox>(p => p
            .Add(x => x.Schema, Schema)
            .Add(x => x.OnAnswer, Microsoft.AspNetCore.Components.EventCallback.Factory.Create<GraphPattern>(this, pattern => onAnswer?.Invoke(pattern))));
    }

    [Fact]
    public async Task Enter_asks_and_hands_over_the_checked_pattern()
    {
        GraphPattern? got = null;
        var cut = Render(_ => new GraphPattern([new PatternNode("Route", [new PatternFilter("grade", FilterOperator.Equals, "7a")])], []), onAnswer: p => got = p);

        await cut.Find("input").InputAsync(new Microsoft.AspNetCore.Components.ChangeEventArgs { Value = "routes graded 7a" });
        await cut.Find("input").KeyDownAsync(new KeyboardEventArgs { Key = "Enter" });

        cut.WaitForAssertion(() => Assert.NotNull(got));
        Assert.Equal("Route", got!.Nodes[0].Label);
    }

    [Fact]
    public async Task A_refused_answer_is_explained_and_nothing_runs()
    {
        GraphPattern? got = null;
        var cut = Render(_ => new GraphPattern([new PatternNode("Spaceship", [])], []), onAnswer: p => got = p);

        await cut.Find("input").InputAsync(new Microsoft.AspNetCore.Components.ChangeEventArgs { Value = "spaceships" });
        await cut.Find("input").KeyDownAsync(new KeyboardEventArgs { Key = "Enter" });

        cut.WaitForAssertion(() => Assert.Contains("no type called 'Spaceship'", cut.Markup));
        Assert.Null(got);
    }

    [Fact]
    public void The_example_comes_from_the_models_own_types()
    {
        Assert.Equal("Route records with length m over 10", AskBox.Example(new GraphSchema([], [new GraphPropertyInfo("Route", "length_m", GraphValueType.Number)])));
        Assert.Equal("Route records linked to a Crag", AskBox.Example(Schema));
        Assert.Null(AskBox.Example(new GraphSchema([], [])));
    }

    [Fact]
    public void Escape_is_left_to_the_page_by_marking_the_box_as_a_search_box()
    {
        var cut = Render(_ => throw new InvalidOperationException());

        Assert.True(cut.Find("input").HasAttribute("data-hotkey-search"));
    }

    [Fact]
    public void It_is_hidden_when_sending_text_to_anthropic_is_turned_off()
    {
        var cut = Render(_ => throw new InvalidOperationException(), enabled: false);

        Assert.Empty(cut.FindAll("input"));
    }
}
