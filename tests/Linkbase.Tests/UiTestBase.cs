using AngleSharp.Dom;
using Bunit;
using Linkbase.Core.Models;
using Linkbase.Data;
using Microsoft.Extensions.Configuration;
using Linkbase.Domains.Climbing.Data;
using Linkbase.Core.Graph;
using Microsoft.AspNetCore.Components;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using MudBlazor;
using MudBlazor.Services;

namespace Linkbase.Tests;

// Renders real pages against the real repositories and Postgres. Only the browser's JavaScript and the
// LLM are stood in for. Layout can't be checked here: that's what the browser tests are for.
public abstract class UiTestBase : BunitContext, IAsyncLifetime
{
    protected UiTestBase(PostgresFixture fixture)
    {
        Fixture = fixture;
        DefaultWaitTimeout = TimeSpan.FromSeconds(20);
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddMudServices();

        var factory = fixture.ContextFactory();
        Services.AddSingleton(factory);
        Services.AddSingleton<InsightsRepository>();
        Services.AddSingleton<GraphRepository>();
        Services.AddSingleton<IGraphRepository>(sp => sp.GetRequiredService<GraphRepository>());
        Services.AddSingleton<Linkbase.Core.Views.SavedViewRepository>();
        Services.AddSingleton<Linkbase.Core.Records.RecordRepository>();
        Services.AddSingleton<Linkbase.Core.Records.IRecordRepository>(p => p.GetRequiredService<Linkbase.Core.Records.RecordRepository>());
        Services.AddSingleton<Linkbase.Core.History.HistoryRepository>();
        Services.AddSingleton<Linkbase.Core.History.ChangeReverter>();
        Services.AddSingleton<Linkbase.Core.Records.ExternalRefRepository>();
        Services.AddSingleton<Linkbase.Core.Security.LabelRepository>();
        Services.AddSingleton<Linkbase.Core.Security.IViewerResolver, EveryoneHoldsEverything>();
        AddEverydayRepositories();
        Services.AddSingleton(new DatabaseStatusService(factory, NullLogger<DatabaseStatusService>.Instance));
        Services.AddSingleton(Linkbase.Core.Records.RecordHooks.None);
        Services.AddSingleton(Linkbase.Core.Security.FieldPolicy.Unrestricted);
        Services.AddSingleton(Linkbase.Domains.RegisteredDomains.Registry);
        Services.AddSingleton<Linkbase.Core.Explore.GraphStyles>();
        Services.AddSingleton(new ModelCatalog(new ConfigurationBuilder().Build(), Linkbase.Domains.RegisteredDomains.Registry));
        Services.AddSingleton<CurrentModel>();

        // Explore has a question box. A test that wants one to answer registers its own compiler after this.
        Services.AddSingleton<Microsoft.Extensions.Configuration.IConfiguration>(new Microsoft.Extensions.Configuration.ConfigurationManager());
        Services.AddSingleton(TimeProvider.System);
        Services.AddSingleton<Linkbase.Core.Ask.IQuestionCompiler>(new FakeQuestionCompiler(_ => throw new Linkbase.Core.Ask.QuestionFailedException("No compiler in this test.")));
        Services.AddSingleton<Linkbase.Core.Ask.QuestionService>();
    }

    // MudBlazor's services can only be disposed asynchronously, which xUnit does through this interface.
    Task IAsyncLifetime.InitializeAsync() => Task.CompletedTask;

    Task IAsyncLifetime.DisposeAsync() => DisposeAsync().AsTask();

    protected PostgresFixture Fixture { get; }
    protected IRenderedComponent<MudPopoverProvider> Popovers { get; private set; } = null!;
    protected IRenderedComponent<MudDialogProvider> Dialogs { get; private set; } = null!;

    protected ISnackbar Snackbar => Services.GetRequiredService<ISnackbar>();
    protected string CurrentPath => new Uri(Services.GetRequiredService<NavigationManager>().Uri).AbsolutePath;

    protected static string Unique(string name) => $"{name} {Guid.NewGuid():N}";

    protected IRenderedComponent<TPage> RenderPage<TPage>(Action<ComponentParameterCollectionBuilder<TPage>>? parameters = null)
        where TPage : IComponent
    {
        if (Popovers is null)
        {
            Popovers = Render<MudPopoverProvider>();
            Dialogs = Render<MudDialogProvider>();
            Render<MudSnackbarProvider>();
        }

        return parameters is null ? Render<TPage>() : Render(parameters);
    }

    protected static IElement Control<T>(IRenderedComponent<T> cut, string text)
        where T : IComponent =>
        cut.FindAll("button, a").First(e => e.TextContent.Trim() == text);

