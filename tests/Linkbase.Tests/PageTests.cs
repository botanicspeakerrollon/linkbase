using Bunit;
using Linkbase.Components.Pages;
using Linkbase.Data;
using Linkbase.Domains.Climbing.Data;
using Linkbase.Core.Graph;
using Linkbase.Domains.Climbing.Features.Ascents;
using Linkbase.Domains.Climbing.Features.Browse;
using Linkbase.Domains.Climbing.Features.Insights;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using MudBlazor;
using Npgsql;

namespace Linkbase.Tests;

[Trait("Category", "Database")]
[Collection(PostgresCollection.Name)]
public class HomePageTests(PostgresFixture fixture) : UiTestBase(fixture)
{
    private sealed class FactoryFor(string connectionString) : IDbContextFactory<LinkbaseDbContext>
    {
        public LinkbaseDbContext CreateDbContext() =>
            new(new DbContextOptionsBuilder<LinkbaseDbContext>().UseNpgsql(connectionString).Options);
    }

    private void UseDatabase(string connectionString) =>
        Services.AddSingleton(new DatabaseStatusService(new FactoryFor(connectionString), NullLogger<DatabaseStatusService>.Instance));

    [Fact]
    public async Task Shows_how_many_of_each_type_the_model_has()
    {
        await SeedAsync();
        var cut = RenderPage<Home>();

        Sees(cut, "What's in this model");
        Sees(cut, "Ascent");
        Sees(cut, "Crag");
        Assert.DoesNotContain("Nothing yet", cut.Markup);

        // Climbing's links are counted as they are, as there are few of them.
        Sees(cut, "Relationships");
        Sees(cut, "<td>WITH</td>");
    }

    [Fact]
    public void Says_when_the_database_cant_be_reached()
    {
        UseDatabase("Host=127.0.0.1;Port=1;Database=x;Username=x;Password=x;Timeout=1");
        var cut = RenderPage<Home>();

        Sees(cut, "reach the database");
    }

    [Fact]
    public async Task Says_when_migrations_are_pending()
    {
        UseDatabase(await Fixture.CreateEmptyDatabaseAsync());
        var cut = RenderPage<Home>();

        Sees(cut, "are pending");
    }
}

public class ErrorPageTests : BunitContext
{
    [Fact]
    public void Shows_the_request_id_from_the_current_activity()
    {
        using var activity = new System.Diagnostics.Activity("test").Start();
        var cut = Render<Error>();
        Assert.Contains("Request ID", cut.Markup);
    }

    [Fact]
    public void Leaves_out_the_request_id_when_there_is_none()
    {
        var previous = System.Diagnostics.Activity.Current;
        System.Diagnostics.Activity.Current = null;
        var cut = Render<Error>();
        System.Diagnostics.Activity.Current = previous;
        Assert.DoesNotContain("Request ID", cut.Markup);
    }
}

[Trait("Category", "Database")]
[Collection(PostgresCollection.Name)]
public class LogbookPageTests(PostgresFixture fixture) : UiTestBase(fixture)
{
    [Fact]
    public async Task Lists_ascents_with_the_summary()
    {
        var s = await SeedAsync(date: new DateOnly(2098, 3, 1), notes: "A fine day");
        var cut = RenderPage<LogbookPage>();

        Sees(cut, s.Route);
        Sees(cut, s.Crag);
        Sees(cut, s.Partner);
        Sees(cut, "A fine day");
        Sees(cut, "Summary:");
        cut.Find(".mud-expand-panel-header").Click();
        Sees(cut, "Hardest clean ascent");
        Sees(cut, "2098");
    }

    [Fact]
    public async Task Filters_reload_the_grid_and_the_summary()
    {
        var s = await SeedAsync(date: new DateOnly(2097, 3, 1));
        var other = await SeedAsync(date: new DateOnly(2097, 3, 2));
        var cut = RenderPage<LogbookPage>();
        Sees(cut, other.Route);

        Pick(cut, "Area", s.Country, s.Country);
        Sees(cut, s.Route);
        DoesNotSee(cut, other.Route);
        Sees(cut, "Summary: 1 ascents");

        Pick(cut, "Crag", s.Crag, s.Crag);
        Sees(cut, s.Route);

        var pickers = cut.FindComponents<MudDatePicker>();
        await SetAsync(cut, pickers[0].Instance.DateChanged, new DateTime(2097, 1, 1));
        await SetAsync(cut, pickers[1].Instance.DateChanged, new DateTime(2097, 12, 31));
        Sees(cut, s.Route);

        await SetAsync(cut, cut.FindComponent<MudSelect<GradeSystem?>>().Instance.ValueChanged, GradeSystem.UkTrad);
        await SetAsync(cut, cut.FindComponent<MudSelect<Discipline?>>().Instance.ValueChanged, Discipline.Trad);
        await SetAsync(cut, cut.FindComponent<MudSelect<AscentStyle?>>().Instance.ValueChanged, AscentStyle.Lead);
        Sees(cut, s.Route);

        await SetAsync(cut, cut.FindComponent<MudSelect<AscentStyle?>>().Instance.ValueChanged, AscentStyle.Solo);
        DoesNotSee(cut, s.Route);
    }

