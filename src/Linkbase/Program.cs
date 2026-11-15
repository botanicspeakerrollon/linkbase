using Linkbase;
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

// Security:Identity is read once and decides both the services and the middleware.
var identity = Startup.Identity(builder.Configuration);
var demoSignIn = identity == "demo";
builder.Services.AddLinkbaseSecurity(identity);
builder.Services.AddLinkbaseCore(builder.Configuration);

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

app.UseLinkbaseSecurityHeaders();

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
else if (identity == "principal" && app.Services.GetService<Microsoft.AspNetCore.Authentication.IAuthenticationSchemeProvider>() is not null)
{
    app.UseAuthentication();
}

if (!demoSignIn && !app.Environment.IsDevelopment() && identity != "principal")
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

app.MapModelSwitcher();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Run();
return 0;
