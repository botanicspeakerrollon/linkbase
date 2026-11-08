using Bunit;
using Linkbase.Core;
using Linkbase.Core.Explore;
using Linkbase.Core.Graph;
using Linkbase.Core.Models;
using Linkbase.Core.Records;
using Linkbase.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Linkbase.Tests;

internal sealed class ModelFactory(string connectionString) : IDbContextFactory<LinkbaseDbContext>
{
    public LinkbaseDbContext CreateDbContext() =>
        new(new DbContextOptionsBuilder<LinkbaseDbContext>().UseNpgsql(connectionString).Options);
}

[Trait("Category", "Database")]
[Collection(PostgresCollection.Name)]
public sealed class RecordRepositoryTests(PostgresFixture fixture)
{
    private static readonly string Example = RepoPaths.Resolve("models/consultancy/model.json");

    // A fresh database with the example consultancy in it.
    private async Task<(ModelFactory Factory, RecordRepository Records)> ExampleAsync()
    {
        var factory = new ModelFactory(await fixture.CreateModelDatabaseAsync("SELECT 1"));
        await new ModelLoader(factory).LoadAsync(Example, TextWriter.Null);
        return (factory, new RecordRepository(factory));
    }

    [Fact]
    public async Task Types_come_in_the_model_files_order_with_their_fields()
    {
        var (_, records) = await ExampleAsync();

        var types = await records.TypesAsync();

        Assert.Equal(["Person", "Team", "Client", "Project"], types.Select(t => t.Name));
        Assert.Equal([new RecordField("title", FieldType.Text), new RecordField("joined", FieldType.Date)], types[0].Fields);
        Assert.Equal("#4338ca", (await records.TypeAsync("Team"))!.Color);
        Assert.Null(await records.TypeAsync("Nope"));
    }

    [Fact]
    public async Task Lists_in_name_order_a_page_at_a_time_and_searches_names_and_values()
    {
        var (_, records) = await ExampleAsync();

        var first = await records.ListAsync("Person", null, 0, 3);
        Assert.Equal(8, first.Total);
        Assert.Equal(["Ava Lindqvist", "Ben Okafor", "Chloe Marsh"], first.Rows.Select(r => r.Name));
        Assert.Equal("Backend engineer", (await records.ListAsync("Person", null, 1, 3)).Rows.Single(r => r.Key == "p4").Values["title"]);

        var engineers = await records.ListAsync("Person", "  engineer ", 0, 50);
        Assert.All(engineers.Rows, r => Assert.Contains("engineer", r.Values["title"], StringComparison.OrdinalIgnoreCase));
        Assert.NotEmpty(engineers.Rows);
        Assert.Equal(0, (await records.ListAsync("Person", "100%_", 0, 50)).Total);
    }

    [Fact]
    public async Task Shows_a_record_and_its_links_both_ways()
    {
        var (_, records) = await ExampleAsync();

        var dev = (await records.GetAsync("Person", "p4"))!;
        Assert.Equal(("person:p4", "Dev Raman", "2021-06-01"), (dev.Id, dev.Name, dev.Values["joined"]));
        Assert.Null(await records.GetAsync("Person", "nope"));

        var links = await records.LinksAsync(dev.Id);
        var worksOn = links.Single(g => g.RelType == "WORKS_ON" && g.Outgoing);
        Assert.Equal(["Patient portal", "Stock forecasting"], worksOn.Links.Select(l => l.OtherName));
        Assert.Equal(("Project", 2), (worksOn.Links[0].OtherType, worksOn.Total));

        var portal = await records.LinksAsync(worksOn.Links[0].OtherId);
        Assert.Contains(portal, g => g.RelType == "WORKS_ON" && !g.Outgoing && g.Links.Any(l => l.OtherName == "Dev Raman"));
    }

    [Fact]
    public async Task Only_the_first_few_links_come_back_with_the_total()
    {
        var factory = new ModelFactory(await fixture.CreateModelDatabaseAsync(
            """
            INSERT INTO node_type (name, sort_order) VALUES ('Hub', 1), ('Spoke', 2);
            INSERT INTO link_definition (source_type, rel_type, target_type) VALUES ('Spoke', 'TO', 'Hub');
            INSERT INTO node (id, user_id, type, key, name, props) VALUES ('hub:1', 1, 'Hub', '1', 'Centre', '{}');
            INSERT INTO node (id, user_id, type, key, name, props) SELECT 'spoke:' || i, 1, 'Spoke', i::text, 'Spoke ' || lpad(i::text, 2, '0'), '{}' FROM generate_series(1, 30) i;
            INSERT INTO node_link (source_id, rel_type, target_id) SELECT 'spoke:' || i, 'TO', 'hub:1' FROM generate_series(1, 30) i;
            """));

        var group = (await new RecordRepository(factory).LinksAsync("hub:1")).Single();

        Assert.Equal(30, group.Total);
        Assert.Equal(RecordRepository.LinksShown, group.Links.Count);
        Assert.Equal("Spoke 01", group.Links[0].OtherName);
    }

