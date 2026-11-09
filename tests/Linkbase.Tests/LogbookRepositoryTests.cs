using Linkbase.Core;
using Linkbase.Data;
using Linkbase.Domains.Climbing.Data;
using Microsoft.EntityFrameworkCore;

namespace Linkbase.Tests;

// The Logbook's queries over the generic store, on the made-up logbook from GenericConversionTests.
[Trait("Category", "Database")]
public sealed class LogbookRepositoryTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    private async Task<(LogbookRepository Logbook, ModelFactory Target)> ConvertedAsync()
    {
        var target = await MadeUpLogbook.CreateAsync(fixture);
        return (new LogbookRepository(target), target);
    }

    private static async Task<string> IdOfAsync(ModelFactory factory, string type, string name)
    {
        await using var db = factory.CreateDbContext();
        return await db.Nodes.Where(n => n.Type == type && n.Name == name).Select(n => n.Id).SingleAsync();
    }

    [Fact]
    public async Task Lists_every_ascent_with_its_route_crag_partners_and_trip()
    {
        var (logbook, _) = await ConvertedAsync();

        var page = await logbook.SearchAsync(new LogbookFilter(), AscentSort.Date, true, 0, 10);

        Assert.Equal(4, page.Total);
        Assert.Equal(["Lead", "TopRope", "Redpoint", "Onsight"], page.Rows.Select(r => r.Style.ToString()));
        var first = page.Rows.Single(r => r.Style == AscentStyle.Onsight);
        Assert.Equal(("Crack of Dawn", "E1 5b", GradeSystem.UkTrad, Discipline.Trad, "Hollow Tor"), (first.RouteName, first.Grade, first.GradeSystem, first.Discipline, first.CragName));
        Assert.Equal(new DateOnly(2020, 5, 2), first.Date);
        Assert.Equal(DatePrecision.Day, first.DatePrecision);
        Assert.Equal(["Sam Example"], first.Partners);
        Assert.Equal("Cold hands", first.Notes);
        Assert.Equal(3, first.MyRating);
        Assert.Equal("Onsight", first.StyleRaw);
        Assert.Empty(page.Rows.Single(r => r.Style == AscentStyle.Lead).Partners);
    }

    [Fact]
    public async Task Pages_and_sorts()
    {
        var (logbook, _) = await ConvertedAsync();

        var second = await logbook.SearchAsync(new LogbookFilter(), AscentSort.Date, true, 1, 1);
        Assert.Equal(4, second.Total);
        Assert.Equal(AscentStyle.TopRope, Assert.Single(second.Rows).Style);

        async Task<string[]> Names(AscentSort sort, bool descending) =>
            (await logbook.SearchAsync(new LogbookFilter(), sort, descending, 0, 10)).Rows.Select(r => r.RouteName).ToArray();

        Assert.Equal("Crack of Dawn", (await Names(AscentSort.Route, false))[0]);
        Assert.Equal("Slab Happy", (await Names(AscentSort.Route, true))[0]);
        Assert.Equal("Hollow Tor", (await logbook.SearchAsync(new LogbookFilter(), AscentSort.Crag, false, 0, 10)).Rows[0].CragName);
        Assert.Equal("Les Falaises", (await logbook.SearchAsync(new LogbookFilter(), AscentSort.Crag, true, 0, 10)).Rows[0].CragName);
        Assert.Equal(AscentStyle.Lead, (await logbook.SearchAsync(new LogbookFilter(), AscentSort.Style, false, 0, 10)).Rows[0].Style);
        Assert.Equal(AscentStyle.TopRope, (await logbook.SearchAsync(new LogbookFilter(), AscentSort.Style, true, 0, 10)).Rows[0].Style);
        Assert.Equal(AscentStyle.Onsight, (await logbook.SearchAsync(new LogbookFilter(), AscentSort.Date, false, 0, 10)).Rows[0].Style);
    }

    [Fact]
    public async Task Filters_by_area_including_everything_below_it_crag_dates_grade_discipline_and_style()
    {
        var (logbook, target) = await ConvertedAsync();
        var country = await IdOfAsync(target, "Area", "Wessex");
        var region = await IdOfAsync(target, "Area", "The Downs");
        var france = await IdOfAsync(target, "Area", "Gaule");
        var tor = await IdOfAsync(target, "Crag", "Hollow Tor");

        async Task<int> Count(LogbookFilter filter) => (await logbook.SearchAsync(filter, AscentSort.Date, true, 0, 10)).Total;

        Assert.Equal(3, await Count(new LogbookFilter { AreaId = country }));
        Assert.Equal(3, await Count(new LogbookFilter { AreaId = region }));
        Assert.Equal(1, await Count(new LogbookFilter { AreaId = france }));
        Assert.Equal(3, await Count(new LogbookFilter { CragId = tor }));
        Assert.Equal(2, await Count(new LogbookFilter { From = new DateOnly(2020, 5, 3), To = new DateOnly(2021, 12, 31) }));
        Assert.Equal(3, await Count(new LogbookFilter { GradeSystem = GradeSystem.UkTrad }));
        Assert.Equal(1, await Count(new LogbookFilter { GradeSystem = GradeSystem.French, Discipline = Discipline.Sport }));
        Assert.Equal(0, await Count(new LogbookFilter { GradeSystem = GradeSystem.French, Discipline = Discipline.Trad }));
        Assert.Equal(1, await Count(new LogbookFilter { AreaId = country, Style = AscentStyle.Redpoint }));
    }

    [Fact]
    public async Task Summarises_years_disciplines_and_the_hardest_clean_climb_of_each_system()
    {
        var (logbook, _) = await ConvertedAsync();

        var stats = await logbook.StatsAsync(new LogbookFilter());

        Assert.Equal(4, stats.Total);
        Assert.Equal([(2022, 1), (2021, 1), (2020, 2)], stats.PerYear.Select(y => (y.Year, y.Ascents)));
        Assert.Equal((Discipline.Trad, 3), (stats.ByDiscipline[0].Discipline!.Value, stats.ByDiscipline[0].Ascents));
        Assert.Contains(stats.ByDiscipline, d => d is { Discipline: Discipline.Sport, Ascents: 1 });

        // La Bleue was top-roped, which isn't clean, so France has no entry.
        var hardest = Assert.Single(stats.Hardest);
        Assert.Equal((GradeSystem.UkTrad, "E1 5b", "Crack of Dawn", 2), (hardest.System, hardest.Grade, hardest.RouteName, hardest.Ascents));
    }

    [Fact]
    public async Task An_empty_store_has_an_empty_logbook()
    {
        var logbook = new LogbookRepository(new ModelFactory(await fixture.CreateModelDatabaseAsync("SELECT 1")));

        Assert.Equal(0, (await logbook.SearchAsync(new LogbookFilter(), AscentSort.Date, true, 0, 10)).Total);
        Assert.Equal(0, (await logbook.StatsAsync(new LogbookFilter())).Total);
    }
}
