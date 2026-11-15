using System.Net;
using System.Security.Claims;
using Linkbase.Core.Models;
using Linkbase.Core.Security;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.Server;
using MudBlazor;
using MudBlazor.Utilities;

namespace Linkbase.Components.Layout;

// Demo sign-in (Security:Identity = "demo"): a page that lists the example users in the models' security sections, and signing in
// as one is a click, with no password. It shows row and field security working for different people, and nothing more: anyone
// who can reach the app can be anyone, so a hosted demo must be kept behind something that knows who may open it (the host's
// own sign-in, or an address allowlist). For real people, use Security:Identity = "principal" and a real scheme. See
// docs/history/security-design.md, "Users and sign-in".
public static class DemoSignIn
{
    public const string Scheme = CookieAuthenticationDefaults.AuthenticationScheme;
    private const string Name = "preferred_username";

    public static void Add(IServiceCollection services)
    {
        services.AddAuthentication(Scheme).AddCookie(options =>
        {
            options.Cookie.Name = "linkbase-demo-user";
            options.Cookie.HttpOnly = true;
            options.Cookie.SameSite = SameSiteMode.Lax;
            options.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
            options.LoginPath = "/signin";
            options.ExpireTimeSpan = TimeSpan.FromHours(12);
        });

        // Everything needs a signed-in user except what is marked AllowAnonymous below, so the models stay behind the page.
        services.AddAuthorizationBuilder().SetFallbackPolicy(new Microsoft.AspNetCore.Authorization.AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build());
    }

    public static void Map(WebApplication app)
    {
        app.MapGet("/signin", async (HttpContext context, IServiceProvider services, ModelCatalog catalog, ILoggerFactory loggers, IAntiforgery antiforgery) =>
        {
            var groups = await UsersAsync(services, catalog, loggers.CreateLogger(nameof(DemoSignIn)), context.RequestAborted);
            var token = antiforgery.GetAndStoreTokens(context);
            return Results.Content(Page(groups, context.Request.Query["error"].Count > 0, token.FormFieldName, token.RequestToken ?? ""), "text/html; charset=utf-8");
        }).AllowAnonymous();

        // A POST with an anti-forgery token, so another site can't sign a visitor in as someone by sending them a link.
        app.MapPost("/signin/as", async ([Microsoft.AspNetCore.Mvc.FromForm] string user, HttpContext context, IServiceProvider services, ModelCatalog catalog, ILoggerFactory loggers) =>
        {
            // Only someone a model lists, so the endpoint can't be used to be anybody.
            var groups = await UsersAsync(services, catalog, loggers.CreateLogger(nameof(DemoSignIn)), context.RequestAborted);
            if (!groups.Any(g => g.Users.Any(u => u.Id == user)))
            {
                return Results.Redirect("/signin?error=1");
            }

            var identity = new ClaimsIdentity([new Claim(Name, user)], Scheme, Name, null);
            await context.SignInAsync(Scheme, new ClaimsPrincipal(identity));
            return Results.LocalRedirect("~/");
        }).AllowAnonymous();

        app.MapGet("/signout", async (HttpContext context) =>
        {
            await context.SignOutAsync(Scheme);
            return Results.Redirect("/signin");
        }).AllowAnonymous();
    }

    public sealed record Person(string Id, string Summary);

    public sealed record ModelPeople(string Model, IReadOnlyList<Person> Users);