    [Fact]
    public async Task Creates_records_with_typed_values_and_a_key_made_from_the_name()
    {
        var (factory, records) = await ExampleAsync();

        var key = await records.CreateAsync("Person", null, new RecordInput(" Zoë Adams ", new Dictionary<string, string?> { ["title"] = "Designer", ["joined"] = "2026-01-05" }));
        var again = await records.CreateAsync("Person", "", new RecordInput("Zoë Adams", new Dictionary<string, string?>()));
        var given = await records.CreateAsync("Project", "x-1", new RecordInput("Website", new Dictionary<string, string?> { ["budget"] = "12500.50" }));

        Assert.Equal(("zoe-adams", "zoe-adams-2", "x-1"), (key, again, given));
        var zoe = (await records.GetAsync("Person", key))!;
        Assert.Equal(("Zoë Adams", "Designer", "2026-01-05"), (zoe.Name, zoe.Values["title"], zoe.Values["joined"]));
        Assert.Equal(zoe.CreatedAt, zoe.UpdatedAt);

        await using var db = factory.CreateDbContext();
        var budget = await db.Database.SqlQueryRaw<string>("SELECT jsonb_typeof(props->'budget') AS \"Value\" FROM node WHERE id = 'project:x-1'").SingleAsync();
        Assert.Equal("number", budget);
        Assert.Contains("Website", await db.Nodes.Where(n => n.Id == "project:x-1").Select(n => n.SearchText).SingleAsync());
    }

    [Theory]
    [InlineData("Person", "p1", "Someone", "joined", "2026-01-01", "already a Person with the key p1")]
    [InlineData("Person", null, "  ", "title", "x", "needs a name")]
    [InlineData("Person", null, "Someone", "joined", "5 May", "must be a date")]
    [InlineData("Project", null, "Something", "budget", "lots", "must be a number")]
    [InlineData("Project", null, "Something", "status", "paused", "Status must be one of proposed, active, live or closed")]
    [InlineData("Nope", null, "Something", "x", "y", "no type called Nope")]
    public async Task Explains_why_a_record_cant_be_created(string type, string? key, string name, string field, string value, string message)
    {
        var (_, records) = await ExampleAsync();

        var ex = await Assert.ThrowsAsync<LinkbaseValidationException>(() =>
            records.CreateAsync(type, key, new RecordInput(name, new Dictionary<string, string?> { [field] = value })));

        Assert.Contains(message, ex.Message);
    }

    [Fact]
    public async Task A_choice_is_stored_in_its_options_spelling_and_offers_them_in_order()
    {
        var (_, records) = await ExampleAsync();

        var key = await records.CreateAsync("Project", null, new RecordInput("Website", new Dictionary<string, string?> { ["status"] = " LIVE " }));

        Assert.Equal("live", (await records.GetAsync("Project", key))!.Values["status"]);
        var status = (await records.TypeAsync("Project"))!.Fields.Single(f => f.Key == "status");
        Assert.Equal(new RecordField("status", FieldType.Choice, ["proposed", "active", "live", "closed"]), status);
    }

    [Fact]
    public async Task Booleans_are_checked_and_stored_typed()
    {
        var factory = new ModelFactory(await fixture.CreateModelDatabaseAsync(
            """
            INSERT INTO node_type (name, sort_order) VALUES ('Thing', 1);
            INSERT INTO field_definition (node_type_name, key, type) VALUES ('Thing', 'active', 'boolean');
            """));
        var records = new RecordRepository(factory);

        var key = await records.CreateAsync("Thing", null, new RecordInput("Lamp", new Dictionary<string, string?> { ["active"] = "true" }));

        Assert.Equal("true", (await records.GetAsync("Thing", key))!.Values["active"]);
        var ex = await Assert.ThrowsAsync<LinkbaseValidationException>(() =>
            records.CreateAsync("Thing", null, new RecordInput("Fan", new Dictionary<string, string?> { ["active"] = "maybe" })));
        Assert.Contains("true or false", ex.Message);
    }

