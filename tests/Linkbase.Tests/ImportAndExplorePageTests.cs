using Linkbase.Core.Extraction;
using System.Net;
using Bunit;
using System.Text.Json.Nodes;
using Linkbase.Data;
using Linkbase.Domains.Climbing.Data;
using Linkbase.Core.Graph;
using Linkbase.Core.Explore;
using Linkbase.Core.Views;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.JSInterop;
using MudBlazor;

namespace Linkbase.Tests;

[Trait("Category", "Database")]
public class ImportPageTests(PostgresFixture fixture) : UiTestBase(fixture), IClassFixture<PostgresFixture>
{
    private sealed class NoExtractor : IModelExtractor
    {
        public Task<ExtractedData> ExtractAsync(ExtractionSchema schema, string text, IReadOnlyList<string> knownRecords, CancellationToken cancellationToken) =>
            Task.FromResult(ExtractedData.Empty);
    }

    [Theory]
    [InlineData(null, new[] { "Read text", "CSV file" })]
    [InlineData("false", new[] { "CSV file" })]
    public async Task One_import_page_offers_the_ways_of_importing_and_text_reading_can_be_turned_off(string? enabled, string[] expected)
    {
        var store = await ClimbingStore.CreateAsync(Fixture);
        Services.AddSingleton<IDbContextFactory<LinkbaseDbContext>>(store);
        Services.AddSingleton<IModelExtractor>(new NoExtractor());
        Services.AddSingleton(new PageTextFetcher(new HttpClient()));
        Services.AddSingleton<Linkbase.Core.Staging.ImportStagingService>();
        Services.AddSingleton<ModelExtractionService>();
        Services.AddSingleton<Linkbase.Core.CsvImport.CsvImportService>();
        var configuration = new Microsoft.Extensions.Configuration.ConfigurationManager();
        configuration["Extraction:Enabled"] = enabled;
        Services.AddSingleton<Microsoft.Extensions.Configuration.IConfiguration>(configuration);
        var cut = RenderPage<Linkbase.Core.Import.ImportHubPage>();

        // The first panel's own tabs follow the page's.
        var tabs = cut.FindAll(".mud-tab").Select(t => t.TextContent.Trim()).Take(expected.Length).ToList();
        Assert.Equal(expected, tabs);

        ClickFresh(cut, () => cut.FindAll(".mud-tab").First(t => t.TextContent.Trim() == "CSV file"));
        Assert.Contains("Choose a CSV file", cut.Markup, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_import_page_no_longer_starts_models()
    {
        var store = await ClimbingStore.CreateAsync(Fixture);
        Services.AddSingleton<IDbContextFactory<LinkbaseDbContext>>(store);
        Services.AddSingleton<IModelExtractor>(new NoExtractor());
        Services.AddSingleton(new PageTextFetcher(new HttpClient()));
        Services.AddSingleton<Linkbase.Core.Staging.ImportStagingService>();
        Services.AddSingleton<ModelExtractionService>();
        Services.AddSingleton<Linkbase.Core.CsvImport.CsvImportService>();
        Services.AddSingleton<Microsoft.Extensions.Configuration.IConfiguration>(new Microsoft.Extensions.Configuration.ConfigurationManager());
        var cut = RenderPage<Linkbase.Core.Import.ImportHubPage>();

        // Starting a model makes a database of its own, so it isn't offered where the open model's data would be written.
        Assert.DoesNotContain(cut.FindAll(".mud-tab"), t => t.TextContent.Trim() == "Start from CSVs");
    }
}

[Trait("Category", "Database")]
public class ExplorePageTests : UiTestBase, IClassFixture<PostgresFixture>
{
    private readonly BunitJSModuleInterop _graph;

    public ExplorePageTests(PostgresFixture fixture)
        : base(fixture)
    {
        _graph = JSInterop.SetupModule("./js/graph.js");
        _graph.Mode = JSRuntimeMode.Loose;
    }

    // The browser is there, but the graph module can't be fetched.
    private sealed class NoNetworkJs : IJSRuntime
    {
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) =>
            identifier == "import" ? throw new JSException("no network") : ValueTask.FromResult(default(TValue)!);

        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken cancellationToken, object?[]? args) =>
            InvokeAsync<TValue>(identifier, args);
    }

    private IEnumerable<string> GraphCalls => _graph.Invocations.Safe().Select(i => i.Identifier);

    private void Search<T>(IRenderedComponent<T> cut, string text)
        where T : IComponent
    {
        cut.WaitForAssertion(() => Assert.DoesNotContain("Loading…", cut.Markup));
        var input = cut.Find("input[role=combobox]");
        input.Focus();
        cut.Find("input[role=combobox]").Input(text);
    }

    private static void WaitForOption<T>(IRenderedComponent<T> cut, string text)
        where T : IComponent =>
        cut.WaitForAssertion(() => Assert.Contains(cut.FindAll("[role=option]"), o => o.TextContent.Contains(text, StringComparison.Ordinal)));

    private static void Key<T>(IRenderedComponent<T> cut, string key)
        where T : IComponent =>
        cut.Find("input[role=combobox]").KeyDown(new KeyboardEventArgs { Key = key });

    // Finds one crag by name through the search bar, as someone would type it.
    private IRenderedComponent<ExplorePage> OpenWith(Seeded s)
    {
        var cut = RenderPage<ExplorePage>();
        Search(cut, s.Crag);
        WaitForOption(cut, s.Crag);
        Key(cut, "Enter");
        cut.WaitForAssertion(() => Assert.Contains("pattern-node", cut.Markup));
        Key(cut, "Enter");
        Sees(cut, ">Fit<");
        cut.WaitForAssertion(() => Assert.Contains("update", GraphCalls));
        return cut;
    }

    private IRenderedComponent<ExplorePage> Render(Seeded s)
    {
        var cut = OpenWith(s);
        cut.WaitForAssertion(() => Assert.Contains($"<h2", cut.Markup));
        return cut;
    }

    [Fact]
    public void Starts_empty_and_creates_the_graph()
    {
        var cut = RenderPage<ExplorePage>();

        Sees(cut, "Type anything above");
        Sees(cut, "Click a node for its details");
        cut.WaitForAssertion(() => Assert.Contains("create", GraphCalls));
        Assert.True(Control(cut, "Fit").HasAttribute("disabled"));
    }

    [Fact]
    public void The_loading_bar_is_gone_once_the_page_has_loaded()
    {
        var cut = RenderPage<ExplorePage>();
        cut.WaitForAssertion(() => Assert.Contains("create", GraphCalls));
        cut.WaitForAssertion(() => Assert.Empty(cut.FindAll(".explore-busy")));
        Assert.Equal("false", cut.Find(".explore-canvas").GetAttribute("aria-busy"));
    }

    [Fact]
    public void The_loading_bar_is_gone_even_when_the_graph_cant_be_drawn()
    {
        Services.AddSingleton<IJSRuntime>(new NoNetworkJs());
        var cut = RenderPage<ExplorePage>();

        Sees(cut, "The graph can't be drawn");
        cut.WaitForAssertion(() => Assert.Empty(cut.FindAll(".explore-busy")));
    }

    [Fact]
    public void Says_when_the_graph_libraries_cant_load()
    {
        Services.AddSingleton<IJSRuntime>(new NoNetworkJs());
        var cut = RenderPage<ExplorePage>();

        Sees(cut, "The graph can't be drawn: no network");
    }

    [Fact]
    public async Task Search_shows_a_node_and_its_details()
    {
        var s = await SeedAsync();
        var cut = Render(s);

        Sees(cut, s.Crag);
        Assert.Contains("select", GraphCalls);
        DoesNotSee(cut, "Type anything above");
    }

