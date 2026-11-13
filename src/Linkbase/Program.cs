using Linkbase.Components;
using Linkbase.Core;
using Linkbase.Core.Graph;
using Linkbase.Core.CsvImport;
using Linkbase.Core.Models;
using Linkbase.Core.Records;
using Linkbase.Data;
using Linkbase.Domains;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.HttpLogging;
using Microsoft.EntityFrameworkCore;
using MudBlazor.Services;

var builder = WebApplication.CreateBuilder(args);

// The development database's connection string is in a git-ignored file that init-dev.ps1 writes. Environment variables and
// the command line are added again after it, so they still win, as the recovery rehearsal needs.
if (builder.Environment.IsDevelopment())
{
    builder.Configuration.AddJsonFile("appsettings.Development.local.json", optional: true);
    builder.Configuration.AddEnvironmentVariables();
    if (args.Length > 0)
    {
        builder.Configuration.AddCommandLine(args);
    }
}

// Add services to the container.
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

builder.Services.AddMudServices();

// One log line per request, on stdout like every other log: what was asked for and how it went, never bodies, headers
// or query strings, which can hold personal data. appsettings.json makes the console JSON outside Development.
builder.Services.AddHttpLogging(options =>
{
    options.LoggingFields = HttpLoggingFields.RequestMethod | HttpLoggingFields.RequestPath
        | HttpLoggingFields.ResponseStatusCode | HttpLoggingFields.Duration;
    options.CombineLogs = true;
});

// Each model has its own database, and the factory connects to whichever model is current.
builder.Services.AddSingleton<ModelCatalog>();
builder.Services.AddScoped<CurrentModel>();
builder.Services.AddScoped<IDbContextFactory<LinkbaseDbContext>, ModelDbContextFactory>();

// Data Protection signs cookies and anti-forgery tokens. Its keys live in the default model's database, so they survive
// restarts and every pod shares them. It resolves a context from the container, so one is registered for it alone; pages
// and repositories still use the factory above.
builder.Services.AddScoped<LinkbaseDbContext>(services =>
{
    var catalog = services.GetRequiredService<ModelCatalog>();
    return new LinkbaseDbContext(new DbContextOptionsBuilder<LinkbaseDbContext>().UseNpgsql(catalog.ConnectionString(catalog.Default)).Options);
});
builder.Services.AddDataProtection().SetApplicationName("Linkbase").PersistKeysToDbContext<LinkbaseDbContext>();

Telemetry.Add(builder.Services, builder.Configuration);

builder.Services.AddScoped<DatabaseStatusService>();
builder.Services.AddHealthChecks().AddCheck<ModelDatabasesHealthCheck>("databases", tags: ["ready"]);
builder.Services.AddScoped<GraphRepository>();
builder.Services.AddScoped<Linkbase.Core.Views.SavedViewRepository>();
builder.Services.AddScoped<ModelLoader>();
builder.Services.AddScoped<RecordHooks>();
builder.Services.AddScoped<RecordRepository>();
builder.Services.AddScoped<Linkbase.Core.History.HistoryRepository>();
builder.Services.AddScoped<Linkbase.Core.History.ChangeReverter>();
builder.Services.AddScoped<ExternalRefRepository>();
builder.Services.AddScoped<Linkbase.Core.Home.HomeRepository>();
builder.Services.AddScoped<Linkbase.Core.Cleanup.CleanupRepository>();
builder.Services.AddScoped<Linkbase.Core.Paths.PathRepository>();
builder.Services.AddScoped<Linkbase.Core.Timeline.TimelineRepository>();
builder.Services.AddScoped<CsvImportService>();
builder.Services.AddScoped<CsvModelStarter>();
builder.Services.AddScoped<Linkbase.Core.Staging.ImportStagingService>();
builder.Services.AddHttpClient<Linkbase.Core.Extraction.PageTextFetcher>(client => client.Timeout = TimeSpan.FromSeconds(30));
builder.Services.AddScoped<Linkbase.Core.Extraction.ModelExtractionService>();
builder.Services.AddScoped<Linkbase.Core.Extraction.IModelExtractor>(_ =>
    builder.Configuration["Extraction:Fixture"] is { Length: > 0 } fixture
        ? new Linkbase.Core.Extraction.FixtureModelExtractor(Linkbase.Core.RepoPaths.Resolve(fixture))
        : new Linkbase.Core.Extraction.AnthropicModelExtractor(builder.Configuration));
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddScoped<Linkbase.Core.Ask.QuestionService>();
builder.Services.AddScoped<Linkbase.Core.Ask.IQuestionCompiler>(_ =>
    builder.Configuration["Ask:Fixture"] is { Length: > 0 } fixture
        ? new Linkbase.Core.Ask.FixtureQuestionCompiler(Linkbase.Core.RepoPaths.Resolve(fixture))
        : new Linkbase.Core.Ask.AnthropicQuestionCompiler(builder.Configuration));
foreach (var domain in RegisteredDomains.All)
{
    domain.AddServices(builder.Services, builder.Configuration);
}

var app = builder.Build();

// Admin commands run instead of the web app: dotnet run --project src/Linkbase -- import all
if (ModelCommands.IsCommand(args))
{
    await using var scope = app.Services.CreateAsyncScope();
    return await new ModelCommands(scope.ServiceProvider, Console.Out).RunAsync(args);
}

foreach (var domain in RegisteredDomains.All)
{
    if (await domain.TryRunCommandAsync(args, app.Services, app.Configuration) is { } exitCode)
    {
        return exitCode;
    }
}

app.UseHttpLogging();

// Configure the HTTP request pipeline.
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    // The default HSTS value is 30 days. You may want to change this for production scenarios, see https://aka.ms/aspnetcore-hsts.
    app.UseHsts();
}
app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);

// In a container, TLS ends at the Route or ingress in front of the app, which has no HTTPS port of its own, so the image
// turns this off. ASPNETCORE_FORWARDEDHEADERS_ENABLED tells the app the original request was HTTPS.
if (app.Configuration.GetValue("HttpsRedirection", true))
{
    app.UseHttpsRedirection();
}

app.UseAntiforgery();

app.MapStaticAssets();

// For a platform's probes. Live runs no checks, so a slow database never gets a working pod restarted; ready checks every
// model's database. Neither writes a request log line, as probes call them every few seconds.
app.MapHealthChecks("/health/live", new HealthCheckOptions { Predicate = _ => false }).WithHttpLogging(HttpLoggingFields.None);
app.MapHealthChecks("/health/ready", new HealthCheckOptions { Predicate = check => check.Tags.Contains("ready") }).WithHttpLogging(HttpLoggingFields.None);

// The switcher in the rail comes here. The choice is kept in a cookie, and a fresh page load starts a circuit with it.
app.MapGet("/model/{key}", (string key, ModelCatalog catalog, HttpContext context) =>
{
    if (catalog.Find(key) is null)
    {
        return Results.NotFound();
    }

    context.Response.Cookies.Append(CurrentModel.Cookie, key, new CookieOptions { HttpOnly = true, SameSite = SameSiteMode.Lax, MaxAge = TimeSpan.FromDays(365) });
    return Results.LocalRedirect("~/");
});
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Run();
return 0;