    [Fact]
    public async Task Editing_changes_the_name_and_fields_and_keeps_values_the_model_doesnt_define()
    {
        var factory = new ModelFactory(await fixture.CreateModelDatabaseAsync(
            """
            INSERT INTO node_type (name, sort_order) VALUES ('Thing', 1);
            INSERT INTO field_definition (node_type_name, key, type) VALUES ('Thing', 'size', 'number'), ('Thing', 'colour', 'text');
            INSERT INTO node (id, user_id, type, key, name, props, created_at, updated_at)
            VALUES ('thing:1', 1, 'Thing', '1', 'Old', '{"size": 3, "colour": "red", "legacy": "kept"}', now() - interval '1 day', now() - interval '1 day');
            """));
        var records = new RecordRepository(factory);

        await records.UpdateAsync("thing:1", new RecordInput("New", new Dictionary<string, string?> { ["size"] = "4.5", ["colour"] = "" }));

        var thing = (await records.GetAsync("Thing", "1"))!;
        Assert.Equal("New", thing.Name);
        Assert.Equal(new Dictionary<string, string> { ["size"] = "4.5", ["legacy"] = "kept" }, thing.Values);
        Assert.True(thing.UpdatedAt > thing.CreatedAt);
        await Assert.ThrowsAsync<LinkbaseValidationException>(() => records.UpdateAsync("thing:nope", new RecordInput("X", new Dictionary<string, string?>())));
    }

    [Fact]
    public async Task Links_only_follow_the_models_relationships_and_a_linked_record_cant_be_deleted()
    {
        var (factory, records) = await ExampleAsync();
        var key = await records.CreateAsync("Person", null, new RecordInput("New starter", new Dictionary<string, string?>()));
        var id = $"person:{key}";

        Assert.Contains(new LinkOption("WORKS_ON", true, "Project"), await records.LinkOptionsAsync("Person"));
        Assert.Contains(new LinkOption("WORKS_ON", false, "Person"), await records.LinkOptionsAsync("Project"));
        var project = (await records.FindAsync("Project", "portal", 10)).Single();
        Assert.Equal("Patient portal", project.Name);
        Assert.Single(await records.FindAsync("Project", project.Key, 10));

        var graph = new GraphRepository(factory);
        long WorksOn(IEnumerable<ConnectionCount> counts) => counts.Single(c => c.RelType == "WORKS_ON").Links;
        await records.AddLinkAsync(id, "WORKS_ON", project.Id);
        Assert.Equal(13, WorksOn(await graph.ConnectionCountsAsync()));
        Assert.Contains("already linked", (await Assert.ThrowsAsync<LinkbaseValidationException>(() => records.AddLinkAsync(id, "WORKS_ON", project.Id))).Message);
        Assert.Contains("no Project WORKS_ON Person", (await Assert.ThrowsAsync<LinkbaseValidationException>(() => records.AddLinkAsync(project.Id, "WORKS_ON", id))).Message);
        Assert.Contains("no longer exists", (await Assert.ThrowsAsync<LinkbaseValidationException>(() => records.AddLinkAsync(id, "WORKS_ON", "project:nope"))).Message);

        Assert.Contains("1 link(s)", (await Assert.ThrowsAsync<LinkbaseValidationException>(() => records.DeleteAsync(id))).Message);
        var link = (await records.LinksAsync(id)).Single().Links.Single();
        await records.RemoveLinkAsync(link.Id);
        await records.RemoveLinkAsync(link.Id);
        Assert.Equal(12, WorksOn(await graph.ConnectionCountsAsync()));
        await records.DeleteAsync(id);
        Assert.Null(await records.GetAsync("Person", key));
    }

    [Fact]
    public void Explore_opens_a_model_files_types_on_their_record_page()
    {
        Assert.Equal("/data/Stop/8503000%3A0", GraphStyle.Link(new GraphNode("stop:8503000:0", "Stop", "Zürich HB", [])));
        Assert.Equal("/data/Big%20Thing", GraphStyle.RecordListLink("Big Thing"));
    }

    [Theory]
    [InlineData(940, "940")]
    [InlineData(9_440, "9.4k")]
    [InlineData(943_759, "944k")]
    [InlineData(999_700, "1M")]
    [InlineData(2_573_093, "2.6M")]
    [InlineData(41_970_007, "42M")]
    public void Counts_are_short_enough_to_draw_on_a_link(long count, string shown) => Assert.Equal(shown, GraphStyle.Compact(count));
}

// The generated pages, against a model database of their own so they don't disturb the climbing data the other tests use.
[Trait("Category", "Database")]
[Collection(PostgresCollection.Name)]
public sealed class RecordPageTests(PostgresFixture fixture) : UiTestBase(fixture)
{
    private RecordRepository Records => Services.GetRequiredService<RecordRepository>();

