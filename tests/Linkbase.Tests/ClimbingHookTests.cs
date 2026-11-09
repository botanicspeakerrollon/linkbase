using System.Globalization;
using System.Text.Json.Nodes;
using Linkbase.Core;
using Linkbase.Core.Records;
using Linkbase.Data;
using Linkbase.Domains.Climbing.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace Linkbase.Tests;

// The climbing module's behaviour on the generic store: grade ranks, ascents named for their routes, and trips. Everything
// here is made up. See docs/climbing-to-generic-plan.md.
[Trait("Category", "Database")]
[Collection(PostgresCollection.Name)]
public sealed class ClimbingHookTests(PostgresFixture fixture) : IDisposable
{
    private static readonly string ModelPath = RepoPaths.Resolve("models/climbing/model.json");
    private readonly string _corrections = Path.Combine(Path.GetTempPath(), $"corrections-{Guid.NewGuid():N}.json");

    public void Dispose()
    {
        if (File.Exists(_corrections))
        {
            File.Delete(_corrections);
        }
    }

    private RecordRepository Records(ModelFactory factory)
    {
        File.WriteAllText(_corrections, """{ "partnerRenames": [], "mountainProjectPartners": [], "tripMerges": [] }""");
        var hook = new ClimbingRecordHook(factory, new GenericTripService(factory), () => _corrections, NullLogger<ClimbingRecordHook>.Instance);
        return new RecordRepository(factory, new RecordHooks([hook]));
    }

    private async Task<(ModelFactory Source, ModelFactory Target)> ConvertedAsync()
    {
        var (source, target) = await GenericConversionTests.TypedAsync(fixture);
        await new GenericConverter(source, target).ConvertAsync(ModelPath, TextWriter.Null);
        return (source, target);
    }

    private static async Task<string> IdOfAsync(ModelFactory factory, string type, string name)
    {
        await using var db = factory.CreateDbContext();
        return await db.Nodes.Where(n => n.Type == type && n.Name == name).Select(n => n.Id).SingleAsync();
    }

    private static string Rank(GradeSystem system, string grade) =>
        GradeRanking.Rank(system, grade)!.Value.ToString(CultureInfo.InvariantCulture);

    private static RecordInput Ascent(string routeId, string date) =>
        new("Ascent", new Dictionary<string, string?> { ["date"] = date, ["date_precision"] = "day", ["style"] = "lead", ["source"] = "manual" })
        {
            Links = [new NewLink("OF", routeId)],
        };

    [Fact]
    public async Task Trips_rebuilt_on_the_generic_store_match_the_typed_tables()
    {
        var (source, target) = await ConvertedAsync();

        // The typed tables rebuild their trips, and the generic store does the same from the same ascents.
        await new TripService(source).RebuildAsync([]);
        await new GenericTripService(target).RebuildAsync([]);

        var typed = await ClimbingGraph.ReadAsync(source);
        await using var db = target.CreateDbContext();
        var actual = (await db.Nodes.AsNoTracking().Where(n => n.Type == "Trip").ToListAsync()).OrderBy(n => n.Key).ToList();
        var expected = typed.Nodes.Where(n => n.Type == "Trip").OrderBy(n => n.Key).ToList();
        Assert.NotEmpty(expected);
        Assert.Equal(expected.Select(n => (n.Id, n.Name)), actual.Select(n => (n.Id, n.Name)));
        foreach (var (want, have) in expected.Zip(actual))
        {
            Assert.True(JsonNode.DeepEquals(want.Props, JsonNode.Parse(have.Props)), $"{want.Id}: {have.Props} vs {want.Props.ToJsonString()}");
        }

        var links = (await db.NodeLinks.AsNoTracking().Where(l => l.RelType == "ON_TRIP" || l.RelType == "VISITED").ToListAsync())
            .Select(l => $"{l.SourceId} {l.RelType} {l.TargetId} {JsonNode.Parse(l.Props)!.ToJsonString()}").Order().ToList();
        var wanted = typed.Links.Where(l => l.RelType is "ON_TRIP" or "VISITED").Select(l => $"{l.Signature} {l.Props.ToJsonString()}").Order().ToList();
        Assert.Equal(wanted, links);
    }

