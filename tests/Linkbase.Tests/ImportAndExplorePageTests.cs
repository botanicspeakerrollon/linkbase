using System.Net;
using Bunit;
using Linkbase.Data;
using Linkbase.Domains.Climbing.Data;
using Linkbase.Core.Graph;
using Linkbase.Core.Explore;
using Linkbase.Domains.Climbing.Features.WebImport;
using Linkbase.Domains.Climbing.Import.Web;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.JSInterop;
using MudBlazor;

namespace Linkbase.Tests;

[Trait("Category", "Database")]
[Collection(PostgresCollection.Name)]
public class ImportPageTests(PostgresFixture fixture) : UiTestBase(fixture)
{
    private void UseExtractor(Func<string, CancellationToken, Task<ExtractedPage>> answer, Func<HttpResponseMessage>? page = null)
    {
        Services.AddSingleton<IClimbingExtractor>(new StubExtractor(answer));
        Services.AddSingleton(new PageTextFetcher(new HttpClient(new StubHandler(page ?? (() => new HttpResponseMessage(HttpStatusCode.NotFound))))));
        Services.AddSingleton<WebImportService>();
    }

    private static ExtractedPage Page(string crag, params string[] routes) => new(
        [],
        [new ExtractedCrag(crag, null, null, null, null)],
        routes.Select(r => new ExtractedRoute(r, crag, null, null, null, null, null, null)).ToList());

    private static void OpenTab(IRenderedComponent<ImportPage> cut, string name) =>
        ClickFresh(cut, () => cut.FindAll(".mud-tab").First(t => t.TextContent.Trim() == name));

    [Fact]
    public void Extract_needs_something_to_read()
    {
        UseExtractor((_, _) => Task.FromResult(Page("x")));
        var cut = RenderPage<ImportPage>();

        Assert.True(Control(cut, "Extract").HasAttribute("disabled"));
        OpenTab(cut, "Fetch a page");
        Sees(cut, "Web address");
        Assert.True(Control(cut, "Extract").HasAttribute("disabled"));
    }

    [Fact]
    public void Pasted_text_goes_to_the_review_page()
    {
        var crag = Unique("Crag");
        UseExtractor((_, _) => Task.FromResult(Page(crag)));
        var cut = RenderPage<ImportPage>();

        Type(cut, "Text from the page", crag);
        Type(cut, "Where it came from (optional)", "https://example.com/a");
        Press(cut, "Extract");

        WaitUntil(() => CurrentPath.StartsWith("/import/", StringComparison.Ordinal), "the review page");
    }