    private async Task UseExampleAsync()
    {
        var factory = new ModelFactory(await Fixture.CreateModelDatabaseAsync("SELECT 1"));
        await new ModelLoader(factory).LoadAsync(RepoPaths.Resolve("models/consultancy/model.json"), TextWriter.Null);
        Services.AddSingleton<IDbContextFactory<LinkbaseDbContext>>(factory);
    }

    [Fact]
    public async Task The_list_shows_every_record_of_a_type_and_searches()
    {
        await UseExampleAsync();
        var cut = RenderPage<RecordListPage>(p => p.Add(x => x.Type, "Person"));

        Sees(cut, "Ava Lindqvist");
        Sees(cut, "Backend engineer");
        Sees(cut, "/data/Person/p4");

        Type(cut, "Search", "Dev");
        cut.WaitForAssertion(() => Assert.DoesNotContain("Ava Lindqvist", cut.Markup));
        Sees(cut, "Dev Raman");

        Type(cut, "Search", "zzz");
        Sees(cut, "Nothing matches that.");
    }

    [Fact]
    public async Task Says_when_there_is_no_such_type_or_record()
    {
        await UseExampleAsync();

        Sees(RenderPage<RecordListPage>(p => p.Add(x => x.Type, "Nope")), "no type called Nope");
        Sees(Render<RecordPage>(p => p.Add(x => x.Type, "Person").Add(x => x.Key, "nope")), "no Person with the key nope");
    }

    [Fact]
    public async Task Adding_a_record_opens_it()
    {
        await UseExampleAsync();
        var cut = RenderPage<RecordListPage>(p => p.Add(x => x.Type, "Team"));

        Press(cut, "Add Team");
        Type(Dialogs, "Name", "Data science");
        Press(Dialogs, "Save");

        NavigatedTo("/data/Team/data-science");
        Assert.NotNull(await Records.GetAsync("Team", "data-science"));
    }

    [Fact]
    public async Task A_record_shows_its_values_and_links_and_can_be_edited()
    {
        await UseExampleAsync();
        var cut = RenderPage<RecordPage>(p => p.Add(x => x.Type, "Person").Add(x => x.Key, "p4"));

        Sees(cut, "Dev Raman");
        Sees(cut, "2021-06-01");
        Sees(cut, "WORKS_ON →");
        Sees(cut, "Patient portal");

        Press(cut, "Edit");
        Type(Dialogs, "Title", "Lead backend engineer");
        Press(Dialogs, "Save");

        Told("Saved.");
        Sees(cut, "Lead backend engineer");
        Assert.Equal("Lead backend engineer", (await Records.GetAsync("Person", "p4"))!.Values["title"]);
    }

    [Fact]
    public async Task A_record_with_links_isnt_deleted_until_they_are_removed()
    {
        await UseExampleAsync();
        var cut = RenderPage<RecordPage>(p => p.Add(x => x.Type, "Client").Add(x => x.Key, "c2"));
        Sees(cut, "Remove the link to");

        Press(cut, "Delete");
        Press(Dialogs, "Delete");
        Told("Remove them first");

        while (cut.FindAll("button[aria-label^='Remove the link to']").Count > 0)
        {
            var before = cut.FindAll("button[aria-label^='Remove the link to']").Count;
            ClickFresh(cut, () => cut.Find("button[aria-label^='Remove the link to']"));
            cut.WaitForAssertion(() => Assert.True(cut.FindAll("button[aria-label^='Remove the link to']").Count < before));
        }

        Sees(cut, "No links yet.");
        Press(cut, "Delete");
        Press(Dialogs, "Delete");
        NavigatedTo("/data/Client");
        Assert.Null(await Records.GetAsync("Client", "c2"));
    }

    [Fact]
    public async Task Links_are_added_from_the_models_relationships()
    {
        await UseExampleAsync();
        // A client can only be linked one way, from a project, so that's chosen already.
        var key = await Records.CreateAsync("Client", null, new RecordInput("New client", new Dictionary<string, string?>()));
        var cut = RenderPage<RecordPage>(p => p.Add(x => x.Type, "Client").Add(x => x.Key, key));
        Sees(cut, "No links yet.");

        Press(cut, "Add link");
        Sees(Dialogs, "Project FOR Client");
        Pick(Dialogs, "Project", "repairs", "Repairs tracker");
        Press(Dialogs, "Link");

        Told("Linked.");
        Sees(cut, "Repairs tracker");
        Sees(cut, "← FOR");
    }

    [Fact]
    public async Task Home_links_a_model_files_types_to_their_lists()
    {
        await UseExampleAsync();
        var cut = RenderPage<Linkbase.Components.Pages.Home>();

        Sees(cut, "href=\"/data/Person\"");
    }
}