    [Theory]
    [InlineData(nameof(AscentRow.RouteName))]
    [InlineData(nameof(AscentRow.CragName))]
    [InlineData(nameof(AscentRow.Style))]
    [InlineData(nameof(AscentRow.Date))]
    public async Task Sorts_by_each_column(string column)
    {
        var s = await SeedAsync(date: new DateOnly(2096, 3, 1));
        var cut = RenderPage<LogbookPage>();
        Sees(cut, "Summary:");
        Pick(cut, "Area", s.Country, s.Country);
        Sees(cut, s.Route);

        var grid = cut.FindComponent<MudDataGrid<AscentRow>>();
        await grid.InvokeAsync(() => grid.Instance.SetSortAsync(column, SortDirection.Ascending, a => a.Id));

        Sees(cut, s.Route);
    }

    [Fact]
    public async Task Shows_each_kind_of_summary_row()
    {
        var s = await SeedAsync(date: new DateOnly(2095, 3, 1));
        await using (var db = Fixture.CreateContext())
        {
            await db.Routes.Where(r => r.Id == s.RouteId).ExecuteUpdateAsync(u => u.SetProperty(r => r.Discipline, (Discipline?)null).SetProperty(r => r.UpdatedAt, DateTimeOffset.UtcNow));
        }

        var cut = RenderPage<LogbookPage>();
        Sees(cut, "Summary:");
        Pick(cut, "Area", s.Country, s.Country);
        Sees(cut, "Summary: 1 ascents");
        cut.Find(".mud-expand-panel-header").Click();
        Sees(cut, "None");
    }
}

[Trait("Category", "Database")]
[Collection(PostgresCollection.Name)]
public class AscentFormPageTests(PostgresFixture fixture) : UiTestBase(fixture)
{
    [Fact]
    public void Says_when_the_ascent_is_gone()
    {
        var cut = RenderPage<AscentFormPage>(p => p.Add(x => x.Id, -1));
        Sees(cut, "no longer exists");
    }

    [Fact]
    public void Save_needs_a_crag_and_route()
    {
        var cut = RenderPage<AscentFormPage>();
        Sees(cut, "Add ascent");
        Sees(cut, "Choose a crag first");

        Press(cut, "Save");

        Sees(cut, "Choose a crag");
        Assert.Equal("/", CurrentPath);
    }

    [Fact]
    public async Task Adds_an_ascent_on_an_existing_route()
    {
        var s = await SeedAsync();
        var cut = RenderPage<AscentFormPage>();

        Pick(cut, "Crag", s.Crag, s.Crag);
        Pick(cut, "Route", s.Route, s.Route);
        var partner = Unique("Partner");
        Pick(cut, "Add partner", partner, partner);
        Sees(cut, partner);
        Pick(cut, "Add partner", s.Partner, s.Partner);
        Sees(cut, s.Partner);

        await SetAsync(cut, cut.FindComponent<MudRating>().Instance.SelectedValueChanged, 2);
        Press(cut, "Clear");
        await SetAsync(cut, cut.FindComponent<MudRating>().Instance.SelectedValueChanged, 3);
        Type(cut, "Notes", "Second go");

        Press(cut, "Save");

        Told("Ascent saved.");
        NavigatedTo("/logbook");
        await using var db = Fixture.CreateContext();
        var saved = await db.Ascents.AsNoTracking().Where(a => a.RouteId == s.RouteId && a.Id != s.AscentId).SingleAsync();
        Assert.Equal((short)3, saved.MyRating);
        Assert.Equal("Second go", saved.Notes);
    }

