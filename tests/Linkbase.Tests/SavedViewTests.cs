using Bunit;
using Linkbase.Core;
using Linkbase.Core.Graph;
using Linkbase.Core.Records;
using Linkbase.Core.Views;
using Linkbase.Data;
using Microsoft.AspNetCore.Components;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Linkbase.Tests;

public class ViewStateTests
{
    [Fact]
    public void State_survives_the_address_and_uses_only_characters_an_address_needs_no_escaping_for()
    {
        var state = new ExploreStateForTest().Make();
        var text = ViewState.Encode(state);

        Assert.Matches("^[A-Za-z0-9_-]+$", text);
        var back = ViewState.Decode<ExploreState>(text)!;
        Assert.Equal(state.Pattern!.Nodes[0].Label, back.Pattern!.Nodes[0].Label);
        Assert.Equal(Linkbase.Core.Graph.FilterOperator.GreaterOrEqual, back.Pattern.Nodes[0].Filters[0].Operator);
        Assert.Equal(state.Nodes, back.Nodes);
        Assert.True(back.Totals);
    }

    [Fact]
    public void A_pinned_layouts_positions_survive_the_address()
    {
        var state = new ExploreState { Nodes = ["crag:1"], Positions = [new ScenePosition("crag:1", 1.5, -2.5)] };

        var back = ViewState.Decode<ExploreState>(ViewState.Encode(state))!;

        Assert.Equal(state.Positions, back.Positions);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("!!!")]
    [InlineData("e30")]
    [InlineData("bm90LWpzb24")]
    public void Text_that_isnt_a_view_reads_as_nothing_or_as_an_empty_view(string? text)
    {
        var state = ViewState.Decode<ExploreState>(text);

        Assert.True(state is null || state.IsEmpty);
    }

    [Fact]
    public void Text_that_is_too_long_isnt_read()
    {
        var long_ = ViewState.Encode(new ListState { Search = new string('a', ViewState.MaxLength) });

        Assert.Null(ViewState.Decode<ListState>(long_));
    }

    [Fact]
    public void An_empty_state_has_no_address()
    {
        Assert.Null(new ListState().Encode());
        Assert.Null(new ExploreState().Encode());
        Assert.NotNull(new ListState { Search = "x" }.Encode());
    }

    [Fact]
    public void A_views_link_opens_the_page_it_was_saved_from()
    {
        Assert.Equal("/explore?v=abc", ViewState.Href(SavedViewKind.Explore, null, "abc"));
        Assert.Equal("/data/Route?v=abc", ViewState.Href(SavedViewKind.List, "Route", "abc"));
        Assert.Equal("/data/Big%20Wall?v=abc", ViewState.Href(SavedViewKind.List, "Big Wall", "abc"));
    }

    private sealed class ExploreStateForTest
    {
        public ExploreState Make() => new()
        {
            Pattern = new GraphPattern([new PatternNode("Route", [new PatternFilter("length_m", Linkbase.Core.Graph.FilterOperator.GreaterOrEqual, "20")])], []),
            Nodes = ["route:1", "crag:2"],
            Totals = true,
        };
    }
}

[Trait("Category", "Database")]
public class SavedViewRepositoryTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    private static string Unique(string name) => $"{name} {Guid.NewGuid():N}";

    private async Task<SavedViewRepository> CreateAsync() => new(await ClimbingStore.CreateAsync(fixture));

    [Fact]
    public async Task Saving_under_an_existing_name_replaces_the_view_and_keeps_it_pinned()
    {
        var views = await CreateAsync();
        var name = Unique("Mine");
        await views.SaveAsync($"  {name}  ", SavedViewKind.Explore, null, "one", pin: true);
        await views.SaveAsync(name, SavedViewKind.List, "Route", "two", pin: false);

        var saved = Assert.Single(await views.ListAsync());
        Assert.Equal((name, SavedViewKind.List, "Route", "two", true), (saved.Name, saved.Kind, saved.Scope, saved.State, saved.Pinned));
    }

    [Fact]
    public async Task Views_are_listed_by_name_and_only_pinned_ones_are_on_Home()
    {
        var views = await CreateAsync();
        await views.SaveAsync("b", SavedViewKind.Explore, null, "1", pin: true);
        await views.SaveAsync("a", SavedViewKind.Explore, null, "2", pin: false);

        Assert.Equal(["a", "b"], (await views.ListAsync()).Select(v => v.Name));
        Assert.Equal(["b"], (await views.PinnedAsync()).Select(v => v.Name));

        var a = (await views.ListAsync()).First();
        await views.SetPinnedAsync(a.Id, true);
        Assert.Equal(["a", "b"], (await views.PinnedAsync()).Select(v => v.Name));
        await views.SetPinnedAsync(a.Id, false);
        await views.DeleteAsync(a.Id);
        Assert.Equal(["b"], (await views.ListAsync()).Select(v => v.Name));
    }

    [Theory]
    [InlineData("", "state")]
    [InlineData("   ", "state")]
    [InlineData("name", "")]
    public async Task A_view_needs_a_name_and_a_state(string name, string state)
    {
        var views = await CreateAsync();

        await Assert.ThrowsAsync<LinkbaseValidationException>(() => views.SaveAsync(name, SavedViewKind.Explore, null, state, false));
    }

    [Fact]
    public async Task A_name_or_a_state_that_is_too_long_is_refused()
    {
        var views = await CreateAsync();

        await Assert.ThrowsAsync<LinkbaseValidationException>(() => views.SaveAsync(new string('n', SavedViewRepository.MaxNameLength + 1), SavedViewKind.Explore, null, "s", false));
        await Assert.ThrowsAsync<LinkbaseValidationException>(() => views.SaveAsync("n", SavedViewKind.Explore, null, new string('s', ViewState.MaxLength + 1), false));
    }
}