    [Fact]
    public async Task One_enter_searches_for_free_text()
    {
        var note = $"Windy {Guid.NewGuid():N}";
        await SeedAsync(notes: note);
        var cut = RenderPage<ExplorePage>();

        Search(cut, note);
        WaitForOption(cut, "Search everything for");
        Key(cut, "Enter");

        cut.WaitForAssertion(() => Assert.Contains("update", GraphCalls));
        Sees(cut, "Anything");
        Sees(cut, note);
        Sees(cut, "Ascent 1");
    }

    [Fact]
    public async Task Typing_graph_shows_the_model_and_a_type_leads_to_its_nodes()
    {
        await SeedAsync();
        var cut = RenderPage<ExplorePage>();

        Search(cut, "graph");
        WaitForOption(cut, "Graph model");
        Key(cut, "Enter");

        cut.WaitForAssertion(() => Assert.Contains("update", GraphCalls));
        var drawn = _graph.Invocations.Safe().Last(i => i.Identifier == "update").Arguments;
        Assert.Equal((await Services.GetRequiredService<GraphRepository>().SchemaAsync()).Labels.Count, ((object[])drawn[0]!).Length);
        Assert.NotEmpty((object[])drawn[1]!);
        Assert.Contains((object[])drawn[1]!, e => System.Text.RegularExpressions.Regex.IsMatch(e.ToString()!, @"relType = AT \(\d"));

        Press(cut, "Views");
        DoesNotSee(Dialogs, "Keep nodes exactly where they are");
        Press(Dialogs, "Close");

        await cut.InvokeAsync(() => cut.Instance.OnNodeRightClicked("model:Crag", 10, 20));
        DoesNotSee(cut, "explore-menu");
        Sees(cut, "Show all Crag nodes");
        Sees(cut, "← AT Route (");
        Assert.False(HasControl(cut, "Remove"));
        Assert.False(HasControl(cut, "Open and edit"));

        Press(cut, "Show all Crag nodes");

        cut.WaitForAssertion(() => Assert.True(((object[])_graph.Invocations.Safe().Last(i => i.Identifier == "update").Arguments[0]!).Length > 0));
        DoesNotSee(cut, "Show all Crag nodes");
    }

    [Fact]
    public async Task Totals_sum_the_number_fields_of_the_search_and_say_when_there_are_none()
    {
        await SeedAsync();
        var cut = RenderPage<ExplorePage>();
        Search(cut, "graph");
        WaitForOption(cut, "Graph model");
        Key(cut, "Enter");
        cut.WaitForAssertion(() => Assert.Contains("update", GraphCalls));
        Assert.False(HasControl(cut, "Totals"));

        await cut.InvokeAsync(() => cut.Instance.OnNodeDoubleClicked("model:Route"));
        cut.WaitForAssertion(() => Assert.True(HasControl(cut, "Totals")));
        Press(cut, "Totals");

        Sees(cut, "Over every match of the search");
        Sees(cut, "Length");
        Sees(cut, ">Sum<");
    }

    [Fact]
    public async Task Totals_follow_the_next_search()
    {
        var s = await SeedAsync();
        var cut = RenderPage<ExplorePage>();
        Search(cut, "graph");
        WaitForOption(cut, "Graph model");
        Key(cut, "Enter");
        cut.WaitForAssertion(() => Assert.Contains("update", GraphCalls));
        await cut.InvokeAsync(() => cut.Instance.OnNodeDoubleClicked("model:Route"));
        cut.WaitForAssertion(() => Assert.True(HasControl(cut, "Totals")));
        Press(cut, "Totals");
        Sees(cut, "Over every match of the search");

        // Now a different search, typed in the bar as before.
        Search(cut, s.Crag);
        WaitForOption(cut, s.Crag);
        Key(cut, "Enter");
        cut.WaitForAssertion(() => Assert.Contains("pattern-node", cut.Markup));
        Key(cut, "Enter");

        cut.WaitForAssertion(() => Assert.Contains("have no number fields to total", cut.Markup));
    }

    [Fact]
    public async Task A_ranked_search_lists_its_winners_with_their_counts_and_survives_a_permalink()
    {
        var s = await SeedAsync();
        var pattern = new GraphPattern([new PatternNode("Route", []), new PatternNode("Crag", [])], [new PatternRel("AT", true)])
        {
            Rank = new PatternRank(Group: 1, Count: 0, Top: 3),
        };
        var cut = OpenAt(new ExploreState { Pattern = pattern }.Encode());

        cut.WaitForAssertion(() => Assert.Contains("Ranking of the search", cut.Markup));
        Sees(cut, s.Crag);
        Sees(cut, "Different Route records");
        Sees(cut, "reach the most different Route records");
    }

    private static GraphPattern NamedRoute(string name) =>
        new([new PatternNode("Route", [new PatternFilter("name", Linkbase.Core.Graph.FilterOperator.Equals, name)])], []);

    // Opens Explore at an address, as a link would.
    private IRenderedComponent<ExplorePage> OpenAt(string? state)
    {
        Services.GetRequiredService<NavigationManager>().NavigateTo(ViewState.Href(SavedViewKind.Explore, null, state!));
        return RenderPage<ExplorePage>();
    }

    private string Address => Services.GetRequiredService<NavigationManager>().Uri;

    [Fact]
    public async Task A_permalink_opens_the_search_with_its_table_and_totals()
    {
        var s = await SeedAsync();
        var state = new ExploreState { Pattern = NamedRoute(s.Route), Table = true, Totals = true };
        var cut = OpenAt(state.Encode());

        Sees(cut, s.Route);
        Sees(cut, "explore-table");
        Sees(cut, ">Sum<");
        Assert.Contains("update", GraphCalls);
    }

    [Fact]
    public async Task A_permalink_can_open_the_cards_and_a_card_lists_only_filled_fields_and_selects_its_node()
    {
        var s = await SeedAsync(grade: "E1 5b");
        var cut = OpenAt(new ExploreState { Pattern = NamedRoute(s.Route), Cards = true }.Encode());

        Sees(cut, "explore-cards");
        DoesNotSee(cut, "explore-table\"");
        var card = cut.FindAll(".explore-card").First(c => c.TextContent.Contains(s.Route, StringComparison.Ordinal));
        Assert.Contains("E1 5b", card.TextContent, StringComparison.Ordinal);
        Assert.DoesNotContain(cut.FindAll(".explore-card dd"), d => string.IsNullOrWhiteSpace(d.TextContent));

        ClickFresh(cut, () => cut.FindAll(".explore-card").First(c => c.TextContent.Contains(s.Route, StringComparison.Ordinal)));
        Sees(cut, s.Route + "</h2>");
        Sees(cut, "explore-card-selected");
        Assert.Equal($"route:{s.RouteId}", _graph.Invocations.Safe().Last(i => i.Identifier == "select").Arguments[0]);
    }

    [Fact]
    public async Task A_permalink_can_hold_the_nodes_themselves()
    {
        var s = await SeedAsync();
        var state = new ExploreState { Nodes = [$"route:{s.RouteId}", $"crag:{s.CragId}", "crag:nobody"] };
        var cut = OpenAt(state.Encode());

        Told("no longer exist");
        cut.WaitForAssertion(() => Assert.Contains("update", GraphCalls));
        var drawn = (object[])_graph.Invocations.Safe().Last(i => i.Identifier == "update").Arguments[0]!;
        Assert.Equal(2, drawn.Length);
    }

    [Fact]
    public async Task The_address_follows_the_page_and_a_plain_page_has_a_plain_address()
    {
        var s = await SeedAsync();
        var cut = OpenWith(s);
        var query = new Uri(Address).Query;
        Assert.StartsWith("?v=", query, StringComparison.Ordinal);
        var state = ViewState.Decode<ExploreState>(query[3..]);
        Assert.NotNull(state!.Pattern);
        Assert.Contains($"crag:{s.CragId}", state.Nodes!);

        Press(cut, "Clear");
        cut.WaitForAssertion(() => Assert.Equal("", new Uri(Address).Query));
    }