    [Fact]
    public void Shows_progress_through_a_long_page_and_can_cancel()
    {
        var release = new TaskCompletionSource();
        UseExtractor(async (_, token) =>
        {
            await release.Task.WaitAsync(token);
            return Page("x");
        });
        var cut = RenderPage<ImportPage>();

        Type(cut, "Text from the page", string.Join("\n\n", Enumerable.Repeat(new string('x', 500), 80)));
        Press(cut, "Extract");
        cut.WaitForAssertion(() => Assert.Contains("role=\"status\"", cut.Markup));
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll(".mud-progress-linear")));

        Press(cut, "Cancel");

        Sees(cut, "Cancelled.");
        DoesNotSee(cut, "role=\"status\"");
    }

    [Fact]
    public void Extraction_failures_are_shown_with_the_batch()
    {
        var message = Unique("The model refused");
        UseExtractor((_, _) => throw new ExtractionFailedException(message));
        var cut = RenderPage<ImportPage>();

        Type(cut, "Text from the page", "Some text");
        Press(cut, "Extract");

        Sees(cut, message);
        Sees(cut, "Failed");
    }

    [Fact]
    public void A_fetch_failure_suggests_pasting()
    {
        UseExtractor((_, _) => Task.FromResult(Page("x")));
        var cut = RenderPage<ImportPage>();

        OpenTab(cut, "Fetch a page");
        Type(cut, "Web address", "https://example.com/missing");
        Press(cut, "Extract");

        Sees(cut, "paste");
    }

    [Fact]
    public void A_fetched_page_goes_to_the_review_page()
    {
        var crag = Unique("Crag");
        var html = $"<html><head><title>T</title></head><body><article><h1>{crag}</h1><p>{string.Join(" ", Enumerable.Repeat($"{crag} is a very good place for climbing on most days of the year.", 30))}</p></article></body></html>";
        UseExtractor(
            (_, _) => Task.FromResult(Page(crag)),
            () => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(html, System.Text.Encoding.UTF8, "text/html") });
        var cut = RenderPage<ImportPage>();

        OpenTab(cut, "Fetch a page");
        Type(cut, "Web address", "https://example.com/" + Guid.NewGuid());
        Press(cut, "Extract");

        WaitUntil(() => CurrentPath.StartsWith("/import/", StringComparison.Ordinal), "the review page");
    }

    private async Task<long> CommitAsync(string crag, string route, string country)
    {
        await using (var db = Fixture.CreateContext())
        {
            db.Areas.Add(new Area { UserId = AppUser.DefaultId, Name = country, Kind = AreaKind.Country });
            await db.SaveChangesAsync();
        }

        var service = Services.GetRequiredService<WebImportService>();
        var repository = Services.GetRequiredService<WebImportRepository>();
        var batch = await service.ExtractFromTextAsync($"{crag}\n{route}", null, null, CancellationToken.None);
        var staged = (await repository.LoadStagedAsync(batch))!;
        await using (var db = Fixture.CreateContext())
        {
            staged.Crags[0].AreaId = await db.Areas.Where(a => a.Name == country).Select(a => a.Id).SingleAsync();
        }

        await repository.CommitAsync(batch, staged.Crags);
        return batch;
    }

    [Fact]
    public async Task Undo_asks_first_then_reports_what_it_removed()
    {
        var (crag, route, country) = (Unique("Crag"), Unique("Route"), Unique("Country"));
        UseExtractor((_, _) => Task.FromResult(Page(crag, route)));
        await CommitAsync(crag, route, country);
        var cut = RenderPage<ImportPage>();

        Press(cut, "Undo");
        Press(Dialogs, "Cancel");
        Dialogs.WaitForAssertion(() => Assert.False(HasControl(Dialogs, "Cancel")));
        Sees(cut, "Undo");

        Press(cut, "Undo");
        Press(Dialogs, "Undo");

        Sees(cut, "Undo removed 1 route, 1 crag");
        cut.Find(".mud-alert-close button").Click();
        DoesNotSee(cut, "Undo removed");
    }

    [Fact]
    public async Task Undo_keeps_rows_that_have_ascents()
    {
        var (crag, route, country) = (Unique("Crag"), Unique("Route"), Unique("Country"));
        UseExtractor((_, _) => Task.FromResult(Page(crag, route)));
        await CommitAsync(crag, route, country);
        await using (var db = Fixture.CreateContext())
        {
            var routeId = await db.Routes.Where(r => r.Name == route).Select(r => r.Id).SingleAsync();
            await Services.GetRequiredService<AscentRepository>().CreateAsync(new AscentInput
            {
                RouteId = routeId,
                Date = new DateOnly(2020, 1, 1),
                DatePrecision = DatePrecision.Day,
                Style = AscentStyle.Lead,
            });
        }

        var cut = RenderPage<ImportPage>();
        Press(cut, "Undo");
        Press(Dialogs, "Undo");

        Sees(cut, "Kept these");
        Sees(cut, route);
    }

    [Fact]
    public async Task A_rejected_undo_is_reported()
    {
        var (crag, route, country) = (Unique("Crag"), Unique("Route"), Unique("Country"));
        UseExtractor((_, _) => Task.FromResult(Page(crag, route)));
        var batch = await CommitAsync(crag, route, country);
        var cut = RenderPage<ImportPage>();
        Sees(cut, "Undo");
        await Services.GetRequiredService<WebImportRepository>().UndoAsync(batch);

        Press(cut, "Undo");
        Press(Dialogs, "Undo");

        cut.WaitForAssertion(() => Assert.NotEmpty(Snackbar.ShownSnackbars));
    }

    [Fact]
    public async Task Leaving_the_page_cancels_the_extraction()
    {
        var cancelled = new TaskCompletionSource();
        UseExtractor(async (_, token) =>
        {
            token.Register(() => cancelled.TrySetResult());
            await Task.Delay(Timeout.Infinite, token);
            return Page("x");
        });
        var cut = RenderPage<ImportPage>();
        Type(cut, "Text from the page", "text");
        Press(cut, "Extract");
        cut.WaitForAssertion(() => Assert.Contains("role=\"status\"", cut.Markup));

        await DisposeComponentsAsync();

        await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(10));
    }
}

