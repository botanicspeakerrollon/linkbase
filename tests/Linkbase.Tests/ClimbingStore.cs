using System.Text.Json.Nodes;
using Linkbase.Core;
using Linkbase.Core.Models;
using Linkbase.Data;
using Microsoft.EntityFrameworkCore;

namespace Linkbase.Tests;

// A database of its own with the climbing model loaded and no records, for tests of pages that read and write the generic
// store, and a few made-up places to put in it.
internal static class ClimbingStore
{
    public sealed record Place(string AreaId, string Country, string CragId, string Crag, string RouteId, string Route);

    public static async Task<ModelFactory> CreateAsync(PostgresFixture fixture)
    {
        var store = new ModelFactory(await fixture.CreateModelDatabaseAsync("SELECT 1"));
        await new ModelLoader(store).LoadAsync(RepoPaths.Resolve("models/climbing/model.json"), TextWriter.Null);
        return store;
    }

    // A record, and a link from it if given. Keys are numbers from the store's own count, so they never clash.
    public static async Task<string> AddAsync(ModelFactory store, string type, string name, JsonObject? props = null, string? rel = null, string? target = null)
    {
        await using var db = store.CreateDbContext();
        var key = (db.Nodes.Where(n => n.Type == type).AsEnumerable().Select(n => long.TryParse(n.Key, out var k) ? k : 0).DefaultIfEmpty(0).Max() + 1).ToString();
        var id = $"{type.ToLowerInvariant()}:{key}";
        db.Nodes.Add(new Node { Id = id, UserId = AppUser.DefaultId, Type = type, Key = key, Name = name, Props = (props ?? []).ToJsonString() });
        await db.SaveChangesAsync();
        if (rel is not null)
        {
            await LinkAsync(store, id, rel, target!);
        }

        return id;
    }

    // A link, with the count the home page shows for its relationship.
    public static async Task LinkAsync(ModelFactory store, string source, string rel, string target)
    {
        await using var db = store.CreateDbContext();
        db.NodeLinks.Add(new NodeLink { SourceId = source, RelType = rel, TargetId = target });
        await db.SaveChangesAsync();
        var types = await db.Nodes.Where(n => n.Id == source || n.Id == target).Select(n => new { n.Id, n.Type }).ToDictionaryAsync(n => n.Id, n => n.Type);
        var (from, to) = (types[source], types[target]);
        await db.LinkDefinitions.Where(d => d.SourceType == from && d.RelType == rel && d.TargetType == to)
            .ExecuteUpdateAsync(s => s.SetProperty(d => d.Links, d => d.Links + 1));
    }

    public static Task<string> AddCountryAsync(ModelFactory store, string name) =>
        AddAsync(store, "Area", name, new JsonObject { ["kind"] = "country" });

    public static async Task<Place> AddPlaceAsync(ModelFactory store)
    {
        var (country, crag, route) = ($"Country {Guid.NewGuid():N}", $"Crag {Guid.NewGuid():N}", $"Route {Guid.NewGuid():N}");
        var area = await AddCountryAsync(store, country);
        var cragId = await AddAsync(store, "Crag", crag, null, "IN", area);
        var routeId = await AddAsync(store, "Route", route, null, "AT", cragId);
        return new Place(area, country, cragId, crag, routeId, route);
    }
}
