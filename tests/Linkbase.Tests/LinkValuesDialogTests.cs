using Bunit;
using Linkbase.Core;
using Linkbase.Core.Models;
using Linkbase.Core.Records;
using Linkbase.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor;

namespace Linkbase.Tests;

// The dialog on a record page that edits the values one link carries, tried on the consultancy's Person WORKS_ON Project.
[Trait("Category", "Database")]
public class LinkValuesDialogTests(PostgresFixture fixture) : UiTestBase(fixture), IClassFixture<PostgresFixture>
{
    private async Task<(RecordRepository Records, RecordLink Link, IDialogReference Dialog)> OpenAsync(bool requireReason)
    {
        var factory = new ModelFactory(await Fixture.CreateModelDatabaseAsync("SELECT 1"));
        await TestModels.LoadAsync(factory, RepoPaths.Resolve("models/consultancy/model.json"), TextWriter.Null);
        await using (var db = factory.CreateDbContext())
        {
            await db.Database.ExecuteSqlRawAsync(requireReason ? "UPDATE model_setting SET require_reason = true" : "UPDATE model_setting SET require_reason = false");
        }

        Services.AddSingleton<IDbContextFactory<LinkbaseDbContext>>(factory);
        var records = new RecordRepository(factory, Linkbase.Core.Records.RecordHooks.None, Linkbase.Core.Security.FieldPolicy.Unrestricted);
        var link = (await records.LinksAsync("person:p4")).Single(g => g.RelType == "WORKS_ON" && g.Outgoing).Links
            .First(l => l.OtherId == "project:pr4" && l.Values["role"] == "Engineer");
        var attributes = await records.LinkAttributesAsync("Person", "WORKS_ON", "Project");

        RenderPage<MudText>();
        var parameters = new DialogParameters<LinkValuesDialog>
        {
            { d => d.LinkId, link.Id }, { d => d.Attributes, attributes }, { d => d.Current, link.Values },
        };
        var dialog = await Services.GetRequiredService<IDialogService>().ShowAsync<LinkValuesDialog>("Edit the link", parameters);
        Sees(Dialogs, "Edit the link");
        return (records, link, dialog);
    }

    [Fact]
    public async Task Cancel_closes_it_and_changes_nothing()
    {
        var (records, link, dialog) = await OpenAsync(requireReason: false);

        Press(Dialogs, "Cancel");

        Assert.True((await dialog.Result)!.Canceled);
        var after = (await records.LinksAsync("person:p4")).Single(g => g.RelType == "WORKS_ON" && g.Outgoing).Links.Single(l => l.Id == link.Id);
        Assert.Equal(link.Values, after.Values);
    }

    [Fact]
    public async Task Save_stores_the_values_and_closes_it()
    {
        var (_, _, dialog) = await OpenAsync(requireReason: false);

        Press(Dialogs, "Save");

        var result = await dialog.Result;
        Assert.False(result!.Canceled);
        Assert.Equal(true, result.Data);
    }

    [Fact]
    public async Task A_model_that_wants_a_reason_keeps_it_open_with_the_reason_asked_for()
    {
        var (_, _, dialog) = await OpenAsync(requireReason: true);

        Press(Dialogs, "Save");

        Sees(Dialogs, "Say why you");
        Assert.False(dialog.Result.IsCompleted);
    }
}