    [Fact]
    public async Task A_link_that_cant_be_read_says_so_and_leaves_the_page_empty()
    {
        await SeedAsync();
        var cut = OpenAt("not-a-view");

        Told("can't be read");
        Sees(cut, "Type anything above");
    }

    [Fact]
    public async Task A_link_naming_things_the_model_lacks_is_refused_rather_than_run()
    {
        await SeedAsync();
        var pattern = new GraphPattern([new PatternNode("Unicorn", [])], []);
        var cut = OpenAt(new ExploreState { Pattern = pattern, Filters = [new PatternFilter("nope", Linkbase.Core.Graph.FilterOperator.Equals, "x")] }.Encode());

        Told("Unknown type 'Unicorn'");
        Sees(cut, "Type anything above");
    }

    [Fact]
    public async Task Views_can_be_saved_from_Explore_opened_and_pinned_to_Home()
    {
        var s = await SeedAsync();
        var cut = OpenWith(s);
        var name = Unique("Crag view");

        Press(cut, "Views");
        Type(Dialogs, "Name for this view", name);
        Press(Dialogs, "Save");
        Told("Saved the view");

        var views = Services.GetRequiredService<Linkbase.Core.Views.SavedViewRepository>();
        var saved = (await views.ListAsync()).Single(v => v.Name == name);
        Assert.Equal(SavedViewKind.Explore, saved.Kind);
        Assert.StartsWith("/explore?v=", saved.Href, StringComparison.Ordinal);
        Assert.False(saved.Pinned);

        ClickFresh(Dialogs, () => Dialogs.Find($"button[aria-label='Pin {name}']"));
        for (var tries = 0; tries < 100 && !(await views.PinnedAsync()).Any(v => v.Name == name); tries++)
        {
            await Task.Delay(50);
        }

        Assert.Contains(await views.PinnedAsync(), v => v.Name == name);

        var home = RenderPage<Linkbase.Components.Pages.Home>();
        Sees(home, "Pinned views");
        Sees(home, name);
    }

    [Fact]
    public async Task Totals_say_when_the_search_has_no_number_fields()
    {
        var s = await SeedAsync();
        var cut = OpenWith(s);

        Press(cut, "Totals");

        cut.WaitForAssertion(() => Assert.Contains("have no number fields to total", cut.Markup));
    }

    [Fact]
    public async Task Double_clicking_a_type_in_the_model_shows_its_nodes_and_searching_replaces_the_model()
    {
        var s = await SeedAsync();
        var cut = RenderPage<ExplorePage>();
        Search(cut, "graph");
        WaitForOption(cut, "Graph model");
        Key(cut, "Enter");
        Sees(cut, "Fit");
        cut.WaitForAssertion(() => Assert.Contains("update", GraphCalls));

        await cut.InvokeAsync(() => cut.Instance.OnNodeDoubleClicked("model:Area"));
        cut.WaitForAssertion(() => Assert.DoesNotContain(
            ((object[])_graph.Invocations.Safe().Last(i => i.Identifier == "update").Arguments[0]!).Select(n => n.ToString()),
            n => n!.Contains("model:", StringComparison.Ordinal)));

        Press(cut, "Clear");
        Search(cut, "graph");
        WaitForOption(cut, "Graph model");
        Key(cut, "Enter");
        cut.WaitForAssertion(() => Assert.False(Control(cut, "Fit").HasAttribute("disabled")));

        Search(cut, s.Crag);
        WaitForOption(cut, "Search everything for");
        Key(cut, "Enter");

        cut.WaitForAssertion(() => Assert.Equal(s.Crag, cut.Find(".explore-panel h2").TextContent.Trim()));
    }

    [Fact]
    public async Task A_node_with_properties_lists_them()
    {
        var s = await SeedAsync(grade: "HVS 5a");
        var cut = RenderPage<ExplorePage>();

        Search(cut, s.Route);
        WaitForOption(cut, s.Route);
        Key(cut, "Enter");
        cut.WaitForAssertion(() => Assert.Contains("pattern-node", cut.Markup));
        Key(cut, "Enter");

        Sees(cut, s.Route + "</h2>");
        Sees(cut, "HVS 5a");
    }

    [Fact]
    public async Task Clicks_select_and_background_clicks_deselect()
    {
        var s = await SeedAsync();
        var cut = Render(s);

        await cut.InvokeAsync(() => cut.Instance.OnBackgroundClicked());
        Sees(cut, "Click a node for its details");

        await cut.InvokeAsync(() => cut.Instance.OnNodeClicked($"crag:{s.CragId}"));
        cut.WaitForAssertion(() => Assert.Equal(s.Crag, cut.Find(".explore-panel h2").TextContent.Trim()));

        await cut.InvokeAsync(() => cut.Instance.OnNodeClicked("crag:-1"));
        Sees(cut, "Click a node for its details");
    }

    [Fact]
    public async Task Clicking_a_link_shows_its_ends_in_the_panel_and_a_node_click_replaces_it()
    {
        var s = await SeedAsync();
        var cut = Render(s);
        Press(cut, "Expand");
        Sees(cut, "Route 1");

        var edge = cut.Instance.FirstEdgeKeyForTest();
        await cut.InvokeAsync(() => cut.Instance.OnEdgeClicked(edge));
        cut.WaitForAssertion(() => Assert.Contains("From", cut.Find(".explore-panel").TextContent));

        await cut.InvokeAsync(() => cut.Instance.OnNodeClicked($"crag:{s.CragId}"));
        cut.WaitForAssertion(() => Assert.Equal(s.Crag, cut.Find(".explore-panel h2").TextContent.Trim()));
    }

    [Fact]
    public async Task Expanding_adds_neighbours_then_says_when_nothing_is_new()
    {
        var s = await SeedAsync();
        var cut = Render(s);

        Press(cut, "Expand");
        Sees(cut, "Expand again");
        Sees(cut, "Route 1");

        await cut.InvokeAsync(() => cut.Instance.OnNodeDoubleClicked($"crag:{s.CragId}"));
        Told("Nothing new to add with the current filters.");

        await cut.InvokeAsync(() => cut.Instance.OnNodeDoubleClicked("crag:-1"));
    }

    [Fact]
    public async Task The_right_click_menu_expands_removes_and_links()
    {
        var s = await SeedAsync();
        var cut = Render(s);

        await cut.InvokeAsync(() => cut.Instance.OnNodeRightClicked("crag:-1", 1, 2));
        Assert.DoesNotContain("explore-menu", cut.Markup);

        await cut.InvokeAsync(() => cut.Instance.OnNodeRightClicked($"crag:{s.CragId}", 10, 20));
        Sees(cut, "left: 10px; top: 20px");
        Sees(cut, "Remove others");
        ClickFresh(cut, () => cut.FindAll(".explore-menu button").First(b => b.TextContent.Contains("Route", StringComparison.Ordinal)));
        DoesNotSee(cut, "explore-menu");
        Sees(cut, "Route 1");

        await cut.InvokeAsync(() => cut.Instance.OnNodeRightClicked($"crag:{s.CragId}", 10, 20));
        Press(cut, "Expand all (2)");
        DoesNotSee(cut, "explore-menu");

        await cut.InvokeAsync(() => cut.Instance.OnNodeRightClicked($"crag:{s.CragId}", 10, 20));
        Sees(cut, "explore-menu");
        await cut.InvokeAsync(() => cut.Instance.OnViewportChanged());
        DoesNotSee(cut, "explore-menu");
        await cut.InvokeAsync(() => cut.Instance.OnViewportChanged());

        await cut.InvokeAsync(() => cut.Instance.OnNodeRightClicked($"crag:{s.CragId}", 10, 20));
        Press(cut, "Remove others");
        DoesNotSee(cut, "Route 1");

        await cut.InvokeAsync(() => cut.Instance.OnNodeRightClicked($"crag:{s.CragId}", 10, 20));
        Press(cut, "Remove");
        Sees(cut, "Type anything above");
    }

