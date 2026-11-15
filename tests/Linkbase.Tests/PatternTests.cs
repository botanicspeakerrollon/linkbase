using System.Text.Json.Nodes;
using Linkbase.Data;
using Linkbase.Domains.Climbing.Data;
using Linkbase.Core.Graph;
using Linkbase.Core.Explore;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace Linkbase.Tests;

public class PatternBuilderTests
{
    private static readonly GraphSchema Schema = new(
        [
            new GraphConnection("Crag", "IN", "Country"),
            new GraphConnection("Route", "AT", "Crag"),
            new GraphConnection("Ascent", "OF", "Route"),
        ],
        [
            new GraphPropertyInfo("Crag", "name", GraphValueType.Text),
            new GraphPropertyInfo("Crag", "rock_type", GraphValueType.Text),
            new GraphPropertyInfo("Country", "name", GraphValueType.Text),
            new GraphPropertyInfo("Route", "name", GraphValueType.Text),
            new GraphPropertyInfo("Route", "grade", GraphValueType.Text),
            new GraphPropertyInfo("Route", "length_m", GraphValueType.Number),
            new GraphPropertyInfo("Ascent", "date", GraphValueType.Date),
            new GraphPropertyInfo("Trip", "abroad", GraphValueType.Boolean),
        ]);

    private static PatternBuilder New() => new(Schema);

    private static PatternSuggestion Pick(PatternBuilder builder, string text, string suggestion) =>
        builder.Suggest(text, [], []).First(s => s.Text == suggestion);

    private static GraphPattern RankedChain() => new(
        [new PatternNode("Ascent", []), new PatternNode("Route", []), new PatternNode("Crag", [])],
        [new PatternRel("OF", true), new PatternRel("AT", true)])
    {
        Rank = new PatternRank(2, 0, 5),
    };

    [Fact]
    public void A_loaded_ranking_is_kept_when_the_phrase_is_edited_and_run()
    {
        var builder = New();
        builder.Load(RankedChain());
        builder.Apply(Pick(builder, "", "IN → Country"));

        Assert.Equal(new PatternRank(2, 0, 5), builder.ToPattern()!.Rank);
    }

    private static PatternBuilder ChainOfThree()
    {
        var builder = New();
        builder.Load(new GraphPattern(
            [new PatternNode("Ascent", []), new PatternNode("Route", []), new PatternNode("Crag", [])],
            [new PatternRel("OF", true), new PatternRel("AT", true)]));
        return builder;
    }

    [Fact]
    public void A_number_measure_takes_a_number_field_of_the_counted_step_and_falls_back_when_there_is_none()
    {
        var builder = New();
        builder.Load(RankedChain());
        builder.SetRankCount(1);
        Assert.Equal(["length_m"], builder.NumberKeys(builder.Rank!.Count));

        builder.SetRankMeasure(RankMeasure.Sum);
        Assert.Equal((RankMeasure.Sum, "length_m"), (builder.Rank.Measure, builder.Rank.Key));

        // Counting a type with no number fields leaves the measure as it was, then the swap falls back to counting.
        builder.SetRankCount(0);
        Assert.Equal((RankMeasure.Distinct, (string?)null), (builder.Rank!.Measure, builder.Rank.Key));
        builder.SetRankMeasure(RankMeasure.Highest);
        Assert.Equal(RankMeasure.Distinct, builder.Rank.Measure);

        builder.SetRankFewest(true);
        Assert.True(builder.Rank.Fewest);
    }

    [Fact]
    public void Typing_fewest_offers_a_ranking_that_starts_with_the_fewest()
    {
        var builder = ChainOfThree();
        var offer = Assert.Single(builder.Suggest("fewest", [], []), s => s.Kind == SuggestionKind.Rank);
        builder.Apply(offer);
        Assert.True(builder.Rank!.Fewest);
    }

    [Fact]
    public void A_ranking_can_be_added_by_typing_and_starts_from_the_first_step_to_the_last()
    {
        var builder = ChainOfThree();
        var offer = Pick(builder, "rank", "Rank Ascent by most different Crag");
        Assert.Equal(SuggestionKind.Rank, offer.Kind);

        builder.Apply(offer);

        Assert.Equal(new PatternRank(0, 2, 5), builder.ToPattern()!.Rank);
        Assert.DoesNotContain(builder.Suggest("rank", [], []), s => s.Kind == SuggestionKind.Rank);
    }

    [Theory]
    [InlineData("")]
    [InlineData("ra")]
    [InlineData("grade")]
    public void A_ranking_is_not_offered_unless_asked_for_by_name(string typed)
    {
        Assert.DoesNotContain(ChainOfThree().Suggest(typed, [], []), s => s.Kind == SuggestionKind.Rank);
    }

    [Fact]
    public void A_ranking_needs_a_chain_of_at_least_two_steps()
    {
        var builder = New();
        builder.Apply(Pick(builder, "", "Crag"));
        Assert.DoesNotContain(builder.Suggest("rank", [], []), s => s.Kind == SuggestionKind.Rank);
    }

    [Fact]
    public void A_plain_search_has_no_ranking()
    {
        var builder = New();
        builder.Apply(Pick(builder, "", "Crag"));
        Assert.Null(builder.Rank);
        Assert.Null(builder.ToPattern()!.Rank);
    }

    [Fact]
    public void Removing_a_step_the_ranking_points_at_drops_it()
    {
        var builder = New();
        builder.Load(RankedChain());
        builder.RemoveLast();

        Assert.Null(builder.Rank);
        Assert.Equal(2, builder.ToPattern()!.Nodes.Count);
    }

    [Fact]
    public void Clearing_drops_the_ranking()
    {
        var builder = New();
        builder.Load(RankedChain());
        builder.Clear();
        Assert.Null(builder.Rank);
    }

    [Fact]
    public void The_ranking_can_be_changed_and_never_points_both_ends_at_one_step()
    {
        var builder = New();
        builder.Load(RankedChain());

        builder.SetRankTop(3);
        builder.SetRankGroup(1);
        Assert.Equal(new PatternRank(1, 0, 3), builder.Rank);

        builder.SetRankCount(1);
        Assert.Equal(new PatternRank(0, 1, 3), builder.Rank);

        builder.RemoveRank();
        Assert.Null(builder.Rank);
    }