    protected static bool HasControl<T>(IRenderedComponent<T> cut, string text)
        where T : IComponent =>
        cut.FindAll("button, a").Any(e => e.TextContent.Trim() == text);

    protected static void Press<T>(IRenderedComponent<T> cut, string text)
        where T : IComponent
    {
        cut.WaitForAssertion(() => Assert.True(HasControl(cut, text), $"No control named {text}"));
        ClickFresh(cut, () => Control(cut, text));
    }

    protected static void ClickFresh<T>(IRenderedComponent<T> cut, Func<IElement> find)
        where T : IComponent =>
        Retrying(cut, () => find().Click());

    protected static void MouseDownFresh<T>(IRenderedComponent<T> cut, Func<IElement> find)
        where T : IComponent =>
        Retrying(cut, () => find().MouseDown());

    // A page still loading can sit unchanged long enough to look settled, then re-render between finding an element and
    // using it, which retires its handler. Finding it again and retrying is then safe, because the event never fired.
    private static void Retrying<T>(IRenderedComponent<T> cut, Action act)
        where T : IComponent
    {
        for (var attempt = 1; ; attempt++)
        {
            Settle(cut);
            try
            {
                act();

                // bUnit keeps an event sent to a retired handler as an unhandled error and only throws it at the next wait,
                // so wait once here to have it thrown where it can be retried, not in a later assertion.
                cut.WaitForAssertion(() => { });
                return;
            }
            catch (Bunit.Rendering.UnknownEventHandlerIdException) when (attempt < 5)
            {
            }
        }
    }

    // A re-render retires an element's event handlers, including one still arriving when it's clicked.
    // Handler ids are part of the markup, so unchanged markup means they're safe to use.
    private static void Settle<T>(IRenderedComponent<T> cut)
        where T : IComponent
    {
        var markup = cut.Markup;
        do
        {
            markup = cut.Markup;
            Thread.Sleep(75);
        }
        while (markup != cut.Markup);
    }

    protected static void Sees<T>(IRenderedComponent<T> cut, string text)
        where T : IComponent =>
        cut.WaitForAssertion(() => Assert.Contains(text, cut.Markup));

    protected static void DoesNotSee<T>(IRenderedComponent<T> cut, string text)
        where T : IComponent =>
        cut.WaitForAssertion(() => Assert.DoesNotContain(text, cut.Markup));

    protected void Told(string message) =>
        WaitUntil(() => Snackbar.ShownSnackbars.Any(s => s.Message?.Contains(message, StringComparison.Ordinal) == true), message);

    protected void NavigatedTo(string path) => WaitUntil(() => CurrentPath == path, $"navigation to {path}, at {CurrentPath}");