[Trait("Category", "Database")]
[Collection(PostgresCollection.Name)]
public class StagePageTests(PostgresFixture fixture) : UiTestBase(fixture)
{
    private async Task<long> StageAsync(string text, ExtractedPage page, string? url = null)
    {
        var service = new WebImportService(
            new PageTextFetcher(new HttpClient()),
            new StubExtractor((_, _) => Task.FromResult(page)),
            Services.GetRequiredService<WebImportRepository>());
        return await service.ExtractFromTextAsync(text, url, null, CancellationToken.None);
    }

    private static ExtractedPage Page(string crag, string? area, params string[] routes) => new(
        [],
        [new ExtractedCrag(crag, area, null, null, null)],
        routes.Select(r => new ExtractedRoute(r, crag, "VS", null, null, null, null, null)).ToList());

    [Fact]
    public void Says_when_the_import_is_gone()
    {
        var cut = RenderPage<StagePage>(p => p.Add(x => x.BatchId, -1));
        Sees(cut, "This import no longer exists.");
    }

    [Fact]
    public async Task A_page_with_nothing_found_says_so()
    {
        var batch = await StageAsync("Nothing here", new ExtractedPage([], [], []));
        var cut = RenderPage<StagePage>(p => p.Add(x => x.BatchId, batch));

        Sees(cut, "No crags or routes were found");
        Assert.True(Control(cut, "Add to logbook").HasAttribute("disabled"));
    }

    [Fact]
    public async Task Shows_warnings_and_the_source()
    {
        var crag = Unique("Crag");
        var page = new ExtractedPage([], [new ExtractedCrag(crag, null, null, null, null), new ExtractedCrag("Invented", null, null, null, null)], []);
        var batch = await StageAsync(crag, page, "https://example.com/" + Guid.NewGuid());
        var cut = RenderPage<StagePage>(p => p.Add(x => x.BatchId, batch));

        Sees(cut, "Removed from the model");
        Sees(cut, "Invented");
        Sees(cut, "https://example.com/");
    }

    [Fact]
    public async Task A_new_crag_can_be_given_a_new_area_and_added()
    {
        var s = await SeedAsync();
        var (crag, route, area) = (Unique("Crag"), Unique("Route"), Unique("New area"));
        var batch = await StageAsync($"{crag}\n{route}\n{s.Country}", Page(crag, s.Country, route));
        var cut = RenderPage<StagePage>(p => p.Add(x => x.BatchId, batch));

        Sees(cut, $"The text says: {s.Country}");
        Sees(cut, route);
        Assert.Contains("Create new", cut.Markup);

        // Part of the name, because the field already holds all of it and retyping that wouldn't search.
        Pick(cut, "Area", s.Country[..^1], s.Country);
        var newArea = cut.FindComponents<MudTextField<string>>().First(f => f.Instance.Label == "Or a new area named");
        await SetAsync(cut, newArea.Instance.ValueChanged, area);
        await SetAsync(cut, newArea.Instance.ValueChanged, "");
        await SetAsync(cut, newArea.Instance.ValueChanged, area);

        Press(cut, "Add to logbook");

        Told("Added 1 new route and 1 new crag.");
        NavigatedTo("/import");
        await using var db = Fixture.CreateContext();
        var saved = await db.Crags.AsNoTracking().Where(c => c.Name == crag).Select(c => new { Area = c.Area.Name, c.Area.ParentId }).SingleAsync();
        Assert.Equal(area, saved.Area);
        Assert.Equal(s.AreaId, saved.ParentId);

        var reviewed = RenderPage<StagePage>(p => p.Add(x => x.BatchId, batch));
        Sees(reviewed, "so there's nothing left to review");
    }

