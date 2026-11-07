using Linkbase.Components;
using Linkbase.Core.Graph;
using Linkbase.Core.CsvImport;
using Linkbase.Core.Models;
using Linkbase.Core.Records;
using Linkbase.Data;
using Linkbase.Domains;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.HttpLogging;
using Microsoft.EntityFrameworkCore;
using MudBlazor.Services;

var builder = WebApplication.CreateBuilder(args);

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

builder.Services.AddScoped<DatabaseStatusService>();
builder.Services.AddHealthChecks().AddCheck<ModelDatabasesHealthCheck>("databases", tags: ["ready"]);
builder.Services.AddScoped<GraphRepository>();
builder.Services.AddScoped<ModelLoader>();
builder.Services.AddScoped<RecordRepository>();
builder.Services.AddScoped<CsvImportService>();
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
