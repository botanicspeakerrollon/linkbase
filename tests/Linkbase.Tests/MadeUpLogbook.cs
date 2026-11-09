using System.Text.Json.Nodes;
using Linkbase.Data;
using Linkbase.Domains.Climbing.Data;
using Microsoft.EntityFrameworkCore;

namespace Linkbase.Tests;

// A made-up climbing logbook in the generic store, in a database of its own: two countries, a region, two crags (one with
// aliases), three routes, four ascents on two trips (one link inferred), a partner on two ascents, and ids from UKC and
// Mountain Project. Nothing in it comes from a real logbook.
internal static class MadeUpLogbook
{
    public static async Task<ModelFactory> CreateAsync(PostgresFixture fixture)
    {
        var store = await ClimbingStore.CreateAsync(fixture);
        var links = new List<(string Source, string Rel, string Target, JsonObject? Props)>();

        async Task<string> Node(string type, string name, JsonObject props, string? rel = null, string? target = null)
        {
            var id = await ClimbingStore.AddAsync(store, type, name, props);
            if (rel is not null)
            {
                links.Add((id, rel, target!, null));
            }

            return id;
        }

        var wessex = await Node("Area", "Wessex", new JsonObject { ["kind"] = "country" });
        var gaule = await Node("Area", "Gaule", new JsonObject { ["kind"] = "country" });
        var downs = await Node("Area", "The Downs", new JsonObject { ["kind"] = "region" }, "IN", wessex);
        var tor = await Node("Crag", "Hollow Tor", new JsonObject
        {
            ["rock_type"] = "granite",
            ["lat"] = 51.5,
            ["lon"] = -1.25,
            ["aliases"] = new JsonArray("Hollow", "The Tor"),
            ["notes"] = "Quiet",
        }, "IN", downs);
        var cliffs = await Node("Crag", "Les Falaises", [], "IN", gaule);
        var crack = await Node("Route", "Crack of Dawn", Route("E1 5b", "uk_trad", "trad", 30, 1), "AT", tor);
        var slab = await Node("Route", "Slab Happy", Route("VS 4c", "uk_trad", "trad", null, null), "AT", tor);
        var blue = await Node("Route", "La Bleue", Route("6a+", "french", "sport", null, null), "AT", cliffs);
        var sam = await Node("Partner", "Sam Example", []);

        var spring = await Node("Trip", "Wessex, May 2020", new JsonObject
        {
            ["trip_key"] = "Wessex 2020-05-02",
            ["start_date"] = "2020-05-02",
            ["end_date"] = "2020-05-03",
            ["sort_date"] = "2020-05-02",
            ["home"] = true,
        });
        var summer = await Node("Trip", "Gaule, July 2021", new JsonObject
        {
            ["trip_key"] = "Gaule 2021-07-01 (dates unknown)",
            ["sort_date"] = "2021-07-01",
            ["abroad"] = true,
            ["approximate"] = true,
            ["dates_partly_unknown"] = true,
        });
        links.Add((spring, "VISITED", wessex, null));
        links.Add((summer, "VISITED", gaule, null));

        var first = await Node("Ascent", "Crack of Dawn", Ascent("2020-05-02", "day", "onsight", "Onsight", "Cold hands", 3, "ukc"), "OF", crack);
        var second = await Node("Ascent", "Slab Happy", Ascent("2020-05-03", "day", "redpoint", null, null, null, "ukc"), "OF", slab);
        var third = await Node("Ascent", "La Bleue", Ascent("2021-07-01", "month", "top_rope", null, null, null, "mountain_project"), "OF", blue);
        var fourth = await Node("Ascent", "Crack of Dawn", Ascent("2022-01-01", "year", "lead", null, null, null, "manual"), "OF", crack);
        links.Add((first, "ON_TRIP", spring, new JsonObject { ["inferred"] = false }));
        links.Add((second, "ON_TRIP", spring, new JsonObject { ["inferred"] = true }));
        links.Add((third, "ON_TRIP", summer, new JsonObject { ["inferred"] = false }));
        links.Add((first, "WITH", sam, null));
        links.Add((third, "WITH", sam, null));

        await using var db = store.CreateDbContext();
        foreach (var (source, rel, target, props) in links)
        {
            db.NodeLinks.Add(new NodeLink { SourceId = source, RelType = rel, TargetId = target, Props = (props ?? []).ToJsonString() });
        }

        db.ExternalRefs.Add(new ExternalRef { NodeId = tor, NodeType = "Crag", Source = DataSource.Ukc, ExternalId = "1234", Url = "https://example.test/crag/1234" });
        db.ExternalRefs.Add(new ExternalRef { NodeId = third, NodeType = "Ascent", Source = DataSource.MountainProject, ExternalId = "tick-9" });
        await db.SaveChangesAsync();

        // The relationship counts the home page shows.
        foreach (var group in await db.NodeLinks.GroupBy(l => new { SourceType = l.Source.Type, l.RelType, TargetType = l.Target.Type })
                     .Select(g => new { g.Key.SourceType, g.Key.RelType, g.Key.TargetType, Count = g.Count() })
                     .ToListAsync())
        {
            var count = (long)group.Count;
            await db.LinkDefinitions
                .Where(d => d.SourceType == group.SourceType && d.RelType == group.RelType && d.TargetType == group.TargetType)
                .ExecuteUpdateAsync(s => s.SetProperty(d => d.Links, count));
        }

        return store;
    }

    private static JsonObject Route(string grade, string system, string discipline, int? length, int? pitches)
    {
        var props = new JsonObject { ["grade"] = grade, ["grade_system"] = system, ["discipline"] = discipline };
        if (length is { } l)
        {
            props["length_m"] = l;
        }

        if (pitches is { } p)
        {
            props["pitches"] = p;
        }

        if (GradeRanking.Rank(SnakeCaseEnumConverter<GradeSystem>.FromStore(system), grade) is { } rank)
        {
            props["grade_rank"] = rank;
        }

        return props;
    }

    private static JsonObject Ascent(string date, string precision, string style, string? styleRaw, string? notes, int? rating, string source)
    {
        var props = new JsonObject { ["date"] = date, ["date_precision"] = precision, ["style"] = style, ["source"] = source };
        if (styleRaw is not null)
        {
            props["style_raw"] = styleRaw;
        }

        if (notes is not null)
        {
            props["notes"] = notes;
        }

        if (rating is { } r)
        {
            props["my_rating"] = r;
        }

        return props;
    }
}
