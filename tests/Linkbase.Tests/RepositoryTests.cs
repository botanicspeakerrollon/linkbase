using Linkbase.Data;
using Linkbase.Domains.Climbing.Data;
using Linkbase.Core.Graph;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace Linkbase.Tests;

[Trait("Category", "Database")]
[Collection(PostgresCollection.Name)]
public class RepositoryTests(PostgresFixture fixture)
{
    private static string Unique(string name) => $"{name} {Guid.NewGuid():N}";

    private TripRefresher Refresher() => new(
        new TripService(fixture.ContextFactory()),
        () => "no-corrections-in-tests.json",
        NullLogger<TripRefresher>.Instance);

    private AreaRepository Areas() => new(fixture.ContextFactory());
    private AscentRepository Ascents() => new(fixture.ContextFactory(), Areas(), Refresher());
    private CragRepository Crags() => new(fixture.ContextFactory(), Refresher());
    private RouteRepository Routes() => new(fixture.ContextFactory(), Refresher());

    private static AscentInput NewAscent(string route, string crag, string country, string date = "2020-05-02", AscentStyle style = AscentStyle.Lead) => new()
    {
        NewRoute = new NewRouteInput(route, "VS 4c", GradeSystem.UkTrad, Discipline.Trad, 20, 1, null, new NewCragInput(crag, null, country)),
        Date = DateOnly.Parse(date),
        DatePrecision = DatePrecision.Day,
        Style = style,
    };

    private async Task<(long AscentId, long RouteId, long CragId, long AreaId)> CreateAsync(AscentInput input)
    {
        var id = await Ascents().CreateAsync(input);
        await using var db = fixture.CreateContext();
        var row = await db.Ascents.AsNoTracking().Where(a => a.Id == id)
            .Select(a => new { a.RouteId, a.Route.CragId, a.Route.Crag.AreaId })
            .SingleAsync();
        return (id, row.RouteId, row.CragId, row.AreaId);
    }

    [Fact]
    public async Task Create_can_make_a_new_country_crag_route_and_partners_in_one_go()
    {
        var (route, crag, country, partner) = (Unique("Route"), Unique("Crag"), Unique("Country"), Unique("Partner"));
        var created = await CreateAsync(NewAscent(route, crag, country) with { Partners = [partner, partner, " "], Notes = "  Great  ", MyRating = 3 });

        var edit = await Ascents().GetForEditAsync(created.AscentId);

        Assert.NotNull(edit);
        Assert.Equal(route, edit.RouteName);
        Assert.Equal(crag, edit.CragName);
        Assert.Equal([partner], edit.Partners);
        Assert.Equal("Great", edit.Notes);
        Assert.Equal((short)3, edit.MyRating);

        await using var db = fixture.CreateContext();
        var source = await db.Ascents.AsNoTracking().Where(a => a.Id == created.AscentId).Select(a => a.Source).SingleAsync();
        Assert.Equal(DataSource.Manual, source);
    }

    [Fact]
    public async Task Partial_dates_are_stored_as_the_first_of_the_period()
    {
        var month = await CreateAsync(NewAscent(Unique("R"), Unique("C"), Unique("Country")) with
        {
            Date = new DateOnly(2020, 5, 17),
            DatePrecision = DatePrecision.Month,
        });
        var year = await CreateAsync(NewAscent(Unique("R"), Unique("C"), Unique("Country")) with
        {
            Date = new DateOnly(2020, 5, 17),
            DatePrecision = DatePrecision.Year,
        });

        Assert.Equal(new DateOnly(2020, 5, 1), (await Ascents().GetForEditAsync(month.AscentId))!.Date);
        Assert.Equal(new DateOnly(2020, 1, 1), (await Ascents().GetForEditAsync(year.AscentId))!.Date);
    }

    [Fact]
    public async Task Rejects_invalid_input()
    {
        await Assert.ThrowsAsync<LinkbaseValidationException>(() =>
            Ascents().CreateAsync(NewAscent(Unique("R"), Unique("C"), Unique("Country")) with { MyRating = 4 }));
        await Assert.ThrowsAsync<LinkbaseValidationException>(() =>
            Ascents().CreateAsync(NewAscent(" ", Unique("C"), Unique("Country"))));
        await Assert.ThrowsAsync<LinkbaseValidationException>(() =>
            Ascents().CreateAsync(new AscentInput { Date = default, DatePrecision = DatePrecision.Day, Style = AscentStyle.Lead }));
    }

    [Fact]
    public async Task Update_changes_fields_and_replaces_partners()
    {
        var (first, second) = (Unique("Partner"), Unique("Partner"));
        var created = await CreateAsync(NewAscent(Unique("R"), Unique("C"), Unique("Country")) with { Partners = [first] });

        await Ascents().UpdateAsync(created.AscentId, new AscentInput
        {
            RouteId = created.RouteId,
            Date = new DateOnly(2021, 1, 2),
            DatePrecision = DatePrecision.Day,
            Style = AscentStyle.Onsight,
            Partners = [second],
        });

        var edit = await Ascents().GetForEditAsync(created.AscentId);
        Assert.Equal(new DateOnly(2021, 1, 2), edit!.Date);
        Assert.Equal(AscentStyle.Onsight, edit.Style);
        Assert.Equal([second], edit.Partners);

        await using var db = fixture.CreateContext();
        var ascent = await db.Ascents.AsNoTracking().SingleAsync(a => a.Id == created.AscentId);
        Assert.True(ascent.UpdatedAt > ascent.CreatedAt);
    }