    [Fact]
    public async Task Partners_can_be_removed_and_not_added_twice()
    {
        var s = await SeedAsync();
        var cut = RenderPage<AscentFormPage>(p => p.Add(x => x.Id, s.AscentId));
        Sees(cut, s.Partner);

        cut.Find(".mud-chip-close-button").Click();

        DoesNotSee(cut, s.Partner);
    }

    [Fact]
    public async Task Creates_a_new_crag_route_and_country_together()
    {
        var (crag, route, country) = (Unique("New crag"), Unique("New route"), Unique("New country"));
        var cut = RenderPage<AscentFormPage>();

        Pick(cut, "Crag", crag, $"Create new crag: {crag}");
        Pick(cut, "Area for the new crag", country, $"Create new country: {country}");
        Pick(cut, "Route", route, $"Create new route: {route}");
        Type(cut, "Grade", "E2 5c");
        await SetAsync(cut, cut.FindComponent<MudSelect<GradeSystem?>>().Instance.ValueChanged, GradeSystem.UkTrad);
        await SetAsync(cut, cut.FindComponent<MudSelect<Discipline?>>().Instance.ValueChanged, Discipline.Trad);

        Press(cut, "Save");

        Told("Ascent saved.");
        var detail = await Fixture.CreateContext().Routes.AsNoTracking().Where(r => r.Name == route).Select(r => new { r.Grade, Crag = r.Crag.Name, Area = r.Crag.Area.Name }).SingleAsync();
        Assert.Equal("E2 5c", detail.Grade);
        Assert.Equal(crag, detail.Crag);
        Assert.Equal(country, detail.Area);
    }

    [Fact]
    public async Task Creates_a_new_route_at_an_existing_crag_and_a_new_crag_in_an_existing_area()
    {
        var s = await SeedAsync();
        var route = Unique("New route");
        var cut = RenderPage<AscentFormPage>();

        Pick(cut, "Crag", s.Crag, s.Crag);
        Pick(cut, "Route", route, $"Create new route: {route}");
        Press(cut, "Save");
        Told("Ascent saved.");

        var (crag, second) = (Unique("New crag"), Unique("New route"));
        var form = RenderPage<AscentFormPage>();
        Pick(form, "Crag", crag, $"Create new crag: {crag}");
        Pick(form, "Area for the new crag", s.Country, s.Country);
        Pick(form, "Route", second, $"Create new route: {second}");
        Press(form, "Save");
        WaitUntil(() => Fixture.CreateContext().Crags.Any(c => c.Name == crag && c.AreaId == s.AreaId), "the new crag");
    }

    [Fact]
    public async Task Changing_crag_clears_the_route()
    {
        var s = await SeedAsync();
        var other = await SeedAsync();
        var cut = RenderPage<AscentFormPage>(p => p.Add(x => x.Id, s.AscentId));
        Sees(cut, "Edit ascent");

        Pick(cut, "Crag", other.Crag, other.Crag);
        Press(cut, "Save");

        Sees(cut, "Choose a route");
    }

    [Fact]
    public async Task Prefills_from_a_route_link()
    {
        var s = await SeedAsync();
        Services.GetRequiredService<Microsoft.AspNetCore.Components.NavigationManager>().NavigateTo($"/ascents/new?routeId={s.RouteId}");
        var cut = RenderPage<AscentFormPage>();

        cut.WaitForAssertion(() => Assert.Equal(s.Route, Field(cut, "Route").QuerySelector("input")!.GetAttribute("value")));
    }

    [Fact]
    public async Task Edits_show_the_original_style_and_save_changes()
    {
        var s = await SeedAsync(notes: "Old", rating: 2);
        await using (var db = Fixture.CreateContext())
        {
            await db.Ascents.Where(a => a.Id == s.AscentId).ExecuteUpdateAsync(u => u.SetProperty(a => a.StyleRaw, "Lead RP").SetProperty(a => a.UpdatedAt, DateTimeOffset.UtcNow));
        }

        var cut = RenderPage<AscentFormPage>(p => p.Add(x => x.Id, s.AscentId));
        Sees(cut, "Originally: Lead RP");

        Type(cut, "Notes", "New");
        Press(cut, "Save");

        Told("Ascent saved.");
        await using var check = Fixture.CreateContext();
        Assert.Equal("New", await check.Ascents.AsNoTracking().Where(a => a.Id == s.AscentId).Select(a => a.Notes).SingleAsync());
    }

