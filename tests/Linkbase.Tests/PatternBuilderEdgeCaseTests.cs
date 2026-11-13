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
