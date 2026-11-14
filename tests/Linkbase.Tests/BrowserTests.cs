using Linkbase.Core.Extraction;
using Linkbase.Core;
using System.Net.Sockets;
using System.Text.Json;
using Linkbase.Components;
using Linkbase.Data;
using Linkbase.Domains.Climbing.Data;
using Linkbase.Core.Graph;
using Linkbase.Domains.Climbing.Import;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Playwright;

namespace Linkbase.Tests;

// The real app, started the way Program.cs starts it, inside the test process. Arguments outrank every other
// configuration source, so it can only reach the test database. It stops when the test process ends.
internal static class RunningApp
{
    private static readonly SemaphoreSlim Starting = new(1, 1);
    private static readonly Dictionary<string, string> Urls = [];

    private static int FreePort()
    {
        var listener = new TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        var port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    public static int RunEntryPoint(params string[] args) =>
        (int)typeof(App).Assembly.EntryPoint!.Invoke(null, [args])!;

    public static async Task<string> StartAsync(string connectionString, params string[] moreArgs)
    {
        var instance = string.Join(" ", moreArgs.Prepend(connectionString));
        await Starting.WaitAsync();
        try
        {
            if (!Urls.TryGetValue(instance, out var url))
            {
                url = $"http://127.0.0.1:{FreePort()}";
                var bound = url;
                var thread = new Thread(() => RunEntryPoint(
                    [
                        $"--urls={bound}",
                        "--applicationName=Linkbase",
                        "--environment=Development",
                        $"--contentRoot={RepoPaths.Resolve("src/Linkbase")}",
                        $"--ConnectionStrings:Linkbase={connectionString}",
                        .. moreArgs,
                    ]))
                {
                    IsBackground = true,
                };
                thread.Start();

                using var client = new HttpClient();
                for (var attempt = 0; ; attempt++)
                {
                    try
                    {
                        (await client.GetAsync(url)).EnsureSuccessStatusCode();
                        break;
                    }
                    catch (HttpRequestException) when (attempt < 150)
                    {
                        await Task.Delay(200);
                    }
                }

                Urls[instance] = url;
            }

            return url;
        }
        finally
        {
            Starting.Release();
        }
    }
}

[Trait("Category", "Browser")]
public sealed class BrowserTests(PostgresFixture fixture) : IAsyncLifetime, IClassFixture<PostgresFixture>
{
    private const float RailWidth = 56;
    private const string Positions = "() => import('/js/graph.js').then(m => m.positions())";

    // One database for every browser test, with the climbing model loaded, because each distinct database starts an app.
    private static readonly SemaphoreSlim StoreLock = new(1, 1);
    private static ModelFactory? s_store;

    private ModelFactory _store = null!;
    private readonly List<string> _consoleErrors = [];
    private string _url = "";
    private IPlaywright _playwright = null!;
    private IBrowser _browser = null!;
    private IPage _page = null!;

    private sealed record NodePosition(string NodeId, double X, double Y);

    private sealed record Point(double X, double Y);

    public async Task InitializeAsync()
    {
        await StoreLock.WaitAsync();
        try
        {
            s_store ??= await ClimbingStore.CreateAsync(fixture);
        }
        finally
        {
            StoreLock.Release();
        }

        _store = s_store;
        _url = await RunningApp.StartAsync(_store.ConnectionString);
        _playwright = await Playwright.CreateAsync();
        _browser = await _playwright.Chromium.LaunchAsync();
        _page = await _browser.NewPageAsync(new() { ViewportSize = new() { Width = 1400, Height = 900 } });
        _page.Console += (_, message) =>
        {
            if (message.Type == "error")
            {
                _consoleErrors.Add(message.Text);
            }
        };
    }

    public async Task DisposeAsync()
    {
        await _browser.DisposeAsync();
        _playwright.Dispose();
    }

    [Fact]
    public async Task Probes_say_the_app_is_live_and_ready()
    {
        // Development's settings add the sample models, whose databases aren't here, so this instance points them at the
        // test database, or it would rightly say it isn't ready.
        var url = await RunningApp.StartAsync(
            _store.ConnectionString,
            $"--ConnectionStrings:Consultancy={_store.ConnectionString}",
            $"--ConnectionStrings:SwissRail={_store.ConnectionString}",
            $"--ConnectionStrings:ClimbingDemo={_store.ConnectionString}");
        using var client = new HttpClient();

        var live = await client.GetAsync($"{url}/health/live");
        var ready = await client.GetAsync($"{url}/health/ready");

        Assert.Equal((System.Net.HttpStatusCode.OK, "Healthy"), (live.StatusCode, await live.Content.ReadAsStringAsync()));
        Assert.Equal((System.Net.HttpStatusCode.OK, "Healthy"), (ready.StatusCode, await ready.Content.ReadAsStringAsync()));
    }

    private sealed record Seeded(string Crag, string Route, string Country, long CragId, long RouteId);

    private async Task<Seeded> SeedAsync()
    {
        var place = await ClimbingStore.AddPlaceAsync(_store);
        var props = new System.Text.Json.Nodes.JsonObject { ["date"] = "2015-05-02", ["date_precision"] = "day", ["style"] = "lead", ["source"] = "manual" };
        await ClimbingStore.AddAsync(_store, "Ascent", place.Route, props, "OF", place.RouteId);
        return new Seeded(place.Crag, place.Route, place.Country, Key(place.CragId), Key(place.RouteId));
    }

    private static long Key(string nodeId) => long.Parse(nodeId[(nodeId.IndexOf(':') + 1)..], System.Globalization.CultureInfo.InvariantCulture);

    // Adds this many made-up nodes around one on the canvas, the way expanding does, and removes any beyond that count.
    private async Task AddNodesAsync(string anchorId, int count)
    {
        await _page.EvaluateAsync("""
            ({ anchorId, count }) => import('/js/graph.js').then(async m => {
                const known = (await m.positions()).map(p => p.nodeId).filter(id => !id.startsWith('extra-'));
                const nodes = [...known, ...Array.from({ length: count }, (_, i) => 'extra-' + (i + 1))]
                    .map(id => ({ id, label: 'n', kind: 'crag', caption: 'n' }));
                m.update(nodes, m.edgeList(), anchorId);
            })
            """, new { anchorId, count });
        await _page.WaitForTimeoutAsync(300);
    }

