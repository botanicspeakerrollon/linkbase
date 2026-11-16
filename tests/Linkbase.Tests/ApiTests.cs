using System.Net;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Text.Encodings.Web;
using System.Text.Json.Nodes;
using Linkbase;
using Linkbase.Core;
using Linkbase.Core.Api;
using Linkbase.Core.Models;
using Linkbase.Core.Security;
using Linkbase.Data;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Linkbase.Tests;

public sealed class ApiKeyTokenTests
{
    [Fact]
    public void A_token_has_a_key_id_and_a_secret_and_only_the_secrets_hash_is_kept()
    {
        var (token, keyId, hash) = ApiKeyToken.Generate();

        Assert.StartsWith("lbk_", token);
        var parsed = ApiKeyToken.Parse(token);
        Assert.Equal(keyId, parsed!.Value.KeyId);
        Assert.Equal(hash, ApiKeyToken.Hash(parsed.Value.Secret));
        Assert.DoesNotContain(parsed.Value.Secret, keyId);
        Assert.NotEqual(token, ApiKeyToken.Generate().Token);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("eyJhbGciOiJSUzI1NiJ9.e30.sig")]
    [InlineData("lbk_")]
    [InlineData("lbk_abc")]
    [InlineData("lbk__secret")]
    [InlineData("lbk_abc_")]
    [InlineData("lbk_a_b_c")]
    public void Anything_not_shaped_like_a_key_is_not_one(string? token) => Assert.Null(ApiKeyToken.Parse(token));
}

