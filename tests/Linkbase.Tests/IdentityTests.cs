using System.Security.Claims;
using Linkbase.Core.Security;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace Linkbase.Tests;

// Who is signed in, and how that becomes grants: from the sign-in's own claims, or from a provider a deployment wrote.
public sealed class IdentityTests
{
    private static CurrentUser Ann(params (string Type, string Value)[] claims) =>
        new("ann", claims.Select(c => KeyValuePair.Create(c.Type, c.Value)).ToList());

    private static ModelSecurity Model(GrantSource? level = null, GrantSource? any = null, GrantSource? all = null) => new()
    {
        Level = new SecurityPart { Name = "Class", Values = ["public", "private"], Source = level },
        AnyOf = new SecurityPart { Name = "Dept", Source = any },
        AllOf = new SecurityPart { Name = "Proj", Source = all },
    };

    private static GrantSource Claims(string claim, Dictionary<string, string>? map = null) =>
        new() { Type = GrantSourceType.Claims, Claim = claim, Map = map };

    private sealed class Provider(string name, Func<string, IReadOnlyList<string>> grants) : ICustomGrantProvider
    {
        public string Name => name;

        public Task<IReadOnlyList<string>> GetAsync(CurrentUser user, string partKey, CancellationToken cancellationToken) =>
            Task.FromResult(grants(partKey));
    }

    private sealed class Throwing : ICustomGrantProvider
    {
        public string Name => "broken";

        public Task<IReadOnlyList<string>> GetAsync(CurrentUser user, string partKey, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("directory down");
    }

    private sealed class FakeAuthentication(ClaimsPrincipal user) : AuthenticationStateProvider
    {
        public override Task<AuthenticationState> GetAuthenticationStateAsync() => Task.FromResult(new AuthenticationState(user));
    }

    private static IConfiguration Config(params (string Key, string Value)[] settings) =>
        new ConfigurationBuilder().AddInMemoryCollection(settings.Select(s => KeyValuePair.Create(s.Key, (string?)s.Value))).Build();

    private static ClaimsPrincipal Principal(params Claim[] claims) => new(new ClaimsIdentity(claims, "test"));

    [Fact]
    public async Task Claims_become_grants_and_a_map_renames_them()
    {
        var security = Model(
            level: Claims("clearance", new() { ["SECRET"] = "private" }),
            any: Claims("groups"),
            all: Claims("projects", new() { ["CN=X,OU=Proj"] = "X" }));

        var viewer = await ViewerDirectory.Create(security).ResolveAsync(Ann(
            ("clearance", "SECRET"), ("groups", "Finance"), ("groups", "HR"), ("projects", "CN=X,OU=Proj"), ("projects", "CN=Other")));

        Assert.Equal(1, viewer.Level);
        Assert.Equal(["Finance", "HR"], viewer.AnyOf.Order());
        Assert.Equal(["X"], viewer.AllOf);
    }

    [Fact]
    public async Task A_user_without_the_claim_or_with_an_unmapped_level_has_nothing()
    {
        var security = Model(level: Claims("clearance", new() { ["SECRET"] = "private" }), any: Claims("groups"));

        var viewer = await ViewerDirectory.Create(security).ResolveAsync(Ann(("clearance", "UNKNOWN")));

        Assert.Equal(-1, viewer.Level);
        Assert.Empty(viewer.AnyOf);
    }

    [Fact]
    public async Task A_custom_provider_supplies_grants_by_part()
    {
        var security = Model(
            level: new() { Type = GrantSourceType.Custom, Provider = "hr" },
            any: new() { Type = GrantSourceType.Custom, Provider = "hr" });
        var provider = new Provider("hr", part => part == ModelSecurity.LevelKey ? ["private"] : ["Finance"]);

        var viewer = await ViewerDirectory.Create(security, providers: [provider]).ResolveAsync(Ann());

        Assert.Equal(1, viewer.Level);
        Assert.Equal(["Finance"], viewer.AnyOf);
    }

    [Fact]
    public async Task A_provider_that_throws_or_is_missing_grants_nothing()
    {
        var security = Model(any: new() { Type = GrantSourceType.Custom, Provider = "broken" }, all: new() { Type = GrantSourceType.Custom, Provider = "absent" });

        var viewer = await ViewerDirectory.Create(security, logger: NullLogger.Instance, providers: [new Throwing()]).ResolveAsync(Ann());

        Assert.Empty(viewer.AnyOf);
        Assert.Empty(viewer.AllOf);
    }

    [Fact]
    public async Task The_configured_accessor_names_one_user_or_nobody()
    {
        var named = await new ConfiguredUserAccessor(Config(("Security:User", "ann"))).GetAsync();
        var none = await new ConfiguredUserAccessor(Config()).GetAsync();

        Assert.Equal("ann", named.Id);
        Assert.Null(none.Id);
    }

    [Fact]
    public async Task The_principal_accessor_reads_the_signed_in_user_and_their_claims()
    {
        var principal = Principal(new Claim("preferred_username", "ann@corp"), new Claim("groups", "Finance"), new Claim("groups", "HR"));
        var user = await new PrincipalUserAccessor(new FakeAuthentication(principal), Config()).GetAsync();

        Assert.Equal("ann@corp", user.Id);
        Assert.Equal(["Finance", "HR"], user.Claims.Where(c => c.Key == "groups").Select(c => c.Value));
    }

    [Fact]
    public async Task The_principal_accessor_uses_the_configured_id_claim_and_falls_back_to_the_name()
    {
        var principal = Principal(new Claim(ClaimTypes.Name, @"CORP\ann"), new Claim("employee_id", "e42"));

        var byClaim = await new PrincipalUserAccessor(new FakeAuthentication(principal), Config(("Security:UserClaim", "employee_id"))).GetAsync();
        var byName = await new PrincipalUserAccessor(new FakeAuthentication(principal), Config()).GetAsync();

        Assert.Equal("e42", byClaim.Id);
        Assert.Equal(@"CORP\ann", byName.Id);
    }

    [Fact]
    public async Task Someone_not_signed_in_is_nobody()
    {
        var anonymous = await new PrincipalUserAccessor(new FakeAuthentication(new ClaimsPrincipal(new ClaimsIdentity())), Config()).GetAsync();

        Assert.Null(anonymous.Id);
        Assert.Equal(Viewer.Nobody, await ViewerDirectory.Create(Model()).ResolveAsync(anonymous));
    }

    [Fact]
    public void Sources_are_checked_for_what_they_need()
    {
        var problems = new List<string>();
        Model(
            level: new() { Type = GrantSourceType.Claims },
            any: new() { Type = GrantSourceType.Custom },
            all: new() { Type = GrantSourceType.Claims, Claim = "projects", Url = "https://x.example/{user}" }).Validate(problems);

        Assert.Contains(problems, p => p.Contains("needs a claim"));
        Assert.Contains(problems, p => p.Contains("needs a provider"));
        Assert.Contains(problems, p => p.Contains("can only have a claim and a map"));
    }

    [Fact]
    public void Valid_claims_and_custom_sources_pass_and_users_alongside_them_are_not_orphaned()
    {
        var problems = new List<string>();
        Model(any: Claims("groups", new() { ["CN=Finance"] = "Finance" }), all: new() { Type = GrantSourceType.Custom, Provider = "hr" }).Validate(problems);

        Assert.DoesNotContain(problems, p => p.Contains("claim") || p.Contains("provider"));
    }
}
