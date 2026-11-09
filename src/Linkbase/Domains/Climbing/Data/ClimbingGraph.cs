using System.Globalization;
using System.Text.Json.Nodes;
using Linkbase.Data;
using Microsoft.EntityFrameworkCore;

namespace Linkbase.Domains.Climbing.Data;

// A node as the generic store holds it. Props leaves out values that are null, as the model loader does.
public sealed record GraphNodeData(string Id, string Type, string Key, string Name, JsonObject Props, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt);

public sealed record GraphLinkData(string SourceId, string RelType, string TargetId, JsonObject Props)
{
    public string Signature => $"{SourceId} {RelType} {TargetId}";
}

public sealed record GraphRefData(string NodeId, string NodeType, DataSource Source, string ExternalId, string? Url, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt);

// The climbing typed tables as the generic store would hold them: the types and relationships of models/climbing/model.json.
// Node ids stay what the graph views gave them, such as crag:12, so saved scenes and Explore still line up. See
// docs/climbing-to-generic-plan.md.
public sealed class ClimbingGraph
{
    public required IReadOnlyList<GraphNodeData> Nodes { get; init; }
    public required IReadOnlyList<GraphLinkData> Links { get; init; }
    public required IReadOnlyList<GraphRefData> Refs { get; init; }

    public static async Task<ClimbingGraph> ReadAsync(IDbContextFactory<LinkbaseDbContext> factory, CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var nodes = new List<GraphNodeData>();
        var links = new List<GraphLinkData>();
        var empty = () => new JsonObject();

        var areas = await db.Areas.AsNoTracking().OrderBy(a => a.Id)
            .Select(a => new { a.Id, a.ParentId, a.Name, a.Kind, a.CreatedAt, a.UpdatedAt })
            .ToListAsync(cancellationToken);
        foreach (var a in areas)
        {
            nodes.Add(Node("Area", a.Id, a.Name, a.CreatedAt, a.UpdatedAt, ("kind", Choice(a.Kind))));
            if (a.ParentId is { } parent)
            {
                links.Add(new GraphLinkData(Id("Area", a.Id), "IN", Id("Area", parent), empty()));
            }
        }

        foreach (var c in await db.Crags.AsNoTracking().OrderBy(c => c.Id)
                     .Select(c => new { c.Id, c.AreaId, c.Name, c.RockType, c.Lat, c.Lon, c.Aliases, c.Notes, c.CreatedAt, c.UpdatedAt })
                     .ToListAsync(cancellationToken))
        {
            nodes.Add(Node("Crag", c.Id, c.Name, c.CreatedAt, c.UpdatedAt,
                ("rock_type", Text(c.RockType)), ("lat", Number(c.Lat)), ("lon", Number(c.Lon)), ("aliases", List(c.Aliases)), ("notes", Text(c.Notes))));
            links.Add(new GraphLinkData(Id("Crag", c.Id), "IN", Id("Area", c.AreaId), empty()));
        }

        foreach (var r in await db.Routes.AsNoTracking().OrderBy(r => r.Id)
                     .Select(r => new { r.Id, r.CragId, r.Name, r.Grade, r.GradeSystem, r.Discipline, r.LengthM, r.Pitches, r.CreatedAt, r.UpdatedAt })
                     .ToListAsync(cancellationToken))
        {
            nodes.Add(Node("Route", r.Id, r.Name, r.CreatedAt, r.UpdatedAt,
                ("grade", Text(r.Grade)),
                ("grade_system", r.GradeSystem is { } system ? Choice(system) : null),
                ("discipline", r.Discipline is { } discipline ? Choice(discipline) : null),
                ("length_m", Number(r.LengthM)),
                ("pitches", Number(r.Pitches)),
                ("grade_rank", Number(GradeRanking.Rank(r.GradeSystem, r.Grade)))));
            links.Add(new GraphLinkData(Id("Route", r.Id), "AT", Id("Crag", r.CragId), empty()));
        }

        // An ascent's name is its route's.
        var routeNames = await db.Routes.AsNoTracking().ToDictionaryAsync(r => r.Id, r => r.Name, cancellationToken);
        var cragAreas = await db.Crags.AsNoTracking().ToDictionaryAsync(c => c.Id, c => c.AreaId, cancellationToken);
        var routeCrags = await db.Routes.AsNoTracking().ToDictionaryAsync(r => r.Id, r => r.CragId, cancellationToken);
        var ascents = await db.Ascents.AsNoTracking().OrderBy(a => a.Id)
            .Select(a => new { a.Id, a.RouteId, a.Date, a.DatePrecision, a.Style, a.StyleRaw, a.Notes, a.MyRating, a.Source, a.TripId, a.TripInferred, a.CreatedAt, a.UpdatedAt })
            .ToListAsync(cancellationToken);
        foreach (var a in ascents)
        {
            nodes.Add(Node("Ascent", a.Id, routeNames[a.RouteId], a.CreatedAt, a.UpdatedAt,
                ("date", Text(a.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture))),
                ("date_precision", Choice(a.DatePrecision)),
                ("style", Choice(a.Style)),
                ("style_raw", Text(a.StyleRaw)),
                ("notes", Text(a.Notes)),
                ("my_rating", Number(a.MyRating)),
                ("source", Choice(a.Source))));
            links.Add(new GraphLinkData(Id("Ascent", a.Id), "OF", Id("Route", a.RouteId), empty()));
            if (a.TripId is { } trip)
            {
                links.Add(new GraphLinkData(Id("Ascent", a.Id), "ON_TRIP", Id("Trip", trip), new JsonObject { ["inferred"] = a.TripInferred }));
            }
        }