    [Fact]
    public async Task Search_filters_and_pages()
    {
        var country = Unique("Country");
        var crag = Unique("Crag");
        var one = await CreateAsync(NewAscent(Unique("R"), crag, country, "2019-01-01", AscentStyle.Onsight));
        var two = await CreateAsync(NewAscent(Unique("R"), Unique("Crag"), country, "2020-01-01", AscentStyle.Lead));

        var byArea = await Ascents().SearchAsync(new AscentFilter { AreaId = one.AreaId }, AscentSort.Date, true, 0, 10);
        Assert.Equal(2, byArea.Total);
        Assert.Equal(two.AscentId, byArea.Rows[0].Id);

        var byCrag = await Ascents().SearchAsync(new AscentFilter { CragId = one.CragId }, AscentSort.Date, true, 0, 10);
        Assert.Equal(one.AscentId, byCrag.Rows.Single().Id);

        var byStyle = await Ascents().SearchAsync(new AscentFilter { AreaId = one.AreaId, Style = AscentStyle.Lead }, AscentSort.Date, true, 0, 10);
        Assert.Equal(two.AscentId, byStyle.Rows.Single().Id);

        var byDate = await Ascents().SearchAsync(new AscentFilter { AreaId = one.AreaId, From = new DateOnly(2019, 6, 1) }, AscentSort.Date, true, 0, 10);
        Assert.Equal(two.AscentId, byDate.Rows.Single().Id);

        var secondPage = await Ascents().SearchAsync(new AscentFilter { AreaId = one.AreaId }, AscentSort.Date, true, 1, 1);
        Assert.Equal(2, secondPage.Total);
        Assert.Equal(one.AscentId, secondPage.Rows.Single().Id);
    }

    [Fact]
    public async Task Area_filter_includes_sub_areas()
    {
        var country = Unique("Country");
        var created = await CreateAsync(NewAscent(Unique("R"), Unique("Crag"), country));
        await using var db = fixture.CreateContext();
        var region = new Area { UserId = AppUser.DefaultId, ParentId = created.AreaId, Name = Unique("Region"), Kind = AreaKind.Region };
        db.Areas.Add(region);
        await db.SaveChangesAsync();
        await db.Crags.Where(c => c.Id == created.CragId).ExecuteUpdateAsync(s => s.SetProperty(c => c.AreaId, region.Id));

        var result = await Ascents().SearchAsync(new AscentFilter { AreaId = created.AreaId }, AscentSort.Date, true, 0, 10);

        Assert.Equal(created.AscentId, result.Rows.Single().Id);
    }

    [Fact]
    public async Task Stats_count_by_year_and_discipline_and_find_the_hardest_clean_ascent()
    {
        var country = Unique("Country");
        var crag = Unique("Crag");
        var easy = NewAscent("Easy " + Unique("R"), crag, country, "2019-01-01");
        var created = await CreateAsync(easy);
        await Ascents().CreateAsync(new AscentInput
        {
            NewRoute = new NewRouteInput("Hard " + Unique("R"), "E1 5b", GradeSystem.UkTrad, Discipline.Trad, null, null, created.CragId, null),
            Date = new DateOnly(2020, 1, 1),
            DatePrecision = DatePrecision.Day,
            Style = AscentStyle.Redpoint,
        });
        await Ascents().CreateAsync(new AscentInput
        {
            NewRoute = new NewRouteInput("Failed " + Unique("R"), "E9 7a", GradeSystem.UkTrad, Discipline.Trad, null, null, created.CragId, null),
            Date = new DateOnly(2020, 1, 1),
            DatePrecision = DatePrecision.Day,
            Style = AscentStyle.Dnf,
        });

        var stats = await Ascents().StatsAsync(new AscentFilter { AreaId = created.AreaId });

        Assert.Equal(3, stats.Total);
        Assert.Equal([new YearCount(2020, 2), new YearCount(2019, 1)], stats.PerYear);
        Assert.Equal(3, stats.ByDiscipline.Single(d => d.Discipline == Discipline.Trad).Ascents);
        Assert.Equal("E1 5b", stats.Hardest.Single(h => h.System == GradeSystem.UkTrad).Grade);
    }

