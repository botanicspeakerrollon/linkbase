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