    // The users of every model that has some. Read as nobody, from a scope of its own: the setting isn't behind row security.
    private static async Task<IReadOnlyList<ModelPeople>> UsersAsync(IServiceProvider services, ModelCatalog catalog, ILogger logger, CancellationToken cancellationToken)
    {
        var groups = new List<ModelPeople>();
        foreach (var model in catalog.Models)
        {
            try
            {
                await using var scope = services.CreateAsyncScope();
                scope.ServiceProvider.GetRequiredService<CurrentModel>().Select(model.Key);
                if (scope.ServiceProvider.GetRequiredService<AuthenticationStateProvider>() is IHostEnvironmentAuthenticationStateProvider host)
                {
                    host.SetAuthenticationState(Task.FromResult(new AuthenticationState(new ClaimsPrincipal(new ClaimsIdentity()))));
                }

                var security = await scope.ServiceProvider.GetRequiredService<LabelRepository>().SettingsAsync(cancellationToken);
                if (security?.Users is { Count: > 0 } users)
                {
                    groups.Add(new ModelPeople(model.Name, users.Select(u => new Person(u.Key, Summary(u.Value))).ToList()));
                }
            }
            catch (Exception ex) when (ex is Npgsql.NpgsqlException or InvalidOperationException)
            {
                logger.LogWarning("Couldn't read the users of {Model}: {Reason}", model.Name, ex.Message);
            }
        }

        return groups;
    }

    private static string Summary(UserGrants grants)
    {
        var parts = new List<string>();
        if (grants.Level is not null)
        {
            parts.Add(grants.Level);
        }

        parts.AddRange(grants.AnyOf ?? []);
        parts.AddRange((grants.AllOf ?? []).Select(v => "NDA " + v));
        return parts.Count == 0 ? "no access to restricted records" : string.Join(" · ", parts);
    }

    private static string Page(IReadOnlyList<ModelPeople> groups, bool failed, string tokenName, string token)
    {
        var p = AppTheme.Theme.PaletteDark;
        string C(MudColor c) => c.ToString(MudColorOutputFormats.Hex);
        string E(string text) => WebUtility.HtmlEncode(text);

        var body = new System.Text.StringBuilder();
        if (groups.Count == 0)
        {
            body.Append("<p class=muted>No model has example users yet. Add a <code>security.users</code> table to a model file, load it, and reload this page.</p>");
        }

        foreach (var group in groups)
        {
            body.Append(System.Globalization.CultureInfo.InvariantCulture, $"<h2>{E(group.Model)}</h2><ul>");
            foreach (var person in group.Users)
            {
                body.Append(System.Globalization.CultureInfo.InvariantCulture, $"<li><form method=post action=\"/signin/as\"><input type=hidden name=\"{E(tokenName)}\" value=\"{E(token)}\"><button name=user value=\"{E(person.Id)}\"><b>{E(person.Id)}</b><span>{E(person.Summary)}</span></button></form></li>");
            }

            body.Append("</ul>");
        }

        return $$"""
            <!DOCTYPE html>
            <html lang="en"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width, initial-scale=1"><meta name="color-scheme" content="dark">
            <title>Sign in</title>
            <style>
              body { margin: 0; background: {{C(p.Background)}}; color: {{C(p.TextPrimary)}}; font: 14px/1.45 "Segoe UI", system-ui, sans-serif; }
              main { max-width: 640px; margin: 8vh auto; padding: 0 16px; }
              h1 { font-size: 22px; margin: 0 0 4px; } h2 { font-size: 15px; margin: 28px 0 8px; color: {{C(p.TextSecondary)}}; }
              .muted, .note { color: {{C(p.TextSecondary)}}; } .err { color: {{C(p.Error)}}; }
              ul { list-style: none; margin: 0; padding: 0; display: grid; gap: 6px; }
              button { width: 100%; font: inherit; cursor: pointer; display: flex; justify-content: space-between; gap: 16px; padding: 10px 14px; border: 1px solid {{C(p.LinesDefault)}}; border-radius: 6px;
                  background: {{C(p.Surface)}}; color: inherit; text-align: left; }
              button:hover, button:focus-visible { border-color: {{C(p.Primary)}}; outline: none; } button span { color: {{C(p.TextSecondary)}}; text-align: right; }
              code { background: {{C(p.Surface)}}; padding: 1px 4px; border-radius: 3px; }
            </style></head>
            <body><main>
              <h1>Sign in</h1>
              <p class="note">Demo sign-in: pick a made-up person. There's no password, and what they can see follows their grants.</p>
              {{(failed ? "<p class=err>That isn't one of the example users.</p>" : "")}}
              {{body}}
            </main></body></html>
            """;
    }
}