    private async Task<IReadOnlyList<NodePosition>> NodesAsync()
    {
        var json = await _page.EvaluateAsync<JsonElement>(Positions);
        return json.Deserialize<List<NodePosition>>(new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
    }

    // Polled from here because WaitForFunction treats a promise as an immediate yes.
    private async Task WaitForAsync(Func<int, bool> nodeCount, string what)
    {
        var deadline = DateTime.UtcNow.AddSeconds(20);
        while (!nodeCount((await NodesAsync()).Count))
        {
            Assert.True(DateTime.UtcNow < deadline, $"Timed out waiting for {what}");
            await Task.Delay(50);
        }
    }

    private Task WaitForNodesAsync(int atLeast) => WaitForAsync(count => count >= atLeast, $"at least {atLeast} nodes");

    private Task WaitForExactNodesAsync(int exactly) => WaitForAsync(count => count == exactly, $"{exactly} nodes");

    // Where a node is in the window, for real mouse events.
    private async Task<Point> OnScreenAsync(string nodeId)
    {
        var position = await _page.EvaluateAsync<JsonElement>("id => import('/js/graph.js').then(m => m.renderedPosition(id))", nodeId);
        var canvas = (await _page.Locator(".explore-canvas").BoundingBoxAsync())!;
        return new Point(canvas.X + position.GetProperty("x").GetDouble(), canvas.Y + position.GetProperty("y").GetDouble());
    }

    private async Task RightClickAsync(string nodeId)
    {
        var at = await OnScreenAsync(nodeId);
        await _page.Mouse.ClickAsync((float)at.X, (float)at.Y, new() { Button = MouseButton.Right });
        await _page.Locator(".explore-menu").WaitForAsync();
    }

    private async Task SearchForAsync(string text)
    {
        var search = _page.GetByRole(AriaRole.Combobox, new() { Name = "Search phrase" });
        await Assertions.Expect(search).Not.ToHaveAttributeAsync("placeholder", "Loading…");
        await search.FillAsync(text);
        await _page.Locator("[role=option]", new() { HasText = text }).First.WaitForAsync();
        await search.PressAsync("Enter");
        await _page.Locator(".pattern-node").First.WaitForAsync();
        await search.PressAsync("Enter");
    }

    private async Task OpenExploreAsync()
    {
        await _page.GotoAsync($"{_url}/explore");
        await _page.Locator(".explore-canvas canvas").First.WaitForAsync();
    }

    private ILocator Button(string name) => _page.GetByRole(AriaRole.Button, new() { Name = name, Exact = true });

    [Fact]
    public async Task Every_page_is_dark_and_free_of_console_errors()
    {
        var s = await SeedAsync();
        foreach (var path in new[] { "/", "/logbook", "/explore", "/insights", "/import" })
        {
            await _page.GotoAsync(_url + path, new() { WaitUntil = WaitUntilState.NetworkIdle });

            var background = await _page.EvaluateAsync<string>("() => getComputedStyle(document.querySelector('.mud-layout') ?? document.body).backgroundColor");
            var parts = background[(background.IndexOf('(', StringComparison.Ordinal) + 1)..^1].Split(',').Select(p => double.Parse(p, System.Globalization.CultureInfo.InvariantCulture)).ToArray();
            Assert.True(parts[0] < 60 && parts[1] < 60 && parts[2] < 60, $"{path} has a light background: {background}");
            Assert.False(await _page.Locator("#blazor-error-ui").IsVisibleAsync(), $"{path} showed the error bar");
        }

        Assert.Empty(_consoleErrors);
    }

    private ILocator FieldNamed(string label) =>
        _page.Locator(".mud-input-control", new() { Has = _page.Locator($"label:text-is('{label}')") }).First;

    private async Task SeesOptionAsync(ILocator select, string option)
    {
        await select.Locator(".mud-input-slot").First.ClickAsync();
        await _page.Locator(".mud-popover-open .mud-list-item", new() { HasText = option }).First.WaitForAsync();
        await _page.Keyboard.PressAsync("Escape");
        await Assertions.Expect(_page.Locator(".mud-popover-open .mud-list-item")).ToHaveCountAsync(0);
    }

    private async Task PickAsync(string label, string text, string option)
    {
        // Clicked and typed as a person would. A pick returns focus to its own field a moment later.
        var input = FieldNamed(label).Locator("input").First;
        var choice = _page.Locator(".mud-popover-open .mud-list-item", new() { HasText = option }).First;
        for (var attempt = 0; ; attempt++)
        {
            await input.ClickAsync();
            await input.FillAsync("");
            await input.PressSequentiallyAsync(text);
            try
            {
                await choice.ClickAsync(new() { Timeout = 3000 });
                break;
            }
            catch (TimeoutException) when (attempt < 3)
            {
                // Focus went back to the last field, so the typing was lost. Try again.
            }
        }

        await Assertions.Expect(_page.Locator(".mud-popover-open .mud-list-item")).ToHaveCountAsync(0);
        await _page.WaitForTimeoutAsync(300);
    }

    // Without ToStringFunc these would show raw enum names, which is easy to break and only shows up here.
    [Fact]
    public async Task Quick_find_jumps_to_a_record_and_a_page_from_the_keyboard_alone()
    {
        var s = await SeedAsync();
        await _page.GotoAsync($"{_url}/insights", new() { WaitUntil = WaitUntilState.NetworkIdle });

        // Ctrl+K opens it with the box focused, even though the page's own field was focused first.
        await _page.Keyboard.PressAsync("/");
        await _page.Keyboard.PressAsync("Control+k");
        var box = _page.GetByPlaceholder("Find a record or page, or add one");
        await Assertions.Expect(box).ToBeFocusedAsync();

        await box.FillAsync(s.Crag);
        await Assertions.Expect(_page.Locator(".quickfind-item.active .quickfind-title")).ToHaveTextAsync(s.Crag);
        await _page.Keyboard.PressAsync("Enter");
        await Assertions.Expect(_page.Locator(".quickfind")).ToHaveCountAsync(0);
        await Assertions.Expect(_page).ToHaveURLAsync(new System.Text.RegularExpressions.Regex("/data/Crag/"));

        await _page.Keyboard.PressAsync("Control+k");
        await Assertions.Expect(box).ToBeFocusedAsync();
        await box.FillAsync("explore");
        await Assertions.Expect(_page.Locator(".quickfind-item.active .quickfind-title")).ToHaveTextAsync("Explore");
        await _page.Keyboard.PressAsync("Enter");
        await Assertions.Expect(_page).ToHaveURLAsync(new System.Text.RegularExpressions.Regex("/explore$"));

        // Esc closes it and changes nothing.
        await _page.Keyboard.PressAsync("Control+k");
        await Assertions.Expect(box).ToBeFocusedAsync();
        await _page.Keyboard.PressAsync("Escape");
        await Assertions.Expect(_page.Locator(".quickfind")).ToHaveCountAsync(0);
    }

    [Fact]
    public async Task Selects_offer_readable_names()
    {
        var s = await SeedAsync();

        await _page.GotoAsync($"{_url}/logbook", new() { WaitUntil = WaitUntilState.NetworkIdle });
        await SeesOptionAsync(FieldNamed("Grade system"), "UK trad");
        await SeesOptionAsync(FieldNamed("Discipline"), "Trad");
        await SeesOptionAsync(FieldNamed("Style"), "Top rope");

        await OpenExploreAsync();
        await SeesOptionAsync(FieldNamed("Relationships"), "AT");
        await SeesOptionAsync(FieldNamed("Property"), "discipline");
    }

    private sealed record NodeBox(string NodeId, double X1, double Y1, double X2, double Y2);

    private async Task<List<string>> OverlapsAsync()
    {
        var json = await _page.EvaluateAsync<JsonElement>("() => import('/js/graph.js').then(m => m.boxes())");
        var boxes = json.Deserialize<List<NodeBox>>(new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        return boxes
            .SelectMany((a, i) => boxes.Skip(i + 1).Where(b => a.X1 < b.X2 && b.X1 < a.X2 && a.Y1 < b.Y2 && b.Y1 < a.Y2).Select(b => $"{a.NodeId} and {b.NodeId}"))
            .ToList();
    }

    private async Task<long> AddCragsAsync(string prefix, int count, int routesOnFirst = 0)
    {
        var area = await ClimbingStore.AddCountryAsync(_store, $"Country {Guid.NewGuid():N}");
        var crags = await ClimbingStore.AddManyAsync(_store, "Crag", Enumerable.Range(0, count).Select(i => $"{prefix} crag with a long name {i:000}"), null, "IN", area);
        await ClimbingStore.AddManyAsync(_store, "Route", Enumerable.Range(0, routesOnFirst).Select(_ => $"Fan {Guid.NewGuid():N}"), null, "AT", crags[0]);
        return Key(crags[0]);
    }

    [Fact]
    public async Task A_search_lays_out_without_overlaps_and_so_does_adding_to_it()
    {
        var prefix = $"Spread{Guid.NewGuid():N}";
        var firstCrag = await AddCragsAsync(prefix, 40, routesOnFirst: 30);
        await OpenExploreAsync();

        await SearchForAsync(prefix + " crag");
        await WaitForExactNodesAsync(40);
        Assert.Empty(await OverlapsAsync());

        var before = await NodesAsync();
        await RightClickAsync($"crag:{firstCrag}");
        await _page.Locator(".explore-menu button", new() { HasText = "Expand all" }).ClickAsync();
        await WaitForNodesAsync(71);
        Assert.Empty(await OverlapsAsync());

        var after = await NodesAsync();
        foreach (var node in before)
        {
            var moved = after.Single(n => n.NodeId == node.NodeId);
            Assert.Equal(node.X, moved.X, 6);
            Assert.Equal(node.Y, moved.Y, 6);
        }
    }

    [Fact]
    public async Task Dragging_a_node_pulls_its_links_along_and_then_everything_settles()
    {
        var s = await SeedAsync();
        var other = await SeedAsync();
        var (cragNode, otherNode) = ($"crag:{s.CragId}", $"crag:{other.CragId}");
        await OpenExploreAsync();
        await SearchForAsync(s.Crag);
        await WaitForNodesAsync(1);
        await RightClickAsync(cragNode);
        await _page.Locator(".explore-menu button", new() { HasText = "Expand all" }).ClickAsync();
        await WaitForNodesAsync(3);
        await _page.GetByRole(AriaRole.Button, new() { Name = "Clear search" }).ClickAsync();
        await SearchForAsync(other.Crag);
        await WaitForNodesAsync(4);

        var before = await NodesAsync();
        var linked = before.First(n => n.NodeId == $"route:{s.RouteId}");
        var start = await OnScreenAsync(cragNode);

        await _page.Mouse.MoveAsync((float)start.X, (float)start.Y);
        await _page.Mouse.DownAsync();
        await _page.Mouse.MoveAsync((float)start.X + 150, (float)start.Y + 90, new() { Steps = 20 });
        await _page.WaitForTimeoutAsync(600);

        var during = await NodesAsync();
        var followed = during.First(n => n.NodeId == linked.NodeId);
        Assert.True(Math.Abs(followed.X - linked.X) + Math.Abs(followed.Y - linked.Y) > 20, "The linked node stayed behind");
        Assert.Equal(before.First(n => n.NodeId == otherNode).X, during.First(n => n.NodeId == otherNode).X, 6);

        await _page.Mouse.UpAsync();
        var dropped = during.First(n => n.NodeId == cragNode);
        await _page.WaitForTimeoutAsync(1500);

        // Once settled, it holds still, and the dropped node stays where it was put.
        var settled = await NodesAsync();
        await _page.WaitForTimeoutAsync(400);
        var later = await NodesAsync();
        foreach (var node in settled)
        {
            var same = later.Single(n => n.NodeId == node.NodeId);
            Assert.Equal(node.X, same.X, 6);
            Assert.Equal(node.Y, same.Y, 6);
        }

        Assert.Equal(dropped.X, settled.First(n => n.NodeId == cragNode).X, 6);
        Assert.Empty(_consoleErrors);
    }

    // A model the app has never heard of, in its own database, so nothing about it can be hard-coded.
    [Fact]
    public async Task Explore_works_for_types_it_has_never_seen()
    {
        var connection = await fixture.CreateModelDatabaseAsync(
            """
            INSERT INTO node_type (name, color, icon, sort_order) VALUES ('Station', '#0f766e', 'Train', 1), ('Line', NULL, NULL, 2);
            INSERT INTO field_definition (node_type_name, key, type) VALUES
                ('Station', 'platforms', 'number'), ('Station', 'opened_date', 'date'), ('Station', 'operator', 'text'), ('Line', 'operator', 'text');
            INSERT INTO link_definition (source_type, rel_type, target_type, links) VALUES ('Line', 'CALLS_AT', 'Station', 2);
            INSERT INTO node (id, user_id, type, key, name, props) VALUES
                ('station:1', 1, 'Station', '1', 'Zurich HB', '{"platforms": 26, "opened_date": "1847-08-09", "operator": "SBB"}'),
                ('station:2', 1, 'Station', '2', 'Bern', '{"platforms": 12, "opened_date": "1858-07-01", "operator": "SBB"}'),
                ('line:1', 1, 'Line', '1', 'IC1', '{"operator": "SBB"}');
            INSERT INTO node_link (source_id, target_id, rel_type) VALUES ('line:1', 'station:1', 'CALLS_AT'), ('line:1', 'station:2', 'CALLS_AT');
            """);
        var url = await RunningApp.StartAsync(connection);

        await _page.GotoAsync($"{url}/explore");
        await _page.Locator(".explore-canvas canvas").First.WaitForAsync();
        var search = _page.GetByRole(AriaRole.Combobox, new() { Name = "Search phrase" });
        await Assertions.Expect(search).ToHaveAttributeAsync("placeholder", new System.Text.RegularExpressions.Regex("such as Station"));
        await Assertions.Expect(_page.Locator(".explore-panel").GetByText("Line", new() { Exact = true })).ToBeVisibleAsync();

        // The type, then a property of it, typed through the search bar.
        await search.FillAsync("Stat");
        await _page.Locator("[role=option]", new() { HasText = "Type" }).First.WaitForAsync();
        await search.PressAsync("Enter");
        await _page.Locator(".pattern-node", new() { HasText = "Station" }).WaitForAsync();
        await search.FillAsync("platforms");
        await _page.Locator("[role=option]", new() { HasText = "platforms" }).First.WaitForAsync();
        await search.PressAsync("Enter");
        await _page.Locator("[role=option]", new() { HasText = ">" }).First.ClickAsync();
        await search.FillAsync("20");
        await _page.Locator("[role=option]", new() { HasText = "> 20" }).First.ClickAsync();
        await _page.Locator(".pattern-node-filter", new() { HasText = "platforms > " }).WaitForAsync();
        await search.PressAsync("Enter");
        await WaitForExactNodesAsync(1);
        await Assertions.Expect(_page.Locator("aside h2")).ToHaveTextAsync("Zurich HB");
        await Assertions.Expect(_page.Locator(".explore-panel")).ToContainTextAsync("SBB");
        await Assertions.Expect(_page.Locator(".explore-panel").GetByRole(AriaRole.Link, new() { Name = "Open and edit" })).ToHaveAttributeAsync("href", "/data/Station/1");

        // Its own relationship in the menu, filtered by its own property.
        await RightClickAsync("station:1");
        await _page.Locator(".explore-menu button", new() { HasText = "CALLS_AT" }).ClickAsync();
        await WaitForExactNodesAsync(2);
        await _page.Locator(".explore-panel").GetByText("Line 1").WaitForAsync();

        await RightClickAsync("line:1");
        await _page.Locator(".explore-menu button", new() { HasText = "Expand all" }).ClickAsync();
        await WaitForExactNodesAsync(3);

        // The model is built from it too.
        await _page.GetByRole(AriaRole.Button, new() { Name = "Clear search" }).ClickAsync();
        await search.FillAsync("graph");
        await _page.Locator("[role=option]", new() { HasText = "Graph model" }).WaitForAsync();
        await search.PressAsync("Enter");
        await WaitForExactNodesAsync(2);
        Assert.Contains(await NodesAsync(), n => n.NodeId == "model:Station");

        // Home says how many links of each kind there are.
        await _page.GotoAsync(url);
        await Assertions.Expect(_page.Locator(".home-links")).ToContainTextAsync("CALLS_AT");
        await Assertions.Expect(_page.Locator(".home-links")).ToContainTextAsync("2");
        Assert.Empty(_consoleErrors);
    }

    [Fact]
    public async Task The_rail_switches_between_models_each_in_its_own_database()
    {
        await SeedAsync();
        var stations = await fixture.CreateModelDatabaseAsync(
            """
            INSERT INTO node_type (name, color, icon, sort_order) VALUES ('Station', '#0f766e', 'Train', 1);
            INSERT INTO node (id, user_id, type, key, name, props) VALUES ('station:1', 1, 'Station', '1', 'Zurich HB', '{}'), ('station:2', 1, 'Station', '2', 'Bern', '{}');
            """);
        var url = await RunningApp.StartAsync(
            _store.ConnectionString,
            "--Models:climbing:Name=Climbing test",
            "--Models:climbing:ConnectionString=Linkbase",
            "--Models:climbing:Domains:0=Climbing",
            "--Models:climbing:Order=1",
            "--Models:stations:Name=Stations test",
            "--Models:stations:ConnectionString=Stations",
            "--Models:stations:Order=99",
            $"--ConnectionStrings:Stations={stations}");

        await _page.GotoAsync(url);
        await Assertions.Expect(_page.Locator("h1")).ToHaveTextAsync("Climbing test");
        await Assertions.Expect(_page.GetByText("What's in this model")).ToBeVisibleAsync();
        await Assertions.Expect(_page.Locator(".mud-chip").GetByText(new System.Text.RegularExpressions.Regex("^Ascent "))).ToBeVisibleAsync();

        await _page.Locator(".app-rail").HoverAsync();
        // The real button, because MudMenu's wrapper has the button role too and takes its name from what's inside.
        await _page.Locator("button[aria-label='Switch model']").ClickAsync();
        await _page.Locator(".mud-popover-open").GetByText("Stations test").ClickAsync();

        await Assertions.Expect(_page.Locator("h1")).ToHaveTextAsync("Stations test");
        await Assertions.Expect(_page.GetByText("Station 2")).ToBeVisibleAsync();
        await Assertions.Expect(_page.Locator(".app-rail a[href='logbook']")).ToHaveCountAsync(0);

        // Its types are in the rail, each with a generated list and record pages.
        await _page.Locator(".app-rail a[href='data/Station']").ClickAsync();
        await Assertions.Expect(_page.Locator("h1")).ToHaveTextAsync("Station");
        await _page.GetByRole(AriaRole.Link, new() { Name = "Zurich HB" }).ClickAsync();
        await Assertions.Expect(_page.Locator("h1")).ToHaveTextAsync("Zurich HB");
        await Assertions.Expect(_page.GetByText("No links yet.")).ToBeVisibleAsync();

        // Explore follows the choice too, even after a fresh page load, because the choice is kept.
        await _page.GotoAsync($"{url}/explore");
        var search = _page.GetByRole(AriaRole.Combobox, new() { Name = "Search phrase" });
        await Assertions.Expect(search).ToHaveAttributeAsync("placeholder", new System.Text.RegularExpressions.Regex("such as Station"));

        Assert.Empty(_consoleErrors);

        // The browser logs the 404 as an error, which is expected here.
        Assert.Equal(404, (await _page.GotoAsync($"{url}/model/nope"))!.Status);
        await _page.GotoAsync($"{url}/model/climbing");
        await Assertions.Expect(_page.Locator("h1")).ToHaveTextAsync("Climbing test");
    }

    [Fact]
    public async Task Free_text_and_the_graph_model_work_on_the_real_canvas()
    {
        var s = await SeedAsync();
        await OpenExploreAsync();
        var search = _page.GetByRole(AriaRole.Combobox, new() { Name = "Search phrase" });
        await Assertions.Expect(search).Not.ToHaveAttributeAsync("placeholder", "Loading…");

        // Part of a name, with no type chosen, and one Enter.
        await search.FillAsync(s.Crag.Split(' ')[1][..16]);
        await _page.Locator("[role=option]", new() { HasText = "Search everything for" }).WaitForAsync();
        await search.PressAsync("Enter");
        await WaitForExactNodesAsync(1);
        await Assertions.Expect(_page.Locator(".pattern-node .pattern-node-filter")).ToContainTextAsync("contains");

        // The chip holds its condition, rather than sitting beside it.
        var chip = (await _page.Locator(".pattern-node").BoundingBoxAsync())!;
        var condition = (await _page.Locator(".pattern-node-filter").BoundingBoxAsync())!;
        Assert.True(condition.X >= chip.X && condition.X + condition.Width <= chip.X + chip.Width, "The condition isn't inside its node");

        await _page.GetByRole(AriaRole.Button, new() { Name = "Clear search" }).ClickAsync();
        await search.FillAsync("graph");
        await _page.Locator("[role=option]", new() { HasText = "Graph model" }).WaitForAsync();
        await search.PressAsync("Enter");
        await WaitForExactNodesAsync((await new GraphRepository(_store).SchemaAsync()).Labels.Count);
        Assert.All(await NodesAsync(), n => Assert.StartsWith("model:", n.NodeId, StringComparison.Ordinal));

        var at = await OnScreenAsync("model:Crag");
        await _page.Mouse.ClickAsync((float)at.X, (float)at.Y);
        await Assertions.Expect(_page.Locator("aside h2")).ToHaveTextAsync("Crag");

        await _page.Mouse.DblClickAsync((float)at.X, (float)at.Y);
        var deadline = DateTime.UtcNow.AddSeconds(20);
        while ((await NodesAsync()).Any(n => n.NodeId.StartsWith("model:", StringComparison.Ordinal)))
        {
            Assert.True(DateTime.UtcNow < deadline, "The model stayed on the canvas");
            await Task.Delay(50);
        }

        Assert.Empty(_consoleErrors);
    }

    private async Task<(string Name, byte[] Bytes)> DownloadAsync(string menuItem)
    {
        var download = await _page.RunAndWaitForDownloadAsync(async () =>
        {
            await Button("Export").ClickAsync();
            await _page.GetByRole(AriaRole.Menuitem, new() { Name = menuItem }).ClickAsync();
        });
        return (download.SuggestedFilename, await File.ReadAllBytesAsync((await download.PathAsync())!));
    }

    [Fact]
    public async Task Keys_and_exports_work_in_the_real_browser()
    {
        var s = await SeedAsync();
        await OpenExploreAsync();
        var search = _page.GetByRole(AriaRole.Combobox, new() { Name = "Search phrase" });
        await Assertions.Expect(search).Not.ToHaveAttributeAsync("placeholder", "Loading…");

        // The search box is ready to type into as the page opens, with no suggestions over the page yet, and a key typed
        // there is just typing.
        await Assertions.Expect(search).ToBeFocusedAsync();
        await Assertions.Expect(_page.Locator(".pattern-list")).ToHaveCountAsync(0);
        await _page.Keyboard.TypeAsync("t");
        await Assertions.Expect(search).ToHaveValueAsync("t");
        await search.FillAsync("");

        // Search, leave the box with Esc, then pick and expand a node, all from the keyboard.
        await SearchForAsync(s.Crag);
        await WaitForExactNodesAsync(1);
        await _page.Keyboard.PressAsync("Escape");
        await Assertions.Expect(_page.Locator(".explore-canvas")).ToBeFocusedAsync();
        await _page.Keyboard.PressAsync("n");
        await Assertions.Expect(_page.Locator("aside h2")).ToHaveTextAsync(s.Crag);
        await _page.Keyboard.PressAsync("e");
        await WaitForNodesAsync(2);

        // Ctrl+O opens the selected node's page in a new tab, and the graph stays where it is.
        var opened = await _page.Context.RunAndWaitForPageAsync(() => _page.Keyboard.PressAsync("Control+o"));
        await opened.WaitForLoadStateAsync();
        Assert.Contains($"/data/Crag/{s.CragId}", opened.Url, StringComparison.Ordinal);
        await opened.CloseAsync();
        await Assertions.Expect(_page.Locator("aside h2")).ToHaveTextAsync(s.Crag);

        // / goes back to the box from anywhere, with its suggestions open. Esc closes them, then leaves again.
        await _page.Keyboard.PressAsync("/");
        await Assertions.Expect(search).ToBeFocusedAsync();
        await _page.Locator(".pattern-list").WaitForAsync();
        await _page.Keyboard.PressAsync("Escape");
        await Assertions.Expect(_page.Locator(".pattern-list")).ToHaveCountAsync(0);
        await Assertions.Expect(search).ToBeFocusedAsync();
        await _page.Keyboard.PressAsync("Escape");
        await Assertions.Expect(_page.Locator(".explore-canvas")).ToBeFocusedAsync();

        // The search suggestions drop down over the graph's toolbar, not under it.
        await search.FocusAsync();
        await _page.Locator(".pattern-list").WaitForAsync();
        var export = (await Button("Export").BoundingBoxAsync())!;
        var list = (await _page.Locator(".pattern-list").BoundingBoxAsync())!;
        Assert.True(list.Y + list.Height > export.Y + export.Height / 2 && list.X + list.Width > export.X, "The suggestions don't reach the toolbar, so this checks nothing");
        Assert.True(await _page.EvaluateAsync<bool>("""
            () => {
                const box = document.querySelector('.explore-tools .mud-menu button').getBoundingClientRect();
                return document.elementFromPoint(box.x + box.width / 2, box.y + box.height / 2)?.closest('.pattern-list') != null;
            }
            """), "The toolbar covers the search suggestions");
        await _page.Keyboard.PressAsync("Escape");
        await Assertions.Expect(_page.Locator(".pattern-list")).ToHaveCountAsync(0);
        await Assertions.Expect(search).ToBeFocusedAsync();
        await _page.Keyboard.PressAsync("Escape");

        await _page.Keyboard.PressAsync("t");
        await Assertions.Expect(_page.Locator(".explore-table-wrap")).ToBeVisibleAsync();
        await Assertions.Expect(_page.Locator("tr.explore-table-row", new() { HasText = s.Route })).ToBeVisibleAsync();
        await _page.Keyboard.PressAsync("t");
        await Assertions.Expect(_page.Locator(".explore-table-wrap")).ToHaveCountAsync(0);

        var (nodesFile, nodes) = await DownloadAsync("Nodes (CSV)");
        Assert.Matches(@"^linkbase-nodes-.*\.csv$", nodesFile);
        var text = System.Text.Encoding.UTF8.GetString(nodes);
        Assert.StartsWith("﻿id,type,name", text, StringComparison.Ordinal);
        Assert.Contains(s.Route, text, StringComparison.Ordinal);

        var (pictureFile, picture) = await DownloadAsync("Picture of the graph (PNG)");
        Assert.EndsWith(".png", pictureFile, StringComparison.Ordinal);
        Assert.Equal(new byte[] { 0x89, (byte)'P', (byte)'N', (byte)'G' }, picture[..4]);
        await _page.EvaluateAsync("() => document.activeElement?.blur()");

        await _page.Keyboard.PressAsync("?");
        var dialog = _page.GetByRole(AriaRole.Dialog);
        await Assertions.Expect(dialog).ToContainTextAsync("Keyboard shortcuts");

        // MudBlazor starts listening for Esc just after the dialog appears, so under load the first press can come too soon.
        for (var attempt = 0; attempt < 5 && await dialog.CountAsync() > 0; attempt++)
        {
            await _page.Keyboard.PressAsync("Escape");
            await Task.Delay(300);
        }

        await Assertions.Expect(dialog).ToHaveCountAsync(0);

        await _page.Keyboard.PressAsync("g");
        await _page.Keyboard.PressAsync("h");
        await _page.WaitForURLAsync($"{_url}/");

        await _page.Keyboard.PressAsync("g");
        await _page.Keyboard.PressAsync("e");
        await _page.WaitForURLAsync($"{_url}/explore");

        Assert.Empty(_consoleErrors);
    }

    [Fact]
    public async Task Unknown_addresses_and_the_error_page_render()
    {
        await _page.GotoAsync($"{_url}/no-such-page");
        await Assertions.Expect(_page.GetByText("not found", new() { Exact = false }).First).ToBeVisibleAsync();

        await _page.GotoAsync($"{_url}/Error");
        await Assertions.Expect(_page.GetByText("An error occurred while processing your request.")).ToBeVisibleAsync();
    }

    [Fact]
    public void Commands_run_instead_of_the_web_app()
    {
        // A model that doesn't exist, so this stops before reading the file or connecting to anything.
        var exitCode = RunningApp.RunEntryPoint("model", "load", "models/none.json", "no-such-model");

        Assert.Equal(1, exitCode);
    }

    [Fact]
    public async Task The_rail_opens_on_hover_without_covering_the_search_bar()
    {
        await OpenExploreAsync();
        var rail = _page.Locator(".app-rail");
        var search = _page.Locator(".pattern-bar");

        Assert.Equal(RailWidth, (await rail.BoundingBoxAsync())!.Width, 0);

        // The rail opens on mouseenter, so the pointer has to come from outside it. Playwright's starts at (0, 0), which is
        // inside the rail, and an earlier test may leave it anywhere.
        await _page.Mouse.MoveAsync(900, 500);
        await rail.HoverAsync();
        await _page.WaitForFunctionAsync("() => document.querySelector('.app-rail').getBoundingClientRect().width > 100");
        await _page.WaitForTimeoutAsync(400);

        var open = (await rail.BoundingBoxAsync())!;
        Assert.True((await search.BoundingBoxAsync())!.X >= open.X + open.Width, "The open rail covers the search bar");

        await _page.Mouse.MoveAsync(900, 500);
        await _page.WaitForFunctionAsync("() => document.querySelector('.app-rail').getBoundingClientRect().width < 100");
    }

    [Fact]
    public async Task Without_the_graph_libraries_explore_says_so()
    {
        await _page.RouteAsync("**/lib/cytoscape-*/**", route => route.AbortAsync());

        await _page.GotoAsync($"{_url}/explore");

        await Assertions.Expect(_page.GetByText("The graph can't be drawn")).ToBeVisibleAsync();
        await Assertions.Expect(_page.GetByText("Could not load", new() { Exact = false })).ToBeVisibleAsync();
    }

    // Everything graph.js does, with real mouse events on the real canvas.
    [Fact]
    public async Task Explore_works_with_a_real_mouse_and_keeps_nodes_still()
    {
        var s = await SeedAsync();
        var cragNode = $"crag:{s.CragId}";
        var viewName = $"View {Guid.NewGuid():N}";
        var cdp = await _page.Context.NewCDPSessionAsync(_page);
        await cdp.SendAsync("Profiler.enable");
        await cdp.SendAsync("Profiler.startPreciseCoverage", new Dictionary<string, object> { ["callCount"] = true, ["detailed"] = true });

        await OpenExploreAsync();
        await Button("Fit").IsDisabledAsync();
        await SearchForAsync(s.Crag);
        await WaitForNodesAsync(1);
        await Assertions.Expect(_page.Locator("aside h2")).ToHaveTextAsync(s.Crag);

        // Expanding from the menu keeps what's already there exactly where it was.
        var before = (await NodesAsync()).Single();
        await RightClickAsync(cragNode);
        await _page.Locator(".explore-menu button", new() { HasText = "Expand all" }).ClickAsync();
        await WaitForNodesAsync(2);
        var after = (await NodesAsync()).Single(n => n.NodeId == cragNode);
        Assert.Equal(before.X, after.X, 6);
        Assert.Equal(before.Y, after.Y, 6);

        // A drag brings the links along, and adding while that's still settling doesn't break anything.
        var grip = await OnScreenAsync(cragNode);
        await _page.Mouse.MoveAsync((float)grip.X, (float)grip.Y);
        await _page.Mouse.DownAsync();
        await _page.Mouse.MoveAsync((float)grip.X + 80, (float)grip.Y + 40, new() { Steps = 8 });
        await _page.WaitForTimeoutAsync(200);
        await _page.Mouse.UpAsync();
        await _page.WaitForTimeoutAsync(1200);
        await Button("Fit").ClickAsync();

        // A click selects, and clicking the background clears it.
        var at = await OnScreenAsync(cragNode);
        await _page.Mouse.ClickAsync((float)at.X, (float)at.Y);
        await Assertions.Expect(_page.Locator("aside h2")).ToHaveTextAsync(s.Crag);
        var canvas = (await _page.Locator(".explore-canvas").BoundingBoxAsync())!;
        await _page.Mouse.ClickAsync(canvas.X + 4, canvas.Y + 4);
        await Assertions.Expect(_page.Locator("aside h2")).ToHaveCountAsync(0);

        // Zooming closes the menu, which would otherwise drift away from its node.
        await RightClickAsync(cragNode);
        await _page.Mouse.MoveAsync(canvas.X + 200, canvas.Y + 200);
        await _page.Mouse.WheelAsync(0, -200);
        await Assertions.Expect(_page.Locator(".explore-menu")).ToHaveCountAsync(0);

        // Clicking the background closes the menu too.
        await Button("Fit").ClickAsync();
        await RightClickAsync(cragNode);
        await _page.Mouse.ClickAsync(canvas.X + 4, canvas.Y + 4, new() { Button = MouseButton.Right });
        await Assertions.Expect(_page.Locator(".explore-menu")).ToHaveCountAsync(0);

        // A double-click expands.
        at = await OnScreenAsync(cragNode);
        await _page.Mouse.DblClickAsync((float)at.X, (float)at.Y);
        await Assertions.Expect(Button("Expand again")).ToBeVisibleAsync();

        // The canvas follows the window.
        await _page.SetViewportSizeAsync(1200, 700);
        await _page.SetViewportSizeAsync(1400, 900);
        await Button("Fit").ClickAsync();

        // A view saved with its layout pinned comes back exactly as saved.
        await Button("Views").ClickAsync();
        await _page.GetByLabel("Name for this view").FillAsync(viewName);
        await _page.GetByLabel("Keep nodes exactly where they are").CheckAsync();
        await Button("Save").ClickAsync();
        var dialog = _page.GetByRole(AriaRole.Dialog);
        var viewLink = _page.GetByRole(AriaRole.Link, new() { Name = viewName, Exact = true });
        await Assertions.Expect(viewLink).ToBeVisibleAsync();
        var saved = await NodesAsync();
        await dialog.GetByRole(AriaRole.Button, new() { Name = "Close", Exact = true }).ClickAsync();
        await Button("Clear").ClickAsync();
        await WaitForExactNodesAsync(0);

        await Button("Views").ClickAsync();
        await _page.GetByRole(AriaRole.Link, new() { Name = viewName, Exact = true }).ClickAsync();
        await WaitForExactNodesAsync(saved.Count);
        var restored = await NodesAsync();
        foreach (var node in saved)
        {
            var match = restored.SingleOrDefault(n => n.NodeId == node.NodeId);
            Assert.True(match is not null, $"Saved [{string.Join(", ", saved.Select(n => n.NodeId))}] but restored [{string.Join(", ", restored.Select(n => n.NodeId))}]");
            Assert.Equal(node.X, match.X, 6);
            Assert.Equal(node.Y, match.Y, 6);
        }

        await Button("Views").ClickAsync();
        await _page.GetByRole(AriaRole.Button, new() { Name = $"Delete view {viewName}" }).ClickAsync();
        await Assertions.Expect(viewLink).ToHaveCountAsync(0);
        await dialog.GetByRole(AriaRole.Button, new() { Name = "Close", Exact = true }).ClickAsync();
        await Assertions.Expect(dialog).ToHaveCountAsync(0);

        // Expanding keeps the zoom the user chose, and keeps the expanded node in view.
        var zoomBox = (await _page.Locator(".explore-canvas").BoundingBoxAsync())!;
        await _page.Mouse.MoveAsync(zoomBox.X + zoomBox.Width / 2, zoomBox.Y + zoomBox.Height / 2);
        await _page.Mouse.WheelAsync(0, -300);
        await _page.WaitForTimeoutAsync(300);
        var zoomBefore = await _page.EvaluateAsync<double>("() => import('/js/graph.js').then(m => m.zoom())");
        // Clicking a link selects it.
        var other = (await NodesAsync()).First(n => n.NodeId != cragNode).NodeId;
        var (end1, end2) = (await OnScreenAsync(cragNode), await OnScreenAsync(other));
        await _page.Mouse.ClickAsync((float)((end1.X + end2.X) / 2), (float)((end1.Y + end2.Y) / 2));
        await AddNodesAsync(cragNode, 1);
        var zoomAfter = await _page.EvaluateAsync<double>("() => import('/js/graph.js').then(m => m.zoom())");
        Assert.Equal(zoomBefore, zoomAfter, 6);

        // Pushed out of sight, the view pans to bring what was added back, without zooming.
        foreach (var direction in new[] { -1, 1 })
        {
            var from = direction < 0 ? (X: zoomBox.X + zoomBox.Width - 10, Y: zoomBox.Y + zoomBox.Height - 10) : (X: zoomBox.X + 10, Y: zoomBox.Y + 10);
            await _page.Mouse.MoveAsync((float)from.X, (float)from.Y);
            await _page.Mouse.DownAsync();
            await _page.Mouse.MoveAsync((float)(from.X + direction * zoomBox.Width * 0.9), (float)(from.Y + direction * zoomBox.Height * 0.9), new() { Steps = 6 });
            await _page.Mouse.UpAsync();
            await AddNodesAsync(cragNode, 1 + (direction < 0 ? 1 : 2));
            foreach (var id in new[] { cragNode, direction < 0 ? "extra-2" : "extra-3" })
            {
                var shown = await OnScreenAsync(id);
                Assert.InRange(shown.X, zoomBox.X, zoomBox.X + zoomBox.Width);
                Assert.InRange(shown.Y, zoomBox.Y, zoomBox.Y + zoomBox.Height);
            }

            Assert.Equal(zoomBefore, await _page.EvaluateAsync<double>("() => import('/js/graph.js').then(m => m.zoom())"), 6);
        }

        // A crowd too big for the screen at this zoom zooms out, just enough to show it.
        await AddNodesAsync(cragNode, 60);
        Assert.True(await _page.EvaluateAsync<double>("() => import('/js/graph.js').then(m => m.zoom())") < zoomBefore);
        await AddNodesAsync(cragNode, 0);

        // Removing, from the menu.
        var menu = _page.Locator(".explore-menu");
        await RightClickAsync(cragNode);
        await menu.GetByRole(AriaRole.Button, new() { Name = "Remove others", Exact = true }).ClickAsync();
        await WaitForExactNodesAsync(1);
        await RightClickAsync(cragNode);
        await menu.GetByRole(AriaRole.Button, new() { Name = "Remove", Exact = true }).ClickAsync();
        await WaitForExactNodesAsync(0);
        await Assertions.Expect(_page.Locator(".explore-empty")).ToBeVisibleAsync();

        // A node with no saved position still gets drawn.
        await _page.EvaluateAsync("() => import('/js/graph.js').then(m => m.restore([{ id: 'x', label: 'Crag', caption: 'x' }], [], []))");
        await WaitForExactNodesAsync(1);

        // Redrawing mid-drag stops the physics rather than leaving it running on nodes that have gone.
        var held = await OnScreenAsync("x");
        await _page.Mouse.MoveAsync((float)held.X, (float)held.Y);
        await _page.Mouse.DownAsync();
        await _page.Mouse.MoveAsync((float)held.X + 40, (float)held.Y + 20, new() { Steps = 5 });
        await _page.EvaluateAsync("() => import('/js/graph.js').then(m => m.restore([{ id: 'x', label: 'Crag', caption: 'x' }], [], []))");
        await _page.Mouse.UpAsync();
        Assert.Empty(await OverlapsAsync());

        // A node chosen from the keyboard is brought into view if it's off screen, and left alone if it isn't.
        await _page.EvaluateAsync("() => import('/js/graph.js').then(m => m.reveal('no-such-node'))");
        var onScreen = await OnScreenAsync("x");
        await _page.EvaluateAsync("() => import('/js/graph.js').then(m => m.reveal('x'))");
        Assert.Equal(onScreen, await OnScreenAsync("x"));

        // Dragging the background pans the view until the node is off the right-hand edge.
        var canvasBox = (await _page.Locator(".explore-canvas").BoundingBoxAsync())!;
        await _page.Mouse.MoveAsync(canvasBox.X + 20, canvasBox.Y + 20);
        await _page.Mouse.DownAsync();
        await _page.Mouse.MoveAsync(canvasBox.X + 20 + canvasBox.Width, canvasBox.Y + 20, new() { Steps = 8 });
        await _page.Mouse.UpAsync();
        Assert.True((await OnScreenAsync("x")).X > canvasBox.X + canvasBox.Width, "Panning didn't move the node off screen");
        await _page.EvaluateAsync("() => import('/js/graph.js').then(m => m.reveal('x'))");
        var revealed = await OnScreenAsync("x");
        Assert.InRange(revealed.X, canvasBox.X, canvasBox.X + canvasBox.Width);
        Assert.InRange(revealed.Y, canvasBox.Y, canvasBox.Y + canvasBox.Height);

        // The picture export draws what's on the canvas.
        var picture = await _page.RunAndWaitForDownloadAsync(() => _page.EvaluateAsync("() => import('/js/graph.js').then(m => m.savePng('scene.png'))"));
        Assert.Equal("scene.png", picture.SuggestedFilename);

        // Leaving the page takes the graph down with it.
        await _page.Locator(".app-rail a[href='logbook']").ClickAsync();
        await _page.WaitForURLAsync("**/logbook");
        await WaitForExactNodesAsync(0);
        Assert.True(await _page.EvaluateAsync<bool>("() => import('/js/graph.js').then(m => m.renderedPosition('x') === null)"));
        Assert.Empty(await OverlapsAsync());
        await _page.EvaluateAsync("() => import('/js/graph.js').then(m => { m.update([], [], null); m.select(null); m.restore([], [], []); m.fit(); m.reveal('x'); })");
        Assert.False(await _page.EvaluateAsync<bool>("() => import('/js/graph.js').then(m => m.savePng('nothing.png'))"));
        await Assertions.Expect(_page.GetByRole(AriaRole.Heading, new() { Name = "Logbook" })).ToBeVisibleAsync();

        Assert.Empty(_consoleErrors);
        AssertGraphScriptIsCovered(await cdp.SendAsync("Profiler.takePreciseCoverage"));
    }

    // The browser reports which bytes of graph.js ran. Only the missing-library and failed-load paths are left to
    // the test that blocks the libraries, because they need a page where the scripts can't load.
    private static void AssertGraphScriptIsCovered(JsonElement? coverage)
    {
        var scripts = coverage!.Value.GetProperty("result").EnumerateArray().ToList();
        var script = scripts.FirstOrDefault(s => s.GetProperty("url").GetString()!.Contains("graph", StringComparison.Ordinal));
        Assert.True(script.ValueKind == JsonValueKind.Object, "Scripts: " + string.Join(", ", scripts.Select(s => s.GetProperty("url").GetString())));
        var source = File.ReadAllText(RepoPaths.Resolve("src/Linkbase/wwwroot/js/graph.js"));
        var counts = new int[source.Length + 16];
        Array.Fill(counts, -1);
        foreach (var function in script.GetProperty("functions").EnumerateArray())
        {
            foreach (var range in function.GetProperty("ranges").EnumerateArray())
            {
                for (var i = range.GetProperty("startOffset").GetInt32(); i < range.GetProperty("endOffset").GetInt32() && i < counts.Length; i++)
                {
                    counts[i] = range.GetProperty("count").GetInt32();
                }
            }
        }

        // Which of these run depends on where the layout happens to put nodes, so the overlap test checks them by result.
        var separate = source.IndexOf("function separateNew", StringComparison.Ordinal);
        Array.Fill(counts, 1, separate, source.IndexOf("// Fits everything", StringComparison.Ordinal) - separate);

        var missed = new List<string>();
        for (var i = 0; i < source.Length; i++)
        {
            if (counts[i] == 0)
            {
                var end = i;
                while (end < source.Length && counts[end] == 0)
                {
                    end++;
                }

                missed.Add(source[i..end].Trim());
                i = end;
            }
        }

        missed.RemoveAll(block => block.Contains("Could not load", StringComparison.Ordinal) || block.Contains("loading = null", StringComparison.Ordinal)
            || block.Contains("dragGap", StringComparison.Ordinal) || block == "|| 1"
            || block.Contains("dragMaxSettleFrames", StringComparison.Ordinal));
        Assert.True(missed.Count == 0, "Not run in graph.js:\n" + string.Join("\n---\n", missed));
    }
}