    [Fact]
    public void The_sentence_and_heading_say_what_each_measure_works_out()
    {
        var route = RankedChain() with { Rank = new PatternRank(2, 1, 5) { Measure = RankMeasure.Sum, Key = "length_m" } };
        Assert.Contains("biggest total length_m across the Route records", PatternRankText.Describe(route));
        Assert.Equal("Total length_m", PatternRankText.Column(route));

        var fewest = route with { Rank = route.Rank! with { Measure = RankMeasure.Average, Fewest = true } };
        Assert.Contains("lowest average length_m", PatternRankText.Describe(fewest));

        var matches = RankedChain() with { Rank = new PatternRank(2, 0, 5) { Measure = RankMeasure.Matches, Fewest = true } };
        Assert.Contains("fewest matches with Ascent records, every match counted even when a record is reached more than once", PatternRankText.Describe(matches));
        Assert.Equal("Matches with Ascent records", PatternRankText.Column(matches));
        Assert.Equal("Different Ascent records", PatternRankText.Column(RankedChain()));
    }

    [Fact]
    public void Steps_are_named_by_type_and_numbered_only_when_a_type_repeats()
    {
        var builder = New();
        builder.Load(new GraphPattern([new PatternNode("Crag", []), new PatternNode("Crag", []), new PatternNode(null, [])],
            [new PatternRel("IN", true), new PatternRel("IN", true)]));
        Assert.Equal(["Crag (step 1)", "Crag (step 2)", "Anything"], builder.StepNames);
    }

    [Fact]
    public void A_ranking_is_described_in_plain_words()
    {
        var text = PatternRankText.Describe(RankedChain());
        Assert.StartsWith("The 5 Crag records that reach the most different Ascent records, by way of Route.", text);

        var one = RankedChain() with { Rank = new PatternRank(2, 1, 1) };
        Assert.StartsWith("The Crag record that reaches the most different Route records, directly.", PatternRankText.Describe(one));
        Assert.Equal("", PatternRankText.Describe(new GraphPattern([new PatternNode("Crag", [])], [])));
    }

    [Fact]
    public void Typing_c_offers_the_crag_and_country_labels()
    {
        // Crag comes first, so C then Enter chooses it.
        var labels = New().Suggest("C", [], []).Where(s => s.Kind == SuggestionKind.Label).Select(s => s.Text);
        Assert.Equal(["Crag", "Country"], labels);
    }

    [Fact]
    public void An_empty_box_lists_every_label()
    {
        var suggestions = New().Suggest("", [], []);
        Assert.Equal(Schema.Labels.Append("Graph model"), suggestions.Select(s => s.Text));
    }

    [Fact]
    public void Types_are_called_types_in_the_suggestions()
    {
        Assert.All(New().Suggest("", [], []).Where(s => s.Kind == SuggestionKind.Label), s => Assert.Equal("Type", s.Hint));
    }

    [Theory]
    [InlineData("graph")]
    [InlineData("GRA")]
    [InlineData("model")]
    [InlineData("schema")]
    public void Graph_offers_the_model_first(string typed) =>
        Assert.Equal(SuggestionKind.Model, New().Suggest(typed, [], []).First().Kind);

    [Fact]
    public void The_model_is_only_offered_before_a_search_has_started()
    {
        var builder = New();
        builder.Apply(Pick(builder, "Crag", "Crag"));
        Assert.DoesNotContain(builder.Suggest("graph", [], []), s => s.Kind == SuggestionKind.Model);
    }

    [Fact]
    public void Any_text_can_be_searched_for_across_everything()
    {
        var builder = New();
        var free = builder.Suggest("no such thing", [], []).Single(s => s.Kind == SuggestionKind.FreeText);
        Assert.Equal("Search everything for \"no such thing\"", free.Text);

        builder.Apply(free);

        var node = builder.ToPattern()!.Nodes.Single();
        Assert.Null(node.Label);
        Assert.Equal(new PatternFilter(PatternFilter.AnyField, FilterOperator.Contains, "no such thing"), node.Filters.Single());
        Assert.Null(builder.Chips.Single().Label);
        Assert.Equal(["contains \"no such thing\""], builder.Chips.Single().Filters);
    }

    [Fact]
    public void Free_text_comes_after_types_so_c_then_enter_still_chooses_crag()
    {
        Assert.Equal("Crag", New().Suggest("C", [], []).First().Text);
        Assert.Equal(SuggestionKind.FreeText, New().Suggest("Lar", [new PropertyMatch("Crag", "name", "Larkfield")], []).First().Kind);
    }

    [Fact]
    public void Free_text_can_narrow_a_chosen_type()
    {
        var builder = New();
        builder.Apply(Pick(builder, "Crag", "Crag"));
        var free = builder.Suggest("windy", [], []).Single(s => s.Kind == SuggestionKind.FreeText);
        Assert.Equal("Crag containing \"windy\"", free.Text);

        builder.Apply(free);

        Assert.Equal("Crag", builder.Chips.Single().Label);
        Assert.Equal(["contains \"windy\""], builder.Chips.Single().Filters);
    }

    [Fact]
    public void Choosing_a_label_makes_a_chip_and_offers_its_relationships_and_properties()
    {
        var builder = New();
        builder.Apply(Pick(builder, "Crag", "Crag"));

        Assert.Equal("Crag", builder.Chips.Single().Label);
        Assert.Empty(builder.Chips.Single().Filters);

        var next = builder.Suggest("", [], []).Select(s => s.Text).ToList();
        Assert.Contains("IN → Country", next);
        Assert.Contains("← AT Route", next);
        Assert.Contains("rock_type", next);
        Assert.DoesNotContain("grade", next);
    }

    [Fact]
    public void A_label_alone_is_a_complete_pattern()
    {
        var builder = New();
        builder.Apply(Pick(builder, "Crag", "Crag"));
        var pattern = builder.ToPattern()!;
        Assert.Equal("Crag", pattern.Nodes.Single().Label);
        Assert.Empty(pattern.Rels);
    }