    [Fact]
    public async Task The_table_lists_whats_on_the_canvas_by_type_and_a_row_selects_its_node()
    {
        var s = await SeedAsync(grade: "E1 5b");
        var cut = Render(s);
        Press(cut, "Expand");
        Sees(cut, "Route 1");
        DoesNotSee(cut, "explore-table");

        Press(cut, "Table");

        Sees(cut, "explore-table");
        var headings = cut.FindAll(".explore-table-heading").Select(h => h.TextContent).ToList();
        Assert.Contains(headings, h => h.Contains("Crag", StringComparison.Ordinal));
        Assert.Contains(headings, h => h.Contains("Route", StringComparison.Ordinal));
        Assert.Contains(headings, h => h.Contains("Links", StringComparison.Ordinal));
        Assert.Contains(cut.FindAll(".explore-table th"), th => th.TextContent.Trim() == "Grade");
        Sees(cut, "E1 5b");

        ClickFresh(cut, () => cut.FindAll("tr.explore-table-row").First(r => r.TextContent.Contains(s.Route, StringComparison.Ordinal)));
        Sees(cut, s.Route + "</h2>");
        Sees(cut, "explore-table-selected");
        Assert.Equal($"route:{s.RouteId}", _graph.Invocations.Safe().Last(i => i.Identifier == "select").Arguments[0]);

        // The filter narrows every table.
        cut.Find(".explore-table-filter input").Input("E1 5b");
        cut.WaitForAssertion(() => Assert.DoesNotContain(cut.FindAll("tr.explore-table-row"), r => r.TextContent.Contains(s.Crag, StringComparison.Ordinal)));
        cut.Find(".explore-table-filter input").Input("nothing has this " + Guid.NewGuid());
        Sees(cut, "Nothing on the canvas matches that.");

        cut.Find(".explore-table-filter input").Input("");
        cut.WaitForAssertion(() => Assert.Contains(cut.FindAll("tr.explore-table-row"), r => r.TextContent.Contains(s.Route, StringComparison.Ordinal)));

        // A type's heading folds its table away, and stays folded as the canvas changes.
        ClickFresh(cut, () => cut.FindAll("button.explore-table-heading").First(h => h.TextContent.Contains("Route", StringComparison.Ordinal)));
        cut.WaitForAssertion(() => Assert.DoesNotContain(cut.FindAll("tr.explore-table-row"), r => r.TextContent.Contains(s.Route, StringComparison.Ordinal)));
        Assert.Equal("false", cut.FindAll("button.explore-table-heading").First(h => h.TextContent.Contains("Route", StringComparison.Ordinal)).GetAttribute("aria-expanded"));
        Assert.Contains(cut.FindAll("tr.explore-table-row"), r => r.TextContent.Contains(s.Crag, StringComparison.Ordinal));

        Assert.True(HasControl(cut, "Expand all"));
        Press(cut, "Collapse all");
        cut.WaitForAssertion(() => Assert.Empty(cut.FindAll("tr.explore-table-row")));
        Assert.False(HasControl(cut, "Collapse all"));
        Press(cut, "Expand all");
        cut.WaitForAssertion(() => Assert.Contains(cut.FindAll("tr.explore-table-row"), r => r.TextContent.Contains(s.Route, StringComparison.Ordinal)));

        Press(cut, "Graph");
        DoesNotSee(cut, "explore-table-wrap");
        cut.WaitForAssertion(() => Assert.Contains("fit", GraphCalls));
    }

    [Fact]
    public async Task The_search_bar_can_go_back_to_an_earlier_node_and_drop_one_condition()
    {
        var s = await SeedAsync();
        var cut = RenderPage<ExplorePage>();
        Search(cut, s.Crag);
        WaitForOption(cut, s.Crag);
        Key(cut, "Enter");
        cut.WaitForAssertion(() => Assert.Contains("pattern-node-filter", cut.Markup));
        Search(cut, "AT");
        WaitForOption(cut, "← AT Route");
        Key(cut, "Enter");
        cut.WaitForAssertion(() => Assert.Equal(2, cut.FindAll(".pattern-node").Count));
        Assert.Empty(cut.FindAll(".pattern-node-focused"));

        Key(cut, "ArrowLeft");
        cut.WaitForAssertion(() => Assert.Contains("Crag", cut.Find(".pattern-node-focused").TextContent, StringComparison.Ordinal));
        Assert.StartsWith("Add a filter to Anything", cut.Find("input[role=combobox]").GetAttribute("placeholder"), StringComparison.Ordinal);
        Assert.DoesNotContain(cut.FindAll("[role=option]"), o => o.TextContent.Contains("AT", StringComparison.Ordinal));

        Key(cut, "ArrowRight");
        cut.WaitForAssertion(() => Assert.Empty(cut.FindAll(".pattern-node-focused")));

        // Clicking a node chooses it too.
        ClickFresh(cut, () => cut.FindAll(".pattern-node")[0]);
        cut.WaitForAssertion(() => Assert.Single(cut.FindAll(".pattern-node-focused")));

        // One condition comes off, and the rest of the phrase stays.
        ClickFresh(cut, () => cut.Find(".pattern-filter-remove"));
        cut.WaitForAssertion(() => Assert.Empty(cut.FindAll(".pattern-node-filter")));
        Assert.Equal(2, cut.FindAll(".pattern-node").Count);
    }

