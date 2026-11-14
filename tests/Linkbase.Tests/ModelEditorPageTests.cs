using Bunit;
using Linkbase.Core;
using Linkbase.Core.Models;
using Linkbase.Data;
using Microsoft.AspNetCore.Components;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Linkbase.Tests;

// The /model page: ModelEditorPage, tried against the example consultancy model.
[Trait("Category", "Database")]
public sealed class ModelEditorPageTests(PostgresFixture fixture) : UiTestBase(fixture), IClassFixture<PostgresFixture>
{
    private async Task<IRenderedComponent<ModelEditorPage>> OpenAsync()
    {
        var factory = new ModelFactory(await Fixture.CreateModelDatabaseAsync("SELECT 1"));
        await TestModels.LoadAsync(factory, RepoPaths.Resolve("models/consultancy/model.json"), TextWriter.Null);
        Services.AddSingleton<IDbContextFactory<LinkbaseDbContext>>(factory);
        Services.AddSingleton<Linkbase.Core.Records.RecordRepository>();
        Services.AddSingleton<ModelEditor>();
        var cut = RenderPage<ModelEditorPage>();
        Sees(cut, "Person");
        return cut;
    }

    private static void OpenTab<T>(IRenderedComponent<T> cut, string name)
        where T : IComponent
    {
        cut.WaitForAssertion(() => Assert.Contains(cut.FindAll(".mud-tab"), t => t.TextContent.Trim() == name));
        ClickFresh(cut, () => cut.FindAll(".mud-tab").First(t => t.TextContent.Trim() == name));
    }

    [Fact]
    public async Task Shows_every_type_with_its_fields_and_record_count()
    {
        var cut = await OpenAsync();

        Sees(cut, "Person");
        Sees(cut, "Team");
        Sees(cut, "Client");
        Sees(cut, "Project");
        Sees(cut, "name_first");
        Sees(cut, "skills");
        Sees(cut, "8 record(s)");
    }

    [Fact]
    public async Task Relationships_tab_shows_every_relationship_with_its_values()
    {
        var cut = await OpenAsync();

        OpenTab(cut, "Relationships (r)");

        Sees(cut, "Person WORKS_ON Project");
        Sees(cut, "role");
        Sees(cut, "days_a_week");
        Sees(cut, "Required");
    }

    [Fact]
    public async Task Adds_a_type_through_the_dialog()
    {
        var cut = await OpenAsync();

        Press(cut, "Add type");
        Sees(Dialogs, "Add a type");
        Type(Dialogs, "Name", "Supplier");
        Press(Dialogs, "Add");

        Sees(cut, "Supplier");
    }

    [Fact]
    public async Task Refuses_a_bad_type_name_and_keeps_the_dialog_open()
    {
        var cut = await OpenAsync();

        Press(cut, "Add type");
        Type(Dialogs, "Name", "supplier");
        Press(Dialogs, "Add");

        Told("A type's name is letters and digits");
        Sees(Dialogs, "Add a type");
    }

    [Fact]
    public async Task Adds_a_text_field_to_a_type()
    {
        var cut = await OpenAsync();

        Press(cut, "Field");
        Sees(Dialogs, "Add a field to Person");
        Type(Dialogs, "Key", "nickname");
        Press(Dialogs, "Add");

        Sees(cut, "nickname");
    }

    [Fact]
    public async Task Deleting_a_field_with_values_asks_again_before_discarding()
    {
        var cut = await OpenAsync();

        ClickFresh(cut, () => cut.Find("[aria-label='Delete sector']"));
        Sees(Dialogs, "Delete sector?");
        Press(Dialogs, "Delete");

        Sees(Dialogs, "have a value for sector");
        Press(Dialogs, "Delete anyway");

        DoesNotSee(cut, "sector");
    }
}