    [Fact]
    public async Task A_rejected_save_stays_on_the_page()
    {
        var s = await SeedAsync();
        var cut = RenderPage<AscentFormPage>(p => p.Add(x => x.Id, s.AscentId));
        Sees(cut, "Edit ascent");
        await using (var db = Fixture.CreateContext())
        {
            await db.AscentPartners.Where(a => a.AscentId == s.AscentId).ExecuteDeleteAsync();
            await db.Ascents.Where(a => a.Id == s.AscentId).ExecuteDeleteAsync();
        }

        Press(cut, "Save");

        cut.WaitForAssertion(() => Assert.NotEmpty(Snackbar.ShownSnackbars));
        Assert.Equal("/", CurrentPath);
    }

    [Fact]
    public async Task Delete_asks_first()
    {
        var s = await SeedAsync();
        var cut = RenderPage<AscentFormPage>(p => p.Add(x => x.Id, s.AscentId));

        Press(cut, "Delete");
        Press(Dialogs, "Cancel");
        await using var db = Fixture.CreateContext();
        Dialogs.WaitForAssertion(() => Assert.False(HasControl(Dialogs, "Cancel")));
        Assert.True(await db.Ascents.AnyAsync(a => a.Id == s.AscentId));

        Press(cut, "Delete");
        Press(Dialogs, "Delete");

        Told("Ascent deleted.");
        Assert.False(await db.Ascents.AnyAsync(a => a.Id == s.AscentId));
    }
}

[Trait("Category", "Database")]
[Collection(PostgresCollection.Name)]
public class BrowsePageTests(PostgresFixture fixture) : UiTestBase(fixture)
{
    [Fact]
    public async Task Walks_from_countries_to_a_crag()
    {
        var s = await SeedAsync();
        var top = RenderPage<BrowsePage>();
        Sees(top, s.Country);
        Sees(top, "Country");

        var area = RenderPage<BrowsePage>(p => p.Add(x => x.AreaId, s.AreaId));
        Sees(area, s.Crag);
        Sees(area, "Browse");
    }

    [Fact]
    public async Task Lists_sub_areas_of_an_area()
    {
        var s = await SeedAsync();
        var child = Unique("Region");
        await using (var db = Fixture.CreateContext())
        {
            db.Areas.Add(new Area { UserId = AppUser.DefaultId, Name = child, Kind = AreaKind.Region, ParentId = s.AreaId });
            await db.SaveChangesAsync();
        }

        var cut = RenderPage<BrowsePage>(p => p.Add(x => x.AreaId, s.AreaId));
        Sees(cut, child);
        Sees(cut, ">Area<");
    }

    [Fact]
    public void Says_when_an_area_is_gone()
    {
        var cut = RenderPage<BrowsePage>(p => p.Add(x => x.AreaId, -1));
        Sees(cut, "This area no longer exists.");
    }
}

[Trait("Category", "Database")]
[Collection(PostgresCollection.Name)]
public class CragPageTests(PostgresFixture fixture) : UiTestBase(fixture)
{
    [Fact]
    public void Says_when_the_crag_is_gone()
    {
        var cut = RenderPage<CragPage>(p => p.Add(x => x.Id, -1));
        Sees(cut, "This crag no longer exists.");
    }

    [Fact]
    public async Task Shows_its_facts_and_routes()
    {
        var s = await SeedAsync();
        await Services.GetRequiredService<CragRepository>().UpdateAsync(s.CragId, new CragInput(s.Crag, s.AreaId, "Gritstone", 53.1234, -1.5, ["Alias"], "Windy"));

        var cut = RenderPage<CragPage>(p => p.Add(x => x.Id, s.CragId));

        Sees(cut, "1 route");
        Sees(cut, "Gritstone");
        Sees(cut, "53.1234, -1.5");
        Sees(cut, "Also known as Alias");
        Sees(cut, "Windy");
        Sees(cut, s.Route);
        Sees(cut, "2015");

        foreach (var column in new[] { "Route", "Ascents", "Last climbed" })
        {
            ClickFresh(cut, () => cut.FindAll(".mud-table-sort-label").First(l => l.TextContent.Trim() == column));
        }

        Sees(cut, s.Route);
    }

