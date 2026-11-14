using System.Net;
using System.Net.Sockets;
using System.Threading.Channels;
using Microsoft.Playwright;

namespace Linkbase.Tests;

// Blazor Server sends each keystroke to the server and applies the server's reply to the page. On a slow link the reply to an
// earlier keystroke arrives after later ones were typed, and can put the field back to what it held before. That never
// happens on localhost, so these tests put a delay between the browser and the app, like a colleague on the other side of the
// world, and check that what was typed is what the field holds.

// Passes TCP through, holding every chunk back for `delay` in each direction, in order. HTTP and WebSockets both work, since
// it knows neither.
internal sealed class DelayProxy : IAsyncDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _accepting;

    public DelayProxy(Uri target, TimeSpan delay)
    {
        _listener.Start();
        Url = $"http://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}";
        _accepting = Task.Run(async () =>
        {
            while (!_stop.IsCancellationRequested)
            {
                var client = await _listener.AcceptTcpClientAsync(_stop.Token);
                _ = Task.Run(() => RelayAsync(client, target, delay));
            }
        });
    }

    public string Url { get; }

    private async Task RelayAsync(TcpClient client, Uri target, TimeSpan delay)
    {
        using var upstream = new TcpClient();
        await upstream.ConnectAsync(target.Host, target.Port);
        using (client)
        {
            await Task.WhenAny(
                PumpAsync(client.GetStream(), upstream.GetStream(), delay),
                PumpAsync(upstream.GetStream(), client.GetStream(), delay));
        }
    }

    private async Task PumpAsync(Stream from, Stream to, TimeSpan delay)
    {
        var queue = Channel.CreateUnbounded<(byte[] Data, DateTime Due)>();
        var writer = Task.Run(async () =>
        {
            await foreach (var (data, due) in queue.Reader.ReadAllAsync())
            {
                var wait = due - DateTime.UtcNow;
                if (wait > TimeSpan.Zero)
                {
                    await Task.Delay(wait);
                }

                await to.WriteAsync(data);
            }
        });
        try
        {
            var buffer = new byte[16 * 1024];
            int read;
            while ((read = await from.ReadAsync(buffer, _stop.Token)) > 0)
            {
                queue.Writer.TryWrite((buffer[..read], DateTime.UtcNow + delay));
            }
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException)
        {
        }
        finally
        {
            queue.Writer.TryComplete();
            await writer;
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync();
        _listener.Stop();
        try
        {
            await _accepting;
        }
        catch (OperationCanceledException)
        {
        }
        catch (ObjectDisposedException)
        {
        }
    }
}

[Trait("Category", "Browser")]
public sealed class LatencyTypingTests(PostgresFixture fixture) : IAsyncLifetime, IClassFixture<PostgresFixture>
{
    // 150 ms each way is 300 ms round trip: about New Zealand to the UK.
    private static readonly TimeSpan OneWay = TimeSpan.FromMilliseconds(150);

    // A steady, ordinary typing speed: faster than the round trip, so replies arrive while the next keys are being typed.
    private const int KeyDelayMs = 90;
    private const string Sentence = "the quick brown fox jumps over the lazy dog";

    private ModelFactory _store = null!;
    private DelayProxy _proxy = null!;
    private IPlaywright _playwright = null!;
    private IBrowser _browser = null!;
    private IPage _page = null!;

    public async Task InitializeAsync()
    {
        _store = await ClimbingStore.CreateAsync(fixture);
        var app = await RunningApp.StartAsync(_store.ConnectionString);
        _proxy = new DelayProxy(new Uri(app), OneWay);
        _playwright = await Playwright.CreateAsync();
        _browser = await _playwright.Chromium.LaunchAsync();
        _page = await _browser.NewPageAsync(new() { ViewportSize = new() { Width = 1400, Height = 900 } });
    }

    public async Task DisposeAsync()
    {
        await _browser.DisposeAsync();
        _playwright.Dispose();
        await _proxy.DisposeAsync();
    }

    private async Task TypeAsync(ILocator field)
    {
        // Keys typed before the browser's connection to the server is up are lost, which isn't what's being tested.
        await _page.WaitForTimeoutAsync(4000);
        await field.ClickAsync();
        await field.PressSequentiallyAsync(Sentence, new() { Delay = KeyDelayMs });

        // Give every reply time to arrive, then look: a reply that lands late is the bug.
        await _page.WaitForTimeoutAsync(1500);
        Assert.Equal(Sentence, await field.InputValueAsync());
    }

    // The page is drawn before the browser's connection to the server is up, and over a slow link that takes a moment, so
    // anything pressed too early does nothing. Try again until the thing it opens is there.
    private static async Task OpenAsync(Func<Task> action, ILocator opened)
    {
        for (var attempt = 0; attempt < 20; attempt++)
        {
            await action();
            try
            {
                await opened.WaitForAsync(new() { Timeout = 1500 });
                return;
            }
            catch (TimeoutException)
            {
            }
        }

        await opened.WaitForAsync();
    }

    [Fact]
    public async Task Quick_find_keeps_what_is_typed_on_a_slow_link()
    {
        await _page.GotoAsync($"{_proxy.Url}/insights", new() { WaitUntil = WaitUntilState.NetworkIdle });
        var box = _page.GetByPlaceholder("Find a record or page, or add one");
        await _page.WaitForTimeoutAsync(4000);
        await OpenAsync(() => _page.Keyboard.PressAsync("Control+k"), box);

        await TypeAsync(box);
    }

    [Fact]
    public async Task The_explore_search_box_keeps_what_is_typed_on_a_slow_link()
    {
        await _page.GotoAsync($"{_proxy.Url}/explore", new() { WaitUntil = WaitUntilState.NetworkIdle });

        await TypeAsync(_page.GetByLabel("Search phrase"));
    }

    [Fact]
    public async Task The_record_list_search_keeps_what_is_typed_on_a_slow_link()
    {
        await _page.GotoAsync($"{_proxy.Url}/data/Crag", new() { WaitUntil = WaitUntilState.NetworkIdle });

        await TypeAsync(_page.GetByLabel("Search", new() { Exact = true }));
    }
}