    [Fact]
    public async Task Deleting_an_ascent_removes_its_links_but_keeps_the_route()
    {
        var created = await CreateAsync(NewAscent(Unique("R"), Unique("C"), Unique("Country")) with { Partners = [Unique("Partner")] });
        await using (var db = fixture.CreateContext())
        {
            db.ExternalRefs.Add(new ExternalRef { EntityType = ExternalEntityType.Ascent, EntityId = created.AscentId, Source = DataSource.Ukc, ExternalId = Unique("x") });
            await db.SaveChangesAsync();
        }

        await Ascents().DeleteAsync(created.AscentId);

        await using var check = fixture.CreateContext();
        Assert.False(await check.Ascents.AnyAsync(a => a.Id == created.AscentId));
        Assert.False(await check.ExternalRefs.AnyAsync(r => r.EntityType == ExternalEntityType.Ascent && r.EntityId == created.AscentId));
        Assert.True(await check.Routes.AnyAsync(r => r.Id == created.RouteId));
    }

    [Fact]
    public async Task Crags_and_routes_in_use_cannot_be_deleted_until_empty()
    {
        var created = await CreateAsync(NewAscent(Unique("R"), Unique("C"), Unique("Country")));

        var routeError = await Assert.ThrowsAsync<LinkbaseValidationException>(() => Routes().DeleteAsync(created.RouteId));
        Assert.Contains("1 ascent", routeError.Message);
        var cragError = await Assert.ThrowsAsync<LinkbaseValidationException>(() => Crags().DeleteAsync(created.CragId));
        Assert.Contains("1 route", cragError.Message);

        await Ascents().DeleteAsync(created.AscentId);
        await Routes().DeleteAsync(created.RouteId);
        await Crags().DeleteAsync(created.CragId);

        await using var db = fixture.CreateContext();
        Assert.False(await db.Crags.AnyAsync(c => c.Id == created.CragId));
    }

    [Fact]
    public async Task Crag_search_finds_part_of_a_name_aliases_and_similar_spellings()
    {
        var suffix = Guid.NewGuid().ToString("N")[..10];
        var created = await CreateAsync(NewAscent(Unique("R"), $"Stanage {suffix}", Unique("Country")));
        await Crags().UpdateAsync(created.CragId, new CragInput($"Stanage {suffix}", created.AreaId, "Grit", 53.3, -1.6, [$"Edge{suffix}"], null));

        Assert.Contains(await Crags().FindCragCandidatesAsync(suffix), c => c.Id == created.CragId);
        Assert.Contains(await Crags().FindCragCandidatesAsync($"Edge{suffix}"), c => c.Id == created.CragId);
        Assert.Contains(await Crags().FindCragCandidatesAsync($"Stannage {suffix}"), c => c.Id == created.CragId);
    }

    [Fact]
    public async Task Crag_validation_rejects_bad_coordinates()
    {
        var created = await CreateAsync(NewAscent(Unique("R"), Unique("C"), Unique("Country")));
        await Assert.ThrowsAsync<LinkbaseValidationException>(() =>
            Crags().UpdateAsync(created.CragId, new CragInput("Name", created.AreaId, null, 91, 0, [], null)));
    }

    [Fact]
    public async Task Browse_rolls_counts_up_to_parent_areas()
    {
        var created = await CreateAsync(NewAscent(Unique("R"), Unique("Crag"), Unique("Country")));
        await using (var db = fixture.CreateContext())
        {
            var region = new Area { UserId = AppUser.DefaultId, ParentId = created.AreaId, Name = Unique("Region"), Kind = AreaKind.Region };
            db.Areas.Add(region);
            await db.SaveChangesAsync();
            await db.Crags.Where(c => c.Id == created.CragId).ExecuteUpdateAsync(s => s.SetProperty(c => c.AreaId, region.Id));
        }

        var root = await Areas().GetContentsAsync(null);
        var summary = root!.SubAreas.Single(a => a.Id == created.AreaId);
        Assert.Equal(1, summary.Crags);
        Assert.Equal(1, summary.Ascents);

        var country = await Areas().GetContentsAsync(created.AreaId);
        Assert.Empty(country!.Crags);
        Assert.Equal(1, country.SubAreas.Single().Ascents);

        Assert.Null(await Areas().GetContentsAsync(-1));
    }

    [Fact]
    public async Task Crag_country_view_finds_the_country_of_a_deeply_nested_crag()
    {
        var created = await CreateAsync(NewAscent(Unique("R"), Unique("Crag"), Unique("Country")));
        await using var db = fixture.CreateContext();
        var region = new Area { UserId = AppUser.DefaultId, ParentId = created.AreaId, Name = Unique("Region"), Kind = AreaKind.Region };
        db.Areas.Add(region);
        await db.SaveChangesAsync();
        var sub = new Area { UserId = AppUser.DefaultId, ParentId = region.Id, Name = Unique("Sub"), Kind = AreaKind.Area };
        db.Areas.Add(sub);
        await db.SaveChangesAsync();
        await db.Crags.Where(c => c.Id == created.CragId).ExecuteUpdateAsync(s => s.SetProperty(c => c.AreaId, sub.Id));

        var countryId = await db.Database
            .SqlQuery<long>($"""SELECT country_id AS "Value" FROM crag_country WHERE crag_id = {created.CragId}""")
            .SingleAsync();

        Assert.Equal(created.AreaId, countryId);
    }
}