    [Fact]
    public async Task Edits_in_a_dialog()
    {
        var s = await SeedAsync();
        var cut = RenderPage<CragPage>(p => p.Add(x => x.Id, s.CragId));

        Press(cut, "Edit");
        Sees(Dialogs, "Other names");
        Press(Dialogs, "Cancel");
        Dialogs.WaitForAssertion(() => Assert.False(HasControl(Dialogs, "Cancel")));

        Press(cut, "Edit");
        var renamed = Unique("Renamed");
        Type(Dialogs, "Name", renamed);
        Type(Dialogs, "Other names", "One, Two");
        // Part of the name, because the field already holds all of it and retyping that wouldn't search.
        Pick(Dialogs, "Area", s.Country[..^1], s.Country);
        Press(Dialogs, "Save");

        Told("Crag saved.");
        Sees(cut, renamed);
        Sees(cut, "Also known as One, Two");
    }

    [Fact]
    public async Task The_dialog_requires_a_name_and_reports_rejection()
    {
        var s = await SeedAsync();
        var cut = RenderPage<CragPage>(p => p.Add(x => x.Id, s.CragId));

        Press(cut, "Edit");
        Sees(Dialogs, "Other names");
        Type(Dialogs, "Name", "");
        Press(Dialogs, "Save");
        Sees(Dialogs, "A crag needs a name");

        Type(Dialogs, "Name", Unique("Renamed"));
        Type(Dialogs, "Latitude", "45");
        await using (var db = Fixture.CreateContext())
        {
            await db.ExternalRefs.Where(r => r.EntityId == s.CragId).ExecuteDeleteAsync();
            await db.AscentPartners.Where(a => a.AscentId == s.AscentId).ExecuteDeleteAsync();
            await db.Ascents.Where(a => a.Id == s.AscentId).ExecuteDeleteAsync();
            await db.Routes.Where(r => r.Id == s.RouteId).ExecuteDeleteAsync();
            await db.Crags.Where(c => c.Id == s.CragId).ExecuteDeleteAsync();
        }

        Press(Dialogs, "Save");
        Sees(Dialogs, "mud-alert");
    }

    [Fact]
    public async Task Delete_asks_first_and_refuses_when_routes_remain()
    {
        var s = await SeedAsync();
        var cut = RenderPage<CragPage>(p => p.Add(x => x.Id, s.CragId));

        Press(cut, "Delete");
        Press(Dialogs, "Cancel");
        Dialogs.WaitForAssertion(() => Assert.False(HasControl(Dialogs, "Cancel")));

        Press(cut, "Delete");
        Press(Dialogs, "Delete");
        cut.WaitForAssertion(() => Assert.NotEmpty(Snackbar.ShownSnackbars));
        Assert.Equal("/", CurrentPath);
    }

    [Fact]
    public async Task Deletes_an_empty_crag()
    {
        var s = await SeedAsync();
        await using (var db = Fixture.CreateContext())
        {
            await db.AscentPartners.Where(a => a.AscentId == s.AscentId).ExecuteDeleteAsync();
            await db.Ascents.Where(a => a.Id == s.AscentId).ExecuteDeleteAsync();
            await db.Routes.Where(r => r.Id == s.RouteId).ExecuteDeleteAsync();
        }

        var cut = RenderPage<CragPage>(p => p.Add(x => x.Id, s.CragId));
        Press(cut, "Delete");
        Press(Dialogs, "Delete");

        Told("Crag deleted.");
        NavigatedTo($"/browse/area/{s.AreaId}");
    }
}

[Trait("Category", "Database")]
[Collection(PostgresCollection.Name)]
public class RoutePageTests(PostgresFixture fixture) : UiTestBase(fixture)
{
    [Fact]
    public void Says_when_the_route_is_gone()
    {
        var cut = RenderPage<RoutePage>(p => p.Add(x => x.Id, -1));
        Sees(cut, "This route no longer exists.");
    }

    [Fact]
    public async Task Shows_its_facts_and_ascents()
    {
        var s = await SeedAsync(notes: "Good", rating: 2);
        await Services.GetRequiredService<RouteRepository>().UpdateAsync(s.RouteId, new RouteInput(s.Route, "VS 4c", GradeSystem.UkTrad, Discipline.Trad, 20, 2, "My note", s.CragId));

        var cut = RenderPage<RoutePage>(p => p.Add(x => x.Id, s.RouteId));

        Sees(cut, "VS 4c");
        Sees(cut, "20 m");
        Sees(cut, "2 pitches");
        Sees(cut, "My note");
        Sees(cut, "My ascents (1)");
        Sees(cut, s.Partner);
        Sees(cut, "Good");
    }

