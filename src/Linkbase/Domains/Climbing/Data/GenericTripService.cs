using System.Globalization;
using System.Text.Json.Nodes;
using Linkbase.Core.Records;
using Linkbase.Data;
using Linkbase.Domains.Climbing.Import;
using Microsoft.EntityFrameworkCore;

namespace Linkbase.Domains.Climbing.Data;

public sealed record TripRebuildSummary(int Trips, int LinkedAscents, int InferredLinks);

// Works out the trips for the generic store: works out the trips from the Ascent records and replaces the Trip records and their
// links. An ascent's country is the top of the areas above its route's crag. A trip keeps its node key across rebuilds,
// found by its trip_key field, so its id and any saved view stay the same. New trips take the next number.
public sealed class GenericTripService(IDbContextFactory<LinkbaseDbContext> contextFactory)
{
    public async Task<TripRebuildSummary> RebuildAsync(IReadOnlyList<TripMerge> merges, CancellationToken cancellationToken = default)
    {
        var records = new RecordRepository(contextFactory, RecordHooks.None, Linkbase.Core.Security.FieldPolicy.Unrestricted);
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);

        var areaParents = FirstTargets(await db.NodeLinks.AsNoTracking()
            .Where(l => l.RelType == "IN" && l.Source.Type == "Area")
            .OrderBy(l => l.SourceId).ThenBy(l => l.TargetId)
            .Select(l => new LinkEnds(l.SourceId, l.TargetId))
            .ToListAsync(cancellationToken));
        var areaNames = await db.Nodes.AsNoTracking().Where(n => n.Type == "Area").ToDictionaryAsync(n => n.Id, n => n.Name, cancellationToken);
        var cragAreas = FirstTargets(await db.NodeLinks.AsNoTracking()
            .Where(l => l.RelType == "IN" && l.Source.Type == "Crag")
            .OrderBy(l => l.SourceId).ThenBy(l => l.TargetId)
            .Select(l => new LinkEnds(l.SourceId, l.TargetId))
            .ToListAsync(cancellationToken));
        var routeCrags = FirstTargets(await db.NodeLinks.AsNoTracking()
            .Where(l => l.RelType == "AT" && l.Source.Type == "Route")
            .OrderBy(l => l.SourceId).ThenBy(l => l.TargetId)
            .Select(l => new LinkEnds(l.SourceId, l.TargetId))
            .ToListAsync(cancellationToken));
        var ascentRoutes = FirstTargets(await db.NodeLinks.AsNoTracking()
            .Where(l => l.RelType == "OF" && l.Source.Type == "Ascent")
            .OrderBy(l => l.SourceId).ThenBy(l => l.TargetId)
            .Select(l => new LinkEnds(l.SourceId, l.TargetId))
            .ToListAsync(cancellationToken));
        var dated = await db.Nodes.AsNoTracking().Where(n => n.Type == "Ascent").OrderBy(n => n.Id).Select(n => new { n.Id, n.Props }).ToListAsync(cancellationToken);

        string CountryOf(string areaId)
        {
            while (areaParents.TryGetValue(areaId, out var parent))
            {
                areaId = parent;
            }

            return areaNames[areaId];
        }

        // The builder works in numbers, so each ascent is its position in this list.
        var ascents = new List<(string Id, TripAscent Ascent)>();
        foreach (var node in dated)
        {
            var props = JsonNode.Parse(node.Props) as JsonObject ?? [];
            if (!ascentRoutes.TryGetValue(node.Id, out var route) || !routeCrags.TryGetValue(route, out var crag) || !cragAreas.TryGetValue(crag, out var area)
                || !DateOnly.TryParseExact((string?)props["date"], "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
            {
                continue;
            }

            var precision = SnakeCaseEnumConverter<DatePrecision>.TryFromStore((string?)props["date_precision"], out var found) ? found : DatePrecision.Day;
            ascents.Add((node.Id, new TripAscent(ascents.Count, CountryOf(area), date, precision)));
        }

        var plan = TripBuilder.Build(ascents.Select(a => a.Ascent).ToList(), merges);

        // A trip that was there keeps its key, found by its trip_key.
        var existing = new Dictionary<string, string>();
        var highest = 0L;
        foreach (var trip in await db.Nodes.AsNoTracking().Where(n => n.Type == "Trip").Select(n => new { n.Key, n.Props }).ToListAsync(cancellationToken))
        {
            if (long.TryParse(trip.Key, CultureInfo.InvariantCulture, out var number))
            {
                highest = Math.Max(highest, number);
            }

            if ((JsonNode.Parse(trip.Props) as JsonObject)?["trip_key"] is { } tripKey)
            {
                existing[(string)tripKey!] = trip.Key;
            }
        }

        var derived = new List<DerivedRecord>();
        var links = new List<DerivedLink>();
        foreach (var planned in plan)
        {
            var key = existing.TryGetValue(planned.Key, out var kept) ? kept : (++highest).ToString(CultureInfo.InvariantCulture);
            derived.Add(new DerivedRecord(key, planned.Name, new Dictionary<string, string?>
            {
                ["trip_key"] = planned.Key,
                ["start_date"] = Date(planned.StartDate),
                ["end_date"] = Date(planned.EndDate),
                ["sort_date"] = Date(planned.SortDate),
                ["notes"] = planned.Notes,
                ["abroad"] = Bool(planned.Abroad),
                ["home"] = Bool(planned.Home),
                ["approximate"] = Bool(planned.Approximate),
                ["dates_partly_unknown"] = Bool(planned.DatesPartlyUnknown),
                ["manual"] = Bool(planned.Manual),
            }));

            var inferred = planned.InferredAscentIds.ToHashSet();
            foreach (var id in planned.AscentIds)
            {
                links.Add(new DerivedLink(ascents[(int)id].Id, "ON_TRIP", $"trip:{key}", new JsonObject { ["inferred"] = inferred.Contains(id) }));
            }

            foreach (var country in planned.AscentIds
                         .Select(id => CountryId(ascents[(int)id].Id))
                         .Distinct()
                         .Order())
            {
                links.Add(new DerivedLink($"trip:{key}", "VISITED", country));
            }
        }

        string CountryId(string ascentId)
        {
            var area = cragAreas[routeCrags[ascentRoutes[ascentId]]];
            while (areaParents.TryGetValue(area, out var parent))
            {
                area = parent;
            }

            return area;
        }

        await records.ReplaceDerivedAsync("Climbing", "Trip", derived, links, cancellationToken);
        return new TripRebuildSummary(plan.Count, plan.Sum(p => p.AscentIds.Count), plan.Sum(p => p.InferredAscentIds.Count));
    }

    private sealed record LinkEnds(string SourceId, string TargetId);

    // An item can have more than one link of a kind, for example an area filed under two parents. The lowest target wins, so
    // rebuilds agree with each other.
    private static Dictionary<string, string> FirstTargets(IEnumerable<LinkEnds> links)
    {
        var map = new Dictionary<string, string>();
        foreach (var link in links)
        {
            map.TryAdd(link.SourceId, link.TargetId);
        }

        return map;
    }

    private static string? Date(DateOnly? date) => date?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    private static string Bool(bool value) => value ? "true" : "false";
}