    [Fact]
    public async Task A_new_area_name_set_first_keeps_the_area_chosen_after_as_its_parent()
    {
        var s = await SeedAsync();
        var (crag, area) = (Unique("Crag"), Unique("New area"));
        var batch = await StageAsync(crag, Page(crag, null));
        var cut = RenderPage<StagePage>(p => p.Add(x => x.BatchId, batch));
        Sees(cut, "The text doesn't say");

        var newArea = cut.FindComponents<MudTextField<string>>().First(f => f.Instance.Label == "Or a new area named");
        await SetAsync(cut, newArea.Instance.ValueChanged, area);
        var picker = cut.FindComponent<MudAutocomplete<AreaOption>>();
        await SetAsync(cut, picker.Instance.ValueChanged, new AreaOption(s.AreaId, s.Country, s.Country));

        Press(cut, "Add to logbook");

        Told("Added");
        await using var db = Fixture.CreateContext();
        Assert.Equal(s.AreaId, await db.Crags.AsNoTracking().Where(c => c.Name == crag).Select(c => c.Area.ParentId).SingleAsync());
    }

    [Fact]
    public async Task A_new_area_with_no_parent_can_be_marked_as_a_country()
    {
        var (crag, country) = (Unique("Crag"), Unique("New country"));
        var batch = await StageAsync(crag, Page(crag, null));
        var cut = RenderPage<StagePage>(p => p.Add(x => x.BatchId, batch));
        Sees(cut, "The text doesn't say");
        DoesNotSee(cut, "It's a country");

        var newArea = cut.FindComponents<MudTextField<string>>().First(f => f.Instance.Label == "Or a new area named");
        await SetAsync(cut, newArea.Instance.ValueChanged, country);
        Press(cut, "Add to logbook");
        Sees(cut, "or mark it as a country");

        var isCountry = cut.FindComponents<MudCheckBox<bool>>().First(c => c.Instance.Label == "It's a country");
        await SetAsync(cut, isCountry.Instance.ValueChanged, true);
        await SetAsync(cut, isCountry.Instance.ValueChanged, false);
        await SetAsync(cut, isCountry.Instance.ValueChanged, true);
        Press(cut, "Add to logbook");

        Told("Added");
        await using var db = Fixture.CreateContext();
        var saved = await db.Crags.AsNoTracking().Where(c => c.Name == crag).Select(c => new { c.Area.Name, c.Area.Kind, c.Area.ParentId }).SingleAsync();
        Assert.Equal(country, saved.Name);
        Assert.Equal(AreaKind.Country, saved.Kind);
        Assert.Null(saved.ParentId);
    }

    [Fact]
    public async Task The_country_choice_goes_once_a_parent_is_chosen()
    {
        var s = await SeedAsync();
        var crag = Unique("Crag");
        var batch = await StageAsync(crag, Page(crag, null));
        var cut = RenderPage<StagePage>(p => p.Add(x => x.BatchId, batch));
        Sees(cut, "The text doesn't say");

        var newArea = cut.FindComponents<MudTextField<string>>().First(f => f.Instance.Label == "Or a new area named");
        await SetAsync(cut, newArea.Instance.ValueChanged, Unique("New area"));
        Sees(cut, "It's a country");

        await SetAsync(cut, cut.FindComponent<MudAutocomplete<AreaOption>>().Instance.ValueChanged, new AreaOption(s.AreaId, s.Country, s.Country));

        DoesNotSee(cut, "It's a country");
    }