        foreach (var p in await db.Partners.AsNoTracking().OrderBy(p => p.Id)
                     .Select(p => new { p.Id, p.Name, p.CreatedAt, p.UpdatedAt })
                     .ToListAsync(cancellationToken))
        {
            nodes.Add(Node("Partner", p.Id, p.Name, p.CreatedAt, p.UpdatedAt));
        }

        foreach (var ap in await db.Set<AscentPartner>().AsNoTracking().OrderBy(ap => ap.AscentId).ThenBy(ap => ap.PartnerId)
                     .Select(ap => new { ap.AscentId, ap.PartnerId })
                     .ToListAsync(cancellationToken))
        {
            links.Add(new GraphLinkData(Id("Ascent", ap.AscentId), "WITH", Id("Partner", ap.PartnerId), empty()));
        }

        foreach (var t in await db.Trips.AsNoTracking().OrderBy(t => t.Id)
                     .Select(t => new { t.Id, t.Key, t.Name, t.StartDate, t.EndDate, t.SortDate, t.Notes, t.Abroad, t.Home, t.Approximate, t.DatesPartlyUnknown, t.Manual, t.CreatedAt, t.UpdatedAt })
                     .ToListAsync(cancellationToken))
        {
            nodes.Add(Node("Trip", t.Id, t.Name, t.CreatedAt, t.UpdatedAt,
                ("trip_key", Text(t.Key)),
                ("start_date", Date(t.StartDate)),
                ("end_date", Date(t.EndDate)),
                ("sort_date", Date(t.SortDate)),
                ("notes", Text(t.Notes)),
                ("abroad", Bool(t.Abroad)),
                ("home", Bool(t.Home)),
                ("approximate", Bool(t.Approximate)),
                ("dates_partly_unknown", Bool(t.DatesPartlyUnknown)),
                ("manual", Bool(t.Manual))));
        }

        // A trip visits the countries its ascents were in. A country is the top of an area's parents.
        var parents = areas.ToDictionary(a => a.Id, a => a.ParentId);
        long CountryOf(long areaId)
        {
            while (parents[areaId] is { } parent)
            {
                areaId = parent;
            }

            return areaId;
        }

        var visited = ascents.Where(a => a.TripId is not null)
            .Select(a => (Trip: a.TripId!.Value, Country: CountryOf(cragAreas[routeCrags[a.RouteId]])))
            .Distinct()
            .OrderBy(v => v.Trip).ThenBy(v => v.Country);
        foreach (var (trip, country) in visited)
        {
            links.Add(new GraphLinkData(Id("Trip", trip), "VISITED", Id("Area", country), empty()));
        }

        var refs = new List<GraphRefData>();
        foreach (var r in await db.ExternalRefs.AsNoTracking()
                     .Where(r => r.EntityType != null && r.EntityId != null)
                     .OrderBy(r => r.Id)
                     .Select(r => new { r.EntityType, r.EntityId, r.Source, r.ExternalId, r.Url, r.CreatedAt, r.UpdatedAt })
                     .ToListAsync(cancellationToken))
        {
            var type = r.EntityType!.Value.ToString();
            refs.Add(new GraphRefData(Id(type, r.EntityId!.Value), type, r.Source, r.ExternalId, r.Url, r.CreatedAt, r.UpdatedAt));
        }

        return new ClimbingGraph { Nodes = nodes, Links = links, Refs = refs };
    }

    // The id the graph views gave a row, such as crag:12. Also the generic store's id for the node.
    public static string Id(string type, long id) => $"{type.ToLowerInvariant()}:{id}";

    private static GraphNodeData Node(string type, long id, string name, DateTimeOffset created, DateTimeOffset updated, params (string Key, JsonNode? Value)[] values)
    {
        var props = new JsonObject();
        foreach (var (key, value) in values)
        {
            if (value is not null)
            {
                props[key] = value;
            }
        }

        return new GraphNodeData(Id(type, id), type, id.ToString(CultureInfo.InvariantCulture), name, props, created, updated);
    }

    private static JsonNode Bool(bool value) => JsonValue.Create(value);

    private static JsonNode? Text(string? value) => string.IsNullOrEmpty(value) ? null : JsonValue.Create(value);

    private static JsonNode? Number(double? value) => value is { } v ? JsonValue.Create(v) : null;

    private static JsonNode? Number(int? value) => value is { } v ? JsonValue.Create(v) : null;

    private static JsonNode? Number(short? value) => value is { } v ? JsonValue.Create((int)v) : null;

    private static JsonNode? Date(DateOnly? value) => value is { } v ? JsonValue.Create(v.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)) : null;

    private static JsonNode? List(IReadOnlyCollection<string> values) =>
        values.Count == 0 ? null : new JsonArray(values.Select(v => (JsonNode?)JsonValue.Create(v)).ToArray());

    private static JsonNode Choice<T>(T value) where T : struct, Enum => JsonValue.Create(SnakeCaseEnumConverter<T>.ToStore(value))!;
}
