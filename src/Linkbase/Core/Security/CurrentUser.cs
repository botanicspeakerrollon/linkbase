using System.Security.Claims;
using Microsoft.AspNetCore.Components.Authorization;

namespace Linkbase.Core.Security;

// Who is signed in, as far as grants are concerned: an id the grant sources look up, and the claims the sign-in provided
// (from an identity provider such as Entra ID, Okta or Keycloak, or from Active Directory), for a source that reads them.
// An unknown user has no id, and is nobody.
public sealed record CurrentUser(string? Id, IReadOnlyList<KeyValuePair<string, string>> Claims)
{
    public static CurrentUser Anonymous { get; } = new(null, []);

    public CurrentUser(string? id) : this(id, [])
    {
    }
}

// Supplies the current user, per request or circuit. Which one runs is the Security:Identity setting; a deployment can register
// its own to bring in a user from somewhere else, such as a header set by a gateway it trusts.
public interface ICurrentUserAccessor
{
    ValueTask<CurrentUser> GetAsync(CancellationToken cancellationToken = default);
}

// The one user named by the Security:User setting. For a demo, a developer's machine and tests, where nobody signs in.
// With none named the user is nobody, who sees nothing of a model that uses levels.
public sealed class ConfiguredUserAccessor(IConfiguration configuration) : ICurrentUserAccessor
{
    public ValueTask<CurrentUser> GetAsync(CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(configuration["Security:User"] is { Length: > 0 } id ? new CurrentUser(id) : CurrentUser.Anonymous);
}

// The user ASP.NET Core authentication has signed in: whatever scheme the deployment adds (OpenID Connect, Negotiate for
// Active Directory, a cookie, a gateway's header) puts a principal on the request, and this reads it. The user's id is the
// claim named by Security:UserClaim, or the first of preferred_username, name and the name identifier that is there. Someone
// not signed in is nobody.
public sealed class PrincipalUserAccessor(AuthenticationStateProvider authentication, IConfiguration configuration) : ICurrentUserAccessor
{
    private static readonly string[] IdClaims = ["preferred_username", ClaimTypes.Name, "name", ClaimTypes.NameIdentifier, "sub"];

    public async ValueTask<CurrentUser> GetAsync(CancellationToken cancellationToken = default)
    {
        var principal = (await authentication.GetAuthenticationStateAsync()).User;
        if (principal.Identity?.IsAuthenticated != true)
        {
            return CurrentUser.Anonymous;
        }

        var named = configuration["Security:UserClaim"];
        string[] candidates = named is { Length: > 0 } ? [named] : IdClaims;
        var id = candidates.Select(principal.FindFirstValue).FirstOrDefault(v => !string.IsNullOrEmpty(v));
        return new CurrentUser(id, principal.Claims.Select(c => KeyValuePair.Create(c.Type, c.Value)).ToList());
    }
}

// How an in-house developer brings grants from a system this app has no adapter for, such as an LDAP query or a database. Register
// it with the container; a security part names it with { "type": "custom", "provider": "<Name>" }. It does its own caching.
// Anything it throws is logged and grants nothing, so a failure shows people less and never more.
public interface ICustomGrantProvider
{
    // What the part's source names.
    string Name { get; }

    // The values the user holds for a part: "level", "anyOf" or "allOf". Strings for a set, one string for a level.
    Task<IReadOnlyList<string>> GetAsync(CurrentUser user, string partKey, CancellationToken cancellationToken);
}
