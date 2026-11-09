using Bunit;
using Linkbase.Core;
using Linkbase.Components.Pages;
using Linkbase.Data;
using Linkbase.Domains.Climbing.Data;
using Linkbase.Core.Graph;
using Linkbase.Domains.Climbing.Features.Ascents;
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
    // The made-up climbing model in a database of its own, which the page reads instead of the shared one.
    private async Task<ModelFactory> GenericAsync()
    {
        var (source, target) = await GenericConversionTests.TypedAsync(Fixture);
        await new GenericConverter(source, target).ConvertAsync(RepoPaths.Resolve("models/climbing/model.json"), TextWriter.Null);
        Services.AddSingleton<IDbContextFactory<LinkbaseDbContext>>(target);
        Services.AddSingleton<LogbookRepository>();
        return target;
    }

    [Fact]
    public async Task Lists_ascents_with_the_summary()
    {
        await GenericAsync();
        var cut = RenderPage<LogbookPage>();

        Sees(cut, "Crack of Dawn");
        Sees(cut, "Hollow Tor");
        Sees(cut, "Sam Example");
        Sees(cut, "Cold hands");
        Sees(cut, "Summary: 4 ascents");
        cut.Find(".mud-expand-panel-header").Click();
        Sees(cut, "Hardest clean ascent");
        Sees(cut, "E1 5b");
        Sees(cut, "2022");
    }

    [Fact]
    public async Task Filters_reload_the_grid_and_the_summary()
    {
        await GenericAsync();
        var cut = RenderPage<LogbookPage>();
        Sees(cut, "La Bleue");

        Pick(cut, "Area", "Wessex", "Wessex");
        Sees(cut, "Crack of Dawn");
        DoesNotSee(cut, "La Bleue");
        Sees(cut, "Summary: 3 ascents");

        Pick(cut, "Crag", "Hollow", "Hollow Tor");
        Sees(cut, "Crack of Dawn");

        var pickers = cut.FindComponents<MudDatePicker>();
        await SetAsync(cut, pickers[0].Instance.DateChanged, new DateTime(2020, 5, 3));
        await SetAsync(cut, pickers[1].Instance.DateChanged, new DateTime(2020, 12, 31));
        Sees(cut, "Summary: 1 ascents");
        Sees(cut, "Slab Happy");
        DoesNotSee(cut, "Crack of Dawn");

        await SetAsync(cut, cut.FindComponent<MudSelect<GradeSystem?>>().Instance.ValueChanged, GradeSystem.UkTrad);
        await SetAsync(cut, cut.FindComponent<MudSelect<Discipline?>>().Instance.ValueChanged, Discipline.Trad);
        await SetAsync(cut, cut.FindComponent<MudSelect<AscentStyle?>>().Instance.ValueChanged, AscentStyle.Redpoint);
        Sees(cut, "Slab Happy");

        await SetAsync(cut, cut.FindComponent<MudSelect<AscentStyle?>>().Instance.ValueChanged, AscentStyle.Solo);
        DoesNotSee(cut, "Slab Happy");
    }

    [Theory]
    [InlineData(nameof(LogbookRow.RouteName))]
    [InlineData(nameof(LogbookRow.CragName))]
    [InlineData(nameof(LogbookRow.Style))]
    [InlineData(nameof(LogbookRow.Date))]
    public async Task Sorts_by_each_column(string column)
    {
        await GenericAsync();
        var cut = RenderPage<LogbookPage>();
        Sees(cut, "Summary:");
        Sees(cut, "Crack of Dawn");

        var grid = cut.FindComponent<MudDataGrid<LogbookRow>>();
        await grid.InvokeAsync(() => grid.Instance.SetSortAsync(column, SortDirection.Ascending, a => a.Id));

        Sees(cut, "Crack of Dawn");
    }

    [Fact]
    public async Task Links_each_ascent_to_its_record_page()
    {
        await GenericAsync();
        var cut = RenderPage<LogbookPage>();
        Sees(cut, "Crack of Dawn");

        cut.WaitForAssertion(() => Assert.Contains(cut.FindAll("a"), a => a.GetAttribute("href")?.StartsWith("/data/Ascent/", StringComparison.Ordinal) == true));
    }

    [Fact]
    public async Task Says_so_when_the_model_has_no_ascents_in_the_generic_store()
    {
        var empty = new ModelFactory(await Fixture.CreateModelDatabaseAsync("SELECT 1"));
        Services.AddSingleton<IDbContextFactory<LinkbaseDbContext>>(empty);
        Services.AddSingleton<LogbookRepository>();

        var cut = RenderPage<LogbookPage>();

        Sees(cut, "no ascents in the generic store");
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