    [Fact]
    public async Task An_unchosen_area_is_reported_and_stays_on_the_page()
    {
        var crag = Unique("Crag");
        var batch = await StageAsync(crag, Page(crag, null));
        var cut = RenderPage<StagePage>(p => p.Add(x => x.BatchId, batch));
        Sees(cut, crag);

        Press(cut, "Add to logbook");

        Sees(cut, "mud-alert");
        Assert.Equal("/", CurrentPath);
    }

    [Fact]
    public async Task An_ambiguous_crag_must_be_chosen_and_its_routes_rematch()
    {
        var s = await SeedAsync();
        var twin = await SeedAsync();
        await using (var db = Fixture.CreateContext())
        {
            await db.Crags.Where(c => c.Id == twin.CragId).ExecuteUpdateAsync(u => u.SetProperty(c => c.Name, s.Crag).SetProperty(c => c.UpdatedAt, DateTimeOffset.UtcNow));
            await db.Routes.Where(r => r.Id == twin.RouteId).ExecuteUpdateAsync(u => u.SetProperty(r => r.Name, s.Route + " two").SetProperty(r => r.UpdatedAt, DateTimeOffset.UtcNow));
        }

        var batch = await StageAsync($"{s.Crag}\n{s.Route}", Page(s.Crag, null, s.Route));
        var cut = RenderPage<StagePage>(p => p.Add(x => x.BatchId, batch));
        Sees(cut, "Ambiguous");
        Sees(cut, "Choose…");

        var select = cut.FindComponents<MudSelect<long>>().First(f => f.Instance.Label == "Use");
        await SetAsync(cut, select.Instance.ValueChanged, s.CragId);

        Sees(cut, "Open this crag");
        Sees(cut, "Existing");
        Sees(cut, ">Open<");
        Press(cut, "Add to logbook");
        Told("Matched 1 route and 1 crag already in your logbook.");
    }

    [Fact]
    public async Task Leaving_a_crag_out_hides_its_routes_and_fields()
    {
        var (crag, route) = (Unique("Crag"), Unique("Route"));
        var batch = await StageAsync($"{crag}\n{route}", Page(crag, null, route));
        var cut = RenderPage<StagePage>(p => p.Add(x => x.BatchId, batch));
        Sees(cut, route);

        await SetAsync(cut, cut.FindComponents<MudCheckBox<bool>>().First(c => c.Instance.Label == "Add").Instance.ValueChanged, false);

        DoesNotSee(cut, "Rock type");
    }
}