    protected static void WaitUntil(Func<bool> condition, string what)
    {
        var deadline = DateTime.UtcNow.AddSeconds(20);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, $"Timed out waiting for {what}");
            Thread.Sleep(20);
        }
    }

    protected static IElement Field<T>(IRenderedComponent<T> cut, string label)
        where T : IComponent
    {
        cut.WaitForAssertion(() => Assert.Contains(cut.FindAll(".mud-input-control"), c => c.QuerySelector("label")?.TextContent.Trim() == label));
        return cut.FindAll(".mud-input-control").First(c => c.QuerySelector("label")?.TextContent.Trim() == label);
    }

    protected static void Type<T>(IRenderedComponent<T> cut, string label, string text)
        where T : IComponent
    {
        Enter(cut, label, text);
    }

    // Fields only listen for input events when they update as you type.
    private static void Enter<T>(IRenderedComponent<T> cut, string label, string text)
        where T : IComponent
    {
        // A dialog still loading can re-render after the field is found, and the typed text goes with the old one.
        Field(cut, label);
        Settle(cut);
        try
        {
            Field(cut, label).QuerySelector("input, textarea")!.Input(text);
        }
        catch (MissingEventHandlerException)
        {
            Field(cut, label).QuerySelector("input, textarea")!.Change(text);
        }
    }

    // Types into an autocomplete and picks from the real dropdown, which lives in the popover provider.
    protected void Pick<T>(IRenderedComponent<T> cut, string label, string text, string option)
        where T : IComponent
    {
        Enter(cut, label, text);
        Popovers.WaitForAssertion(() => Assert.True(Popovers.FindAll(".mud-popover-open .mud-list-item").Any(i => i.TextContent.Contains(option, StringComparison.Ordinal)), $"No {option} in {Popovers.Markup}"));
        ClickFresh(Popovers, () => Popovers.FindAll(".mud-popover-open .mud-list-item").First(i => i.TextContent.Contains(option, StringComparison.Ordinal)));
    }

    protected static Task SetAsync<TPage, TValue>(IRenderedComponent<TPage> cut, EventCallback<TValue> callback, TValue value)
        where TPage : IComponent =>
        cut.InvokeAsync(() => callback.InvokeAsync(value));

    protected sealed record Seeded(long AscentId, long RouteId, long CragId, long AreaId, string Route, string Crag, string Country, string Partner);

    private ModelFactory? _climbing;

    // The climbing model in a database of its own, which every repository reads instead of the shared one from here on. Call
    // it before rendering a page.
    private protected async Task<ModelFactory> UseClimbingStoreAsync()
    {
        if (_climbing is null)
        {
            _climbing = await ClimbingStore.CreateAsync(Fixture);
            Services.AddSingleton<IDbContextFactory<LinkbaseDbContext>>(_climbing);
            Services.AddSingleton<GraphRepository>();
            Services.AddSingleton<IGraphRepository>(sp => sp.GetRequiredService<GraphRepository>());
            Services.AddSingleton<Linkbase.Core.Views.SavedViewRepository>();
            Services.AddSingleton<Linkbase.Core.Records.RecordRepository>();
            Services.AddSingleton<Linkbase.Core.Records.IRecordRepository>(p => p.GetRequiredService<Linkbase.Core.Records.RecordRepository>());
            Services.AddSingleton<Linkbase.Core.History.HistoryRepository>();
            Services.AddSingleton<Linkbase.Core.History.ChangeReverter>();
            Services.AddSingleton<Linkbase.Core.Records.ExternalRefRepository>();
            Services.AddSingleton<Linkbase.Core.Security.LabelRepository>();
            Services.AddSingleton<InsightsRepository>();
            AddEverydayRepositories();
        }

        return _climbing;
    }

    // The home, cleanup, path and timeline repositories. Registered again with the others when the store changes, so they
    // read the same database.
    private void AddEverydayRepositories()
    {
        Services.AddSingleton<Linkbase.Core.Home.HomeRepository>();
        Services.AddSingleton<Linkbase.Core.Cleanup.CleanupRepository>();
        Services.AddSingleton<Linkbase.Core.Paths.PathRepository>();
        Services.AddSingleton<Linkbase.Core.Paths.TraceRepository>();
        Services.AddSingleton<Linkbase.Core.Timeline.TimelineRepository>();
    }

    private static long KeyOf(string nodeId) => long.Parse(nodeId[(nodeId.IndexOf(':') + 1)..], System.Globalization.CultureInfo.InvariantCulture);

    protected async Task<Seeded> SeedAsync(
        DateOnly? date = null,
        AscentStyle style = AscentStyle.Lead,
        string grade = "VS 4c",
        string? notes = null,
        short? rating = null)
    {
        var store = await UseClimbingStoreAsync();
        var (route, crag, country, partner) = (Unique("Route"), Unique("Crag"), Unique("Country"), Unique("Partner"));
        var area = await ClimbingStore.AddCountryAsync(store, country);
        var cragId = await ClimbingStore.AddAsync(store, "Crag", crag, null, "IN", area);
        var routeId = await ClimbingStore.AddAsync(
            store, "Route", route,
            new System.Text.Json.Nodes.JsonObject { ["grade"] = grade, ["grade_system"] = "uk_trad", ["discipline"] = "trad", ["length_m"] = 20, ["pitches"] = 2 }, "AT", cragId);
        var partnerId = await ClimbingStore.AddAsync(store, "Partner", partner);
        var props = new System.Text.Json.Nodes.JsonObject
        {
            ["date"] = (date ?? new DateOnly(2015, 5, 2)).ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture),
            ["date_precision"] = "day",
            ["style"] = SnakeCaseEnumConverter<AscentStyle>.ToStore(style),
            ["source"] = "manual",
        };
        if (notes is not null)
        {
            props["notes"] = notes;
        }

        if (rating is { } stars)
        {
            props["my_rating"] = (int)stars;
        }

        var ascentId = await ClimbingStore.AddAsync(store, "Ascent", route, props, "OF", routeId);
        await ClimbingStore.LinkAsync(store, ascentId, "WITH", partnerId);
        return new Seeded(KeyOf(ascentId), KeyOf(routeId), KeyOf(cragId), KeyOf(area), route, crag, country, partner);
    }

    protected sealed class StubHandler(Func<HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(respond());
    }
}

// The viewer in page tests unless one says otherwise: holds every level and value, so no label option is hidden.
public sealed class EveryoneHoldsEverything : Linkbase.Core.Security.IViewerResolver
{
    public ValueTask<Linkbase.Core.Security.Viewer> ResolveAsync(Linkbase.Core.Security.ModelSecurity security, CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(new Linkbase.Core.Security.Viewer(
            int.MaxValue,
            security.AnyOf?.Values ?? [],
            security.AllOf?.Values ?? []));
}
