using System.Net;
using System.Text;
using Linkbase.Core.Security;
using Microsoft.Extensions.Configuration;

namespace Linkbase.Tests;

// Where people's grants come from: the model file, or a directory the app doesn't own, cached for as long as the model says.
public sealed class ViewerDirectoryTests
{
    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 1, 1, 9, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class Stub(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<HttpRequestMessage> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            lock (Requests)
            {
                Requests.Add(request);
            }

            return Task.FromResult(respond(request));
        }
    }

    private static HttpResponseMessage Json(string body, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private static GrantSource Api(string path, int? cacheSeconds = null, string url = "https://dir.example/people/{user}", string? token = null) =>
        new() { Type = GrantSourceType.Api, Url = url, Path = path, CacheSeconds = cacheSeconds, TokenSetting = token };

    private static ModelSecurity Model(GrantSource? level = null, GrantSource? any = null, GrantSource? all = null) => new()
    {
        Level = new SecurityPart { Name = "Class", Values = ["public", "private"], Source = level },
        AnyOf = new SecurityPart { Name = "Dept", Source = any },
        AllOf = new SecurityPart { Name = "Proj", Source = all },
        Users = new Dictionary<string, UserGrants> { ["ann"] = new() { Level = "private", AnyOf = ["Finance"], AllOf = ["X", "Y"] } },
    };

    private static ViewerDirectory Directory(ModelSecurity security, Stub stub, Clock? clock = null, IConfiguration? config = null) =>
        ViewerDirectory.Create(security, new HttpClient(stub), config, clock ?? new Clock());

    [Fact]
    public async Task Grants_in_the_model_file_become_a_viewer()
    {
        var viewer = await ViewerDirectory.Create(Model()).ResolveAsync("ann");

        Assert.Equal(1, viewer.Level);
        Assert.Equal(["Finance"], viewer.AnyOf);
        Assert.Equal(["X", "Y"], viewer.AllOf.Order());
    }

    [Fact]
    public async Task Someone_not_listed_or_not_signed_in_has_nothing()
    {
        var directory = ViewerDirectory.Create(Model());

        foreach (var user in new[] { "unknown", "", null })
        {
            var viewer = await directory.ResolveAsync(user);
            Assert.Equal(-1, viewer.Level);
            Assert.Empty(viewer.AnyOf);
            Assert.Empty(viewer.AllOf);
        }
    }

    [Fact]
    public async Task A_part_the_model_does_not_use_gives_nothing_and_asks_nobody()
    {
        var security = new ModelSecurity { AnyOf = new SecurityPart { Name = "Dept" }, Users = new Dictionary<string, UserGrants> { ["ann"] = new() { AnyOf = ["Finance"] } } };

        var viewer = await ViewerDirectory.Create(security).ResolveAsync("ann");

        Assert.Equal(["Finance"], viewer.AnyOf);
        Assert.Empty(viewer.AllOf);
        Assert.Equal(0, viewer.Level);
    }

    [Fact]
    public async Task Each_part_can_come_from_its_own_source()
    {
        var stub = new Stub(_ => Json("""{ "ndas": ["Harbourline"] }"""));
        var security = Model(all: Api("ndas"));

        var viewer = await Directory(security, stub).ResolveAsync("ann");

        Assert.Equal(1, viewer.Level);          // from the model file
        Assert.Equal(["Finance"], viewer.AnyOf); // from the model file
        Assert.Equal(["Harbourline"], viewer.AllOf); // from the directory
        Assert.Equal("https://dir.example/people/ann", Assert.Single(stub.Requests).RequestUri!.ToString());
    }

    [Fact]
    public async Task Parts_that_share_an_address_make_one_request()
    {
        var stub = new Stub(_ => Json("""{ "level": "private", "data": { "departments": ["Finance", "HR"], "projects": "X" } }"""));
        var security = Model(Api("level"), Api("data.departments"), Api("data.projects"));

        var viewer = await Directory(security, stub).ResolveAsync("ann");

        Assert.Equal(1, viewer.Level);
        Assert.Equal(["Finance", "HR"], viewer.AnyOf.Order());
        Assert.Equal(["X"], viewer.AllOf);
        Assert.Single(stub.Requests);
    }

    [Fact]
    public async Task An_answer_is_kept_for_the_cache_time_and_not_a_moment_longer()
    {
        var stub = new Stub(_ => Json("""{ "d": ["Finance"] }"""));
        var clock = new Clock();
        var directory = Directory(Model(any: Api("d", cacheSeconds: 600)), stub, clock);

        await directory.ResolveAsync("ann");
        clock.Now += TimeSpan.FromSeconds(599);
        await directory.ResolveAsync("ann");
        Assert.Single(stub.Requests);

        clock.Now += TimeSpan.FromSeconds(2);
        await directory.ResolveAsync("ann");
        Assert.Equal(2, stub.Requests.Count);
    }

    [Fact]
    public async Task Any_cache_time_works_including_none_and_a_long_one()
    {
        var none = new Stub(_ => Json("""{ "d": ["Finance"] }"""));
        var noCache = Directory(Model(any: Api("d", cacheSeconds: 0)), none);
        await noCache.ResolveAsync("ann");
        await noCache.ResolveAsync("ann");
        Assert.Equal(2, none.Requests.Count);

        var longStub = new Stub(_ => Json("""{ "d": ["Finance"] }"""));
        var clock = new Clock();
        var kept = Directory(Model(any: Api("d", cacheSeconds: 86_400 * 30)), longStub, clock);
        await kept.ResolveAsync("ann");
        clock.Now += TimeSpan.FromDays(29);
        await kept.ResolveAsync("ann");
        Assert.Single(longStub.Requests);
    }

    [Fact]
    public async Task Different_users_are_cached_separately_and_their_ids_are_escaped()
    {
        var stub = new Stub(r => Json(r.RequestUri!.AbsolutePath.EndsWith("ann", StringComparison.Ordinal) ? """{ "d": ["Finance"] }""" : """{ "d": ["HR"] }"""));
        var directory = Directory(Model(any: Api("d")), stub);

        Assert.Equal(["Finance"], (await directory.ResolveAsync("ann")).AnyOf);
        Assert.Equal(["HR"], (await directory.ResolveAsync("bob smith/../x")).AnyOf);

        Assert.Equal("https://dir.example/people/bob%20smith%2F..%2Fx", stub.Requests[^1].RequestUri!.AbsoluteUri);
    }

    [Fact]
    public async Task A_failing_directory_grants_nothing_and_is_asked_again_next_time()
    {
        var healthy = false;
        var stub = new Stub(_ => healthy ? Json("""{ "d": ["Finance"] }""") : Json("oops", HttpStatusCode.InternalServerError));
        var directory = Directory(Model(any: Api("d", cacheSeconds: 600)), stub);

        Assert.Empty((await directory.ResolveAsync("ann")).AnyOf);

        healthy = true;
        Assert.Equal(["Finance"], (await directory.ResolveAsync("ann")).AnyOf);
        Assert.Equal(2, stub.Requests.Count);
    }

    [Fact]
    public async Task A_directory_that_returns_nonsense_grants_nothing()
    {
        foreach (var body in new[] { "not json", "{}", """{ "d": 5 }""", """{ "d": { "x": 1 } }""", "[1,2]", """{ "d": [1, null, true] }""" })
        {
            var directory = Directory(Model(any: Api("d")), new Stub(_ => Json(body)));
            Assert.Empty((await directory.ResolveAsync("ann")).AnyOf);
        }
    }

    [Fact]
    public async Task A_failed_level_ranks_below_everything_so_the_viewer_sees_nothing()
    {
        var directory = Directory(Model(level: Api("level")), new Stub(_ => Json("{}", HttpStatusCode.ServiceUnavailable)));

        Assert.Equal(-1, (await directory.ResolveAsync("ann")).Level);
    }

    [Fact]
    public async Task A_level_that_is_not_in_the_list_and_values_that_are_not_valid_are_dropped()
    {
        var stub = new Stub(_ => Json("""{ "level": "top secret", "d": ["Finance", "a,b", "{x}", "*", " pad", ""] }"""));
        var viewer = await Directory(Model(Api("level"), Api("d")), stub).ResolveAsync("ann");

        Assert.Equal(-1, viewer.Level);
        Assert.Equal(["Finance"], viewer.AnyOf);
    }

    [Fact]
    public async Task Several_levels_from_a_directory_count_as_the_lowest()
    {
        var stub = new Stub(_ => Json("""{ "level": ["private", "public"] }"""));

        Assert.Equal(0, (await Directory(Model(Api("level")), stub).ResolveAsync("ann")).Level);
    }

    [Fact]
    public async Task The_token_is_read_from_the_settings_and_sent_but_never_from_the_model()
    {
        var stub = new Stub(_ => Json("""{ "d": ["Finance"] }"""));
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Security:Token"] = "s3cret" }).Build();

        await Directory(Model(any: Api("d", token: "Security:Token")), stub, config: config).ResolveAsync("ann");

        var header = Assert.Single(stub.Requests).Headers.Authorization!;
        Assert.Equal("Bearer", header.Scheme);
        Assert.Equal("s3cret", header.Parameter);
    }

    [Fact]
    public async Task A_missing_token_setting_grants_nothing_without_calling_the_directory()
    {
        var stub = new Stub(_ => Json("""{ "d": ["Finance"] }"""));

        var viewer = await Directory(Model(any: Api("d", token: "Security:Token")), stub).ResolveAsync("ann");

        Assert.Empty(viewer.AnyOf);
        Assert.Empty(stub.Requests);
    }

    [Fact]
    public async Task A_context_asks_the_directory_once()
    {
        var stub = new Stub(_ => Json("""{ "d": ["Finance"] }"""));
        var context = new DirectoryViewerContext(Directory(Model(any: Api("d", cacheSeconds: 0)), stub), "ann");

        var first = await context.GetViewerAsync();
        var second = await context.GetViewerAsync();

        Assert.Same(first, second);
        Assert.Single(stub.Requests);
    }

    [Fact]
    public async Task Many_users_at_once_each_cause_one_request()
    {
        var stub = new Stub(_ => Json("""{ "d": ["Finance"] }"""));
        var directory = Directory(Model(any: Api("d")), stub);

        await Task.WhenAll(Enumerable.Range(0, 50).Select(_ => directory.ResolveAsync("ann")));

        Assert.Single(stub.Requests);
    }
}