[Trait("Category", "Database")]
[Collection(PostgresCollection.Name)]
public class ExplorePageTests : UiTestBase
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

    private IEnumerable<string> GraphCalls => _graph.Invocations.Select(i => i.Identifier);

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
        Sees(cut, "Open and edit");
        DoesNotSee(cut, "Type anything above");
    }

    [Fact]
    public async Task One_enter_searches_for_free_text()
    {
        var s = await SeedAsync(notes: $"Windy {Guid.NewGuid():N}");
        var note = (await Services.GetRequiredService<AscentRepository>().GetForEditAsync(s.AscentId))!.Notes!;
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
        var drawn = _graph.Invocations.Last(i => i.Identifier == "update").Arguments;
        Assert.Equal((await Services.GetRequiredService<GraphRepository>().SchemaAsync()).Labels.Count, ((object[])drawn[0]!).Length);
        Assert.NotEmpty((object[])drawn[1]!);
        Assert.Contains((object[])drawn[1]!, e => System.Text.RegularExpressions.Regex.IsMatch(e.ToString()!, @"relType = AT \(\d"));
        Assert.True(Control(cut, "Save").HasAttribute("disabled"));

        await cut.InvokeAsync(() => cut.Instance.OnNodeRightClicked("model:Crag", 10, 20));
        DoesNotSee(cut, "explore-menu");
        Sees(cut, "Show all Crag nodes");
        Sees(cut, "← AT Route (");
        Assert.False(HasControl(cut, "Hide"));
        Assert.False(HasControl(cut, "Open and edit"));

        Press(cut, "Show all Crag nodes");

        cut.WaitForAssertion(() => Assert.True(((object[])_graph.Invocations.Last(i => i.Identifier == "update").Arguments[0]!).Length > 0));
        DoesNotSee(cut, "Show all Crag nodes");
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

        await cut.InvokeAsync(() => cut.Instance.OnNodeDoubleClicked("model:Country"));
        cut.WaitForAssertion(() => Assert.DoesNotContain(
            ((object[])_graph.Invocations.Last(i => i.Identifier == "update").Arguments[0]!).Select(n => n.ToString()),
            n => n!.Contains("model:", StringComparison.Ordinal)));

        Press(cut, "Clear");
        Search(cut, "graph");
        WaitForOption(cut, "Graph model");
        Key(cut, "Enter");
        cut.WaitForAssertion(() => Assert.False(Control(cut, "Fit").HasAttribute("disabled")));

        Search(cut, s.Crag);
        WaitForOption(cut, "Search everything for");
        Key(cut, "Enter");

        Sees(cut, s.Crag + "</h2>");
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
        Sees(cut, "Open and edit");

        await cut.InvokeAsync(() => cut.Instance.OnNodeClicked("crag:-1"));
        Sees(cut, "Click a node for its details");
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
    public async Task The_right_click_menu_expands_dismisses_and_links()
    {
        var s = await SeedAsync();
        var cut = Render(s);

        await cut.InvokeAsync(() => cut.Instance.OnNodeRightClicked("crag:-1", 1, 2));
        Assert.DoesNotContain("explore-menu", cut.Markup);

        await cut.InvokeAsync(() => cut.Instance.OnNodeRightClicked($"crag:{s.CragId}", 10, 20));
        Sees(cut, "left: 10px; top: 20px");
        Sees(cut, "Dismiss others");
        var rel = cut.FindAll(".explore-menu button").First(b => b.TextContent.Contains("Route", StringComparison.Ordinal));
        rel.Click();
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
        Press(cut, "Dismiss others");
        DoesNotSee(cut, "Route 1");

        await cut.InvokeAsync(() => cut.Instance.OnNodeRightClicked($"crag:{s.CragId}", 10, 20));
        Press(cut, "Dismiss");
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
        Assert.Equal($"route:{s.RouteId}", _graph.Invocations.Last(i => i.Identifier == "select").Arguments[0]);

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
        cut.WaitForAssertion(() => Assert.Contains(JSInterop.Invocations, i => i.Identifier == "register"));

        await cut.InvokeAsync(() => cut.Instance.OnHotkey("t"));
        Sees(cut, "explore-table-wrap");
        await cut.InvokeAsync(() => cut.Instance.OnHotkey("t"));
        DoesNotSee(cut, "explore-table-wrap");

        await cut.InvokeAsync(() => cut.Instance.OnHotkey("f"));
        cut.WaitForAssertion(() => Assert.Contains("fit", GraphCalls));

        await cut.InvokeAsync(() => cut.Instance.OnHotkey("e"));
        Sees(cut, "Expand again");
        Sees(cut, "Route 1");

        await cut.InvokeAsync(() => cut.Instance.OnNodeRightClicked($"crag:{s.CragId}", 10, 20));
        Sees(cut, "explore-menu");
        await cut.InvokeAsync(() => cut.Instance.OnHotkey("Escape"));
        DoesNotSee(cut, "explore-menu");
        Sees(cut, s.Crag + "</h2>");
        await cut.InvokeAsync(() => cut.Instance.OnHotkey("Escape"));
        Sees(cut, "Click a node for its details");

        // With nothing selected, the keys that act on a node do nothing.
        await cut.InvokeAsync(() => cut.Instance.OnHotkey("h"));
        await cut.InvokeAsync(() => cut.Instance.OnHotkey("Delete"));
        await cut.InvokeAsync(() => cut.Instance.OnHotkey("q"));
        DoesNotSee(cut, "hidden</");

        await cut.InvokeAsync(() => cut.Instance.OnNodeClicked($"crag:{s.CragId}"));
        await cut.InvokeAsync(() => cut.Instance.OnHotkey("h"));
        Sees(cut, "Show 1 hidden");

        await cut.InvokeAsync(() => cut.Instance.OnNodeClicked($"route:{s.RouteId}"));
        await cut.InvokeAsync(() => cut.Instance.OnHotkey("Delete"));
        Sees(cut, "Click a node for its details");
        Assert.DoesNotContain(
            ((object[])_graph.Invocations.Last(i => i.Identifier == "update").Arguments[0]!).Select(n => n.ToString()),
            n => n!.Contains($"route:{s.RouteId}", StringComparison.Ordinal));

        // c clears the graph, from the table too, and does nothing once it's empty.
        await cut.InvokeAsync(() => cut.Instance.OnHotkey("t"));
        Sees(cut, "explore-table-wrap");
        await cut.InvokeAsync(() => cut.Instance.OnHotkey("c"));
        Sees(cut, "Type anything above");
        DoesNotSee(cut, "explore-table-wrap");
        Assert.Empty((object[])_graph.Invocations.Last(i => i.Identifier == "update").Arguments[0]!);
        await cut.InvokeAsync(() => cut.Instance.OnHotkey("c"));
        Sees(cut, "Type anything above");
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
            var call = download.Invocations.Last();
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
        cut.WaitForAssertion(() => Assert.Matches(@"^linkbase-graph-.*\.png$", (string)_graph.Invocations.Last(i => i.Identifier == "savePng").Arguments[0]!));

        // Hidden nodes aren't shown, so they aren't exported either.
        await cut.InvokeAsync(() => cut.Instance.OnNodeClicked($"route:{s.RouteId}"));
        Press(cut, "Hide");
        Assert.DoesNotContain(s.Route, Saved("Nodes (CSV)"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Hide_show_remove_fit_and_clear()
    {
        var s = await SeedAsync();
        var cut = Render(s);

        Press(cut, "Fit");
        cut.WaitForAssertion(() => Assert.Contains("fit", GraphCalls));

        Press(cut, "Hide");
        Sees(cut, "Show 1 hidden");
        Press(cut, "Show 1 hidden");
        DoesNotSee(cut, "Show 1 hidden");

        await cut.InvokeAsync(() => cut.Instance.OnNodeClicked($"crag:{s.CragId}"));
        Press(cut, "Remove");
        Sees(cut, "Type anything above");

        Search(cut, s.Crag);
        WaitForOption(cut, s.Crag);
        Key(cut, "Enter");
        Key(cut, "Enter");
        Sees(cut, s.Crag + "</h2>");

        Press(cut, "Clear");
        Sees(cut, "Type anything above");
    }

    [Fact]
    public void A_search_with_no_matches_says_so()
    {
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
        await using (var db = Fixture.CreateContext())
        {
            db.Crags.AddRange(Enumerable.Range(0, 105).Select(i => new Crag { UserId = AppUser.DefaultId, Name = $"{prefix} {i:000}", AreaId = s.AreaId }));
            db.Routes.AddRange(Enumerable.Range(0, 105).Select(i => new Route { UserId = AppUser.DefaultId, Name = $"{prefix} route {i:000}", CragId = s.CragId }));
            await db.SaveChangesAsync();
        }

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
        Type(cut, "Value", "boulder");
        Press(cut, "Add filter");
        Sees(cut, "discipline contains boulder");
        DoesNotSee(cut, "Add filter");

        Press(cut, "Expand");
        Told("Nothing new to add");

        cut.Find(".mud-chip-close-button").Click();
        DoesNotSee(cut, "discipline contains boulder");
        await SetAsync(cut, cut.FindComponents<MudSelect<string>>().First(c => c.Instance.Label == "Property").Instance.ValueChanged, "grade");
        Sees(cut, "Add filter");
        await SetAsync(cut, cut.FindComponents<MudSelect<string>>().First(c => c.Instance.Label == "Property").Instance.ValueChanged, (string?)null!);
        DoesNotSee(cut, "Add filter");

        Press(cut, "Expand again");
        Sees(cut, "Route 1");
    }

    [Fact]
    public async Task Scenes_save_load_report_missing_nodes_and_delete()
    {
        var s = await SeedAsync();
        var name = Unique("Scene");
        var cut = Render(s);

        _graph.Setup<ScenePosition[]>("positions").SetResult([new ScenePosition($"crag:{s.CragId}", 1, 2), new ScenePosition("crag:-1", 3, 4)]);
        Press(cut, "Save");
        Told("cene");
        Snackbar.Clear();

        Type(cut, "Scene name", name);
        Press(cut, "Save");
        Told("Scene saved.");
        Sees(cut, name);

        Press(cut, "Clear");
        Press(cut, name);
        Told("1 node in this scene no longer exist.");
        Sees(cut, s.Crag);
        cut.WaitForAssertion(() => Assert.Contains("restore", GraphCalls));

        var loaded = RenderPage<ExplorePage>();
        Sees(loaded, name);
        cut.Find($"button[aria-label='Delete scene {name}']").Click();
        DoesNotSee(cut, $"Delete scene {name}");

        Press(loaded, name);
    }

    [Fact]
    public async Task A_scene_with_every_node_present_reports_nothing()
    {
        var s = await SeedAsync();
        var name = Unique("Scene");
        await Services.GetRequiredService<GraphRepository>().SaveSceneAsync(name, [new ScenePosition($"crag:{s.CragId}", 1, 2)]);

        var cut = RenderPage<ExplorePage>();
        Press(cut, name);

        cut.WaitForAssertion(() => Assert.Contains("restore", GraphCalls));
        Assert.Empty(Snackbar.ShownSnackbars);
    }

    [Fact]
    public async Task Save_and_fit_do_nothing_before_the_graph_has_loaded()
    {
        Services.AddSingleton<IJSRuntime>(new NoNetworkJs());
        var s = await SeedAsync();
        var name = Unique("Scene");
        await Services.GetRequiredService<GraphRepository>().SaveSceneAsync(name, []);
        var cut = OpenWithoutDrawing(s);

        Press(cut, "Fit");
        Press(cut, "Save");
        Press(cut, name);

        Assert.Empty(Snackbar.ShownSnackbars);
    }

    private IRenderedComponent<ExplorePage> OpenWithoutDrawing(Seeded s)
    {
        var cut = RenderPage<ExplorePage>();
        Search(cut, s.Crag);
        WaitForOption(cut, s.Crag);
        Key(cut, "Enter");
        cut.WaitForAssertion(() => Assert.Contains("pattern-node", cut.Markup));
        Key(cut, "Enter");
        Sees(cut, s.Crag + "</h2>");
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
[Collection(PostgresCollection.Name)]
public class PatternSearchBarTests(PostgresFixture fixture) : UiTestBase(fixture)
{
    private static readonly GraphSchema Schema = new(
        [new GraphConnection("Route", "AT", "Crag")],
        [new GraphPropertyInfo("Crag", "name", GraphValueType.Text), new GraphPropertyInfo("Route", "name", GraphValueType.Text)]);

    private GraphPattern? _ran;

    private IRenderedComponent<PatternSearchBar> Bar(GraphSchema? schema = null) => RenderPage<PatternSearchBar>(p => p
        .Add(x => x.Schema, schema ?? Schema)
        .Add(x => x.OnRun, EventCallback.Factory.Create<GraphPattern>(this, pattern => _ran = pattern)));

    private static SearchInput Input(IRenderedComponent<PatternSearchBar> cut) => new(cut);

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
