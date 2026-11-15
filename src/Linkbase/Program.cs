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
builder.Services.AddSingleton(RegisteredDomains.Registry);
builder.Services.AddSingleton<ModelCatalog>();
builder.Services.AddSingleton<ModelCreator>();
// Docs:Pages names repo documents to show in the app (the demo shows the requirements). None by default.
builder.Services.AddSingleton(sp => Linkbase.Core.Docs.DocsService.FromConfiguration(sp.GetRequiredService<IConfiguration>(), sp.GetRequiredService<IHostEnvironment>()));
builder.Services.AddScoped<CurrentModel>();
builder.Services.AddSingleton<Linkbase.Core.Security.RowSecurityState>();
builder.Services.AddScoped<Linkbase.Core.Security.ViewerConnectionInterceptor>();
// Who is asking. Security:Identity picks where the user comes from: "configured" (the default, the user named by Security:User,
// for demos and development) or "principal" (whoever ASP.NET Core authentication has signed in, which is how a deployment
// brings OpenID Connect, Active Directory and the like). Register your own ICurrentUserAccessor and ICustomGrantProvider to
// go further. See docs/history/security-design.md, "Users and sign-in".
builder.Services.AddSingleton<Linkbase.Core.Security.ViewerDirectories>();
var demoSignIn = builder.Configuration["Security:Identity"] == "demo";
if (demoSignIn)
{
    // A page of example users to be, with no password, for showing security working. See DemoSignIn.
    Linkbase.Components.Layout.DemoSignIn.Add(builder.Services);
    builder.Services.AddScoped<Linkbase.Core.Security.ICurrentUserAccessor, Linkbase.Core.Security.PrincipalUserAccessor>();
}
else if (builder.Configuration["Security:Identity"] == "principal")
{
    builder.Services.AddScoped<Linkbase.Core.Security.ICurrentUserAccessor, Linkbase.Core.Security.PrincipalUserAccessor>();
}
else
{
    builder.Services.AddScoped<Linkbase.Core.Security.ICurrentUserAccessor, Linkbase.Core.Security.ConfiguredUserAccessor>();
}

builder.Services.AddScoped<Linkbase.Core.Security.IViewerResolver, Linkbase.Core.Security.DirectoryViewerResolver>();
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
builder.Services.AddScoped<ModelEditor>();
builder.Services.AddScoped<RecordHooks>();
builder.Services.AddScoped<RecordRepository>();
builder.Services.AddScoped<Linkbase.Core.History.HistoryRepository>();
builder.Services.AddScoped<Linkbase.Core.Security.LabelRepository>();
builder.Services.AddScoped<Linkbase.Core.Security.FieldPolicy>();
builder.Services.AddScoped<Linkbase.Core.History.ChangeReverter>();
builder.Services.AddScoped<ExternalRefRepository>();
builder.Services.AddScoped<Linkbase.Core.Home.HomeRepository>();
builder.Services.AddScoped<Linkbase.Core.Cleanup.CleanupRepository>();
builder.Services.AddScoped<Linkbase.Core.Paths.PathRepository>();
builder.Services.AddScoped<Linkbase.Core.Paths.TraceRepository>();
builder.Services.AddScoped<Linkbase.Core.Timeline.TimelineRepository>();
builder.Services.AddScoped<CsvImportService>();
builder.Services.AddScoped<CsvModelStarter>();
builder.Services.AddScoped<Linkbase.Core.Staging.ImportStagingService>();
// The fetcher connects only to public addresses, so a page address can't be used to reach the network the app runs in.
// Extraction:AllowPrivateAddresses lifts that, for a deployment whose pages are on its own network.
var allowPrivateFetch = builder.Configuration.GetValue("Extraction:AllowPrivateAddresses", false);
builder.Services.AddHttpClient<Linkbase.Core.Extraction.PageTextFetcher>(client => client.Timeout = TimeSpan.FromSeconds(30))
    .ConfigurePrimaryHttpMessageHandler(() => allowPrivateFetch
        ? new SocketsHttpHandler { MaxAutomaticRedirections = 5 }
        : new SocketsHttpHandler { MaxAutomaticRedirections = 5, ConnectCallback = Linkbase.Core.Extraction.PublicAddress.ConnectAsync });