    [Fact]
    public void A_relationship_extends_the_pattern_to_the_next_label()
    {
        var builder = New();
        builder.Apply(Pick(builder, "Crag", "Crag"));
        builder.Apply(Pick(builder, "", "← AT Route"));

        var pattern = builder.ToPattern()!;
        Assert.Equal(["Crag", "Route"], pattern.Nodes.Select(n => n.Label));
        Assert.Equal(new PatternRel("AT", false), pattern.Rels.Single());

        // Suggestions now follow from Route, not Crag.
        Assert.Contains(builder.Suggest("", [], []), s => s.Text == "grade");
    }

    [Fact]
    public void A_filter_goes_property_then_operator_then_value()
    {
        var builder = New();
        builder.Apply(Pick(builder, "Crag", "Crag"));
        builder.Apply(Pick(builder, "rock", "rock_type"));
        Assert.Equal("rock type", builder.Chips.Single().Pending);

        builder.Apply(Pick(builder, "", "contains"));
        Assert.True(builder.NeedsValues);
        Assert.Equal("rock type contains", builder.Chips.Single().Pending);

        builder.Apply(builder.Suggest("grit", [], []).First());

        Assert.Equal(new PatternFilter("rock_type", FilterOperator.Contains, "grit"), builder.ToPattern()!.Nodes[0].Filters.Single());
        Assert.Null(builder.Chips.Single().Pending);
        Assert.Equal(["rock type contains \"grit\""], builder.Chips.Single().Filters);
    }

    [Fact]
    public void Conditions_sit_inside_their_own_node()
    {
        var builder = New();
        builder.Apply(Pick(builder, "Crag", "Crag"));
        builder.Apply(Pick(builder, "", "← AT Route"));
        builder.Apply(Pick(builder, "grade", "grade"));
        builder.Apply(Pick(builder, "", "="));
        builder.Apply(builder.Suggest("E1", [], []).First());

        Assert.Equal(
            [new PatternChip("Crag", [], null, null), new PatternChip("Route", ["grade = \"E1\""], null, "← AT")],
            builder.Chips,
            new ChipComparer());
    }

    private sealed class ChipComparer : IEqualityComparer<PatternChip>
    {
        public bool Equals(PatternChip? a, PatternChip? b) =>
            a!.Label == b!.Label && a.Filters.SequenceEqual(b.Filters) && a.Pending == b.Pending && a.RelationshipBefore == b.RelationshipBefore;

        public int GetHashCode(PatternChip chip) => chip.Label?.GetHashCode(StringComparison.Ordinal) ?? 0;
    }

    [Fact]
    public void Operators_depend_on_the_property_type()
    {
        var builder = New();
        builder.Apply(Pick(builder, "Route", "Route"));
        builder.Apply(Pick(builder, "grade", "grade"));
        Assert.Contains(builder.Suggest("", [], []), s => s.Text == "contains");
        Assert.DoesNotContain(builder.Suggest("", [], []), s => s.Text == ">");

        builder.RemoveLast();
        builder.Apply(Pick(builder, "length", "length_m"));
        Assert.Contains(builder.Suggest("", [], []), s => s.Text == ">");
        Assert.DoesNotContain(builder.Suggest("", [], []), s => s.Text == "contains");
    }

    [Fact]
    public void A_number_filter_only_offers_a_valid_number()
    {
        var builder = New();
        builder.Apply(Pick(builder, "Route", "Route"));
        builder.Apply(Pick(builder, "length", "length_m"));
        builder.Apply(Pick(builder, "", ">"));

        Assert.Empty(builder.Suggest("tall", [], []));
        Assert.Equal("50", builder.Suggest("50", [], []).Single().Value);
    }

    [Fact]
    public void Database_values_follow_the_typed_value()
    {
        var builder = New();
        builder.Apply(Pick(builder, "Crag", "Crag"));
        builder.Apply(Pick(builder, "rock", "rock_type"));
        builder.Apply(Pick(builder, "", "="));

        var suggestions = builder.Suggest("gr", [], ["Grit", "Granite"]);

        Assert.Equal(["gr", "Grit", "Granite"], suggestions.Select(s => s.Value));
    }

    [Fact]
    public void A_name_match_starts_the_pattern_with_its_label_and_filter()
    {
        var builder = New();
        var match = builder.Suggest("Lar", [new PropertyMatch("Crag", "name", "Larkfield")], []).Single(s => s.Kind == SuggestionKind.Match);
        Assert.Equal("Crag", match.Hint);

        builder.Apply(match);

        var node = builder.ToPattern()!.Nodes.Single();
        Assert.Equal("Crag", node.Label);
        Assert.Equal(new PatternFilter("name", FilterOperator.Equals, "Larkfield"), node.Filters.Single());
    }

    [Fact]
    public void Typing_a_relationship_first_starts_from_any_node()
    {
        var builder = New();
        builder.Apply(Pick(builder, "AT", "AT → Crag"));

        var pattern = builder.ToPattern()!;
        Assert.Equal([null, "Crag"], pattern.Nodes.Select(n => n.Label));
        Assert.Equal(new PatternRel("AT", true), pattern.Rels.Single());
    }

    [Fact]
    public void Backspace_steps_back_one_pill_at_a_time()
    {
        var builder = New();
        builder.Apply(Pick(builder, "Crag", "Crag"));
        builder.Apply(Pick(builder, "", "← AT Route"));
        builder.Apply(Pick(builder, "grade", "grade"));
        builder.Apply(Pick(builder, "", "contains"));
        builder.Apply(builder.Suggest("E1", [], []).First());
        Assert.Single(builder.Chips[1].Filters);

        builder.RemoveLast();
        Assert.Empty(builder.ToPattern()!.Nodes[1].Filters);

        builder.RemoveLast();
        Assert.Equal(["Crag"], builder.ToPattern()!.Nodes.Select(n => n.Label));

        builder.RemoveLast();
        Assert.True(builder.IsEmpty);

        builder.RemoveLast();
        Assert.True(builder.IsEmpty);
    }

