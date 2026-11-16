using Linkbase.Core;
using Linkbase.Core.CsvImport;
using Linkbase.Core.Graph;
using Linkbase.Core.Models;
using Linkbase.Core.Records;
using Linkbase.Data;
using Linkbase.Domains;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.HttpLogging;
using Microsoft.EntityFrameworkCore;

namespace Linkbase;

// How Program.cs puts the app together, in the parts it reads as: who is asking, what the app is made of, and what the
// pipeline says about every response.
public static class Startup
{
    // Security:Identity picks where the user comes from: "configured" (the default, the user named by Security:User, for
    // demos and development), "principal" (whoever ASP.NET Core authentication has signed in, which is how a deployment
    // brings OpenID Connect, Active Directory and the like) or "demo" (a page of example users). Read once, here.
    public static string Identity(IConfiguration configuration) => configuration["Security:Identity"] ?? "configured";

    // Registers who is asking and what that lets them see. Register your own ICurrentUserAccessor and ICustomGrantProvider to
    // go further. See docs/history/security-design.md, "Users and sign-in".
    public static IServiceCollection AddLinkbaseSecurity(this IServiceCollection services, string identity)
    {
        services.AddSingleton<Core.Security.RowSecurityState>();
        services.AddScoped<Core.Security.ViewerConnectionInterceptor>();
        services.AddSingleton<Core.Security.ViewerDirectories>();
        Type pages;
        switch (identity)
        {
            case "demo":
                // A page of example users to be, with no password, for showing security working. See DemoSignIn.
                Components.Layout.DemoSignIn.Add(services);
                pages = typeof(Core.Security.PrincipalUserAccessor);
                break;
            case "principal":
                pages = typeof(Core.Security.PrincipalUserAccessor);
                break;
            default:
                pages = typeof(Core.Security.ConfiguredUserAccessor);
                break;
        }

        // Pages use the user Security:Identity chose. An API request uses whoever the API's scheme signed in (ApiUserAccessor).
        services.AddHttpContextAccessor();
        services.AddScoped(pages);
        services.AddScoped<Core.Security.ICurrentUserAccessor>(provider => new Core.Api.ApiUserAccessor(
            provider.GetRequiredService<IHttpContextAccessor>(), provider.GetRequiredService<IConfiguration>(),
            () => (Core.Security.ICurrentUserAccessor)provider.GetRequiredService(pages)));

        services.AddScoped<Core.Security.IViewerResolver, Core.Security.DirectoryViewerResolver>();
        services.AddScoped<Core.Security.LabelRepository>();
        services.AddScoped<Core.Security.FieldPolicy>();
        return services;
    }

    // The read API (INT-3). Callers sign in with the scheme named by Api:Scheme, which is API keys unless the deployment
    // registers another, such as JwtBearer for OpenID Connect. See docs/history/api-plan.md.
    public static IServiceCollection AddLinkbaseApi(this IServiceCollection services, IConfiguration configuration)
    {
        if (!Core.Api.ApiSettings.Enabled(configuration))
        {
            return services;
        }

        services.AddSingleton<Core.Api.ApiKeyRepository>();
        services.AddAuthentication().AddScheme<Microsoft.AspNetCore.Authentication.AuthenticationSchemeOptions, Core.Api.ApiKeyAuthenticationHandler>(Core.Api.ApiSettings.KeyScheme, null);
        services.AddAuthorization();
        return services;
    }

