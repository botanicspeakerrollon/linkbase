using Bunit;
using Linkbase.Core;
using Linkbase.Core.Records;
using Linkbase.Core.Security;
using Linkbase.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Linkbase.Tests;

// Labelling from the pages: the record page's Label… button and the list's bulk buttons, on the consultancy model, which labels
// its records. Who can see what is the database's job and is tried in the security tests.
[Trait("Category", "Database")]
public sealed class LabelUiTests(PostgresFixture fixture) : UiTestBase(fixture), IClassFixture<PostgresFixture>
{
    private async Task<ModelFactory> ConsultancyAsync()
    {
        var factory = new ModelFactory(await Fixture.CreateModelDatabaseAsync("SELECT 1"));
        await TestModels.LoadAsync(factory, RepoPaths.Resolve("models/consultancy/model.json"), TextWriter.Null);
        Services.AddSingleton<IDbContextFactory<LinkbaseDbContext>>(factory);
        Services.AddSingleton<RecordRepository>();
        Services.AddSingleton<ExternalRefRepository>();
        Services.AddSingleton<Linkbase.Core.History.HistoryRepository>();
        Services.AddSingleton<LabelRepository>();
        return factory;
    }

    [Fact]
    public async Task A_record_shows_its_label_in_the_models_own_names()
    {
        var factory = await ConsultancyAsync();
        var security = (await new LabelRepository(factory).SettingsAsync())!;

        var cut = RenderPage<RecordPage>(p => p.Add(x => x.Type, "Project").Add(x => x.Key, "pr2"));

        Sees(cut, "Label…");
        Sees(cut, $"{security.Level!.Name}: internal");
        Sees(cut, "Harbourline");
    }

    [Fact]
    public async Task Labelling_a_record_from_its_page_changes_the_label_and_says_so()
    {
        var factory = await ConsultancyAsync();
        var labels = new LabelRepository(factory);
        var cut = RenderPage<RecordPage>(p => p.Add(x => x.Type, "Project").Add(x => x.Key, "pr2"));
        Press(cut, "Label…");

        // The dialog starts from what the record has, so applying it unchanged changes nothing.
        Dialogs.WaitForAssertion(() => Assert.Contains("Set who can see this record", Dialogs.Markup));
        Type(Dialogs, "Reason", "Checking");
        Press(Dialogs, "Apply");
        Told("Nothing needed changing.");
        Assert.Equal("internal", (await labels.GetNodeAsync("project:pr2"))!.Level);
    }

    private sealed class OneViewer(string user) : IViewerResolver
    {
        public async ValueTask<Viewer> ResolveAsync(ModelSecurity security, CancellationToken cancellationToken = default) =>
            await ViewerDirectory.Create(security).ResolveAsync(user, cancellationToken);
    }

    [Fact]
    public async Task The_label_dialog_offers_only_what_the_person_holds()
    {
        await ConsultancyAsync();
        Services.AddSingleton<IViewerResolver>(new OneViewer("ava.lindqvist"));
        var cut = RenderPage<RecordPage>(p => p.Add(x => x.Type, "Project").Add(x => x.Key, "pr2"));
        Press(cut, "Label…");
        Dialogs.WaitForAssertion(() => Assert.Contains("Set who can see this record", Dialogs.Markup));

        // Ava holds Harbourline but not Northgate, and is "internal", so can't pick "confidential".
        Dialogs.FindAll(".mud-input-control").First(c => c.QuerySelector("label")?.TextContent.Trim() == "NDA").MouseDown();
        Dialogs.WaitForAssertion(() => Assert.Contains("Harbourline", Popovers.Markup));
        Assert.DoesNotContain("Northgate", Popovers.Markup);
        Assert.DoesNotContain("confidential", Dialogs.Markup);
    }

    [Fact]
    public async Task The_list_offers_bulk_labelling_only_where_the_model_has_security()
    {
        await ConsultancyAsync();

        var cut = RenderPage<RecordListPage>(p => p.Add(x => x.Type, "Project"));

        Sees(cut, "Label (0)…");
        cut.WaitForAssertion(() => Assert.True(cut.FindAll("input[type=checkbox]").Count > 1));
    }

    [Fact]
    public async Task Each_property_has_a_lock_that_opens_its_own_label_and_marks_one_that_has_it()
    {
        var factory = await ConsultancyAsync();
        await new LabelRepository(factory).EditPropertyAsync("project:pr2", "status", new LabelEdit(Level: "confidential"), null);

        var cut = RenderPage<RecordPage>(p => p.Add(x => x.Type, "Project").Add(x => x.Key, "pr2"));

        cut.WaitForAssertion(() => Assert.Contains(cut.FindAll("button.property-label"), b => b.GetAttribute("aria-label")!.Contains("has its own label")));
        Assert.Contains(cut.FindAll("button.property-label"), b => b.GetAttribute("aria-label")!.StartsWith("Set who can see", StringComparison.Ordinal));

        cut.FindAll("button.property-label").First(b => b.GetAttribute("aria-label")!.Contains("has its own label")).Click();
        Dialogs.WaitForAssertion(() => Assert.Contains("on this record, on top of who can see the record", Dialogs.Markup));
    }
}