    [Fact]
    public void Backspace_mid_filter_undoes_the_operator_then_the_property()
    {
        var builder = New();
        builder.Apply(Pick(builder, "Crag", "Crag"));
        builder.Apply(Pick(builder, "rock", "rock_type"));
        builder.Apply(Pick(builder, "", "contains"));

        builder.RemoveLast();
        Assert.False(builder.NeedsValues);
        Assert.Equal("rock_type", builder.PendingKey);

        builder.RemoveLast();
        Assert.Null(builder.PendingKey);
    }

    [Fact]
    public void An_unfinished_filter_is_left_out_of_the_pattern()
    {
        var builder = New();
        builder.Apply(Pick(builder, "Crag", "Crag"));
        builder.Apply(Pick(builder, "rock", "rock_type"));
        Assert.Empty(builder.ToPattern()!.Nodes[0].Filters);
    }
}

public class GraphPatternSqlTests
{
    private static readonly GraphSchema Schema = new(
        [new GraphConnection("Crag", "WORKS_ON", "Route")],
        [
            new GraphPropertyInfo("Crag", "name", GraphValueType.Text),
            new GraphPropertyInfo("Route", "length_m", GraphValueType.Number),
            new GraphPropertyInfo("Ascent", "date", GraphValueType.Date),
            new GraphPropertyInfo("Crag", "bad'; DROP TABLE route; --", GraphValueType.Text),
        ])
    {
        LinkProperties = [new GraphLinkPropertyInfo("WORKS_ON", "role", GraphValueType.Text)],
    };

    private static GraphPattern Single(string? label, params PatternFilter[] filters) => new([new PatternNode(label, filters)], []);

    private static GraphPattern Linked(PatternRel rel) => new([new PatternNode("Crag", []), new PatternNode(null, [])], [rel]);

    [Fact]
    public void Values_are_parameters_and_never_reach_the_sql_text()
    {
        var value = "x'; DROP TABLE route; --";
        var (sql, parameters) = GraphPatternSql.Build(Single("Crag", new PatternFilter("name", FilterOperator.Contains, value)), Schema, 10, 0);

        Assert.DoesNotContain("DROP", sql);
        Assert.Contains(parameters, p => p.Value is string text && text.Contains("DROP"));
    }

    [Theory]
    [InlineData("Crag'; DROP TABLE route; --")]
    [InlineData("NotALabel")]
    public void Unknown_labels_are_rejected(string label) =>
        Assert.Throws<LinkbaseValidationException>(() => GraphPatternSql.Build(Single(label), Schema, 10, 0));

    [Fact]
    public void Unknown_relationships_are_rejected()
    {
        var pattern = new GraphPattern([new PatternNode("Crag", []), new PatternNode("Route", [])], [new PatternRel("AT'; --", true)]);
        Assert.Throws<LinkbaseValidationException>(() => GraphPatternSql.Build(pattern, Schema, 10, 0));
    }

    [Theory]
    [InlineData("nothing_like_this")]
    [InlineData("bad'; DROP TABLE route; --")]
    public void Unknown_or_unsafe_property_names_are_rejected(string key) =>
        Assert.Throws<LinkbaseValidationException>(() =>
            GraphPatternSql.Build(Single("Crag", new PatternFilter(key, FilterOperator.Contains, "x")), Schema, 10, 0));

    [Fact]
    public void A_filter_on_a_links_own_value_reaches_the_edges_props()
    {
        var (sql, parameters) = GraphPatternSql.Build(Linked(new PatternRel("WORKS_ON", true) { Filters = [new PatternFilter("role", FilterOperator.Contains, "Lead")] }), Schema, 10, 0);

        Assert.Contains("e0.props->>'role'", sql);
        Assert.Contains(parameters, p => p.Value is string text && text.Contains("Lead"));
    }

    [Fact]
    public void An_unknown_value_on_a_link_is_rejected() =>
        Assert.Throws<LinkbaseValidationException>(() =>
            GraphPatternSql.Build(Linked(new PatternRel("WORKS_ON", true) { Filters = [new PatternFilter("nope", FilterOperator.Equals, "x")] }), Schema, 10, 0));

    [Fact]
    public void Free_text_doesnt_work_on_a_links_own_filter() =>
        Assert.Throws<LinkbaseValidationException>(() =>
            GraphPatternSql.Build(Linked(new PatternRel("WORKS_ON", true) { Filters = [new PatternFilter(PatternFilter.AnyField, FilterOperator.Contains, "x")] }), Schema, 10, 0));

    [Fact]
    public void Bad_numbers_and_dates_are_rejected()
    {
        Assert.Throws<LinkbaseValidationException>(() =>
            GraphPatternSql.Build(Single("Route", new PatternFilter("length_m", FilterOperator.GreaterThan, "tall")), Schema, 10, 0));
        Assert.Throws<LinkbaseValidationException>(() =>
            GraphPatternSql.Build(Single("Ascent", new PatternFilter("date", FilterOperator.GreaterThan, "soon")), Schema, 10, 0));
    }

    [Fact]
    public void Text_comparisons_do_not_apply_to_numbers()
    {
        Assert.Throws<LinkbaseValidationException>(() =>
            GraphPatternSql.Build(Single("Route", new PatternFilter("length_m", FilterOperator.Contains, "5")), Schema, 10, 0));
        Assert.Throws<LinkbaseValidationException>(() =>
            GraphPatternSql.Build(Single("Crag", new PatternFilter("name", FilterOperator.GreaterThan, "a")), Schema, 10, 0));
    }

    [Fact]
    public void Wildcards_in_a_value_are_taken_literally()
    {
        var (_, parameters) = GraphPatternSql.Build(Single("Crag", new PatternFilter("name", FilterOperator.Contains, "50%_x")), Schema, 10, 0);
        Assert.Contains(parameters, p => (p.Value as string) == @"%50\%\_x%");
    }

