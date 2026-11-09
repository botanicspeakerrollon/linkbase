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
        return new ModelFactory(await fixture.CreateClimbingDatabaseAsync());
    }

    // A record, and a link from it if given. Keys are numbers, the next for the type in this store.
    public static async Task<string> AddAsync(ModelFactory store, string type, string name, JsonObject? props = null, string? rel = null, string? target = null)
    {
        var id = (await AddManyAsync(store, type, [name], props, rel, target)).Single();
        return id;
    }

    // Several records of one type in one go, each with the same props and, if given, a link to the same target. Returns their ids.
    public static async Task<IReadOnlyList<string>> AddManyAsync(
        ModelFactory store, string type, IEnumerable<string> names, JsonObject? props = null, string? rel = null, string? target = null)
    {
        await using var db = store.CreateDbContext();
        var last = await db.Database
            .SqlQuery<long>($"SELECT coalesce(max(key::bigint), 0) AS \"Value\" FROM node WHERE type = {type} AND key ~ '^[0-9]+$'")
            .SingleAsync();
        var text = (props ?? []).ToJsonString();
        var added = names.Select((name, i) => new Node
        {
            Id = $"{type.ToLowerInvariant()}:{last + 1 + i}",
            UserId = AppUser.DefaultId,
            Type = type,
            Key = (last + 1 + i).ToString(System.Globalization.CultureInfo.InvariantCulture),
            Name = name,
            Props = text,
        }).ToList();
        db.Nodes.AddRange(added);
        await db.SaveChangesAsync();
        if (rel is not null)
        {
            db.NodeLinks.AddRange(added.Select(n => new NodeLink { SourceId = n.Id, RelType = rel, TargetId = target! }));
            await db.SaveChangesAsync();
            var targetType = await db.Nodes.Where(n => n.Id == target).Select(n => n.Type).SingleAsync();
            var count = (long)added.Count;
            await db.LinkDefinitions.Where(d => d.SourceType == type && d.RelType == rel && d.TargetType == targetType)
                .ExecuteUpdateAsync(s => s.SetProperty(d => d.Links, d => d.Links + count));
        }

        return added.Select(n => n.Id).ToList();
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