    // Everything the app is made of apart from sign-in: the models and their databases, the repositories and services, text
    // reading, and each domain's own services.
    public static IServiceCollection AddLinkbaseCore(this IServiceCollection services, IConfiguration configuration)
    {
        // One log line per request, on stdout like every other log: what was asked for and how it went, never bodies,
        // headers or query strings, which can hold personal data. appsettings.json makes the console JSON outside Development.
        services.AddHttpLogging(options =>
        {
            options.LoggingFields = HttpLoggingFields.RequestMethod | HttpLoggingFields.RequestPath
                | HttpLoggingFields.ResponseStatusCode | HttpLoggingFields.Duration;
            options.CombineLogs = true;
        });

        // Each model has its own database, and the factory connects to whichever model is current.
        services.AddSingleton(RegisteredDomains.Registry);
        services.AddSingleton<Linkbase.Core.Explore.GraphStyles>();
        services.AddSingleton<ModelCatalog>();
        services.AddSingleton<ModelCreator>();
        // Docs:Pages names repo documents to show in the app (the demo shows the requirements). None by default.
        services.AddSingleton(sp => Core.Docs.DocsService.FromConfiguration(sp.GetRequiredService<IConfiguration>(), sp.GetRequiredService<IHostEnvironment>()));
        services.AddScoped<CurrentModel>();
        services.AddScoped<IDbContextFactory<LinkbaseDbContext>, ModelDbContextFactory>();

        // Data Protection signs cookies and anti-forgery tokens. Its keys live in the default model's database, so they survive
        // restarts and every pod shares them. It resolves a context from the container, so one is registered for it alone; pages
        // and repositories still use the factory above.
        services.AddScoped<LinkbaseDbContext>(provider =>
        {
            var catalog = provider.GetRequiredService<ModelCatalog>();
            return new LinkbaseDbContext(new DbContextOptionsBuilder<LinkbaseDbContext>().UseNpgsql(catalog.ConnectionString(catalog.Default)).Options);
        });
        services.AddDataProtection().SetApplicationName("Linkbase").PersistKeysToDbContext<LinkbaseDbContext>();

        Telemetry.Add(services, configuration);

        services.AddScoped<DatabaseStatusService>();
        services.AddHealthChecks().AddCheck<ModelDatabasesHealthCheck>("databases", tags: ["ready"]);
        services.AddScoped<GraphRepository>();
        services.AddScoped<IGraphRepository>(provider => provider.GetRequiredService<GraphRepository>());
        services.AddScoped<Core.Views.SavedViewRepository>();
        services.AddScoped<ModelLoader>();
        services.AddScoped<ModelEditor>();
        services.AddScoped<RecordHooks>();
        services.AddScoped<RecordRepository>();
        services.AddScoped<IRecordRepository>(provider => provider.GetRequiredService<RecordRepository>());
        services.AddScoped<Core.History.HistoryRepository>();
        services.AddScoped<Core.History.ChangeReverter>();
        services.AddScoped<ExternalRefRepository>();
        services.AddScoped<Core.Home.HomeRepository>();
        services.AddScoped<Core.Cleanup.CleanupRepository>();
        services.AddScoped<Core.Paths.PathRepository>();
        services.AddScoped<Core.Paths.TraceRepository>();
        services.AddScoped<Core.Timeline.TimelineRepository>();
        services.AddScoped<CsvImportService>();
        services.AddScoped<CsvModelStarter>();
        services.AddScoped<Core.Staging.ImportStagingService>();
        services.AddSingleton(TimeProvider.System);

        // The fetcher connects only to public addresses, so a page address can't be used to reach the network the app runs in.
        // Extraction:AllowPrivateAddresses lifts that, for a deployment whose pages are on its own network.
        var allowPrivateFetch = configuration.GetValue("Extraction:AllowPrivateAddresses", false);
        services.AddHttpClient<Core.Extraction.PageTextFetcher>(client => client.Timeout = TimeSpan.FromSeconds(30))
            .ConfigurePrimaryHttpMessageHandler(() => allowPrivateFetch
                ? new SocketsHttpHandler { MaxAutomaticRedirections = 5 }
                : new SocketsHttpHandler { MaxAutomaticRedirections = 5, ConnectCallback = Core.Extraction.PublicAddress.ConnectAsync });
        services.AddScoped<Core.Extraction.ModelExtractionService>();
        // Llm:Provider picks who reads text and answers questions: "anthropic" (the default) or "openai", which is any server that
        // speaks the OpenAI chat completions format. See the Configuration section of README.md, and LlmProviders for how to add one.
        Core.Llm.LlmProviders.AddLlm(services, configuration);
        services.AddScoped<Core.Extraction.IModelExtractor>(sp =>
            configuration["Extraction:Fixture"] is { Length: > 0 } fixture
                ? new Core.Extraction.FixtureModelExtractor(RepoPaths.Resolve(fixture))
                : new Core.Extraction.LlmModelExtractor(sp.GetRequiredService<Core.Llm.ILlmToolClient>()));
        services.AddScoped<Core.Ask.QuestionService>();
        services.AddScoped<Core.Ask.IQuestionCompiler>(sp =>
            configuration["Ask:Fixture"] is { Length: > 0 } fixture
                ? new Core.Ask.FixtureQuestionCompiler(RepoPaths.Resolve(fixture))
                : new Core.Ask.LlmQuestionCompiler(sp.GetRequiredService<Core.Llm.ILlmToolClient>()));
        foreach (var domain in RegisteredDomains.All)
        {
            domain.AddServices(services, configuration);
        }

        return services;
    }

    // Linkbase is never meant to be found through a search engine. robots.txt asks crawlers to stay out, and this header
    // tells the well-behaved ones that do arrive not to index or cache anything, including non-HTML responses.
    public static WebApplication UseLinkbaseSecurityHeaders(this WebApplication app)
    {
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
        return app;
    }

    // The switcher in the rail comes here. The choice is kept in a cookie, and a fresh page load starts a circuit with it.
    public static WebApplication MapModelSwitcher(this WebApplication app)
    {
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
        return app;
    }
}