    [Fact]
    public async Task A_route_gets_its_grade_rank_when_saved()
    {
        var (_, target) = await ConvertedAsync();
        var records = Records(target);
        var crag = await IdOfAsync(target, "Crag", "Hollow Tor");

        var key = await records.CreateAsync("Route", null, new RecordInput("Hard Corner", new Dictionary<string, string?>
        {
            ["grade"] = "E2 5c",
            ["grade_system"] = "uk_trad",
        }) { Links = [new NewLink("AT", crag)] });

        Assert.Equal(Rank(GradeSystem.UkTrad, "E2 5c"), (await records.GetAsync("Route", key))!.Values["grade_rank"]);

        await records.UpdateAsync("route:" + key, new RecordInput("Hard Corner", new Dictionary<string, string?> { ["grade"] = "6a", ["grade_system"] = "french" }));
        Assert.Equal(Rank(GradeSystem.French, "6a"), (await records.GetAsync("Route", key))!.Values["grade_rank"]);
    }

    [Fact]
    public async Task A_new_ascent_is_named_for_its_route_and_joins_the_trips()
    {
        var (_, target) = await ConvertedAsync();
        var records = Records(target);
        var route = await IdOfAsync(target, "Route", "Slab Happy");

        var key = await records.CreateAsync("Ascent", null, Ascent(route, "2023-03-04"));

        Assert.Equal("Slab Happy", (await records.GetAsync("Ascent", key))!.Name);
        await using var db = target.CreateDbContext();
        var trip = await db.NodeLinks.Where(l => l.SourceId == "ascent:" + key && l.RelType == "ON_TRIP").Select(l => l.TargetId).SingleAsync();
        Assert.StartsWith("trip:", trip);
        Assert.True(await db.NodeLinks.AnyAsync(l => l.SourceId == trip && l.RelType == "VISITED"));
    }

    [Fact]
    public async Task Renaming_a_route_renames_its_ascents_and_moving_an_ascent_moves_its_trip()
    {
        var (_, target) = await ConvertedAsync();
        var records = Records(target);
        var route = await IdOfAsync(target, "Route", "La Bleue");
        var key = await records.CreateAsync("Ascent", null, Ascent(route, "2023-08-01"));
        string before;
        await using (var db = target.CreateDbContext())
        {
            before = await db.NodeLinks.Where(l => l.SourceId == "ascent:" + key && l.RelType == "ON_TRIP").Select(l => l.TargetId).SingleAsync();
        }

        await records.UpdateAsync(route, new RecordInput("La Bleue Renamed", new Dictionary<string, string?> { ["grade"] = "6a+", ["grade_system"] = "french" }));
        await records.UpdateAsync("ascent:" + key, new RecordInput("Ascent", new Dictionary<string, string?>
        {
            ["date"] = "2019-01-05", ["date_precision"] = "day", ["style"] = "lead", ["source"] = "manual",
        }));

        string now;
        await using (var db = target.CreateDbContext())
        {
            Assert.Equal("La Bleue Renamed", (await db.Nodes.SingleAsync(n => n.Id == "ascent:" + key)).Name);
            now = await db.NodeLinks.Where(l => l.SourceId == "ascent:" + key && l.RelType == "ON_TRIP").Select(l => l.TargetId).SingleAsync();
            Assert.NotEqual(before, now);
        }

        // Deleting the only ascent of a trip takes the trip with it.
        await records.DeleteAsync("ascent:" + key);
        await using var last = target.CreateDbContext();
        Assert.False(await last.NodeLinks.AnyAsync(l => l.TargetId == now));
        Assert.False(await last.Nodes.AnyAsync(n => n.Id == now));
    }

    [Fact]
    public async Task Without_the_corrections_file_trips_are_left_alone()
    {
        var (_, target) = await ConvertedAsync();
        var records = Records(target);
        File.Delete(_corrections);
        var route = await IdOfAsync(target, "Route", "Slab Happy");
        int trips;
        await using (var db = target.CreateDbContext())
        {
            trips = await db.Nodes.CountAsync(n => n.Type == "Trip");
        }

        await records.CreateAsync("Ascent", null, Ascent(route, "2023-03-04"));

        await using var after = target.CreateDbContext();
        Assert.Equal(trips, await after.Nodes.CountAsync(n => n.Type == "Trip"));
    }
}