// The read API (INT-3) end to end: a real server on a random port, the real security services and a real database. Each test
// loads the consultancy model with row security on, whose users and fields are made up. See docs/history/api-plan.md.
[Trait("Category", "Database")]
public sealed class ApiTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    private static readonly string Consultancy = RepoPaths.Resolve("models/consultancy/model.json");

    private sealed class Factory(string connectionString) : IDbContextFactory<LinkbaseDbContext>
    {
        public LinkbaseDbContext CreateDbContext() => new(new DbContextOptionsBuilder<LinkbaseDbContext>().UseNpgsql(connectionString).Options);
    }

    private sealed class Host(WebApplication app) : IAsyncDisposable
    {
        public HttpClient Client { get; } = new() { BaseAddress = new Uri(app.Urls.First()) };
        public ApiKeyRepository Keys => app.Services.GetRequiredService<ApiKeyRepository>();

        public async Task<string> KeyForAsync(string user, DateTimeOffset? expires = null) => (await Keys.CreateAsync(user, "test", expires)).Token;

        public async Task<HttpResponseMessage> GetAsync(string path, string? token)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, path);
            if (token is not null)
            {
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            }

            return await Client.SendAsync(request);
        }

        public async Task<JsonNode> JsonAsync(string path, string? token, HttpStatusCode expected = HttpStatusCode.OK)
        {
            using var response = await GetAsync(path, token);
            Assert.Equal(expected, response.StatusCode);
            return JsonNode.Parse(await response.Content.ReadAsStringAsync())!;
        }

        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            await app.StopAsync();
            await app.DisposeAsync();
        }
    }

    private async Task<Host> StartAsync(Dictionary<string, string?>? settings = null, Action<IServiceCollection>? configure = null)
    {
        var connectionString = await fixture.CreateModelDatabaseAsync("SELECT 1");
        var factory = new Factory(connectionString);
        await TestModels.LoadAsync(factory, Consultancy, TextWriter.Null);
        await using (var owner = factory.CreateDbContext())
        {
            await RowSecurity.EnableAsync(owner);
        }

        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        var values = new Dictionary<string, string?>
        {
            ["Models:main:Name"] = "Main",
            ["Models:main:Order"] = "0",
            ["ConnectionStrings:main"] = connectionString,
        };
        foreach (var (key, value) in settings ?? [])
        {
            values[key] = value;
        }

        builder.Configuration.AddInMemoryCollection(values);
        builder.Services.AddLinkbaseSecurity("principal");
        builder.Services.AddLinkbaseCore(builder.Configuration);
        builder.Services.AddLinkbaseApi(builder.Configuration);
        configure?.Invoke(builder.Services);
        var app = builder.Build();
        await app.Services.GetRequiredService<ModelCreator>().LoadAsync();
        if (ApiSettings.Enabled(builder.Configuration))
        {
            app.UseAuthentication();
            app.UseAuthorization();
        }

        app.MapLinkbaseApi();
        await app.StartAsync();
        return new Host(app);
    }

    [Fact]
    public async Task A_caller_without_a_valid_key_is_turned_away_without_being_told_why()
    {
        await using var host = await StartAsync();
        var revoked = await host.KeyForAsync("sam.partner");
        var keyId = ApiKeyToken.Parse(revoked)!.Value.KeyId;
        Assert.True(await host.Keys.RevokeAsync(keyId));
        var expired = await host.KeyForAsync("sam.partner", DateTimeOffset.UtcNow.AddMinutes(-1));
        var wrong = revoked[..^4] + "0000";
        var valid = await host.KeyForAsync("sam.partner");

        foreach (var token in new string?[] { null, "", "lbk_nope_nope", wrong, revoked, expired, "not-a-key" })
        {
            using var response = await host.GetAsync("/api/v1/main/records/Project/pr1", token);
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
            Assert.Contains("Bearer", response.Headers.WwwAuthenticate.ToString());
            Assert.NotNull(JsonNode.Parse(await response.Content.ReadAsStringAsync())!["error"]);
        }

        using var ok = await host.GetAsync("/api/v1/main/records/Project/pr1", valid);
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
    }

    [Fact]
    public async Task A_record_comes_with_its_values_and_links()
    {
        await using var host = await StartAsync();
        var token = await host.KeyForAsync("sam.partner");

        var record = await host.JsonAsync("/api/v1/main/records/Project/pr2", token);

        Assert.Equal(("project:pr2", "Project", "pr2", "Booking redesign"), ((string?)record["id"], (string?)record["type"], (string?)record["key"], (string?)record["name"]));
        Assert.Equal("40000", (string?)record["values"]!["budget"]);
        Assert.Empty(record["maskedFields"]!.AsArray());
        var links = record["links"]!.AsArray();
        Assert.NotEmpty(links);
        Assert.All(links, g => Assert.Contains((string?)g!["direction"], new[] { "in", "out" }));
        Assert.Contains(links, g => g!["links"]!.AsArray().Any(l => (string?)l!["name"] is { Length: > 0 }));
    }

    [Fact]
    public async Task Row_and_field_security_apply_to_the_keys_user_as_on_the_pages()
    {
        await using var host = await StartAsync();
        var sam = await host.KeyForAsync("sam.partner");
        var ava = await host.KeyForAsync("ava.lindqvist");
        var guest = await host.KeyForAsync("guest");
        var stranger = await host.KeyForAsync("someone.unlisted");

        // A field the person may not see is masked, and is not in the values.
        var asAva = await host.JsonAsync("/api/v1/main/records/Project/pr2", ava);
        Assert.DoesNotContain("budget", asAva["values"]!.AsObject().Select(p => p.Key));
        Assert.Contains("budget", asAva["maskedFields"]!.AsArray().Select(f => (string?)f));

        // A record they may not see is not there.
        await host.JsonAsync("/api/v1/main/records/Project/pr4", guest, HttpStatusCode.NotFound);
        await host.JsonAsync("/api/v1/main/records/Project/pr1", guest);
        await host.JsonAsync("/api/v1/main/records/Project/pr1", stranger, HttpStatusCode.NotFound);

        // Search shows each person only what they can see.
        var all = await host.JsonAsync("/api/v1/main/search?q=a&type=Project", sam);
        var some = await host.JsonAsync("/api/v1/main/search?q=a&type=Project", guest);
        var names = (JsonNode node) => node["results"]!.AsArray().Select(r => (string?)r!["id"]).ToList();
        Assert.Contains("project:pr4", names(all));
        Assert.DoesNotContain("project:pr4", names(some));
        Assert.Empty(names(await host.JsonAsync("/api/v1/main/search?q=a&type=Project", stranger)));
    }

    [Fact]
    public async Task Search_answers_as_quick_find_does_and_needs_something_to_look_for()
    {
        await using var host = await StartAsync();
        var token = await host.KeyForAsync("sam.partner");

        var found = await host.JsonAsync("/api/v1/main/search?q=timetable", token);
        var first = found["results"]!.AsArray()[0]!;
        Assert.Equal(("project:pr1", "Project", "pr1", "Timetable app"), ((string?)first["id"], (string?)first["type"], (string?)first["key"], (string?)first["name"]));

        await host.JsonAsync("/api/v1/main/search", token, HttpStatusCode.BadRequest);
        await host.JsonAsync("/api/v1/main/search?q=%20", token, HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task An_unknown_model_or_record_is_not_found_and_other_routes_are_not_there()
    {
        await using var host = await StartAsync();
        var token = await host.KeyForAsync("sam.partner");

        await host.JsonAsync("/api/v1/nothing/search?q=a", token, HttpStatusCode.NotFound);
        await host.JsonAsync("/api/v1/main/records/Project/zz9", token, HttpStatusCode.NotFound);
        await host.JsonAsync("/api/v1/main/records/Nothing/pr1", token, HttpStatusCode.NotFound);
        using var write = await host.Client.PostAsync("/api/v1/main/records/Project/pr1", null);
        Assert.NotEqual(HttpStatusCode.OK, write.StatusCode);
    }

    [Fact]
    public async Task A_key_is_stored_hashed_and_its_use_is_noted()
    {
        await using var host = await StartAsync();
        var (token, info) = await host.Keys.CreateAsync("sam.partner", "reporting", null);
        Assert.Null((await host.Keys.ListAsync()).Single().LastUsedAt);

        await host.JsonAsync("/api/v1/main/records/Project/pr1", token);

        var listed = Assert.Single(await host.Keys.ListAsync());
        Assert.Equal((info.KeyId, "sam.partner", "reporting"), (listed.KeyId, listed.UserId, listed.Name));
        Assert.NotNull(listed.LastUsedAt);
        var secret = ApiKeyToken.Parse(token)!.Value.Secret;
        Assert.DoesNotContain(secret, listed.ToString());
    }

    [Fact]
    public async Task The_api_can_be_turned_off()
    {
        // With the API off nothing registers its keys or scheme, so nothing can answer.
        await using var host = await StartAsync(new() { ["Api:Enabled"] = "false" });

        using var response = await host.GetAsync("/api/v1/main/search?q=a", null);
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    // Swapping to OpenID Connect or anything else that sends a bearer token is one registration and one setting. Here the
    // other scheme is a stand-in that trusts a header, to show the endpoints and security don't depend on keys.
    [Fact]
    public async Task Another_scheme_can_take_the_apis_place_by_setting_its_name()
    {
        await using var host = await StartAsync(new() { ["Api:Scheme"] = "Header" }, services =>
            services.AddAuthentication().AddScheme<AuthenticationSchemeOptions, HeaderHandler>("Header", null));
        var key = await host.KeyForAsync("sam.partner");

        // The key no longer opens the API, and the other scheme's caller reads as that user, under the same row security.
        using var refused = await host.GetAsync("/api/v1/main/records/Project/pr1", key);
        Assert.Equal(HttpStatusCode.Unauthorized, refused.StatusCode);
        using var guest = new HttpRequestMessage(HttpMethod.Get, "/api/v1/main/records/Project/pr4");
        guest.Headers.Add("X-Test-User", "guest");
        Assert.Equal(HttpStatusCode.NotFound, (await host.Client.SendAsync(guest)).StatusCode);
        using var sam = new HttpRequestMessage(HttpMethod.Get, "/api/v1/main/records/Project/pr4");
        sam.Headers.Add("X-Test-User", "sam.partner");
        Assert.Equal(HttpStatusCode.OK, (await host.Client.SendAsync(sam)).StatusCode);
    }

    private sealed class HeaderHandler(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            if (!Request.Headers.TryGetValue("X-Test-User", out var user))
            {
                return Task.FromResult(AuthenticateResult.NoResult());
            }

            var identity = new ClaimsIdentity([new Claim("preferred_username", user.ToString())], Scheme.Name);
            return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), Scheme.Name)));
        }
    }
}