    [Fact]
    public void A_ranking_groups_by_one_position_and_counts_distinct_nodes_at_another()
    {
        var pattern = new GraphPattern([new PatternNode("Crag", []), new PatternNode("Route", [])], [new PatternRel("WORKS_ON", true)]) { Rank = new PatternRank(0, 1, 5) };

        var (sql, parameters) = GraphPatternSql.BuildRank(pattern, Schema);

        Assert.Contains("count(DISTINCT n1.node_id)", sql);
        Assert.Contains("GROUP BY n0.node_id", sql);
        Assert.Contains(parameters, p => p.Value is int top && top == 5);
    }

    [Theory]
    [InlineData(0, 0, 5)]
    [InlineData(0, 2, 5)]
    [InlineData(-1, 1, 5)]
    [InlineData(0, 1, 0)]
    [InlineData(0, 1, 26)]
    public void A_ranking_that_doesnt_fit_its_chain_is_rejected(int group, int count, int top)
    {
        var pattern = new GraphPattern([new PatternNode("Crag", []), new PatternNode("Route", [])], [new PatternRel("WORKS_ON", true)]) { Rank = new PatternRank(group, count, top) };

        Assert.Throws<LinkbaseValidationException>(() => GraphPatternSql.BuildRank(pattern, Schema));
    }

    [Fact]
    public void A_search_with_no_ranking_has_nothing_to_rank() =>
        Assert.Throws<LinkbaseValidationException>(() => GraphPatternSql.BuildRank(Single("Crag"), Schema));

    [Fact]
    public void A_summary_totals_each_number_property_of_the_typed_nodes()
    {
        var (sql, _) = GraphPatternSql.BuildSummary(Single("Route"), Schema);

        Assert.NotNull(sql);
        Assert.Contains("'length_m'", sql);
        Assert.Contains("sum(v)", sql);
    }

    [Fact]
    public void A_summary_has_nothing_to_total_without_a_typed_node_with_number_properties()
    {
        Assert.Null(GraphPatternSql.BuildSummary(Single("Crag"), Schema).Sql);
        Assert.Null(GraphPatternSql.BuildSummary(Single(null), Schema).Sql);
    }

    [Fact]
    public void A_summary_keeps_the_values_of_the_filters_as_parameters()
    {
        var (sql, parameters) = GraphPatternSql.BuildSummary(
            Single("Route", new PatternFilter("length_m", FilterOperator.GreaterThan, "20")), Schema);

        Assert.DoesNotContain("20", sql!.Replace("length_m", ""));
        Assert.Contains(parameters, p => p.Value is decimal d && d == 20);
    }

    [Fact]
    public void A_ranking_by_a_number_must_use_a_number_field_of_the_counted_type()
    {
        var schema = Schema;
        var chain = new GraphPattern([new PatternNode("Crag", []), new PatternNode("Route", [])], [new PatternRel("WORKS_ON", true)]);

        Assert.Contains("length_m", GraphPatternSql.BuildRank(chain with { Rank = new PatternRank(0, 1, 5) { Measure = RankMeasure.Sum, Key = "length_m" } }, schema).Sql);
        foreach (var key in new[] { null, "name", "length_m') OR 1=1 --", "missing" })
        {
            Assert.Throws<LinkbaseValidationException>(() => GraphPatternSql.BuildRank(chain with { Rank = new PatternRank(0, 1, 5) { Measure = RankMeasure.Sum, Key = key } }, schema));
        }

        Assert.Throws<LinkbaseValidationException>(() => GraphPatternSql.BuildRank(chain with { Rank = new PatternRank(0, 1, 5) { Measure = (RankMeasure)99 } }, schema));
    }
}

[Trait("Category", "Database")]
public class GraphPatternQueryTests(PostgresFixture fixture) : IAsyncLifetime, IClassFixture<PostgresFixture>
{
    private ModelFactory _store = null!;

    public async Task InitializeAsync() => _store = await ClimbingStore.CreateAsync(fixture);

    public Task DisposeAsync() => Task.CompletedTask;

    private static string Word() => Guid.NewGuid().ToString("N")[..12];

    private GraphRepository Graph() => new(_store, Linkbase.Domains.RegisteredDomains.Registry, Linkbase.Core.Security.FieldPolicy.Unrestricted);

    private async Task<(string Crag, string Route, string AscentId, string RouteId, string CragId)> CreateAsync(
        string grade = "VS 4c", int length = 20, string date = "2020-05-02", string? notes = null)
    {
        var (crag, route) = ($"Crag {Word()}", $"Route {Word()}");
        var area = await ClimbingStore.AddCountryAsync(_store, $"Country {Word()}");
        var cragId = await ClimbingStore.AddAsync(_store, "Crag", crag, null, "IN", area);
        var routeId = await ClimbingStore.AddAsync(
            _store, "Route", route, new JsonObject { ["grade"] = grade, ["grade_system"] = "uk_trad", ["discipline"] = "trad", ["length_m"] = length, ["pitches"] = 1 }, "AT", cragId);
        var props = new JsonObject { ["date"] = date, ["date_precision"] = "day", ["style"] = "onsight", ["source"] = "manual" };
        if (notes is not null)
        {
            props["notes"] = notes;
        }

        var ascentId = await ClimbingStore.AddAsync(_store, "Ascent", route, props, "OF", routeId);
        return (crag, route, ascentId, routeId, cragId);
    }

    [Fact]
    public async Task Schema_describes_the_labels_links_and_property_types()
    {
        await CreateAsync();

        var schema = await Graph().SchemaAsync();

        Assert.Contains(new GraphConnection("Route", "AT", "Crag"), schema.Connections);
        Assert.Contains(new GraphConnection("Ascent", "OF", "Route"), schema.Connections);
        Assert.Contains(new GraphPropertyInfo("Crag", "name", GraphValueType.Text), schema.Properties);
        Assert.Contains(new GraphPropertyInfo("Route", "grade", GraphValueType.Text), schema.Properties);
        Assert.Contains(new GraphPropertyInfo("Route", "length_m", GraphValueType.Number), schema.Properties);
        Assert.Contains(new GraphPropertyInfo("Ascent", "date", GraphValueType.Date), schema.Properties);
    }