builder.Services.AddScoped<Linkbase.Core.Extraction.ModelExtractionService>();
// Llm:Provider picks who reads text and answers questions: "anthropic" (the default) or "openai", which is any server that
// speaks the OpenAI chat completions format. See the Configuration section of README.md, and LlmProviders for how to add one.
Linkbase.Core.Llm.LlmProviders.AddLlm(builder.Services, builder.Configuration);
builder.Services.AddScoped<Linkbase.Core.Extraction.IModelExtractor>(sp =>
    builder.Configuration["Extraction:Fixture"] is { Length: > 0 } fixture
        ? new Linkbase.Core.Extraction.FixtureModelExtractor(Linkbase.Core.RepoPaths.Resolve(fixture))
        : new Linkbase.Core.Extraction.LlmModelExtractor(sp.GetRequiredService<Linkbase.Core.Llm.ILlmToolClient>()));
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddScoped<Linkbase.Core.Ask.QuestionService>();
builder.Services.AddScoped<Linkbase.Core.Ask.IQuestionCompiler>(sp =>
    builder.Configuration["Ask:Fixture"] is { Length: > 0 } fixture
        ? new Linkbase.Core.Ask.FixtureQuestionCompiler(Linkbase.Core.RepoPaths.Resolve(fixture))
        : new Linkbase.Core.Ask.LlmQuestionCompiler(sp.GetRequiredService<Linkbase.Core.Llm.ILlmToolClient>()));
foreach (var domain in RegisteredDomains.All)
{
    domain.AddServices(builder.Services, builder.Configuration);
}

var app = builder.Build();

// Models made in the app are in the default model's database, so every command and every page sees them.
await app.Services.GetRequiredService<ModelCreator>().LoadAsync();

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

// Linkbase is never meant to be found through a search engine. robots.txt asks crawlers to stay out, and this header
// tells the well-behaved ones that do arrive not to index or cache anything, including non-HTML responses.
app.Use(async (context, next) =>
{
    context.Response.Headers["X-Robots-Tag"] = "noindex, nofollow, noarchive";
    // Nothing here is meant to be framed, sniffed or to leak its address. The policy limits only framing, the base address,
    // plugins and where forms post, so Blazor's and MudBlazor's own scripts and styles are untouched.
    context.Response.Headers["X-Content-Type-Options"] = "nosniff";
    context.Response.Headers["X-Frame-Options"] = "DENY";
    context.Response.Headers["Referrer-Policy"] = "no-referrer";
    context.Response.Headers["Content-Security-Policy"] = "frame-ancestors 'none'; base-uri 'self'; object-src 'none'; form-action 'self'";
    await next();
});

// In a container, TLS ends at the Route or ingress in front of the app, which has no HTTPS port of its own, so the image
// turns this off. ASPNETCORE_FORWARDEDHEADERS_ENABLED tells the app the original request was HTTPS.
if (app.Configuration.GetValue("HttpsRedirection", true))
{
    app.UseHttpsRedirection();
}

// "principal" reads whoever the deployment's own authentication scheme signed in, so the middleware has to run when it has one.
if (demoSignIn)
{
    app.UseAuthentication();
    app.UseAuthorization();
}
else if (builder.Configuration["Security:Identity"] == "principal" && app.Services.GetService<Microsoft.AspNetCore.Authentication.IAuthenticationSchemeProvider>() is not null)
{
    app.UseAuthentication();
}

if (!demoSignIn && !app.Environment.IsDevelopment() && builder.Configuration["Security:Identity"] != "principal")
{
    app.Logger.LogWarning("Security:Identity is 'configured', so nobody signs in: everyone who can reach this app is {User}. Keep it behind something that decides who may open it. See docs/operations.md.",
        builder.Configuration["Security:User"] ?? "the default user");
}

app.UseAntiforgery();

// With demo sign-in every endpoint needs a signed-in user unless it says otherwise: these, which the sign-in page and probes use.
var staticAssets = app.MapStaticAssets();
if (demoSignIn)
{
    staticAssets.AllowAnonymous();
    Linkbase.Components.Layout.DemoSignIn.Map(app);
}

// For a platform's probes. Live runs no checks, so a slow database never gets a working pod restarted; ready checks every
// model's database. Neither writes a request log line, as probes call them every few seconds.
app.MapHealthChecks("/health/live", new HealthCheckOptions { Predicate = _ => false }).WithHttpLogging(HttpLoggingFields.None).AllowAnonymous();
app.MapHealthChecks("/health/ready", new HealthCheckOptions { Predicate = check => check.Tags.Contains("ready") }).WithHttpLogging(HttpLoggingFields.None).AllowAnonymous();

// The switcher in the rail comes here. The choice is kept in a cookie, and a fresh page load starts a circuit with it.
app.MapGet("/model/{key}", async (string key, ModelCatalog catalog, ModelCreator creator, HttpContext context) =>
{
    // Another instance may have made it since this one last looked.
    if (catalog.Find(key) is null)
    {
        await creator.LoadAsync(context.RequestAborted);
    }

    if (catalog.Find(key) is null)
    {
        return Results.NotFound();
    }

    context.Response.Cookies.Append(CurrentModel.Cookie, key, new CookieOptions { HttpOnly = true, Secure = context.Request.IsHttps, SameSite = SameSiteMode.Lax, MaxAge = TimeSpan.FromDays(365) });
    return Results.LocalRedirect("~/");
});
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Run();
return 0;
