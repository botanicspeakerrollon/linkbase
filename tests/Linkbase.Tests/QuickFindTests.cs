using Bunit;
using Linkbase.Components.Layout;
using Linkbase.Core;
using Linkbase.Core.Graph;
using Linkbase.Core.Explore;
using Linkbase.Core.Models;
using Linkbase.Core.Records;
using Linkbase.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor;

namespace Linkbase.Tests;

// Quick-find's choices, which need no page.
public class QuickFindItemsTests
{
    private static readonly GraphStyles Styles = new(Linkbase.Domains.RegisteredDomains.Registry);

    private static readonly RecordType[] Types =
    [
        new("Person", null, null, []),
        new("Trip", null, null, [], Owner: "Climbing"),
    ];

    private static IReadOnlyList<QuickFindItem> Arranged(string? text, params QuickFindItem[] records) =>
        QuickFindItems.Arrange(text, records, QuickFindItems.Pages(Styles, [], Types), QuickFindItems.Actions(Styles, Types));

    [Fact]
    public void With_no_text_it_offers_the_pages_and_the_actions()
    {
        var titles = Arranged(null).Select(i => i.Title).ToList();

        Assert.Contains("Home", titles);
        Assert.Contains("Explore", titles);
        Assert.Contains("Person", titles);
        Assert.Contains("Add Person", titles);
    }

    [Fact]
    public void A_type_a_domain_derives_cannot_be_added_by_hand()
    {
        var titles = Arranged(null).Select(i => i.Title).ToList();

        Assert.Contains("Trip", titles);
        Assert.DoesNotContain("Add Trip", titles);
    }

    [Fact]
    public void Every_word_has_to_match_and_it_ignores_case()
    {
        var found = Arranged("add per");

        var only = Assert.Single(found);
        Assert.Equal("Add Person", only.Title);
        Assert.Equal("Person", only.Choice.AddType);
    }

    [Fact]
    public void Records_come_first_then_pages_that_start_with_the_text()
    {
        var record = QuickFindItems.ForRecord(Styles, new GraphNode("person:p1", "Person", "Persona Grata", []));

        var found = Arranged("pers", record);

        Assert.Equal(["Persona Grata", "Person", "Add Person"], found.Select(i => i.Title));
        Assert.Equal("/data/Person/p1", found[0].Choice.Href);
    }

    [Fact]
    public void A_page_opens_its_address()
    {
        Assert.Equal("/explore", Arranged("explore").Single().Choice.Href);
        Assert.Equal("/data/Person", Arranged("person").First(i => i.Group == QuickFindItems.PagesGroup).Choice.Href);
    }
}

// The dialog itself, on the consultancy's model.
[Trait("Category", "Database")]
public class QuickFindDialogTests(PostgresFixture fixture) : UiTestBase(fixture), IClassFixture<PostgresFixture>
{
    private async Task<IDialogReference> OpenAsync()
    {
        var factory = new ModelFactory(await Fixture.CreateModelDatabaseAsync("SELECT 1"));
        await TestModels.LoadAsync(factory, RepoPaths.Resolve("models/consultancy/model.json"), TextWriter.Null);
        Services.AddSingleton<IDbContextFactory<LinkbaseDbContext>>(factory);

        RenderPage<MudText>();
        var dialog = await Services.GetRequiredService<IDialogService>().ShowAsync<QuickFindDialog>("Quick-find");
        Sees(Dialogs, "Add Person");
        return dialog;
    }

    [Fact]
    public async Task Typing_finds_a_record_and_enter_chooses_it()
    {
        var dialog = await OpenAsync();

        Dialogs.Find("input").Input("add person");
        Dialogs.WaitForAssertion(() => Assert.Equal("Add Person", Dialogs.Find(".quickfind-item.active .quickfind-title").TextContent));
        Dialogs.Find("input").KeyDown(new Microsoft.AspNetCore.Components.Web.KeyboardEventArgs { Key = "Enter" });

        var choice = Assert.IsType<QuickFindChoice>((await dialog.Result)!.Data);
        Assert.Equal("Person", choice.AddType);
    }

    [Fact]
    public async Task Clicking_an_action_chooses_adding_that_type()
    {
        var dialog = await OpenAsync();

        Dialogs.FindAll(".quickfind-item").First(e => e.TextContent.Contains("Add Person")).Click();

        var choice = Assert.IsType<QuickFindChoice>((await dialog.Result)!.Data);
        Assert.Equal("Person", choice.AddType);
    }

    [Fact]
    public async Task Arrow_down_moves_the_choice()
    {
        await OpenAsync();
        var input = Dialogs.Find("input");

        input.KeyDown(new Microsoft.AspNetCore.Components.Web.KeyboardEventArgs { Key = "ArrowDown" });

        Dialogs.WaitForAssertion(() =>
            Assert.Equal("Explore", Dialogs.Find(".quickfind-item.active .quickfind-title").TextContent));
    }
}