    [Fact]
    public async Task A_filtered_label_finds_its_node()
    {
        var s = await CreateAsync();
        var schema = await Graph().SchemaAsync();

        var result = await Graph().RunPatternAsync(
            new GraphPattern([new PatternNode("Crag", [new PatternFilter("name", FilterOperator.Equals, s.Crag.ToUpperInvariant())])], []),
            schema, 10, 0);

        Assert.Equal(s.CragId, result.NodeIds.Single());
        Assert.Equal(0, result.Remaining);
    }

    [Fact]
    public async Task A_pattern_follows_relationships_in_their_direction_and_returns_every_node()
    {
        var s = await CreateAsync();
        var schema = await Graph().SchemaAsync();
        var crag = new PatternNode("Crag", [new PatternFilter("name", FilterOperator.Equals, s.Crag)]);

        var result = await Graph().RunPatternAsync(
            new GraphPattern([crag, new PatternNode("Route", []), new PatternNode("Ascent", [])], [new PatternRel("AT", false), new PatternRel("OF", false)]),
            schema, 10, 0);

        Assert.Equal([s.CragId, s.RouteId, s.AscentId], result.NodeIds);

        var wrongWay = await Graph().RunPatternAsync(
            new GraphPattern([crag, new PatternNode("Route", [])], [new PatternRel("AT", true)]),
            schema, 10, 0);
        Assert.Empty(wrongWay.NodeIds);
    }

    [Fact]
    public async Task Number_date_and_text_filters_compare_as_their_type()
    {
        var s = await CreateAsync(grade: "E2 5c", length: 90, date: "2021-07-01");
        var schema = await Graph().SchemaAsync();
        var byName = new PatternFilter("name", FilterOperator.Equals, s.Route);

        async Task<int> CountAsync(string label, params PatternFilter[] filters) =>
            (await Graph().RunPatternAsync(new GraphPattern([new PatternNode(label, filters)], []), schema, 10, 0)).NodeIds.Count;

        // As numbers, 90 is less than 100. As text it wouldn't be.
        Assert.Equal(1, await CountAsync("Route", byName, new PatternFilter("length_m", FilterOperator.LessThan, "100")));
        Assert.Equal(0, await CountAsync("Route", byName, new PatternFilter("length_m", FilterOperator.GreaterThan, "100")));
        Assert.Equal(1, await CountAsync("Route", byName, new PatternFilter("grade", FilterOperator.StartsWith, "e2")));
        Assert.Equal(0, await CountAsync("Route", byName, new PatternFilter("grade", FilterOperator.EndsWith, "e2")));

        var ascentFilter = new PatternFilter("name", FilterOperator.Equals, s.Route);
        Assert.Equal(1, await CountAsync("Ascent", ascentFilter, new PatternFilter("date", FilterOperator.GreaterOrEqual, "2021-07-01")));
        Assert.Equal(0, await CountAsync("Ascent", ascentFilter, new PatternFilter("date", FilterOperator.LessThan, "2021-07-01")));
    }

    [Fact]
    public async Task Limit_and_offset_page_through_matches()
    {
        var suffix = Word();
        var area = await ClimbingStore.AddCountryAsync(_store, $"Country {Word()}");
        var crag = await ClimbingStore.AddAsync(_store, "Crag", $"Crag {Word()}", null, "IN", area);
        for (var i = 0; i < 3; i++)
        {
            await ClimbingStore.AddAsync(_store, "Route", $"Page {suffix} {i}", null, "AT", crag);
        }

        var schema = await Graph().SchemaAsync();
        var pattern = new GraphPattern([new PatternNode("Route", [new PatternFilter("name", FilterOperator.Contains, suffix)])], []);

        var first = await Graph().RunPatternAsync(pattern, schema, 2, 0);
        Assert.Equal(2, first.NodeIds.Count);
        Assert.Equal(1, first.Remaining);

        var second = await Graph().RunPatternAsync(pattern, schema, 2, 2);
        Assert.Single(second.NodeIds);
        Assert.Equal(0, second.Remaining);
        Assert.Empty(first.NodeIds.Intersect(second.NodeIds));
    }

    [Fact]
    public async Task A_summary_totals_a_number_property_over_every_match_and_counts_each_node_once()
    {
        var area = await ClimbingStore.AddCountryAsync(_store, $"Country {Word()}");
        var cragName = $"Crag {Word()}";
        var crag = await ClimbingStore.AddAsync(_store, "Crag", cragName, null, "IN", area);
        string? firstRoute = null;
        foreach (var length in new[] { 10, 20, 30 })
        {
            var route = await ClimbingStore.AddAsync(
                _store, "Route", $"Route {Word()}", new JsonObject { ["grade"] = "VS 4c", ["grade_system"] = "uk_trad", ["discipline"] = "trad", ["length_m"] = length, ["pitches"] = 1 }, "AT", crag);
            firstRoute ??= route;
        }

        // Two ascents of one route make that route match twice, but it still counts once.
        static JsonObject Ascent() => new() { ["date"] = "2020-05-02", ["date_precision"] = "day", ["style"] = "onsight", ["source"] = "manual" };
        await ClimbingStore.AddAsync(_store, "Ascent", $"Ascent {Word()}", Ascent(), "OF", firstRoute!);
        await ClimbingStore.AddAsync(_store, "Ascent", $"Ascent {Word()}", Ascent(), "OF", firstRoute!);

        var schema = await Graph().SchemaAsync();
        var pattern = new GraphPattern(
            [new PatternNode("Crag", [new PatternFilter("name", FilterOperator.Equals, cragName)]), new PatternNode("Route", []), new PatternNode("Ascent", [])],
            [new PatternRel("AT", false), new PatternRel("OF", false)]);

        var summary = await Graph().SummarisePatternAsync(pattern, schema);

        var withAscents = Assert.Single(summary, s => s is { Label: "Route", Key: "length_m" });
        Assert.Equal((1, 10m), (withAscents.Count, withAscents.Sum));

        // Without the ascents, all three routes are matched.
        var routes = new GraphPattern(
            [new PatternNode("Crag", [new PatternFilter("name", FilterOperator.Equals, cragName)]), new PatternNode("Route", [])],
            [new PatternRel("AT", false)]);
        var all = Assert.Single(await Graph().SummarisePatternAsync(routes, schema), s => s is { Label: "Route", Key: "length_m" });
        Assert.Equal((3, 60m, 10m, 30m, 20m), (all.Count, all.Sum, all.Min, all.Max, all.Average));
    }

