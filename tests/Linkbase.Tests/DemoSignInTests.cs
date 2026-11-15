using System.Net;
using System.Text.RegularExpressions;

namespace Linkbase.Tests;

// The demo sign-in page and the response headers, against the real app started with Security:Identity=demo.
[Trait("Category", "Database")]
public sealed partial class DemoSignInTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    private async Task<string> StartAsync(bool withUsers = false)
    {
        var store = withUsers ? new ModelFactory(await fixture.CreateModelDatabaseAsync("SELECT 1")) : await ClimbingStore.CreateAsync(fixture);
        if (withUsers)
        {
            await TestModels.LoadAsync(store, Linkbase.Core.RepoPaths.Resolve("models/consultancy/model.json"), TextWriter.Null);
        }

        var connection = store.ConnectionString;
        return await RunningApp.StartAsync(
            connection,
            "--Security:Identity=demo",
            $"--ConnectionStrings:Consultancy={connection}",
            $"--ConnectionStrings:SwissRail={connection}",
            $"--ConnectionStrings:ClimbingDemo={connection}",
            $"--ConnectionStrings:LinkbaseCode={connection}",
            $"--ConnectionStrings:ArchiMate={connection}");
    }

    [GeneratedRegex(@"name=""__RequestVerificationToken"" value=""(?<tokenValue>[^""]+)""><button name=user value=""(?<user>[^""]+)""")]
    private static partial Regex FirstUser();

    private static HttpClient Client() =>
        new(new HttpClientHandler { AllowAutoRedirect = false, CookieContainer = new CookieContainer(), UseCookies = true });

    [Fact]
    public async Task Signing_in_is_a_post_that_needs_the_anti_forgery_token()
    {
        var url = await StartAsync();
        using var client = Client();

        // A link can no longer sign anyone in: the old address signs nobody in, and sets no sign-in cookie.
        var get = await client.GetAsync($"{url}/signin/as/anyone");
        Assert.False(get.Headers.TryGetValues("Set-Cookie", out var cookies) && cookies.Any(c => c.StartsWith("linkbase-demo-user", StringComparison.Ordinal)));
        Assert.NotEqual("/", get.Headers.Location?.OriginalString);

        // A post with no token is refused.
        var noToken = await client.PostAsync($"{url}/signin/as", new FormUrlEncodedContent([new("user", "anyone")]));
        Assert.Equal(HttpStatusCode.BadRequest, noToken.StatusCode);
    }

    [Fact]
    public async Task A_listed_user_signs_in_with_the_pages_token_and_nobody_else_does()
    {
        var url = await StartAsync(withUsers: true);
        using var client = Client();
        var page = await (await client.GetAsync($"{url}/signin")).Content.ReadAsStringAsync();
        var match = FirstUser().Match(page);
        Assert.True(match.Success, page);
        var token = match.Groups["tokenValue"].Value;

        var stranger = await client.PostAsync($"{url}/signin/as", new FormUrlEncodedContent([new("user", "not-listed"), new("__RequestVerificationToken", token)]));
        Assert.Equal("/signin?error=1", stranger.Headers.Location?.OriginalString);

        var listed = await client.PostAsync($"{url}/signin/as", new FormUrlEncodedContent([new("user", WebUtility.HtmlDecode(match.Groups["user"].Value)), new("__RequestVerificationToken", token)]));
        Assert.Equal(HttpStatusCode.Redirect, listed.StatusCode);
        Assert.Equal("/", listed.Headers.Location?.OriginalString);
        Assert.Contains(listed.Headers.GetValues("Set-Cookie"), c => c.StartsWith("linkbase-demo-user", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Every_response_carries_the_security_headers()
    {
        var url = await StartAsync();
        using var client = Client();

        var response = await client.GetAsync($"{url}/signin");

        Assert.Equal("nosniff", response.Headers.GetValues("X-Content-Type-Options").Single());
        Assert.Equal("DENY", response.Headers.GetValues("X-Frame-Options").Single());
        Assert.Equal("no-referrer", response.Headers.GetValues("Referrer-Policy").Single());
        Assert.Contains("frame-ancestors 'none'", response.Headers.GetValues("Content-Security-Policy").Single());
    }
}
