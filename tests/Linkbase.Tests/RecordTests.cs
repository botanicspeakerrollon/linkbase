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
        Assert.Equal([new RecordField("title", FieldType.Text), new RecordField("joined", FieldType.Date), new RecordField("skills", FieldType.List)], types[0].Fields);
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
    public async Task Sorts_by_any_field_with_blanks_last_and_numbers_as_numbers()
    {
        var (_, records) = await ExampleAsync();

        var byBudget = await records.ListAsync("Project", null, new RecordSort("budget", true), [], 0, 50);
        Assert.Equal(["Patient portal", "Timetable app"], byBudget.Rows.Take(2).Select(r => r.Name));
        var byJoined = await records.ListAsync("Person", null, new RecordSort("joined"), [], 0, 3);
        Assert.Equal(["Chloe Marsh", "Ava Lindqvist", "Hugo Brandt"], byJoined.Rows.Select(r => r.Name));
        var byKey = await records.ListAsync("Person", null, new RecordSort("key", true), [], 0, 1);
        Assert.Equal("p8", byKey.Rows.Single().Key);
        var bySkills = await records.ListAsync("Person", null, new RecordSort("skills"), [], 0, 50);
        Assert.Equal("Finn Doyle", bySkills.Rows[^1].Name);
    }

    [Fact]
    public async Task Filters_by_field_and_combines_filters_with_search_and_paging()
    {
        var (_, records) = await ExampleAsync();

        var active = await records.ListAsync("Project", null, null, [new RecordFilter("status", FilterOp.Is, "Active")], 0, 50);
        Assert.Equal(["Booking redesign", "Repairs tracker", "Stock forecasting"], active.Rows.Select(r => r.Name));

        var expensive = await records.ListAsync("Project", null, null,
            [new RecordFilter("budget", FilterOp.AtLeast, "40000"), new RecordFilter("budget", FilterOp.AtMost, "85000")], 0, 50);
        Assert.Equal(["Booking redesign", "Stock forecasting", "Timetable app"], expensive.Rows.Select(r => r.Name));

        var recent = await records.ListAsync("Person", null, null, [new RecordFilter("joined", FilterOp.AtLeast, "2022-01-01")], 0, 1);
        Assert.Equal(3, recent.Total);
        Assert.Single(recent.Rows);

        var postgres = await records.ListAsync("Person", "engineer", null, [new RecordFilter("skills", FilterOp.Contains, "postgres")], 0, 50);
        Assert.Equal(["Chloe Marsh", "Dev Raman", "Grace Ito"], postgres.Rows.Select(r => r.Name));
    }

    [Fact]
    public async Task Refuses_filters_and_sorts_that_do_not_fit_the_type()
    {
        var (_, records) = await ExampleAsync();

        await Assert.ThrowsAsync<LinkbaseValidationException>(() => records.ListAsync("Person", null, new RecordSort("nope"), [], 0, 10));
        await Assert.ThrowsAsync<LinkbaseValidationException>(() => records.ListAsync("Person", null, null, [new RecordFilter("nope", FilterOp.Is, "x")], 0, 10));
        await Assert.ThrowsAsync<LinkbaseValidationException>(() => records.ListAsync("Project", null, null, [new RecordFilter("budget", FilterOp.Is, "lots")], 0, 10));
        await Assert.ThrowsAsync<LinkbaseValidationException>(() => records.ListAsync("Project", null, null, [new RecordFilter("status", FilterOp.Is, "gone")], 0, 10));
        await Assert.ThrowsAsync<LinkbaseValidationException>(() => records.ListAsync("Person", null, null, [new RecordFilter("title", FilterOp.AtLeast, "a")], 0, 10));
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
        var given = await records.CreateAsync("Project", "x-1", new RecordInput("Website", new Dictionary<string, string?> { ["budget"] = "12500.50", ["status"] = "proposed" }));

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
    [InlineData("Project", null, "Something", "status", " ", "Status is required")]
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
        Assert.Equal(new RecordField("status", FieldType.Choice, ["proposed", "active", "live", "closed"]) { Required = true }, status);
    }

    [Fact]
    public async Task A_list_keeps_each_line_once_and_shows_them_one_per_line()
    {
        var (factory, records) = await ExampleAsync();

        var key = await records.CreateAsync("Person", null, new RecordInput("Zoë Adams", new Dictionary<string, string?> { ["skills"] = " Figma \r\n\nSQL, Postgres\nfigma" }));

        var zoe = (await records.GetAsync("Person", key))!;
        Assert.Equal("Figma\nSQL, Postgres", zoe.Values["skills"]);
        var skills = (await records.TypeAsync("Person"))!.Fields.Single(f => f.Key == "skills");
        Assert.Equal("Figma, SQL, Postgres", RecordListPage.Shown(skills, zoe.Values["skills"]));
        Assert.Equal("Python\nPostgres", (await records.GetAsync("Person", "p7"))!.Values["skills"]);
        Assert.False((await records.GetAsync("Person", "p6"))!.Values.ContainsKey("skills"));

        await using var db = factory.CreateDbContext();
        var stored = await db.Database.SqlQuery<string>($"SELECT props->>'skills' AS \"Value\" FROM node WHERE id = {"person:" + key}").SingleAsync();
        Assert.Equal("""["Figma", "SQL, Postgres"]""", stored);
        Assert.Equal(1, (await records.ListAsync("Person", "user research", 0, 50)).Total);
    }

    [Fact]
    public async Task A_required_value_cant_be_removed_by_editing()
    {
        var (_, records) = await ExampleAsync();
        var project = (await records.GetAsync("Project", "pr1"))!;

        var ex = await Assert.ThrowsAsync<LinkbaseValidationException>(() =>
            records.UpdateAsync(project.Id, new RecordInput(project.Name, new Dictionary<string, string?> { ["status"] = "" })));

        Assert.Equal("Status is required.", ex.Message);
        Assert.Equal("live", (await records.GetAsync("Project", "pr1"))!.Values["status"]);
    }

    [Fact]
    public async Task A_new_record_brings_the_links_its_type_requires_and_keeps_one()
    {
        var factory = new ModelFactory(await fixture.CreateModelDatabaseAsync("SELECT 1"));
        await new ModelLoader(factory).LoadAsync(Library.Write(), TextWriter.Null);
        var records = new RecordRepository(factory);
        var graph = new GraphRepository(factory);
        long WrittenBy(IEnumerable<ConnectionCount> counts) => counts.Single(c => c.RelType == "WRITTEN_BY").Links;
        Dictionary<string, string?> Isbn() => new() { ["isbn"] = "333" };

        var none = await Assert.ThrowsAsync<LinkbaseValidationException>(() => records.CreateAsync("Book", null, new RecordInput("Quiet Ice", Isbn())));
        Assert.Equal("Each Book needs at least one Book WRITTEN_BY Author link.", none.Message);
        var wrong = await Assert.ThrowsAsync<LinkbaseValidationException>(() =>
            records.CreateAsync("Book", null, new RecordInput("Quiet Ice", Isbn()) { Links = [new NewLink("WRITTEN_BY", "book:b1")] }));
        Assert.Contains("no Book WRITTEN_BY Book relationship", wrong.Message);
        Assert.Null(await records.GetAsync("Book", "quiet-ice"));

        var key = await records.CreateAsync("Book", null, new RecordInput("Quiet Ice", Isbn()) { Links = [new NewLink("WRITTEN_BY", "author:a3")] });
        var id = (await records.GetAsync("Book", key))!.Id;
        Assert.Equal("Pia Lark", (await records.LinksAsync(id)).Single().Links.Single().OtherName);
        Assert.Equal(3, WrittenBy(await graph.ConnectionCountsAsync()));

        // Its only author can't be taken away, but one of two can.
        var only = (await records.LinksAsync(id)).Single().Links.Single();
        var last = await Assert.ThrowsAsync<LinkbaseValidationException>(() => records.RemoveLinkAsync(only.Id));
        Assert.Equal("Each Book needs at least one Book WRITTEN_BY Author link, and this is Quiet Ice's only one. Add another first.", last.Message);
        await records.AddLinkAsync(id, "WRITTEN_BY", "author:a1");
        await records.RemoveLinkAsync(only.Id);
        Assert.Equal("Mara Quill", (await records.LinksAsync(id)).Single().Links.Single().OtherName);

        // Deleting the book takes its author link with it. Deleting an author who still has books doesn't happen.
        Assert.Contains("still has 2 link(s)", (await Assert.ThrowsAsync<LinkbaseValidationException>(() => records.DeleteAsync("author:a1"))).Message);
        await records.DeleteAsync(id);
        Assert.Null(await records.GetAsync("Book", key));
        Assert.Equal(2, WrittenBy(await graph.ConnectionCountsAsync()));
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
    public async Task External_ids_find_their_record_and_go_with_it()
    {
        var (factory, records) = await ExampleAsync();
        var refs = new ExternalRefRepository(factory);
        var key = await records.CreateAsync("Person", null, new RecordInput("New starter", new Dictionary<string, string?>()));
        var id = $"person:{key}";

        await refs.AddAsync(id, DataSource.Ukc, " 123 ", "https://example.test/people/123");
        await refs.AddAsync(id, DataSource.Ukc, "123");
        await refs.AddAsync("project:pr1", DataSource.Ukc, "123");
        Assert.Equal([(DataSource.Ukc, "123", "https://example.test/people/123")], (await refs.RefsAsync(id)).Select(r => (r.Source, r.ExternalId, r.Url)));
        Assert.Equal(id, await refs.FindNodeAsync(DataSource.Ukc, "Person", "123"));
        Assert.Equal("project:pr1", await refs.FindNodeAsync(DataSource.Ukc, "Project", "123"));
        Assert.Null(await refs.FindNodeAsync(DataSource.MountainProject, "Person", "123"));

        Assert.Contains("Another Person already has the UKC id 123", (await Assert.ThrowsAsync<LinkbaseValidationException>(() => refs.AddAsync("person:p1", DataSource.Ukc, "123"))).Message);
        Assert.Contains("no longer exists", (await Assert.ThrowsAsync<LinkbaseValidationException>(() => refs.AddAsync("person:nope", DataSource.Ukc, "9"))).Message);

        await records.DeleteAsync(id);
        Assert.Null(await refs.FindNodeAsync(DataSource.Ukc, "Person", "123"));
        Assert.Equal("project:pr1", await refs.FindNodeAsync(DataSource.Ukc, "Project", "123"));

        var other = (await refs.RefsAsync("project:pr1")).Single();
        await refs.RemoveAsync(other.Id);
        Assert.Empty(await refs.RefsAsync("project:pr1"));
    }

    [Fact]
    public async Task Reloading_a_model_drops_its_nodes_refs_and_keeps_climbings()
    {
        var (factory, _) = await ExampleAsync();
        var refs = new ExternalRefRepository(factory);
        await refs.AddAsync("person:p1", DataSource.Ukc, "1");
        await using (var db = factory.CreateDbContext())
        {
            db.ExternalRefs.Add(new ExternalRef { EntityType = ExternalEntityType.Crag, EntityId = 1, Source = DataSource.Ukc, ExternalId = "1" });
            await db.SaveChangesAsync();
        }

        await new ModelLoader(factory).LoadAsync(Example, TextWriter.Null);

        Assert.Empty(await refs.RefsAsync("person:p1"));
        await using var check = factory.CreateDbContext();
        Assert.True(await check.ExternalRefs.AnyAsync(r => r.EntityType == ExternalEntityType.Crag && r.EntityId == 1));
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
    public async Task A_record_shows_its_ids_elsewhere_and_links_only_web_addresses()
    {
        await UseExampleAsync();
        var refs = Services.GetRequiredService<ExternalRefRepository>();
        await refs.AddAsync("person:p4", DataSource.Ukc, "4401", "https://example.test/people/4401");
        await refs.AddAsync("person:p4", DataSource.MountainProject, "7702", "javascript:alert(1)");

        var cut = RenderPage<RecordPage>(p => p.Add(x => x.Type, "Person").Add(x => x.Key, "p4"));

        Sees(cut, "UKC id");
        Assert.Equal("https://example.test/people/4401", cut.Find("a[href^='https://example.test']").GetAttribute("href"));
        Sees(cut, "Mountain Project id");
        Sees(cut, "7702");
        Assert.Empty(cut.FindAll("a[href^='javascript']"));
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
    public async Task A_new_record_chooses_the_links_its_type_requires()
    {
        var factory = new ModelFactory(await Fixture.CreateModelDatabaseAsync("SELECT 1"));
        await new ModelLoader(factory).LoadAsync(Library.Write(), TextWriter.Null);
        Services.AddSingleton<IDbContextFactory<LinkbaseDbContext>>(factory);
        var cut = RenderPage<RecordListPage>(p => p.Add(x => x.Type, "Book"));

        Press(cut, "Add Book");
        Sees(Dialogs, "Book WRITTEN_BY Author");
        Type(Dialogs, "Name", "Quiet Ice");
        Press(Dialogs, "Save");
        Sees(Dialogs, "Isbn is required");
        Sees(Dialogs, "Choose its Author");

        Type(Dialogs, "Isbn", "333");
        Pick(Dialogs, "Author", "pia", "Pia Lark");
        Press(Dialogs, "Save");

        NavigatedTo("/data/Book/quiet-ice");
        var book = (await Records.GetAsync("Book", "quiet-ice"))!;
        Assert.Equal("Pia Lark", (await Records.LinksAsync(book.Id)).Single().Links.Single().OtherName);
    }

    [Fact]
    public async Task A_new_record_can_create_the_record_its_link_requires()
    {
        var factory = new ModelFactory(await Fixture.CreateModelDatabaseAsync("SELECT 1"));
        await new ModelLoader(factory).LoadAsync(Library.Write(), TextWriter.Null);
        Services.AddSingleton<IDbContextFactory<LinkbaseDbContext>>(factory);
        var cut = RenderPage<RecordListPage>(p => p.Add(x => x.Type, "Book"));

        Press(cut, "Add Book");
        Type(Dialogs, "Name", "Quiet Ice");
        Type(Dialogs, "Isbn", "333");
        Press(Dialogs, "New Author");

        // The new author's dialog is on top of the book's, so its fields and buttons are the last ones.
        Dialogs.WaitForAssertion(() => Assert.Equal(2, Dialogs.FindAll(".mud-dialog").Count));
        ClickFresh(Dialogs, () => Dialogs.FindAll(".mud-dialog")[1].QuerySelectorAll("button").First(b => b.TextContent.Trim() == "Save"));
        Sees(Dialogs, "A record needs a name");
        Dialogs.FindAll(".mud-dialog")[1].QuerySelector("input")!.Change("Zed Quark");
        ClickFresh(Dialogs, () => Dialogs.FindAll(".mud-dialog")[1].QuerySelectorAll("button").First(b => b.TextContent.Trim() == "Save"));

        Sees(Dialogs, "New Author: Zed Quark");
        Assert.Null(await Records.GetAsync("Author", "zed-quark"));
        Press(Dialogs, "Save");

        NavigatedTo("/data/Book/quiet-ice");
        var book = (await Records.GetAsync("Book", "quiet-ice"))!;
        Assert.Equal("Zed Quark", (await Records.LinksAsync(book.Id)).Single().Links.Single().OtherName);
    }

    [Fact]
    public async Task Linked_records_are_created_with_the_record_or_not_at_all()
    {
        var factory = new ModelFactory(await Fixture.CreateModelDatabaseAsync("SELECT 1"));
        await new ModelLoader(factory).LoadAsync(Library.Write(), TextWriter.Null);
        var records = new RecordRepository(factory);
        var none = new Dictionary<string, string?>();

        var key = await records.CreateAsync("Book", null, new RecordInput("Quiet Ice", new Dictionary<string, string?> { ["isbn"] = "333" })
        {
            Creates = [new NewLinkedRecord("WRITTEN_BY", "Author", null, new RecordInput("Zed Quark", none))],
        });
        var links = await records.LinksAsync($"book:{key}");
        Assert.Equal("Zed Quark", links.Single().Links.Single().OtherName);
        Assert.Single(await records.RelationshipsAsync());

        // A book that fails to save takes the author created for it with it.
        var ex = await Assert.ThrowsAsync<LinkbaseValidationException>(() => records.CreateAsync("Book", null, new RecordInput("No isbn", none)
        {
            Creates = [new NewLinkedRecord("WRITTEN_BY", "Author", null, new RecordInput("Ghost Writer", none))],
        }));
        Assert.Equal("Isbn is required.", ex.Message);
        Assert.Null(await records.GetAsync("Author", "ghost-writer"));

        // The author can't be given a key that is taken, and nothing is left behind.
        await Assert.ThrowsAsync<LinkbaseValidationException>(() => records.CreateAsync("Book", null, new RecordInput("Dup", new Dictionary<string, string?> { ["isbn"] = "1" })
        {
            Creates = [new NewLinkedRecord("WRITTEN_BY", "Author", "a1", new RecordInput("Again", none))],
        }));
        Assert.Null(await records.GetAsync("Book", "dup"));

        // A link the model doesn't have is refused.
        await Assert.ThrowsAsync<LinkbaseValidationException>(() => records.CreateAsync("Book", null, new RecordInput("Odd", new Dictionary<string, string?> { ["isbn"] = "2" })
        {
            Creates = [new NewLinkedRecord("WRITTEN_BY", "Book", null, new RecordInput("Sub", new Dictionary<string, string?> { ["isbn"] = "3" }) { Links = [new NewLink("WRITTEN_BY", "author:a1")] })],
        }));
        Assert.Null(await records.GetAsync("Book", "sub"));
    }

    [Fact]
    public async Task Home_links_a_model_files_types_to_their_lists()
    {
        await UseExampleAsync();
        var cut = RenderPage<Linkbase.Components.Pages.Home>();

        Sees(cut, "href=\"/data/Person\"");
    }
}