    [Fact]
    public async Task A_ranking_lists_the_groups_with_the_most_distinct_matches_first()
    {
        var area = await ClimbingStore.AddCountryAsync(_store, $"Country {Word()}");
        var word = Word();
        static JsonObject Ascent() => new() { ["date"] = "2020-05-02", ["date_precision"] = "day", ["style"] = "onsight", ["source"] = "manual" };
        static JsonObject RouteProps() => new() { ["grade"] = "VS 4c", ["grade_system"] = "uk_trad", ["discipline"] = "trad", ["length_m"] = 20, ["pitches"] = 1 };

        // Big has two routes and three ascents, one route being climbed twice. Small has one. Empty has a route and no ascents.
        var big = await ClimbingStore.AddAsync(_store, "Crag", $"Big {word}", null, "IN", area);
        var small = await ClimbingStore.AddAsync(_store, "Crag", $"Small {word}", null, "IN", area);
        var empty = await ClimbingStore.AddAsync(_store, "Crag", $"Empty {word}", null, "IN", area);
        var bigRoutes = new[] { await ClimbingStore.AddAsync(_store, "Route", $"R {Word()}", RouteProps(), "AT", big), await ClimbingStore.AddAsync(_store, "Route", $"R {Word()}", RouteProps(), "AT", big) };
        var smallRoute = await ClimbingStore.AddAsync(_store, "Route", $"R {Word()}", RouteProps(), "AT", small);
        await ClimbingStore.AddAsync(_store, "Route", $"R {Word()}", RouteProps(), "AT", empty);
        foreach (var route in new[] { bigRoutes[0], bigRoutes[0], bigRoutes[1], smallRoute })
        {
            await ClimbingStore.AddAsync(_store, "Ascent", $"Ascent {Word()}", Ascent(), "OF", route);
        }

        var schema = await Graph().SchemaAsync();
        var pattern = new GraphPattern(
            [new PatternNode("Crag", [new PatternFilter("name", FilterOperator.Contains, word)]), new PatternNode("Route", []), new PatternNode("Ascent", [])],
            [new PatternRel("AT", false), new PatternRel("OF", false)])
        { Rank = new PatternRank(0, 2, 5) };

        var ranked = await Graph().RankPatternAsync(pattern, schema);

        Assert.Equal([(big, 3m), (small, 1m)], ranked.Select(r => (r.NodeId, r.Value)));
        Assert.StartsWith("Big", ranked[0].Name);

        var top = await Graph().RankPatternAsync(pattern with { Rank = new PatternRank(0, 2, 1) }, schema);
        Assert.Equal(big, Assert.Single(top).NodeId);
    }

    [Fact]
    public async Task A_ranking_can_total_average_or_take_the_highest_of_a_number_and_can_put_the_fewest_first()
    {
        var area = await ClimbingStore.AddCountryAsync(_store, $"Country {Word()}");
        var word = Word();
        static JsonObject RouteProps(int length) => new() { ["grade"] = "VS 4c", ["grade_system"] = "uk_trad", ["discipline"] = "trad", ["length_m"] = length, ["pitches"] = 1 };

        // Big has routes of 10 and 30 metres, Small has one of 15. Bare has a route with no length at all, so it never appears.
        var big = await ClimbingStore.AddAsync(_store, "Crag", $"Big {word}", null, "IN", area);
        var small = await ClimbingStore.AddAsync(_store, "Crag", $"Small {word}", null, "IN", area);
        var bare = await ClimbingStore.AddAsync(_store, "Crag", $"Bare {word}", null, "IN", area);
        await ClimbingStore.AddAsync(_store, "Route", $"R {Word()}", RouteProps(10), "AT", big);
        await ClimbingStore.AddAsync(_store, "Route", $"R {Word()}", RouteProps(30), "AT", big);
        await ClimbingStore.AddAsync(_store, "Route", $"R {Word()}", RouteProps(15), "AT", small);
        await ClimbingStore.AddAsync(_store, "Route", $"R {Word()}", new JsonObject { ["grade"] = "VS 4c", ["grade_system"] = "uk_trad", ["discipline"] = "trad", ["pitches"] = 1 }, "AT", bare);

        var schema = await Graph().SchemaAsync();
        var pattern = new GraphPattern(
            [new PatternNode("Crag", [new PatternFilter("name", FilterOperator.Contains, word)]), new PatternNode("Route", [])],
            [new PatternRel("AT", false)]);

        async Task<List<(string, decimal)>> Run(PatternRank rank) =>
            (await Graph().RankPatternAsync(pattern with { Rank = rank }, schema)).Select(r => (r.NodeId, r.Value)).ToList();

        Assert.Equal([(big, 40m), (small, 15m)], await Run(new PatternRank(0, 1, 5) { Measure = RankMeasure.Sum, Key = "length_m" }));
        Assert.Equal([(big, 20m), (small, 15m)], await Run(new PatternRank(0, 1, 5) { Measure = RankMeasure.Average, Key = "length_m" }));
        Assert.Equal([(big, 30m), (small, 15m)], await Run(new PatternRank(0, 1, 5) { Measure = RankMeasure.Highest, Key = "length_m" }));
        Assert.Equal([(small, 15m), (big, 40m)], await Run(new PatternRank(0, 1, 5) { Measure = RankMeasure.Sum, Key = "length_m", Fewest = true }));

        // Counts list every crag with a route, fewest first, ties by name. Bare's route has no length, so a number measure skips it.
        Assert.Equal([(bare, 1m), (small, 1m), (big, 2m)], await Run(new PatternRank(0, 1, 5) { Measure = RankMeasure.Matches, Fewest = true }));
        Assert.Equal([(big, 2m), (bare, 1m), (small, 1m)], await Run(new PatternRank(0, 1, 5) { Measure = RankMeasure.Distinct }));
    }

