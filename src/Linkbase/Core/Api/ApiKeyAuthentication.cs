using System.Security.Claims;
using System.Text.Encodings.Web;
using Linkbase.Core.Security;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace Linkbase.Core.Api;

// Where the API's settings are read, and the one place that knows which authentication scheme it uses.
public static class ApiSettings
{
    public const string KeyScheme = "ApiKey";

    public static bool Enabled(IConfiguration configuration) => configuration.GetValue("Api:Enabled", true);

    // The scheme the API asks for a signed-in caller from. To use OpenID Connect or anything else that sends a bearer token,
    // register its scheme (for example JwtBearer) in the deployment and name it here. Nothing else in the API changes.
    public static string Scheme(IConfiguration configuration) => configuration["Api:Scheme"] is { Length: > 0 } scheme ? scheme : KeyScheme;
}

// Reads "Authorization: Bearer lbk_..." and signs in the key's user. A token that isn't shaped like a key is left alone, so
// another scheme can answer it.
public sealed class ApiKeyAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder, ApiKeyRepository keys, IConfiguration configuration)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    private const string BearerPrefix = "Bearer ";

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var header = Request.Headers.Authorization.ToString();
        if (!header.StartsWith(BearerPrefix + ApiKeyToken.Prefix, StringComparison.OrdinalIgnoreCase))
        {
            return AuthenticateResult.NoResult();
        }

        var user = await keys.ValidateAsync(header[BearerPrefix.Length..].Trim(), Context.RequestAborted);
        if (user is null)
        {
            return AuthenticateResult.Fail("Unknown, expired or revoked API key.");
        }

        // The claim the pages read the user's id from, so a key's user is the same person as when they sign in.
        var claim = configuration["Security:UserClaim"] is { Length: > 0 } named ? named : CurrentUserClaims.Preferred;
        var identity = new ClaimsIdentity([new Claim(claim, user)], Scheme.Name, claim, null);
        return AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), Scheme.Name));
    }

    protected override async Task HandleChallengeAsync(AuthenticationProperties properties)
    {
        Response.StatusCode = StatusCodes.Status401Unauthorized;
        Response.Headers.WWWAuthenticate = "Bearer";
        await Response.WriteAsJsonAsync(new { error = "A valid API key is needed. Send it as: Authorization: Bearer <key>" });
    }
}

// Inside an API request the caller is whoever the API's scheme signed in, whatever the Security:Identity setting says. Anywhere
// else it is the user that setting chose. The inner accessor is made only when needed, because the principal accessor needs a
// circuit, which an API request doesn't have. A deployment that registers its own ICurrentUserAccessor replaces this one, and
// then reads the API caller itself, from HttpContext.User.
public sealed class ApiUserAccessor(IHttpContextAccessor http, IConfiguration configuration, Func<ICurrentUserAccessor> inner) : ICurrentUserAccessor
{
    public ValueTask<CurrentUser> GetAsync(CancellationToken cancellationToken = default)
    {
        var context = http.HttpContext;
        return context is not null && context.Request.Path.StartsWithSegments("/api") && context.User.Identity?.IsAuthenticated == true
            ? ValueTask.FromResult(CurrentUser.FromPrincipal(context.User, configuration))
            : inner().GetAsync(cancellationToken);
    }
}