[Trait("Category", "Database")]
public class RecordListViewTests(PostgresFixture fixture) : UiTestBase(fixture), IClassFixture<PostgresFixture>
{
    private async Task UseExampleAsync()
    {
        var factory = new ModelFactory(await Fixture.CreateModelDatabaseAsync("SELECT 1"));
        await TestModels.LoadAsync(factory, RepoPaths.Resolve("models/consultancy/model.json"), TextWriter.Null);
        Services.AddSingleton<IDbContextFactory<LinkbaseDbContext>>(factory);
    }

    private string Address => Services.GetRequiredService<NavigationManager>().Uri;

    [Fact]
    public async Task A_permalink_opens_the_list_searched_and_sorted()
    {
        await UseExampleAsync();
        var address = new ListState { Search = "Dev", Sort = "key", Descending = true }.Encode();
        Services.GetRequiredService<NavigationManager>().NavigateTo(ViewState.Href(SavedViewKind.List, "Person", address!));
        var cut = RenderPage<RecordListPage>(p => p.Add(x => x.Type, "Person"));

        Sees(cut, "Dev Raman");
        DoesNotSee(cut, "Ava Lindqvist");
        Assert.Equal("Dev", cut.Find("input").GetAttribute("value"));
    }

    [Fact]
    public async Task A_permalink_opens_the_list_filtered_and_the_filter_boxes_show_it()
    {
        await UseExampleAsync();
        var address = new ListState { Filters = [new RecordFilter("name_last", FilterOp.Contains, "Raman")] }.Encode();
        Services.GetRequiredService<NavigationManager>().NavigateTo(ViewState.Href(SavedViewKind.List, "Person", address!));
        var cut = RenderPage<RecordListPage>(p => p.Add(x => x.Type, "Person"));

        Sees(cut, "Dev Raman");
        DoesNotSee(cut, "Ava Lindqvist");
    }

    [Fact]
    public async Task Searching_puts_the_search_in_the_address_and_clearing_it_takes_it_out()
    {
        await UseExampleAsync();
        var cut = RenderPage<RecordListPage>(p => p.Add(x => x.Type, "Person"));
        Sees(cut, "Ava Lindqvist");
        Assert.Equal("", new Uri(Address).Query);

        Type(cut, "Search", "Dev");
        cut.WaitForAssertion(() => Assert.StartsWith("?v=", new Uri(Address).Query, StringComparison.Ordinal));
        Assert.Equal("Dev", ViewState.Decode<ListState>(new Uri(Address).Query[3..])!.Search);

        Type(cut, "Search", "");
        cut.WaitForAssertion(() => Assert.Equal("", new Uri(Address).Query));
    }

    [Fact]
    public async Task A_link_naming_a_field_the_type_lacks_says_so_instead_of_failing()
    {
        await UseExampleAsync();
        var address = new ListState { Sort = "nope" }.Encode();
        Services.GetRequiredService<NavigationManager>().NavigateTo(ViewState.Href(SavedViewKind.List, "Person", address!));
        var cut = RenderPage<RecordListPage>(p => p.Add(x => x.Type, "Person"));

        Told("nope");
        Sees(cut, "No Person records yet.");
    }

    [Fact]
    public async Task A_list_view_can_be_saved_and_opened_from_its_page()
    {
        await UseExampleAsync();
        var cut = RenderPage<RecordListPage>(p => p.Add(x => x.Type, "Person"));
        Sees(cut, "Ava Lindqvist");
        Type(cut, "Search", "Dev");
        cut.WaitForAssertion(() => Assert.StartsWith("?v=", new Uri(Address).Query, StringComparison.Ordinal));
        var name = Unique("People called Dev");

        Press(cut, "Views");
        Type(Dialogs, "Name for this view", name);
        Press(Dialogs, "Save");
        Told("Saved the view");

        var saved = (await Services.GetRequiredService<SavedViewRepository>().ListAsync()).Single(v => v.Name == name);
        Assert.Equal((SavedViewKind.List, "Person"), (saved.Kind, saved.Scope));
        Assert.Equal("Dev", ViewState.Decode<ListState>(saved.State)!.Search);
        Assert.StartsWith("/data/Person?v=", saved.Href, StringComparison.Ordinal);
    }
}