    [Fact]
    public async Task A_summary_of_a_search_with_no_matches_has_a_count_of_zero()
    {
        var schema = await Graph().SchemaAsync();
        var none = new GraphPattern([new PatternNode("Route", [new PatternFilter("name", FilterOperator.Equals, $"Nothing {Word()}")])], []);

        var row = Assert.Single(await Graph().SummarisePatternAsync(none, schema), s => s is { Label: "Route", Key: "length_m" });

        Assert.Equal(0, row.Count);
        Assert.Null(row.Sum);
    }

    [Fact]
    public async Task Typing_part_of_a_name_or_value_suggests_matches()
    {
        var s = await CreateAsync(grade: $"G{Word()}");
        var word = s.Crag.Split(' ')[1];

        var byName = await Graph().MatchPropertiesAsync(null, word, default);
        Assert.Contains(new PropertyMatch("Crag", "name", s.Crag), byName);

        var withinLabel = await Graph().MatchPropertiesAsync("Route", word, default);
        Assert.DoesNotContain(withinLabel, m => m.Label == "Crag");

        var grade = (await Graph().SubgraphAsync([s.RouteId])).Nodes.Single().Property("grade")!;
        Assert.Contains(await Graph().MatchPropertiesAsync("Route", grade[1..], default), m => m.Key == "grade" && m.Value == grade);
    }

    [Fact]
    public async Task Long_notes_are_not_suggested_as_matches()
    {
        var word = Word();
        await CreateAsync(notes: $"A long private note about {word}");

        Assert.DoesNotContain(await Graph().MatchPropertiesAsync(null, word), m => m.Key == "notes");
    }

    private async Task<IReadOnlyList<string>> FreeTextAsync(string text, string? label = null)
    {
        var filter = new PatternFilter(PatternFilter.AnyField, FilterOperator.Contains, text);
        var result = await Graph().RunPatternAsync(new GraphPattern([new PatternNode(label, [filter])], []), await Graph().SchemaAsync(), 100, 0);
        return result.NodeIds;
    }

    [Fact]
    public async Task Free_text_finds_names_and_any_property_including_notes()
    {
        var word = Word();
        var s = await CreateAsync(grade: $"G{Word()}", notes: $"Windy day {word}");

        Assert.Equal([s.CragId], await FreeTextAsync(s.Crag.Split(' ')[1].ToUpperInvariant()));
        Assert.Equal([s.AscentId], await FreeTextAsync(word));

        var grade = (await Graph().SubgraphAsync([s.RouteId])).Nodes.Single().Property("grade")!;
        Assert.Contains(s.RouteId, await FreeTextAsync(grade));
    }

    [Fact]
    public async Task Free_text_doesnt_repeat_every_ascent_of_a_route_and_stays_within_a_type()
    {
        var s = await CreateAsync();
        var word = s.Route.Split(' ')[1];

        Assert.Equal([s.RouteId], await FreeTextAsync(word));
        Assert.Empty(await FreeTextAsync(word, "Crag"));
    }

    [Fact]
    public async Task Free_text_finds_an_ascent_by_its_own_notes_even_when_they_repeat_its_routes_name()
    {
        var word = Word();
        var area = await ClimbingStore.AddCountryAsync(_store, $"Country {Word()}");
        var crag = await ClimbingStore.AddAsync(_store, "Crag", $"Crag {Word()}", null, "IN", area);
        var route = await ClimbingStore.AddAsync(_store, "Route", $"Route {word}", null, "AT", crag);
        var named = await ClimbingStore.AddAsync(_store, "Ascent", $"Route {word}", new JsonObject { ["date"] = "2020-01-01", ["date_precision"] = "day", ["style"] = "lead", ["source"] = "manual" }, "OF", route);
        var noted = await ClimbingStore.AddAsync(_store, "Ascent", $"Route {word}", new JsonObject { ["date"] = "2020-01-02", ["date_precision"] = "day", ["style"] = "lead", ["source"] = "manual", ["notes"] = $"Mentions {word} again" }, "OF", route);

        var found = await FreeTextAsync(word);

        Assert.Contains(route, found);
        Assert.Contains(noted, found);
        Assert.DoesNotContain(named, found);
    }

    [Fact]
    public async Task Free_text_tolerates_a_typo()
    {
        var word = Word();
        await ClimbingStore.AddAsync(_store, "Crag", $"Zz{word}x");
        await ClimbingStore.AddAsync(_store, "Crag", $"Zz{word}y");

        var misspelt = await FreeTextAsync($"Zz{word[..^1]}q");
        Assert.Equal(2, misspelt.Count);
    }

    [Fact]
    public async Task Free_text_matches_values_and_never_property_names()
    {
        var word = Word();
        var s = await CreateAsync(grade: $"grade{word}");

        Assert.Contains(s.RouteId, await FreeTextAsync($"grade{word}"));
        Assert.DoesNotContain(s.RouteId, await FreeTextAsync("length_m"));
    }

    [Fact]
    public async Task Free_text_takes_wildcards_literally()
    {
        await CreateAsync();
        Assert.Empty(await FreeTextAsync("%%%" + Word()));
    }

    [Fact]
    public async Task Node_counts_are_per_type()
    {
        await CreateAsync();
        var counts = await Graph().NodeCountsAsync();
        Assert.True(counts["Crag"] > 0);
        Assert.True(counts["Ascent"] > 0);
    }

    [Fact]
    public async Task Property_values_are_suggested_for_a_filter()
    {
        var grade = $"G{Word()}";
        var s = await CreateAsync(grade: grade);

        Assert.Contains(grade, await Graph().PropertyValuesAsync("Route", "grade", grade[..6]));
        Assert.Contains(s.Crag, await Graph().PropertyValuesAsync("Crag", "name", s.Crag));
        Assert.Empty(await Graph().PropertyValuesAsync("Route", "grade", Word()));
    }

    [Fact]
    public async Task Relationship_counts_cover_both_directions()
    {
        var s = await CreateAsync();

        var counts = await Graph().RelationshipCountsAsync(s.RouteId);

        Assert.Contains(new RelationshipCount("AT", true, "Crag", 1), counts);
        Assert.Contains(new RelationshipCount("OF", false, "Ascent", 1), counts);
    }
}
