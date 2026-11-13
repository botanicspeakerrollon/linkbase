using Linkbase.Core;
using Linkbase.Core.Models;
using Linkbase.Core.Records;
using Linkbase.Data;
using Microsoft.EntityFrameworkCore;

namespace Linkbase.Tests;

// Changing a model's types, fields, relationships, link values and hierarchies from the app: ModelEditor. Tried on the
// example consultancy's model, so it can be checked against records already there.
[Trait("Category", "Database")]
public sealed class ModelEditorTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    private static readonly string Example = RepoPaths.Resolve("models/consultancy/model.json");

    private async Task<(ModelFactory Factory, ModelEditor Editor, RecordRepository Records)> ExampleAsync()
    {
        var factory = new ModelFactory(await fixture.CreateModelDatabaseAsync("SELECT 1"));
        await TestModels.LoadAsync(factory, Example, TextWriter.Null);
        return (factory, new ModelEditor(factory), new RecordRepository(factory));
    }

    // A brand new type with no records, so a hierarchy can be tried without existing records getting in the way.
    private static async Task<string> TreeTypeAsync(ModelEditor editor, string type = "Area")
    {
        await editor.AddTypeAsync(type);
        await editor.AddRelationshipAsync(type, "PARENT_OF", type);
        await editor.AddFieldAsync(FieldHome.OfType(type), new FieldSpec("level", FieldType.Choice, ["top", "sub"], Required: true));
        return type;
    }

    [Fact]
    public async Task Outline_has_every_type_field_relationship_and_attribute()
    {
        var (_, editor, _) = await ExampleAsync();

        var outline = await editor.OutlineAsync();

        Assert.Equal(["Person", "Team", "Client", "Project"], outline.Types.Select(t => t.Name));
        var person = outline.Types.Single(t => t.Name == "Person");
        Assert.Contains(person.Fields, f => f.Key == "skills" && f.Multiple);
        var worksOn = outline.Relationships.Single(r => r.Rel == "WORKS_ON");
        Assert.Equal("Person", worksOn.From);
        Assert.Equal("Project", worksOn.To);
        Assert.Contains(worksOn.Attributes, a => a.Key == "role");
        Assert.Contains(worksOn.Attributes, a => a.Key == "days_a_week" && a.Type == FieldType.Number);
    }

    // ---- Types

    [Fact]
    public async Task Adds_a_type_with_a_colour_and_an_icon()
    {
        var (_, editor, _) = await ExampleAsync();

        await editor.AddTypeAsync("Supplier", "#3a7bd5", "Store");

        var type = (await editor.OutlineAsync()).Types.Single(t => t.Name == "Supplier");
        Assert.Equal("#3a7bd5", type.Color);
        Assert.Empty(type.Fields);
    }

    [Fact]
    public async Task Refuses_a_type_name_that_is_not_capitalised_or_already_used()
    {
        var (_, editor, _) = await ExampleAsync();

        await Assert.ThrowsAsync<LinkbaseValidationException>(() => editor.AddTypeAsync("supplier"));
        await Assert.ThrowsAsync<LinkbaseValidationException>(() => editor.AddTypeAsync("Person"));
    }

    [Fact]
    public async Task Refuses_an_invalid_colour_or_unknown_icon()
    {
        var (_, editor, _) = await ExampleAsync();

        await Assert.ThrowsAsync<LinkbaseValidationException>(() => editor.AddTypeAsync("Supplier", "blue"));
        await Assert.ThrowsAsync<LinkbaseValidationException>(() => editor.AddTypeAsync("Supplier", null, "NotAnIcon"));
    }

    [Fact]
    public async Task Deletes_a_type_with_no_records_and_no_relationships()
    {
        var (_, editor, _) = await ExampleAsync();
        await editor.AddTypeAsync("Supplier");

        await editor.DeleteTypeAsync("Supplier");

        Assert.DoesNotContain(await editor.OutlineAsync().ContinueWith(t => t.Result.Types.Select(ty => ty.Name)), n => n == "Supplier");
    }

    [Fact]
    public async Task Refuses_to_delete_a_type_with_records_or_in_a_relationship()
    {
        var (_, editor, _) = await ExampleAsync();

        await Assert.ThrowsAsync<LinkbaseValidationException>(() => editor.DeleteTypeAsync("Person"));
        await editor.AddTypeAsync("Supplier");
        await editor.AddRelationshipAsync("Supplier", "SUPPLIES", "Client");
        await Assert.ThrowsAsync<LinkbaseValidationException>(() => editor.DeleteTypeAsync("Supplier"));
    }

    [Fact]
    public async Task Updates_a_types_colour_icon_and_name_template()
    {
        var (_, editor, _) = await ExampleAsync();

        await editor.UpdateTypeAsync("Client", "#111111", "Domain", null);

        var type = (await editor.OutlineAsync()).Types.Single(t => t.Name == "Client");
        Assert.Equal("#111111", type.Color);
        Assert.Equal("Domain", type.Icon);
    }

    [Fact]
    public async Task Refuses_a_name_template_using_a_field_the_type_does_not_have()
    {
        var (_, editor, _) = await ExampleAsync();

        await Assert.ThrowsAsync<LinkbaseValidationException>(() => editor.UpdateTypeAsync("Client", null, null, "{nope}"));
    }

    // ---- Fields

    [Fact]
    public async Task Adds_a_field_to_a_type()
    {
        var (_, editor, records) = await ExampleAsync();

        await editor.AddFieldAsync(FieldHome.OfType("Client"), new FieldSpec("website", FieldType.Text));

        var client = await records.TypeAsync("Client");
        Assert.Contains(client!.Fields, f => f.Key == "website");
    }

    [Fact]
    public async Task Refuses_a_field_that_already_exists_or_has_a_bad_key()
    {
        var (_, editor, _) = await ExampleAsync();

        await Assert.ThrowsAsync<LinkbaseValidationException>(() => editor.AddFieldAsync(FieldHome.OfType("Client"), new FieldSpec("sector", FieldType.Text)));
        await Assert.ThrowsAsync<LinkbaseValidationException>(() => editor.AddFieldAsync(FieldHome.OfType("Client"), new FieldSpec("Website", FieldType.Text)));
        await Assert.ThrowsAsync<LinkbaseValidationException>(() => editor.AddFieldAsync(FieldHome.OfType("Client"), new FieldSpec("name", FieldType.Text)));
    }

    [Fact]
    public async Task Refuses_a_required_field_when_records_already_have_none()
    {
        var (_, editor, _) = await ExampleAsync();

        await Assert.ThrowsAsync<LinkbaseValidationException>(
            () => editor.AddFieldAsync(FieldHome.OfType("Client"), new FieldSpec("website", FieldType.Text, Required: true)));
    }

    [Fact]
    public async Task Renames_a_field_and_keeps_its_values()
    {
        var (_, editor, records) = await ExampleAsync();

        await editor.RenameFieldAsync(FieldHome.OfType("Client"), "sector", "industry");

        var client = await records.TypeAsync("Client");
        Assert.Contains(client!.Fields, f => f.Key == "industry");
        Assert.DoesNotContain(client.Fields, f => f.Key == "sector");
        var list = await records.ListAsync("Client", null, 0, 50);
        Assert.Contains(list.Rows, r => r.Values.ContainsKey("industry") && r.Values["industry"].Length > 0);
    }

    [Fact]
    public async Task Renames_the_name_template_and_hierarchy_field_along_with_the_field()
    {
        var (_, editor, records) = await ExampleAsync();

        await editor.RenameFieldAsync(FieldHome.OfType("Person"), "name_first", "first_name");

        var person = await records.TypeAsync("Person");
        Assert.Equal("{first_name} {name_last}", person!.NameTemplate);
    }

    [Fact]
    public async Task Changes_a_text_field_to_a_choice_using_the_values_found()
    {
        var (_, editor, records) = await ExampleAsync();

        await editor.ChangeFieldTypeAsync(FieldHome.OfType("Client"), "sector", FieldType.Choice);

        var client = await records.TypeAsync("Client");
        var field = client!.Fields.Single(f => f.Key == "sector");
        Assert.Equal(FieldType.Choice, field.Type);
        Assert.NotEmpty(field.Options);
    }

    [Fact]
    public async Task Refuses_to_change_type_when_a_value_does_not_fit()
    {
        var (_, editor, _) = await ExampleAsync();

        await Assert.ThrowsAsync<LinkbaseValidationException>(() => editor.ChangeFieldTypeAsync(FieldHome.OfType("Client"), "sector", FieldType.Number));
    }

    [Fact]
    public async Task Refuses_to_remove_a_choice_option_still_in_use()
    {
        var (_, editor, _) = await ExampleAsync();

        await Assert.ThrowsAsync<LinkbaseValidationException>(() => editor.UpdateFieldAsync(
            FieldHome.OfType("Project"), new FieldSpec("status", FieldType.Choice, ["active", "closed"], Required: true)));
    }

    [Fact]
    public async Task Deletes_a_field_only_when_told_to_discard_its_values()
    {
        var (_, editor, records) = await ExampleAsync();

        await Assert.ThrowsAsync<LinkbaseValidationException>(() => editor.DeleteFieldAsync(FieldHome.OfType("Client"), "sector"));
        var discarded = await editor.DeleteFieldAsync(FieldHome.OfType("Client"), "sector", discardValues: true);

        Assert.True(discarded > 0);
        var client = await records.TypeAsync("Client");
        Assert.DoesNotContain(client!.Fields, f => f.Key == "sector");
    }

    [Fact]
    public async Task Refuses_to_change_a_field_a_hierarchy_depends_on()
    {
        var (_, editor, _) = await ExampleAsync();
        var type = await TreeTypeAsync(editor);
        await editor.SetHierarchyAsync(type, new HierarchySpec("level", "PARENT_OF", [new LevelSpec("top", true, []), new LevelSpec("sub", false, ["top"])]));

        await Assert.ThrowsAsync<LinkbaseValidationException>(() => editor.DeleteFieldAsync(FieldHome.OfType(type), "level", discardValues: true));
    }

    // ---- Link values

    [Fact]
    public async Task Adds_a_value_to_a_relationships_links()
    {
        var (_, editor, records) = await ExampleAsync();

        await editor.AddFieldAsync(FieldHome.OfLink("Person", "WORKS_ON", "Project"), new FieldSpec("billable", FieldType.Boolean));

        var attributes = await records.LinkAttributesAsync("Person", "WORKS_ON", "Project");
        Assert.Contains(attributes, a => a.Key == "billable");
    }

    [Fact]
    public async Task Refuses_a_value_on_a_relationship_that_does_not_exist()
    {
        var (_, editor, _) = await ExampleAsync();

        await Assert.ThrowsAsync<LinkbaseValidationException>(() => editor.AddFieldAsync(FieldHome.OfLink("Person", "NOPE", "Project"), new FieldSpec("x", FieldType.Text)));
    }

    [Fact]
    public async Task Renames_a_link_value_and_keeps_links_values()
    {
        var (_, editor, records) = await ExampleAsync();

        await editor.RenameFieldAsync(FieldHome.OfLink("Person", "WORKS_ON", "Project"), "role", "job_title");

        var attributes = await records.LinkAttributesAsync("Person", "WORKS_ON", "Project");
        Assert.Contains(attributes, a => a.Key == "job_title");
        Assert.DoesNotContain(attributes, a => a.Key == "role");
    }

    // ---- Relationships

    [Fact]
    public async Task Adds_a_relationship_between_two_types()
    {
        var (_, editor, records) = await ExampleAsync();

        await editor.AddRelationshipAsync("Client", "PARTNERED_WITH", "Team");

        var rel = (await editor.OutlineAsync()).Relationships.Single(r => r.Rel == "PARTNERED_WITH");
        Assert.Equal("Client", rel.From);
        Assert.Equal("Team", rel.To);
    }

    [Fact]
    public async Task Refuses_a_relationship_that_already_exists_or_a_bad_name()
    {
        var (_, editor, _) = await ExampleAsync();

        await Assert.ThrowsAsync<LinkbaseValidationException>(() => editor.AddRelationshipAsync("Person", "WORKS_ON", "Project"));
        await Assert.ThrowsAsync<LinkbaseValidationException>(() => editor.AddRelationshipAsync("Person", "works_on_2", "Project"));
    }

    [Fact]
    public async Task Renames_a_relationship_and_its_links()
    {
        var (_, editor, _) = await ExampleAsync();

        await editor.RenameRelationshipAsync("Person", "WORKS_ON", "Project", "STAFFED_ON");

        var outline = await editor.OutlineAsync();
        Assert.Contains(outline.Relationships, r => r.Rel == "STAFFED_ON");
        Assert.DoesNotContain(outline.Relationships, r => r.Rel == "WORKS_ON");
    }

    [Fact]
    public async Task Refuses_required_when_a_record_has_no_link_and_a_max_already_exceeded()
    {
        var (_, editor, _) = await ExampleAsync();
        await editor.AddRelationshipAsync("Client", "PARTNERED_WITH", "Team");

        await Assert.ThrowsAsync<LinkbaseValidationException>(() => editor.SetRelationshipRulesAsync("Client", "PARTNERED_WITH", "Team", required: true, max: null));
        await Assert.ThrowsAsync<LinkbaseValidationException>(() => editor.SetRelationshipRulesAsync("Person", "MEMBER_OF", "Team", required: false, max: 0));
    }

    [Fact]
    public async Task Deletes_a_relationship_with_no_links()
    {
        var (_, editor, _) = await ExampleAsync();
        await editor.AddRelationshipAsync("Client", "PARTNERED_WITH", "Team");

        await editor.DeleteRelationshipAsync("Client", "PARTNERED_WITH", "Team");

        Assert.DoesNotContain((await editor.OutlineAsync()).Relationships, r => r.Rel == "PARTNERED_WITH");
    }

    [Fact]
    public async Task Refuses_to_delete_a_relationship_that_still_has_links()
    {
        var (_, editor, _) = await ExampleAsync();

        await Assert.ThrowsAsync<LinkbaseValidationException>(() => editor.DeleteRelationshipAsync("Person", "WORKS_ON", "Project"));
    }

    // ---- Hierarchies

    [Fact]
    public async Task Sets_a_hierarchy_then_clears_it()
    {
        var (_, editor, _) = await ExampleAsync();
        var type = await TreeTypeAsync(editor);

        await editor.SetHierarchyAsync(type, new HierarchySpec("level", "PARENT_OF", [new LevelSpec("top", true, []), new LevelSpec("sub", false, ["top"])]));

        var area = (await editor.OutlineAsync()).Types.Single(t => t.Name == type);
        Assert.NotNull(area.Hierarchy);
        Assert.Equal(2, area.Hierarchy!.Levels.Count);

        await editor.SetHierarchyAsync(type, null);
        area = (await editor.OutlineAsync()).Types.Single(t => t.Name == type);
        Assert.Null(area.Hierarchy);
    }

    [Fact]
    public async Task Refuses_a_hierarchy_whose_levels_do_not_cover_the_choices_or_has_no_root()
    {
        var (_, editor, _) = await ExampleAsync();
        var type = await TreeTypeAsync(editor);

        await Assert.ThrowsAsync<LinkbaseValidationException>(() => editor.SetHierarchyAsync(
            type, new HierarchySpec("level", "PARENT_OF", [new LevelSpec("top", false, ["sub"]), new LevelSpec("sub", false, ["top"])])));
    }

    [Fact]
    public async Task Refuses_a_hierarchy_via_a_required_relationship()
    {
        var (_, editor, _) = await ExampleAsync();
        await editor.AddTypeAsync("Area");
        await editor.AddRelationshipAsync("Area", "PARENT_OF", "Area", required: true);
        await editor.AddFieldAsync(FieldHome.OfType("Area"), new FieldSpec("level", FieldType.Choice, ["top", "sub"], Required: true));

        await Assert.ThrowsAsync<LinkbaseValidationException>(() => editor.SetHierarchyAsync(
            "Area", new HierarchySpec("level", "PARENT_OF", [new LevelSpec("top", true, []), new LevelSpec("sub", false, ["top"])])));
    }

    // ---- Audit trail

    [Fact]
    public async Task Writes_a_definition_entry_for_each_change()
    {
        var (factory, editor, _) = await ExampleAsync();

        await editor.AddTypeAsync("Supplier");

        await using var db = factory.CreateDbContext();
        var change = await db.Changes.AsNoTracking().SingleAsync(c => c.EntityId == "type:Supplier");
        Assert.Equal(ChangeEntityKind.Definition, change.EntityKind);
        Assert.Equal(ChangeOp.Create, change.Op);
    }
}