[Trait("Category", "Database")]
public sealed class ApiKeyRepositoryTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    private async Task<(ApiKeyRepository Keys, TestClock Clock)> CreateAsync()
    {
        var connection = await fixture.CreateModelDatabaseAsync("SELECT 1");
        var catalog = new ModelCatalog(new Microsoft.Extensions.Configuration.ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Models:main:Name"] = "Main",
            ["Models:main:Order"] = "0",
            ["ConnectionStrings:main"] = connection,
        }).Build(), Linkbase.Domains.RegisteredDomains.Registry);
        var clock = new TestClock();
        return (new ApiKeyRepository(catalog, clock), clock);
    }

    private sealed class TestClock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 11, 16, 10, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }

    [Fact]
    public async Task A_valid_key_names_its_user_and_a_revoked_or_expired_one_names_nobody()
    {
        var (keys, clock) = await CreateAsync();
        var (token, info) = await keys.CreateAsync("  ava.lindqvist ", "report", clock.Now.AddDays(1));

        Assert.Equal("ava.lindqvist", await keys.ValidateAsync(token));
        Assert.Null(await keys.ValidateAsync(token + "0"));
        Assert.Null(await keys.ValidateAsync(token[..^1]));
        clock.Now = clock.Now.AddDays(2);
        Assert.Null(await keys.ValidateAsync(token));

        var (second, secondInfo) = await keys.CreateAsync("ava.lindqvist", "other", null);
        Assert.Equal("ava.lindqvist", await keys.ValidateAsync(second));
        Assert.True(await keys.RevokeAsync(secondInfo.KeyId));
        Assert.False(await keys.RevokeAsync(secondInfo.KeyId));
        Assert.Null(await keys.ValidateAsync(second));
        Assert.Equal([info.KeyId, secondInfo.KeyId], (await keys.ListAsync()).Select(k => k.KeyId));
    }

    [Fact]
    public async Task Use_is_noted_at_most_every_few_minutes()
    {
        var (keys, clock) = await CreateAsync();
        var (token, _) = await keys.CreateAsync("ava.lindqvist", "report", null);
        var first = clock.Now;

        await keys.ValidateAsync(token);
        clock.Now = first.AddMinutes(1);
        await keys.ValidateAsync(token);
        Assert.Equal(first, (await keys.ListAsync()).Single().LastUsedAt);

        clock.Now = first.AddMinutes(6);
        await keys.ValidateAsync(token);
        Assert.Equal(first.AddMinutes(6), (await keys.ListAsync()).Single().LastUsedAt);
    }

    [Fact]
    public async Task A_key_needs_a_user_and_a_name()
    {
        var (keys, _) = await CreateAsync();

        await Assert.ThrowsAsync<LinkbaseValidationException>(() => keys.CreateAsync("", "x", null));
        await Assert.ThrowsAsync<LinkbaseValidationException>(() => keys.CreateAsync("someone", " ", null));
    }

    [Fact]
    public async Task The_commands_make_list_and_revoke_keys_and_show_the_token_once()
    {
        var (keys, clock) = await CreateAsync();
        var output = new StringWriter();
        var commands = new ApiKeyCommands(keys, clock, output);

        Assert.True(ApiKeyCommands.IsCommand(["api-key", "create", "ava", "report", "30"]));
        Assert.False(ApiKeyCommands.IsCommand(["api-key", "frob"]));
        Assert.Equal(0, await commands.RunAsync(["api-key", "create", "ava.lindqvist", "report", "30"]));
        var token = output.ToString().Split('\n').Select(l => l.Trim()).Single(l => l.StartsWith("lbk_", StringComparison.Ordinal));
        Assert.Equal("ava.lindqvist", await keys.ValidateAsync(token));

        output.GetStringBuilder().Clear();
        await commands.RunAsync(["api-key", "list"]);
        Assert.Contains("active", output.ToString());
        Assert.DoesNotContain(token, output.ToString());

        var keyId = ApiKeyToken.Parse(token)!.Value.KeyId;
        Assert.Equal(0, await commands.RunAsync(["api-key", "revoke", keyId]));
        Assert.Equal(1, await commands.RunAsync(["api-key", "revoke", keyId]));
        Assert.Equal(1, await commands.RunAsync(["api-key", "create", "ava.lindqvist", "report", "0"]));
        output.GetStringBuilder().Clear();
        await commands.RunAsync(["api-key", "list"]);
        Assert.Contains("revoked", output.ToString());
    }
}