    [Fact]
    public async Task Hotkeys_switch_views_fit_expand_hide_remove_and_deselect()
    {
        var s = await SeedAsync();
        var cut = Render(s);
        cut.WaitForAssertion(() => Assert.Contains(JSInterop.Invocations.Safe(), i => i.Identifier == "register"));

        // t cycles graph, table, cards, and back to the graph.
        await cut.InvokeAsync(() => cut.Instance.OnHotkey("t"));
        Sees(cut, "explore-table-wrap");
        Sees(cut, "explore-table\"");
        await cut.InvokeAsync(() => cut.Instance.OnHotkey("t"));
        Sees(cut, "explore-cards");
        DoesNotSee(cut, "explore-table\"");
        await cut.InvokeAsync(() => cut.Instance.OnHotkey("t"));
        DoesNotSee(cut, "explore-table-wrap");

        await cut.InvokeAsync(() => cut.Instance.OnHotkey("f"));
        cut.WaitForAssertion(() => Assert.Contains("fit", GraphCalls));

        await cut.InvokeAsync(() => cut.Instance.OnHotkey("e"));
        Sees(cut, "Expand again");
        Sees(cut, "Route 1");

        // n and p step through what's shown, in the table's order, wrapping round and bringing each into view.
        await cut.InvokeAsync(() => cut.Instance.OnHotkey("Escape"));
        Sees(cut, "Click a node for its details");
        await cut.InvokeAsync(() => cut.Instance.OnHotkey("n"));
        var first = cut.Find("aside h2").TextContent;
        Assert.Equal(_graph.Invocations.Safe().Last(i => i.Identifier == "select").Arguments[0], _graph.Invocations.Safe().Last(i => i.Identifier == "reveal").Arguments[0]);
        await cut.InvokeAsync(() => cut.Instance.OnHotkey("n"));
        Assert.NotEqual(first, cut.Find("aside h2").TextContent);
        await cut.InvokeAsync(() => cut.Instance.OnHotkey("p"));
        Assert.Equal(first, cut.Find("aside h2").TextContent);
        await cut.InvokeAsync(() => cut.Instance.OnHotkey("p"));
        var last = cut.Find("aside h2").TextContent;
        Assert.NotEqual(first, last);
        await cut.InvokeAsync(() => cut.Instance.OnHotkey("Escape"));
        await cut.InvokeAsync(() => cut.Instance.OnHotkey("p"));
        Assert.Equal(last, cut.Find("aside h2").TextContent);

        // In the table, the chosen row is scrolled to.
        await cut.InvokeAsync(() => cut.Instance.OnHotkey("t"));
        await cut.InvokeAsync(() => cut.Instance.OnHotkey("n"));
        cut.WaitForAssertion(() => Assert.Contains(JSInterop.Invocations.Safe(), i => i.Identifier == "scrollIntoView"));
        await cut.InvokeAsync(() => cut.Instance.OnHotkey("t"));
        await cut.InvokeAsync(() => cut.Instance.OnHotkey("t"));
        await cut.InvokeAsync(() => cut.Instance.OnNodeClicked($"crag:{s.CragId}"));

        await cut.InvokeAsync(() => cut.Instance.OnNodeRightClicked($"crag:{s.CragId}", 10, 20));
        Sees(cut, "explore-menu");
        await cut.InvokeAsync(() => cut.Instance.OnHotkey("Escape"));
        DoesNotSee(cut, "explore-menu");
        cut.WaitForAssertion(() => Assert.Equal(s.Crag, cut.Find(".explore-panel h2").TextContent.Trim()));

        await cut.InvokeAsync(() => cut.Instance.OnHotkey("Escape"));
        Sees(cut, "Click a node for its details");

        // With nothing selected, the keys that act on a node do nothing, and h no longer hides anything.
        var drawn = _graph.Invocations.Safe().Count(i => i.Identifier == "update");
        await cut.InvokeAsync(() => cut.Instance.OnHotkey("Delete"));
        await cut.InvokeAsync(() => cut.Instance.OnHotkey("q"));
        await cut.InvokeAsync(() => cut.Instance.OnNodeClicked($"crag:{s.CragId}"));
        await cut.InvokeAsync(() => cut.Instance.OnHotkey("h"));
        Assert.Equal(drawn, _graph.Invocations.Safe().Count(i => i.Identifier == "update"));
        cut.WaitForAssertion(() => Assert.Equal(s.Crag, cut.Find(".explore-panel h2").TextContent.Trim()));

        await cut.InvokeAsync(() => cut.Instance.OnNodeClicked($"route:{s.RouteId}"));
        await cut.InvokeAsync(() => cut.Instance.OnHotkey("Delete"));
        Sees(cut, "Click a node for its details");
        Assert.DoesNotContain(
            ((object[])_graph.Invocations.Safe().Last(i => i.Identifier == "update").Arguments[0]!).Select(n => n.ToString()),
            n => n!.Contains($"route:{s.RouteId}", StringComparison.Ordinal));

        // c clears the graph, from the table too, and does nothing once it's empty.
        await cut.InvokeAsync(() => cut.Instance.OnHotkey("t"));
        Sees(cut, "explore-table-wrap");
        await cut.InvokeAsync(() => cut.Instance.OnHotkey("c"));
        Sees(cut, "Type anything above");
        DoesNotSee(cut, "explore-table-wrap");
        Assert.Empty((object[])_graph.Invocations.Safe().Last(i => i.Identifier == "update").Arguments[0]!);
        await cut.InvokeAsync(() => cut.Instance.OnHotkey("c"));
        await cut.InvokeAsync(() => cut.Instance.OnHotkey("n"));
        Sees(cut, "Type anything above");
    }

