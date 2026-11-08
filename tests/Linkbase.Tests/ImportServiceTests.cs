using Linkbase.Data;
using Linkbase.Domains.Climbing.Data;
using Linkbase.Core.Graph;
using Linkbase.Domains.Climbing.Import;
using Microsoft.EntityFrameworkCore;

namespace Linkbase.Tests;

[Trait("Category", "Database")]
[Collection(PostgresCollection.Name)]
public class ImportServiceTests(PostgresFixture fixture)
{
    private static string Unique(string name) => $"{name} {Guid.NewGuid():N}";

    private AscentImportService Importer() => new(fixture.ContextFactory());

    private static ParsedAscent Row(
        string country,
        string crag,
        string route,
        string ascentId,
        DataSource source = DataSource.Ukc,
        string date = "2020-05-01",
        string[]? partners = null) => new()
    {
        Source = source,
        AreaPath = [country],
        CragName = crag,
        RouteName = route,
        RouteExternalId = $"{crag}|{route}".ToLowerInvariant(),
        Grade = "VS 4c",
        GradeSystem = GradeSystem.UkTrad,
        Discipline = Discipline.Trad,
        AscentExternalId = ascentId,
        Date = DateOnly.Parse(date),
        DatePrecision = DatePrecision.Day,
        Style = AscentStyle.Lead,
        StyleRaw = "Lead",
        Partners = partners ?? [],
    };

    [Fact]
    public async Task Creates_the_whole_chain_with_refs_and_a_batch()
    {
        var (country, crag, route, partner) = (Unique("Country"), Unique("Crag"), Unique("Route"), Unique("Partner"));

        var summary = await Importer().ImportAsync(DataSource.Ukc, null, [Row(country, crag, route, Unique("a"), partners: [partner])]);

        Assert.Equal(new ImportCounts(1, 0), summary.Areas);
        Assert.Equal(new ImportCounts(1, 0), summary.Crags);
        Assert.Equal(new ImportCounts(1, 0), summary.Routes);
        Assert.Equal(new ImportCounts(1, 0), summary.Ascents);
        Assert.Equal(1, summary.PartnersCreated);

        await using var db = fixture.CreateContext();
        var ascent = await db.Ascents.AsNoTracking()
            .Where(a => a.Route.Name == route)
            .Select(a => new { a.Route.Crag.Area.Name, a.Route.Crag.Area.Kind, a.ImportBatchId, Partners = a.Partners.Select(p => p.Partner.Name).ToList() })
            .SingleAsync();
        Assert.Equal(country, ascent.Name);
        Assert.Equal(AreaKind.Country, ascent.Kind);
        Assert.Equal(summary.BatchId, ascent.ImportBatchId);
        Assert.Equal([partner], ascent.Partners);

        var payload = await db.ImportBatches.AsNoTracking().Where(b => b.Id == summary.BatchId).Select(b => b.RawPayload).SingleAsync();
        Assert.NotNull(payload);
    }

    [Fact]
    public async Task Importing_the_same_rows_twice_creates_nothing_new()
    {
        var rows = new[] { Row(Unique("Country"), Unique("Crag"), Unique("Route"), Unique("a")) };
        await Importer().ImportAsync(DataSource.Ukc, null, rows);

        var second = await Importer().ImportAsync(DataSource.Ukc, null, rows);

        Assert.Equal(new ImportCounts(0, 1), second.Crags);
        Assert.Equal(new ImportCounts(0, 1), second.Routes);
        Assert.Equal(new ImportCounts(0, 1), second.Ascents);
    }

    [Fact]
    public async Task Rows_on_the_same_route_share_one_route()
    {
        var (country, crag, route) = (Unique("Country"), Unique("Crag"), Unique("Route"));

        var summary = await Importer().ImportAsync(DataSource.Ukc, null, [
            Row(country, crag, route, Unique("a")),
            Row(country, crag, route, Unique("b"), date: "2021-05-01"),
        ]);

        Assert.Equal(new ImportCounts(1, 1), summary.Routes);
        Assert.Equal(2, summary.Ascents.Created);
    }