    [Fact]
    public async Task A_bare_route_with_one_pitch_and_no_ascents()
    {
        var s = await SeedAsync();
        await Services.GetRequiredService<RouteRepository>().UpdateAsync(s.RouteId, new RouteInput(s.Route, null, null, null, null, 1, null, s.CragId));
        var one = RenderPage<RoutePage>(p => p.Add(x => x.Id, s.RouteId));
        Sees(one, "1 pitch");

        await Services.GetRequiredService<RouteRepository>().UpdateAsync(s.RouteId, new RouteInput(s.Route, null, null, null, null, null, null, s.CragId));
        await Services.GetRequiredService<AscentRepository>().DeleteAsync(s.AscentId);
        var bare = RenderPage<RoutePage>(p => p.Add(x => x.Id, s.RouteId));
        Sees(bare, "My ascents (0)");
        Assert.DoesNotContain("<table", bare.Markup);
    }

    [Fact]
    public async Task Edits_in_a_dialog()
    {
        var s = await SeedAsync();
        var other = await SeedAsync();
        var cut = RenderPage<RoutePage>(p => p.Add(x => x.Id, s.RouteId));

        Press(cut, "Edit");
        Sees(Dialogs, "A short note in your own words");
        Press(Dialogs, "Cancel");
        Dialogs.WaitForAssertion(() => Assert.False(HasControl(Dialogs, "Cancel")));

        Press(cut, "Edit");
        var renamed = Unique("Renamed");
        Type(Dialogs, "Name", renamed);
        Type(Dialogs, "Grade", "E5 6a");
        Pick(Dialogs, "Crag", other.Crag, other.Crag);
        Press(Dialogs, "Save");

        Told("Route saved.");
        Sees(cut, renamed);
        Sees(cut, "E5 6a");
        Assert.Equal(other.CragId, (await Services.GetRequiredService<RouteRepository>().GetAsync(s.RouteId))!.CragId);
    }

    [Fact]
    public async Task The_dialog_requires_a_name_and_reports_rejection()
    {
        var s = await SeedAsync();
        var cut = RenderPage<RoutePage>(p => p.Add(x => x.Id, s.RouteId));

        Press(cut, "Edit");
        Sees(Dialogs, "A short note in your own words");
        Type(Dialogs, "Name", "");
        Press(Dialogs, "Save");
        Sees(Dialogs, "A route needs a name");

        Type(Dialogs, "Name", s.Route);
        await using (var db = Fixture.CreateContext())
        {
            await db.AscentPartners.Where(a => a.AscentId == s.AscentId).ExecuteDeleteAsync();
            await db.Ascents.Where(a => a.Id == s.AscentId).ExecuteDeleteAsync();
            await db.Routes.Where(r => r.Id == s.RouteId).ExecuteDeleteAsync();
        }

        Press(Dialogs, "Save");
        Sees(Dialogs, "mud-alert");
    }

    [Fact]
    public async Task Delete_asks_first_refuses_with_ascents_and_deletes_without()
    {
        var s = await SeedAsync();
        var cut = RenderPage<RoutePage>(p => p.Add(x => x.Id, s.RouteId));

        Press(cut, "Delete");
        Press(Dialogs, "Cancel");
        Dialogs.WaitForAssertion(() => Assert.False(HasControl(Dialogs, "Cancel")));

        Press(cut, "Delete");
        Press(Dialogs, "Delete");
        cut.WaitForAssertion(() => Assert.NotEmpty(Snackbar.ShownSnackbars));
        Assert.Equal("/", CurrentPath);
        Dialogs.WaitForAssertion(() => Assert.False(HasControl(Dialogs, "Delete")));

        await Services.GetRequiredService<AscentRepository>().DeleteAsync(s.AscentId);
        Press(cut, "Delete");
        Press(Dialogs, "Delete");
        Told("Route deleted.");
        NavigatedTo($"/browse/crag/{s.CragId}");
    }
}

[Trait("Category", "Database")]
[Collection(PostgresCollection.Name)]
public class InsightsPageTests(PostgresFixture fixture) : UiTestBase(fixture)
{
    private static void OpenTab(IRenderedComponent<InsightsPage> cut, string name)
    {
        cut.WaitForAssertion(() => Assert.Contains(cut.FindAll(".mud-tab"), t => t.TextContent.Trim() == name));
        ClickFresh(cut, () => cut.FindAll(".mud-tab").First(t => t.TextContent.Trim() == name));
    }