    [Fact]
    public async Task O_opens_the_selected_nodes_own_page_and_does_nothing_without_one()
    {
        var s = await SeedAsync();
        var cut = Render(s);
        cut.WaitForAssertion(() => Assert.Contains(JSInterop.Invocations.Safe(), i => i.Identifier == "register"));
        var before = Address;

        await cut.InvokeAsync(() => cut.Instance.OnHotkey("Escape"));
        await cut.InvokeAsync(() => cut.Instance.OnHotkey("o"));
        Assert.Equal(before, Address);

        await cut.InvokeAsync(() => cut.Instance.OnNodeClicked($"crag:{s.CragId}"));
        cut.WaitForAssertion(() => Assert.Equal(s.Crag, cut.Find(".explore-panel h2").TextContent.Trim()));
        await cut.InvokeAsync(() => cut.Instance.OnHotkey("o"));
        Assert.EndsWith($"/data/Crag/{s.CragId}", Address, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_search_box_starts_focused_and_esc_closes_its_suggestions()
    {
        var s = await SeedAsync();
        var cut = RenderPage<ExplorePage>();
        var focuses = () => JSInterop.Invocations.Safe().Count(i => i.Identifier.EndsWith("focus", StringComparison.Ordinal));

        // Focused as it opens, without the suggestions covering the page.
        cut.WaitForAssertion(() => Assert.Equal(1, focuses()));
        cut.Find("input[role=combobox]").Focus();
        Assert.Empty(cut.FindAll("[role=option]"));

        // Esc closes the suggestions and says so, which is how hotkeys.js knows the next Esc leaves the box. The browser
        // test covers leaving.
        Search(cut, s.Crag);
        WaitForOption(cut, s.Crag);
        Assert.Equal("true", cut.Find("input[role=combobox]").GetAttribute("aria-expanded"));
        Key(cut, "Escape");
        cut.WaitForAssertion(() => Assert.Empty(cut.FindAll("[role=option]")));
        Assert.Equal("false", cut.Find("input[role=combobox]").GetAttribute("aria-expanded"));
        Assert.Equal(1, focuses());
    }

    [Fact]
    public async Task Exports_save_whats_shown_as_csv_json_and_a_picture()
    {
        var s = await SeedAsync();
        var download = JSInterop.SetupModule("./js/download.js");
        download.SetupVoid("saveStream", _ => true).SetVoidResult();
        _graph.Setup<bool>("savePng", _ => true).SetResult(true);
        var cut = Render(s);
        Press(cut, "Expand");
        Sees(cut, "Route 1");

        string Saved(string item)
        {
            var before = download.Invocations.Count;
            Press(cut, "Export");
            Popovers.WaitForAssertion(() => Assert.Contains(Popovers.FindAll(".mud-menu-item"), i => i.TextContent.Contains(item, StringComparison.Ordinal)));
            ClickFresh(Popovers, () => Popovers.FindAll(".mud-menu-item").First(i => i.TextContent.Contains(item, StringComparison.Ordinal)));
            cut.WaitForAssertion(() => Assert.True(download.Invocations.Count > before));
            var call = download.Invocations.Safe().Last();
            Assert.Matches(@"^linkbase-\w+-\d{4}-\d{2}-\d{2}-\d{4}\.(csv|json)$", (string)call.Arguments[0]!);
            var bytes = ((MemoryStream)((DotNetStreamReference)call.Arguments[2]!).Stream).ToArray();
            return System.Text.Encoding.UTF8.GetString(bytes).TrimStart('﻿');
        }

        var nodes = Saved("Nodes (CSV)");
        Assert.StartsWith("id,type,name", nodes, StringComparison.Ordinal);
        Assert.Contains(s.Crag, nodes, StringComparison.Ordinal);
        Assert.Contains(s.Route, nodes, StringComparison.Ordinal);

        var links = Saved("Links (CSV)");
        Assert.Contains($"route:{s.RouteId},Route,{s.Route},AT,crag:{s.CragId},Crag,{s.Crag}", links, StringComparison.Ordinal);

        var json = System.Text.Json.JsonDocument.Parse(Saved("Nodes and links (JSON)"));
        Assert.True(json.RootElement.GetProperty("nodes").GetArrayLength() >= 2);
        Assert.True(json.RootElement.GetProperty("links").GetArrayLength() >= 1);

        Press(cut, "Export");
        ClickFresh(Popovers, () => Popovers.FindAll(".mud-menu-item").First(i => i.TextContent.Contains("Picture", StringComparison.Ordinal)));
        cut.WaitForAssertion(() => Assert.Matches(@"^linkbase-graph-.*\.png$", (string)_graph.Invocations.Safe().Last(i => i.Identifier == "savePng").Arguments[0]!));

        // A removed node isn't shown, so it isn't exported either.
        await cut.InvokeAsync(() => cut.Instance.OnNodeClicked($"route:{s.RouteId}"));
        Press(cut, "Remove");
        Assert.DoesNotContain(s.Route, Saved("Nodes (CSV)"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Remove_fit_and_clear()
    {
        var s = await SeedAsync();
        var cut = Render(s);
        Assert.False(HasControl(cut, "Hide"));

        Press(cut, "Fit");
        cut.WaitForAssertion(() => Assert.Contains("fit", GraphCalls));

        // Removing what an expand brought in, then expanding again, brings it back.
        bool OnGraph(string id) => ((object[])_graph.Invocations.Safe().Last(i => i.Identifier == "update").Arguments[0]!)
            .Any(n => n.ToString()!.Contains($"id = {id},", StringComparison.Ordinal));
        Press(cut, "Expand");
        cut.WaitForAssertion(() => Assert.True(OnGraph($"route:{s.RouteId}")));
        await cut.InvokeAsync(() => cut.Instance.OnNodeClicked($"route:{s.RouteId}"));
        Press(cut, "Remove");
        cut.WaitForAssertion(() => Assert.False(OnGraph($"route:{s.RouteId}")));
        await cut.InvokeAsync(() => cut.Instance.OnNodeClicked($"crag:{s.CragId}"));
        Press(cut, "Expand again");
        cut.WaitForAssertion(() => Assert.True(OnGraph($"route:{s.RouteId}")));

        await cut.InvokeAsync(() => cut.Instance.OnNodeRightClicked($"crag:{s.CragId}", 10, 20));
        Press(cut, "Remove others");
        cut.WaitForAssertion(() => Assert.False(OnGraph($"route:{s.RouteId}")));
        await cut.InvokeAsync(() => cut.Instance.OnNodeClicked($"crag:{s.CragId}"));
        Press(cut, "Remove");
        Sees(cut, "Type anything above");

        Search(cut, s.Crag);
        WaitForOption(cut, s.Crag);
        Key(cut, "Enter");
        Key(cut, "Enter");
        cut.WaitForAssertion(() => Assert.Equal(s.Crag, cut.Find(".explore-panel h2").TextContent.Trim()));

        Press(cut, "Clear");
        Sees(cut, "Type anything above");
    }

    [Fact]
    public async Task A_search_with_no_matches_says_so()
    {
        await UseClimbingStoreAsync();
        var cut = RenderPage<ExplorePage>();

        Search(cut, "Crag");
        WaitForOption(cut, "Crag");
        Key(cut, "Enter");
        Search(cut, "name");
        WaitForOption(cut, "name");
        Key(cut, "Enter");
        cut.WaitForAssertion(() => Assert.Contains("Choose how to compare", cut.Markup));
        Key(cut, "ArrowDown");
        Key(cut, "Enter");
        cut.WaitForAssertion(() => Assert.Contains("Type a value", cut.Markup));
        Search(cut, Unique("Nowhere"));
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[role=option]")));
        Key(cut, "Enter");
        Key(cut, "Enter");

        Told("Nothing matches that search.");
    }

    [Fact]
    public async Task More_than_a_page_of_matches_can_be_shown_in_turn()
    {
        var prefix = Unique("Many");
        var s = await SeedAsync();
        var store = await UseClimbingStoreAsync();
        await ClimbingStore.AddManyAsync(store, "Crag", Enumerable.Range(0, 105).Select(i => $"{prefix} {i:000}"), null, "IN", $"area:{s.AreaId}");
        await ClimbingStore.AddManyAsync(store, "Route", Enumerable.Range(0, 105).Select(i => $"{prefix} route {i:000}"), null, "AT", $"crag:{s.CragId}");

        var cut = RenderPage<ExplorePage>();
        Search(cut, "Crag");
        WaitForOption(cut, "Crag");
        Key(cut, "Enter");
        Search(cut, "name");
        WaitForOption(cut, "name");
        Key(cut, "Enter");
        cut.WaitForAssertion(() => Assert.Contains("Choose how to compare", cut.Markup));
        MouseDownFresh(cut, () => cut.FindAll("[role=option]").First(o => o.TextContent.Contains("starts with", StringComparison.Ordinal)));
        cut.WaitForAssertion(() => Assert.Contains("Type a value", cut.Markup));
        Search(cut, prefix);
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[role=option]")));
        Key(cut, "Enter");
        Key(cut, "Enter");

        Sees(cut, "Showing the first 100 matches. 5 more.");
        Press(cut, "Show more");
        DoesNotSee(cut, "Showing the first");

        Press(cut, "Clear");
        var byName = RenderPage<ExplorePage>();
        Search(byName, s.Crag);
        WaitForOption(byName, s.Crag);
        Key(byName, "Enter");
        Key(byName, "Enter");
        Sees(byName, s.Crag + "</h2>");
        Press(byName, "Expand");
        Sees(byName, "more linked here weren't added");
        Press(byName, "Show more");
        DoesNotSee(byName, "more linked here weren't added");
    }

    [Fact]
    public async Task Expanding_respects_the_filters()
    {
        var s = await SeedAsync();
        var cut = Render(s);

        await cut.InvokeAsync(() => cut.FindComponents<MudSelect<string>>().First(c => c.Instance.Label == "Relationships").Instance.SelectedValuesChanged.InvokeAsync(["AT"]));
        await SetAsync(cut, cut.FindComponents<MudSelect<string>>().First(c => c.Instance.Label == "Property").Instance.ValueChanged, "discipline");
        Sees(cut, "Add filter");
        Assert.True(Control(cut, "Add filter").HasAttribute("disabled"));
        // A choice offers its options rather than a text box.
        await SetAsync(cut, cut.FindComponents<MudSelect<string>>().First(c => c.Instance.Label == "Value").Instance.ValueChanged, "boulder");
        Press(cut, "Add filter");
        Sees(cut, "discipline = boulder");
        DoesNotSee(cut, "Add filter");

        Press(cut, "Expand");
        Told("Nothing new to add");

        cut.Find(".mud-chip-close-button").Click();
        DoesNotSee(cut, "discipline = boulder");
        await SetAsync(cut, cut.FindComponents<MudSelect<string>>().First(c => c.Instance.Label == "Property").Instance.ValueChanged, "grade");
        Sees(cut, "Add filter");
        await SetAsync(cut, cut.FindComponents<MudSelect<string>>().First(c => c.Instance.Label == "Property").Instance.ValueChanged, (string?)null!);
        DoesNotSee(cut, "Add filter");

        Press(cut, "Expand again");
        Sees(cut, "Route 1");
    }

    [Fact]
    public async Task A_view_can_be_saved_with_its_layout_pinned_and_reopening_it_restores_exact_positions()
    {
        var s = await SeedAsync();
        var cut = OpenWith(s);
        var name = Unique("Crag view");
        _graph.Setup<ScenePosition[]>("positions").SetResult([new ScenePosition($"crag:{s.CragId}", 5, 6)]);

        Press(cut, "Views");
        Type(Dialogs, "Name for this view", name);
        Dialogs.FindAll(".mud-checkbox").First(c => c.TextContent.Contains("Keep nodes")).QuerySelector("input")!.Change(true);
        Press(Dialogs, "Save");
        Told("Saved the view");

        var saved = (await Services.GetRequiredService<Linkbase.Core.Views.SavedViewRepository>().ListAsync()).Single(v => v.Name == name);
        var state = ViewState.Decode<ExploreState>(saved.State)!;
        Assert.Equal(new ScenePosition($"crag:{s.CragId}", 5, 6), state.Positions!.Single());

        var loaded = OpenAt(saved.State);
        loaded.WaitForAssertion(() => Assert.Contains("restore", GraphCalls));
        var drawn = (object[])_graph.Invocations.Safe().Last(i => i.Identifier == "restore").Arguments[0]!;
        Assert.Single(drawn);
    }

    [Fact]
    public async Task A_view_saved_with_its_layout_pinned_restores_exact_positions_and_reports_missing_nodes()
    {
        var s = await SeedAsync();
        var state = new ExploreState
        {
            Nodes = [$"crag:{s.CragId}", "crag:nobody"],
            Positions = [new ScenePosition($"crag:{s.CragId}", 1, 2), new ScenePosition("crag:nobody", 3, 4)],
        };

        var cut = OpenAt(state.Encode());

        Told("1 of the records in this view no longer exist.");
        cut.WaitForAssertion(() => Assert.Contains("restore", GraphCalls));
        var drawn = (object[])_graph.Invocations.Safe().Last(i => i.Identifier == "restore").Arguments[0]!;
        Assert.Single(drawn);
    }

    [Fact]
    public async Task A_pinned_layout_view_with_every_node_present_reports_nothing()
    {
        var s = await SeedAsync();
        var state = new ExploreState { Nodes = [$"crag:{s.CragId}"], Positions = [new ScenePosition($"crag:{s.CragId}", 1, 2)] };

        var cut = OpenAt(state.Encode());

        cut.WaitForAssertion(() => Assert.Contains("restore", GraphCalls));
        Assert.Empty(Snackbar.ShownSnackbars);
    }

    [Fact]
    public async Task Saving_a_pinned_layout_view_is_safe_before_the_graph_has_loaded()
    {
        Services.AddSingleton<IJSRuntime>(new NoNetworkJs());
        var s = await SeedAsync();
        var cut = OpenWithoutDrawing(s);
        var name = Unique("View");

        Press(cut, "Fit");
        Press(cut, "Views");
        Type(Dialogs, "Name for this view", name);
        Dialogs.FindAll(".mud-checkbox").First(c => c.TextContent.Contains("Keep nodes")).QuerySelector("input")!.Change(true);
        Press(Dialogs, "Save");

        Told("Saved the view");
    }

    private IRenderedComponent<ExplorePage> OpenWithoutDrawing(Seeded s)
    {
        var cut = RenderPage<ExplorePage>();
        Search(cut, s.Crag);
        WaitForOption(cut, s.Crag);
        Key(cut, "Enter");
        cut.WaitForAssertion(() => Assert.Contains("pattern-node", cut.Markup));
        Key(cut, "Enter");
        cut.WaitForAssertion(() => Assert.Equal(s.Crag, cut.Find(".explore-panel h2").TextContent.Trim()));
        return cut;
    }

    [Fact]
    public async Task Leaving_destroys_the_graph_even_if_the_browser_has_gone()
    {
        var cut = RenderPage<ExplorePage>();
        cut.WaitForAssertion(() => Assert.Contains("create", GraphCalls));
        await DisposeComponentsAsync();
        Assert.Contains("destroy", GraphCalls);

        var again = RenderPage<ExplorePage>();
        _graph.SetupVoid("destroy").SetException(new JSDisconnectedException("gone"));
        again.WaitForAssertion(() => Assert.True(GraphCalls.Count(c => c == "create") >= 2));
        await DisposeComponentsAsync();
    }

    [Fact]
    public async Task Leaving_before_the_graph_has_loaded_is_safe()
    {
        Services.AddSingleton<IJSRuntime>(new NoNetworkJs());
        var cut = RenderPage<ExplorePage>();
        Sees(cut, "can't be drawn");
        await DisposeComponentsAsync();
    }
}

[Trait("Category", "Database")]
public class PatternSearchBarTests(PostgresFixture fixture) : UiTestBase(fixture), IClassFixture<PostgresFixture>
{
    private static readonly GraphSchema Schema = new(
        [new GraphConnection("Route", "AT", "Crag")],
        [new GraphPropertyInfo("Crag", "name", GraphValueType.Text), new GraphPropertyInfo("Route", "name", GraphValueType.Text), new GraphPropertyInfo("Route", "length_m", GraphValueType.Number)]);

    private GraphPattern? _ran;

    private IRenderedComponent<PatternSearchBar> Bar(GraphSchema? schema = null) => RenderPage<PatternSearchBar>(p => p
        .Add(x => x.Schema, schema ?? Schema)
        .Add(x => x.OnRun, EventCallback.Factory.Create<GraphPattern>(this, pattern => _ran = pattern)));

    private static SearchInput Input(IRenderedComponent<PatternSearchBar> cut) => new(cut);

    [Fact]
    public async Task Shows_an_answer_to_a_question_for_editing_without_running_it()
    {
        var cut = Bar();

        await cut.InvokeAsync(() => cut.Instance.ShowPattern(new GraphPattern(
            [new PatternNode("Route", [new PatternFilter("name", Linkbase.Core.Graph.FilterOperator.Contains, "Sun")]), new PatternNode("Crag", [])],
            [new PatternRel("AT", true)])));

        Assert.Contains("AT →", cut.Markup);
        Assert.Contains("Sun", cut.Markup);
        Assert.Null(_ran);
    }

    private static GraphPattern RankedRoutes() => new(
        [new PatternNode("Route", []), new PatternNode("Crag", [])],
        [new PatternRel("AT", true)])
    {
        Rank = new PatternRank(1, 0, 5),
    };

    [Fact]
    public async Task A_ranking_shows_in_the_bar_where_it_can_be_changed_run_and_removed()
    {
        var cut = Bar();
        await cut.InvokeAsync(() => cut.Instance.ShowPattern(RankedRoutes()));

        Assert.NotEmpty(cut.FindAll(".pattern-rank"));
        cut.Find("select[aria-label='How many to show']").Change("3");
        cut.FindAll("button").First(b => b.TextContent.Trim() == "Search").Click();
        Assert.Equal(new PatternRank(1, 0, 3), _ran?.Rank);

        cut.Find("button[aria-label='Remove the ranking']").Click();
        Assert.Empty(cut.FindAll(".pattern-rank"));
    }

    [Fact]
    public async Task The_ranking_offers_totals_only_for_a_counted_type_with_numbers_and_can_put_the_fewest_first()
    {
        var cut = Bar();
        await cut.InvokeAsync(() => cut.Instance.ShowPattern(RankedRoutes()));

        // Route has a number field, so totals are on offer. Counting Crag, which has none, takes them away again.
        Assert.NotEmpty(cut.FindAll("select[aria-label='What to work out'] option[value='Sum']"));
        Assert.Empty(cut.FindAll("select[aria-label='Which number to use']"));
        cut.Find("select[aria-label='What to work out']").Change("Sum");
        cut.Find("select[aria-label='Biggest or smallest first']").Change("fewest");
        cut.FindAll("button").First(b => b.TextContent.Trim() == "Search").Click();

        Assert.Equal(RankMeasure.Sum, _ran?.Rank?.Measure);
        Assert.NotNull(_ran?.Rank?.Key);
        Assert.True(_ran?.Rank?.Fewest);

        // Swapping the ends so Crag is counted leaves no number to total, so the ranking goes back to counting.
        cut.Find("select[aria-label='Which records to count']").Change("1");
        Assert.Empty(cut.FindAll("select[aria-label='What to work out'] option[value='Sum']"));
        Assert.Empty(cut.FindAll("select[aria-label='Which number to use']"));
    }

    [Fact]
    public async Task Typing_rank_adds_a_ranking_by_hand_that_Search_then_runs()
    {
        var cut = Bar();
        await cut.InvokeAsync(() => cut.Instance.ShowPattern(new GraphPattern(
            [new PatternNode("Route", []), new PatternNode("Crag", [])], [new PatternRel("AT", true)])));
        Assert.Empty(cut.FindAll(".pattern-rank"));

        cut.Find("input").Focus();
        Input(cut).Type("rank");
        cut.WaitForAssertion(() => Assert.Contains(cut.FindAll("[role=option]"), o => o.TextContent.Contains("Rank Route by most different Crag", StringComparison.Ordinal)));
        Input(cut).Key("Enter");

        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll(".pattern-rank")));
        Assert.Null(_ran);
        cut.WaitForAssertion(() =>
        {
            cut.FindAll("button").First(b => b.TextContent.Trim() == "Search").Click();
            Assert.Equal(new PatternRank(0, 1, 5), _ran?.Rank);
        });
    }

    [Fact]
    public async Task A_plain_search_shows_no_ranking_in_the_bar()
    {
        var cut = Bar();
        await cut.InvokeAsync(() => cut.Instance.ShowPattern(new GraphPattern([new PatternNode("Crag", [])], [])));

        Assert.DoesNotContain("by most different", cut.Markup);
        Assert.Empty(cut.FindAll(".pattern-rank"));
    }

    private sealed class SearchInput(IRenderedComponent<PatternSearchBar> cut)
    {
        public void Type(string text) => cut.Find("input").Input(text);
        public void Key(string key) => cut.Find("input").KeyDown(new KeyboardEventArgs { Key = key });
        public string? Attribute(string name) => cut.Find("input").GetAttribute(name);
    }

    [Fact]
    public void Waits_for_the_schema()
    {
        var cut = RenderPage<PatternSearchBar>(p => p.Add(x => x.Schema, (GraphSchema?)null));
        Assert.Equal("Loading…", Input(cut).Attribute("placeholder"));

        Input(cut).Type("Cr");
        Input(cut).Key("Enter");
        Input(cut).Key("Backspace");
        cut.Find("input").Focus();
        cut.Find(".pattern-bar").Click();
        Assert.Empty(cut.FindAll("[role=option]"));
    }

    [Fact]
    public void Arrow_keys_wrap_and_escape_closes()
    {
        var cut = Bar();
        cut.Find("input").Focus();
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[role=option]")));
        var count = cut.FindAll("[role=option]").Count;
        Assert.Null(Input(cut).Attribute("aria-activedescendant"));

        Input(cut).Key("ArrowUp");
        Assert.Equal($"pattern-suggestion-{count - 1}", Input(cut).Attribute("aria-activedescendant"));
        Input(cut).Key("ArrowDown");
        Assert.Equal("pattern-suggestion-0", Input(cut).Attribute("aria-activedescendant"));
        Input(cut).Key("ArrowUp");
        Input(cut).Key("ArrowUp");
        Assert.Equal($"pattern-suggestion-{count - 2}", Input(cut).Attribute("aria-activedescendant"));

        Input(cut).Key("Escape");
        Assert.Empty(cut.FindAll("[role=option]"));

        Input(cut).Key("Tab");
        cut.Find("input").Focus();
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[role=option]")));
        cut.Find("input").Blur();
        Assert.Empty(cut.FindAll("[role=option]"));
    }

    [Fact]
    public void Enter_on_nothing_does_nothing()
    {
        var cut = Bar();
        Input(cut).Key("Enter");
        Assert.Null(_ran);
    }

    [Fact]
    public void Builds_a_search_with_pills_placeholders_and_clear()
    {
        var cut = Bar();
        Assert.Equal("Search for anything, start with a type such as Crag, or type graph", Input(cut).Attribute("placeholder"));

        Input(cut).Type("Crag");
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[role=option]")));
        Input(cut).Key("Enter");
        cut.WaitForAssertion(() => Assert.Equal("Add a filter or relationship, or press Enter to search", Input(cut).Attribute("placeholder")));
        Assert.Contains("background: ", cut.Find(".pattern-node").GetAttribute("style"));

        cut.Find("button[aria-label='Clear search']").Click();
        cut.WaitForAssertion(() => Assert.Empty(cut.FindAll(".pattern-node")));

        Input(cut).Type("Crag");
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[role=option]")));
        Input(cut).Key("Enter");
        Input(cut).Type("name");
        cut.WaitForAssertion(() => Assert.Contains(cut.FindAll("[role=option]"), o => o.TextContent.Contains("name", StringComparison.Ordinal)));
        Input(cut).Key("Enter");
        cut.WaitForAssertion(() => Assert.Equal("Choose how to compare", Input(cut).Attribute("placeholder")));
        Assert.Contains(cut.FindAll(".pattern-node-filter"), p => p.ClassList.Contains("pattern-node-pending"));

        Input(cut).Key("Backspace");
        cut.WaitForAssertion(() => Assert.DoesNotContain(cut.FindAll(".pattern-node-filter"), p => p.ClassList.Contains("pattern-node-pending")));

        Press(cut, "Search");
        Assert.NotNull(_ran);
    }

    [Fact]
    public async Task Offers_real_values_and_shows_filters_as_pills()
    {
        var s = await SeedAsync();
        var cut = Bar();

        Input(cut).Type("Crag");
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[role=option]")));
        Input(cut).Key("Enter");
        Input(cut).Type("name");
        cut.WaitForAssertion(() => Assert.Contains(cut.FindAll("[role=option]"), o => o.TextContent.Contains("name", StringComparison.Ordinal)));
        Input(cut).Key("Enter");
        cut.WaitForAssertion(() => Assert.Equal("Choose how to compare", Input(cut).Attribute("placeholder")));
        MouseDownFresh(cut, () => cut.FindAll("[role=option]").First(o => o.TextContent.Contains('=')));
        cut.WaitForAssertion(() => Assert.Equal("Type a value, then press Enter", Input(cut).Attribute("placeholder")));

        Input(cut).Type(s.Crag);
        cut.WaitForAssertion(() => Assert.Contains(cut.FindAll("[role=option]"), o => o.TextContent.Contains(s.Crag, StringComparison.Ordinal)));
        MouseDownFresh(cut, () => cut.FindAll("[role=option]").First(o => o.TextContent.Contains(s.Crag, StringComparison.Ordinal)));

        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll(".pattern-node-filter")));
        Input(cut).Key("Enter");
        Assert.Equal(s.Crag, _ran!.Nodes.Single().Filters.Single().Value);
    }

    [Fact]
    public async Task Matches_a_name_directly_and_a_new_schema_starts_again()
    {
        var s = await SeedAsync();
        var cut = Bar();

        Input(cut).Type(s.Crag);
        cut.WaitForAssertion(() => Assert.Contains(cut.FindAll("[role=option] .pattern-option-hint"), _ => true));
        Input(cut).Key("Enter");
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll(".pattern-node")));

        cut.Render(p => p.Add(x => x.Schema, new GraphSchema(Schema.Connections, Schema.Properties)));
        cut.WaitForAssertion(() => Assert.Empty(cut.FindAll(".pattern-node")));

        cut.Render(p => p.Add(x => x.Schema, cut.Instance.Schema));
        Input(cut).Type("zz");
        Input(cut).Type("zzz");
        await DisposeComponentsAsync();
    }
}