    [Fact]
    public async Task Same_name_from_another_source_in_the_same_place_is_matched_and_gains_a_ref()
    {
        var (country, crag, route) = (Unique("Country"), Unique("Crag"), Unique("Route"));
        await Importer().ImportAsync(DataSource.Ukc, null, [Row(country, crag, route, Unique("a"))]);

        var summary = await Importer().ImportAsync(
            DataSource.MountainProject,
            null,
            [Row(country, crag, route.ToUpperInvariant(), Unique("b"), DataSource.MountainProject, date: "2021-05-01")]);

        Assert.Equal(new ImportCounts(0, 1), summary.Crags);
        Assert.Equal(new ImportCounts(0, 1), summary.Routes);
        Assert.Equal(1, summary.Ascents.Created);

        await using var db = fixture.CreateContext();
        var routeId = await db.Routes.AsNoTracking().Where(r => r.Name == route).Select(r => r.Id).SingleAsync();
        var sources = await db.ExternalRefs.AsNoTracking()
            .Where(r => r.EntityType == ExternalEntityType.Route && r.EntityId == routeId)
            .Select(r => r.Source)
            .ToListAsync();
        Assert.Equal(2, sources.Count);
    }

    [Fact]
    public async Task Similar_route_names_are_kept_separate()
    {
        var (country, crag) = (Unique("Country"), Unique("Crag"));
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var (one, two) = ($"Copper Slab {suffix}", $"Copper Slab Direct {suffix}");

        var summary = await Importer().ImportAsync(DataSource.Ukc, null, [
            Row(country, crag, one, Unique("a")),
            Row(country, crag, two, Unique("b")),
        ]);

        Assert.Equal(new ImportCounts(2, 0), summary.Routes);
    }

    [Fact]
    public async Task The_same_route_and_date_from_another_source_is_kept_as_its_own_ascent()
    {
        var (country, crag, route) = (Unique("Country"), Unique("Crag"), Unique("Route"));
        await Importer().ImportAsync(DataSource.Ukc, null, [Row(country, crag, route, Unique("a"))]);

        var summary = await Importer().ImportAsync(
            DataSource.MountainProject, null, [Row(country, crag, route, Unique("b"), DataSource.MountainProject)]);

        Assert.Equal(1, summary.Ascents.Created);
        await using var db = fixture.CreateContext();
        Assert.Equal(2, await db.Ascents.CountAsync(a => a.Route.Name == route));
    }

    [Fact]
    public async Task A_failure_rolls_back_the_whole_batch()
    {
        var route = Unique("Route");
        var bad = Row(Unique("Country"), Unique("Crag"), Unique("Route"), Unique("b")) with { AreaPath = [] };

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Importer().ImportAsync(DataSource.Ukc, null, [Row(Unique("Country"), Unique("Crag"), route, Unique("a")), bad]));

        await using var db = fixture.CreateContext();
        Assert.False(await db.Routes.AnyAsync(r => r.Name == route));
    }

    [Fact]
    public async Task Trip_rebuild_links_ascents_and_keeps_ids_and_updated_at_stable()
    {
        var country = Unique("Country");
        await Importer().ImportAsync(DataSource.Ukc, null, [Row(country, Unique("Crag"), Unique("Route"), Unique("a"))]);
        var service = new TripService(fixture.ContextFactory());

        await service.RebuildAsync([]);

        await using var db = fixture.CreateContext();
        var first = await db.Ascents.AsNoTracking()
            .Where(a => a.Route.Crag.Area.Name == country)
            .Select(a => new { a.TripId, a.UpdatedAt, a.CreatedAt, TripName = a.Trip!.Name })
            .SingleAsync();
        Assert.NotNull(first.TripId);
        Assert.StartsWith(country, first.TripName);
        Assert.Equal(first.CreatedAt, first.UpdatedAt);

        await service.RebuildAsync([]);

        var second = await db.Ascents.AsNoTracking()
            .Where(a => a.Route.Crag.Area.Name == country)
            .Select(a => a.TripId)
            .SingleAsync();
        Assert.Equal(first.TripId, second);
    }
}