    [Fact]
    public async Task Every_tab_shows_its_tables()
    {
        var s = await SeedAsync(date: new DateOnly(2015, 5, 2));
        foreach (var date in new DateOnly[] { new(2015, 5, 3), new(2016, 6, 1), new(2016, 6, 2), new(2017, 7, 1), new(2017, 7, 2) })
        {
            await Services.GetRequiredService<AscentRepository>().CreateAsync(new AscentInput
            {
                RouteId = s.RouteId,
                Date = date,
                DatePrecision = DatePrecision.Day,
                Style = AscentStyle.Lead,
                Partners = [s.Partner],
            });
        }

        await Services.GetRequiredService<TripService>().RebuildAsync([]);

        var cut = RenderPage<InsightsPage>();

        Sees(cut, "Trips abroad");
        Sees(cut, s.Partner);
        Sees(cut, "Two-day trips");
        Sees(cut, s.Country);

        OpenTab(cut, "Places");
        Sees(cut, "Crags you return to year after year");
        Sees(cut, s.Crag);

        OpenTab(cut, "Seasons");
        Sees(cut, "Climbing days per month");
        Sees(cut, "Discipline mix by year");
        Sees(cut, s.Country);

        OpenTab(cut, "Partners");
        Sees(cut, "By trips");
        Sees(cut, s.Partner);
    }

    [Fact]
    public async Task On_this_day_lists_past_years_newest_first_with_exact_dates_only()
    {
        var older = await SeedAsync(date: new DateOnly(2011, 3, 17), notes: "Older");
        var newer = await SeedAsync(date: new DateOnly(2019, 3, 17), style: AscentStyle.TopRope);
        var otherDay = await SeedAsync(date: new DateOnly(2019, 3, 18));
        var monthOnly = await SeedAsync(date: new DateOnly(2018, 3, 1));
        await using (var db = Fixture.CreateContext())
        {
            await db.Ascents.Where(a => a.Id == monthOnly.AscentId).ExecuteUpdateAsync(u => u
                .SetProperty(a => a.DatePrecision, DatePrecision.Month)
                .SetProperty(a => a.UpdatedAt, DateTimeOffset.UtcNow));
        }

        var rows = await Services.GetRequiredService<InsightsRepository>().OnThisDayAsync(3, 17);
        var mine = rows.Where(r => r.Route == older.Route || r.Route == newer.Route).ToList();
        Assert.Equal([newer.Route, older.Route], mine.Select(r => r.Route));
        Assert.Equal(2019, mine[0].Year);
        Assert.Equal([newer.Partner], mine[0].Partners);
        Assert.DoesNotContain(rows, r => r.Route == otherDay.Route);
        Assert.DoesNotContain(await Services.GetRequiredService<InsightsRepository>().OnThisDayAsync(3, 1), r => r.Route == monthOnly.Route);

        var cut = RenderPage<InsightsPage>(p => p.Add(x => x.OnDate, new DateOnly(2026, 3, 17)));
        OpenTab(cut, "On this day");

        Sees(cut, "17 March in past years");
        Sees(cut, newer.Route);
        Sees(cut, newer.Crag);
        Sees(cut, newer.Partner);
        Sees(cut, "Top rope");
        Sees(cut, "Older");
        DoesNotSee(cut, otherDay.Route);
    }

    [Fact]
    public async Task On_this_day_says_so_when_nothing_was_climbed()
    {
        await using (var db = Fixture.CreateContext())
        {
            Assert.False(await db.Ascents.AnyAsync(a => a.Date.Month == 2 && a.Date.Day == 29));
        }

        var cut = RenderPage<InsightsPage>(p => p.Add(x => x.OnDate, new DateOnly(2024, 2, 29)));
        OpenTab(cut, "On this day");

        Sees(cut, "Nothing logged on this date yet.");
    }

    [Fact]
    public async Task A_trip_with_no_dates_says_so()
    {
        var s = await SeedAsync(date: new DateOnly(2016, 5, 2));
        await Services.GetRequiredService<TripService>().RebuildAsync([]);
        var cut = RenderPage<InsightsPage>();
        Sees(cut, s.Country);
        var instance = typeof(InsightsPage).GetMethod("DateRange", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;
        Assert.Equal("Dates unknown", instance.Invoke(null, [null, null]));
    }
}
