using System.Text.Json.Nodes;
using Linkbase.Core;
using Linkbase.Core.CsvImport;
using Linkbase.Core.Models;
using Linkbase.Core.Records;
using Linkbase.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Linkbase.Tests;

// A small made-up model in which a module keeps one field and derives one type, as climbing will keep grade ranks and
// derive trips.
internal static class OwnedModel
{
    public const string Json =
        """
        {
          "name": "Owned",
          "types": [
            {
              "name": "Route", "source": { "file": "routes.csv", "key": "id", "name": "name" },
              "fields": [ { "key": "grade" }, { "key": "grade_rank", "type": "number", "owner": "Climbing" } ]
            },
            { "name": "Ascent", "source": { "file": "ascents.csv", "key": "id", "name": "name" }, "fields": [ { "key": "date", "type": "date" } ] },
            { "name": "Trip", "owner": "Climbing", "source": { "file": "trips.csv", "key": "id", "name": "name" }, "fields": [ { "key": "country" } ] }
          ],
          "relationships": [
            { "from": "Ascent", "type": "OF", "to": "Route", "source": { "file": "of.csv", "fromColumn": "ascent", "toColumn": "route" } },
            { "from": "Ascent", "type": "ON_TRIP", "to": "Trip", "source": { "file": "on-trip.csv", "fromColumn": "ascent", "toColumn": "trip" } }
          ]
        }
        """;

    public static string Write(string folder, string json = Json)
    {
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "routes.csv"), "id,name,grade,grade_rank\nr1,Long Arete,6a,\nr2,Wet Slab,5c,\n");
        File.WriteAllText(Path.Combine(folder, "ascents.csv"), "id,name,date\na1,Long Arete,2024-05-04\na2,Wet Slab,2024-05-05\n");
        File.WriteAllText(Path.Combine(folder, "trips.csv"), "id,name,country\nt1,Spring trip,Wales\n");
        File.WriteAllText(Path.Combine(folder, "of.csv"), "ascent,route\na1,r1\na2,r2\n");
        File.WriteAllText(Path.Combine(folder, "on-trip.csv"), "ascent,trip\na1,t1\na2,t1\n");
        var path = Path.Combine(folder, "model.json");
        File.WriteAllText(path, json);
        return path;
    }

    public static async Task<ModelFactory> LoadAsync(PostgresFixture fixture, string folder)
    {
        var factory = new ModelFactory(await fixture.CreateModelDatabaseAsync("SELECT 1"));
        await TestModels.LoadAsync(factory, Write(folder), TextWriter.Null);
        return factory;
    }
}

// Stands in for climbing's hook: a route's grade rank is ten times its grade's length, and every change is noted.
internal sealed class FakeClimbingHook : IRecordHook
{
    public string Domain => "Climbing";

    public IReadOnlyCollection<string> Types { get; } = ["Route", "Ascent"];

    public List<RecordChange> Changes { get; } = [];

    public void BeforeSave(RecordSaving record)
    {
        if (record.Type == "Route")
        {
            record.Set("grade_rank", record.Value("grade") is { } grade ? JsonValue.Create(grade.GetValue<string>().Length * 10) : null);
        }
    }

    public Task AfterChangeAsync(IReadOnlyList<RecordChange> changes, CancellationToken cancellationToken)
    {
        Changes.AddRange(changes);
        return Task.CompletedTask;
    }
}

public sealed class RecordHooksTests
{
    [Fact]
    public void Hooks_run_only_for_models_that_use_their_module()
    {
        var catalog = new ModelCatalog(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Models:plain:ConnectionString"] = "Plain",
            ["Models:plain:Order"] = "1",
            ["Models:climbing:ConnectionString"] = "Climbing",
            ["Models:climbing:Domains:0"] = "Climbing",
        }).Build());
        var current = new CurrentModel(catalog);
        var hooks = new RecordHooks([new FakeClimbingHook()], current);

        Assert.False(hooks.Watch("Route"));
        Assert.Empty(hooks.Watched);

        current.Select("climbing");
        Assert.True(hooks.Watch("Route"));
        Assert.False(hooks.Watch("Trip"));
        Assert.Equal(["Route", "Ascent"], hooks.Watched);
    }

    [Fact]
    public void A_hook_sets_only_the_fields_its_module_keeps()
    {
        var type = new RecordType("Route", null, null, [new RecordField("grade", FieldType.Text), new RecordField("rank", FieldType.Number, [], "Climbing")]);
        var props = new JsonObject { ["grade"] = "6a" };
        var saving = new RecordSaving("Climbing", type, "Long Arete", props);

        saving.Set("rank", 20);
        saving.Set("not_in_this_model", 1);
        Assert.Equal("""{"grade":"6a","rank":20}""", props.ToJsonString());

        saving.Set("rank", null);
        Assert.Equal("""{"grade":"6a"}""", props.ToJsonString());
        Assert.Throws<InvalidOperationException>(() => saving.Set("grade", "7a"));
        Assert.Throws<InvalidOperationException>(() => new RecordSaving("Other", type, "x", props).Set("rank", 1));
    }
}

[Trait("Category", "Database")]
public sealed class ModuleOwnedTests(PostgresFixture fixture) : IDisposable, IClassFixture<PostgresFixture>
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), $"linkbase-owned-{Guid.NewGuid():N}");
    private readonly FakeClimbingHook _hook = new();

    public void Dispose()
    {
        if (Directory.Exists(_folder))
        {
            Directory.Delete(_folder, recursive: true);
        }
    }

    private async Task<(ModelFactory Factory, RecordRepository Records)> LoadAsync()
    {
        var factory = await OwnedModel.LoadAsync(fixture, _folder);
        return (factory, new RecordRepository(factory, new RecordHooks([_hook])));
    }

    private static async Task<long> LinksCountedAsync(ModelFactory factory, string relType)
    {
        await using var db = factory.CreateDbContext();
        return await db.LinkDefinitions.Where(d => d.RelType == relType).Select(d => d.Links).SingleAsync();
    }

    [Fact]
    public async Task The_model_file_names_who_keeps_a_field_and_derives_a_type()
    {
        var (_, records) = await LoadAsync();

        var types = await records.TypesAsync();

        Assert.Equal("Climbing", types.Single(t => t.Name == "Trip").Owner);
        Assert.Null(types.Single(t => t.Name == "Route").Owner);
        Assert.Equal([new RecordField("grade", FieldType.Text)], types.Single(t => t.Name == "Route").Editable);
        Assert.Equal("Climbing", types.Single(t => t.Name == "Route").Fields.Single(f => f.Key == "grade_rank").Owner);
    }

    [Fact]
    public void An_owner_must_be_a_domain()
    {
        var path = OwnedModel.Write(_folder, OwnedModel.Json.Replace("\"owner\": \"Climbing\" }", "\"owner\": \"Bouldering\" }"));

        var ex = Assert.Throws<LinkbaseValidationException>(() => ModelFile.Read(path));

        Assert.Contains("Field 'grade_rank' of type 'Route' is owned by 'Bouldering', which isn't a domain. The domains are: Climbing.", ex.Message);
    }

    [Fact]
    public void Only_a_types_fields_can_have_an_owner()
    {
        var path = OwnedModel.Write(_folder, OwnedModel.Json.Replace(
            "\"toColumn\": \"trip\" } }", "\"toColumn\": \"trip\" }, \"fields\": [ { \"key\": \"inferred\", \"owner\": \"Climbing\" } ] }"));

        var ex = Assert.Throws<LinkbaseValidationException>(() => ModelFile.Read(path));

        Assert.Contains("'inferred' has an owner, which only a type's fields can have", ex.Message);
    }

    [Fact]
    public async Task Saving_runs_the_hook_which_keeps_its_field_and_hears_of_each_change()
    {
        var (_, records) = await LoadAsync();

        var key = await records.CreateAsync("Route", null, new RecordInput("Green Groove", new Dictionary<string, string?>
        {
            ["grade"] = "6b+",
            ["grade_rank"] = "999",
        }));

        var route = (await records.GetAsync("Route", key))!;
        Assert.Equal("30", route.Values["grade_rank"]);
        var created = Assert.Single(_hook.Changes);
        Assert.Equal((RecordChangeKind.Created, "Route", "route:green-groove"), (created.Kind, created.Type, created.Id));
        Assert.Equal("30", created.After["grade_rank"]);

        await records.UpdateAsync(route.Id, new RecordInput("Green Groove", new Dictionary<string, string?> { ["grade"] = "7a" }));

        Assert.Equal("20", (await records.GetAsync("Route", key))!.Values["grade_rank"]);
        var updated = _hook.Changes[^1];
        Assert.Equal(RecordChangeKind.Updated, updated.Kind);
        Assert.Equal(("6b+", "7a"), (updated.Before["grade"], updated.After["grade"]));
    }

    [Fact]
    public async Task Without_a_hook_a_kept_field_keeps_its_value()
    {
        var (factory, _) = await LoadAsync();
        var records = new RecordRepository(factory);
        await using (var db = factory.CreateDbContext())
        {
            await db.Nodes.Where(n => n.Id == "route:r1").ExecuteUpdateAsync(s => s
                .SetProperty(n => n.Props, """{"grade": "6a", "grade_rank": 42}""")
                .SetProperty(n => n.UpdatedAt, DateTimeOffset.UtcNow));
        }

        await records.UpdateAsync("route:r1", new RecordInput("Long Arete", new Dictionary<string, string?> { ["grade"] = "6a+", ["grade_rank"] = "1" }));

        var route = (await records.GetAsync("Route", "r1"))!;
        Assert.Equal(("6a+", "42"), (route.Values["grade"], route.Values["grade_rank"]));
    }

    [Fact]
    public async Task Links_are_heard_of_from_both_ends()
    {
        var (_, records) = await LoadAsync();
        await records.CreateAsync("Ascent", "a3", new RecordInput("Wet Slab again", new Dictionary<string, string?>()));
        _hook.Changes.Clear();

        await records.AddLinkAsync("ascent:a3", "OF", "route:r2");

        Assert.Equal(
            [(RecordChangeKind.Linked, "ascent:a3", "route:r2"), (RecordChangeKind.Linked, "route:r2", "ascent:a3")],
            _hook.Changes.Select(c => (c.Kind, c.Id, c.OtherId!)));

        var link = (await records.LinksAsync("ascent:a3")).Single().Links.Single();
        await records.RemoveLinkAsync(link.Id);
        Assert.All(_hook.Changes.Skip(2), c => Assert.Equal(RecordChangeKind.Unlinked, c.Kind));
        Assert.Equal(4, _hook.Changes.Count);
    }

    [Fact]
    public async Task Derived_records_and_their_links_cant_be_changed_by_hand()
    {
        var (_, records) = await LoadAsync();
        var trip = (await records.GetAsync("Trip", "t1"))!;
        var link = (await records.LinksAsync("ascent:a1")).Single(g => g.RelType == "ON_TRIP").Links.Single();

        foreach (var change in new Func<Task>[]
                 {
                     () => records.CreateAsync("Trip", null, new RecordInput("Autumn trip", new Dictionary<string, string?>())),
                     () => records.UpdateAsync(trip.Id, new RecordInput("Renamed", new Dictionary<string, string?>())),
                     () => records.DeleteAsync(trip.Id),
                     () => records.AddLinkAsync("ascent:a1", "ON_TRIP", trip.Id),
                     () => records.RemoveLinkAsync(link.Id),
                 })
        {
            var ex = await Assert.ThrowsAsync<LinkbaseValidationException>(change);
            Assert.Equal("Trip records are derived by Climbing from other records, so they can't be changed here.", ex.Message);
        }

        Assert.Equal([new LinkOption("OF", true, "Route")], await records.LinkOptionsAsync("Ascent"));
        Assert.Empty(await records.LinkOptionsAsync("Trip"));
        Assert.Equal("Spring trip", (await records.GetAsync("Trip", "t1"))!.Name);
    }

    [Fact]
    public async Task Deleting_a_record_takes_its_derived_links_with_it()
    {
        var (factory, records) = await LoadAsync();
        await records.RemoveLinkAsync((await records.LinksAsync("ascent:a1")).Single(g => g.RelType == "OF").Links.Single().Id);

        await records.DeleteAsync("ascent:a1");

        Assert.Null(await records.GetAsync("Ascent", "a1"));
        Assert.Equal(1, await LinksCountedAsync(factory, "ON_TRIP"));
        Assert.Single((await records.LinksAsync("trip:t1")).Single().Links);
        var deleted = _hook.Changes[^1];
        Assert.Equal((RecordChangeKind.Deleted, "ascent:a1", "2024-05-04"), (deleted.Kind, deleted.Id, deleted.Before["date"]));

        var ex = await Assert.ThrowsAsync<LinkbaseValidationException>(() => records.DeleteAsync("ascent:a2"));
        Assert.Contains("still has 1 link(s)", ex.Message);
    }

    [Fact]
    public async Task Required_links_are_heard_of_and_go_with_derived_ones_when_their_record_is_deleted()
    {
        var factory = new ModelFactory(await fixture.CreateModelDatabaseAsync("SELECT 1"));
        await TestModels.LoadAsync(factory, 
            OwnedModel.Write(_folder, OwnedModel.Json.Replace("\"toColumn\": \"route\" } }", "\"toColumn\": \"route\" }, \"required\": true }")), TextWriter.Null);
        var records = new RecordRepository(factory, new RecordHooks([_hook]));

        await records.CreateAsync("Ascent", "a3", new RecordInput("Wet Slab again", new Dictionary<string, string?>()) { Links = [new NewLink("OF", "route:r2")] });

        Assert.Equal(
            [(RecordChangeKind.Created, "ascent:a3", null), (RecordChangeKind.Linked, "ascent:a3", "route:r2"), (RecordChangeKind.Linked, "route:r2", "ascent:a3")],
            _hook.Changes.Select(c => (c.Kind, c.Id, c.OtherId)));

        await records.DeleteAsync("ascent:a1");

        Assert.Null(await records.GetAsync("Ascent", "a1"));
        Assert.Equal((2L, 1L), (await LinksCountedAsync(factory, "OF"), await LinksCountedAsync(factory, "ON_TRIP")));
    }

    [Fact]
    public void A_required_link_cant_go_to_derived_records()
    {
        var path = OwnedModel.Write(_folder, OwnedModel.Json.Replace("\"toColumn\": \"trip\" } }", "\"toColumn\": \"trip\" }, \"required\": true }"));

        var ex = Assert.Throws<LinkbaseValidationException>(() => ModelFile.Read(path));

        Assert.Contains("Relationship Ascent ON_TRIP Trip is required, but Trip records are derived by Climbing, so people couldn't add Ascent records.", ex.Message);
    }

    [Fact]
    public async Task The_module_replaces_its_derived_records_and_links_in_one_go()
    {
        var (factory, records) = await LoadAsync();
        var refs = new ExternalRefRepository(factory);
        await refs.AddAsync("trip:t1", DataSource.Manual, "old-trip");

        var count = await records.ReplaceDerivedAsync("Climbing", "Trip",
            [
                new DerivedRecord("2024-wales", "Wales, May 2024", new Dictionary<string, string?> { ["country"] = "Wales" }),
                new DerivedRecord("2024-peaks", "Peaks, May 2024", new Dictionary<string, string?>()),
            ],
            [
                new DerivedLink("ascent:a1", "ON_TRIP", "trip:2024-wales", new JsonObject { ["inferred"] = true }),
                new DerivedLink("ascent:a2", "ON_TRIP", "trip:2024-peaks"),
            ]);

        Assert.Equal(2, count);
        Assert.Null(await records.GetAsync("Trip", "t1"));
        Assert.Equal("Wales", (await records.GetAsync("Trip", "2024-wales"))!.Values["country"]);
        Assert.Equal("Wales, May 2024", (await records.LinksAsync("ascent:a1")).Single(g => g.RelType == "ON_TRIP").Links.Single().OtherName);
        Assert.Equal(2, await LinksCountedAsync(factory, "ON_TRIP"));
        Assert.Empty(_hook.Changes);
        await using (var db = factory.CreateDbContext())
        {
            Assert.Equal("""{"inferred": true}""", await db.NodeLinks.Where(l => l.TargetId == "trip:2024-wales").Select(l => l.Props).SingleAsync());
        }

        // A record that's replaced by itself keeps its refs. One that goes loses them.
        Assert.Null(await refs.FindNodeAsync(DataSource.Manual, "Trip", "old-trip"));
        await refs.AddAsync("trip:2024-wales", DataSource.Manual, "wales");
        await records.ReplaceDerivedAsync("Climbing", "Trip", [new DerivedRecord("2024-wales", "Wales, May 2024", new Dictionary<string, string?>())], []);
        Assert.Equal("trip:2024-wales", await refs.FindNodeAsync(DataSource.Manual, "Trip", "wales"));

        await records.ReplaceDerivedAsync("Climbing", "Trip", [], []);
        Assert.Equal(0, (await records.ListAsync("Trip", null, 0, 10)).Total);
        Assert.Null(await refs.FindNodeAsync(DataSource.Manual, "Trip", "wales"));
        Assert.Equal(0, await LinksCountedAsync(factory, "ON_TRIP"));
    }

    [Fact]
    public async Task Replacing_derived_records_checks_who_asks_and_what_it_links()
    {
        var (_, records) = await LoadAsync();
        var trip = new DerivedRecord("x", "X", new Dictionary<string, string?>());

        await Assert.ThrowsAsync<InvalidOperationException>(() => records.ReplaceDerivedAsync("Other", "Trip", [trip], []));
        await Assert.ThrowsAsync<InvalidOperationException>(() => records.ReplaceDerivedAsync("Climbing", "Route", [trip], []));
        await Assert.ThrowsAsync<InvalidOperationException>(() => records.ReplaceDerivedAsync("Climbing", "Trip", [trip, trip], []));
        await Assert.ThrowsAsync<InvalidOperationException>(() => records.ReplaceDerivedAsync("Climbing", "Trip", [trip], [new DerivedLink("ascent:a1", "OF", "route:r1")]));
        await Assert.ThrowsAsync<InvalidOperationException>(() => records.ReplaceDerivedAsync("Climbing", "Trip", [trip], [new DerivedLink("ascent:nope", "ON_TRIP", "trip:x")]));
        await Assert.ThrowsAsync<InvalidOperationException>(() => records.ReplaceDerivedAsync("Climbing", "Trip", [trip], [new DerivedLink("trip:x", "ON_TRIP", "ascent:a1")]));
        Assert.Equal("Spring trip", (await records.GetAsync("Trip", "t1"))!.Name);
    }

    [Fact]
    public async Task A_csv_import_runs_the_hooks_and_cant_write_what_a_module_keeps()
    {
        var (factory, _) = await LoadAsync();
        var importer = new CsvImportService(factory, new RecordHooks([_hook]));
        var routes = Path.Combine(_folder, "more-routes.csv");
        File.WriteAllText(routes, "id,name,grade,grade_rank\nr3,Crack of Doom,E1,5\n");
        var file = CsvImportService.Read(routes, "more-routes.csv");

        var kept = await importer.PlanAsync(file, new CsvRecordMapping("Route", 0, 1, [new(2, "grade", FieldType.Text), new(3, "grade_rank", FieldType.Number)]));
        Assert.Contains("Route's grade_rank is kept by Climbing, so it can't be imported. Untick its column.", kept.Problems);

        var result = await importer.ImportAsync(file, new CsvRecordMapping("Route", 0, 1, [new(2, "grade", FieldType.Text)]));

        var records = new RecordRepository(factory);
        Assert.Equal("20", (await records.GetAsync("Route", "r3"))!.Values["grade_rank"]);
        Assert.Equal((RecordChangeKind.Created, "route:r3"), (_hook.Changes.Single().Kind, _hook.Changes.Single().Id));

        await importer.UndoAsync(result.BatchId);
        Assert.Equal((RecordChangeKind.Deleted, "route:r3"), (_hook.Changes[^1].Kind, _hook.Changes[^1].Id));

        var trips = Path.Combine(_folder, "more-trips.csv");
        File.WriteAllText(trips, "id,name,ascent\nt2,Summer trip,a1\n");
        var tripFile = CsvImportService.Read(trips, "more-trips.csv");
        Assert.Contains("Trip records are derived by Climbing from other records, so they can't be imported.",
            (await importer.PlanAsync(tripFile, new CsvRecordMapping("Trip", 0, 1, []))).Problems);
        Assert.Contains("Trip records are derived by Climbing from other records, so links to them can't be imported.",
            (await importer.PlanAsync(tripFile, new CsvLinkMapping("Ascent", 2, "ON_TRIP", "Trip", 0))).Problems);
    }

    [Fact]
    public async Task Undoing_an_import_takes_derived_links_to_its_records_with_them()
    {
        var (factory, records) = await LoadAsync();
        var importer = new CsvImportService(factory, new RecordHooks([_hook]));
        var ascents = Path.Combine(_folder, "more-ascents.csv");
        File.WriteAllText(ascents, "id,name,date\na3,Green Groove,2024-05-06\n");
        var result = await importer.ImportAsync(CsvImportService.Read(ascents, "more-ascents.csv"), new CsvRecordMapping("Ascent", 0, 1, [new(2, "date", FieldType.Date)]));
        await records.ReplaceDerivedAsync("Climbing", "Trip",
            [new DerivedRecord("t1", "Spring trip", new Dictionary<string, string?>())],
            [new DerivedLink("ascent:a1", "ON_TRIP", "trip:t1"), new DerivedLink("ascent:a3", "ON_TRIP", "trip:t1")]);

        await importer.UndoAsync(result.BatchId);

        Assert.Null(await records.GetAsync("Ascent", "a3"));
        Assert.Equal(1, await LinksCountedAsync(factory, "ON_TRIP"));
    }
}

// The generated pages show a module's derived records and kept fields, but offer no way to change them.
[Trait("Category", "Database")]
public sealed class ModuleOwnedPageTests(PostgresFixture fixture) : UiTestBase(fixture), IClassFixture<PostgresFixture>
{
    // The model file and its CSV files are only needed while loading.
    private async Task UseOwnedModelAsync()
    {
        var folder = Path.Combine(Path.GetTempPath(), $"linkbase-owned-{Guid.NewGuid():N}");
        Services.AddSingleton<IDbContextFactory<LinkbaseDbContext>>(await OwnedModel.LoadAsync(Fixture, folder));
        Directory.Delete(folder, recursive: true);
    }

    [Fact]
    public async Task A_derived_type_has_no_add_edit_delete_or_link_buttons()
    {
        await UseOwnedModelAsync();

        var list = RenderPage<RecordListPage>(p => p.Add(x => x.Type, "Trip"));
        Sees(list, "Spring trip");
        Sees(list, "derived by Climbing");
        Assert.DoesNotContain("Add Trip", list.Markup);

        var trip = RenderPage<RecordPage>(p => p.Add(x => x.Type, "Trip").Add(x => x.Key, "t1"));
        Sees(trip, "Long Arete");
        Sees(trip, "derived by Climbing");
        Assert.DoesNotContain(">Edit<", trip.Markup);
        Assert.DoesNotContain("Delete", trip.Markup);
        Assert.DoesNotContain("Add link", trip.Markup);
        Assert.DoesNotContain("Remove the link", trip.Markup);
    }

    [Fact]
    public async Task Links_to_derived_records_cant_be_removed_and_kept_fields_cant_be_edited()
    {
        await UseOwnedModelAsync();

        var ascent = RenderPage<RecordPage>(p => p.Add(x => x.Type, "Ascent").Add(x => x.Key, "a1"));
        Sees(ascent, "Spring trip");
        Sees(ascent, "Remove the link to Long Arete");
        Assert.DoesNotContain("Remove the link to Spring trip", ascent.Markup);

        var route = RenderPage<RecordPage>(p => p.Add(x => x.Type, "Route").Add(x => x.Key, "r1"));
        Sees(route, "kept by Climbing");
        Press(route, "Edit");
        Sees(Dialogs, "Grade");
        Assert.DoesNotContain("Grade rank", Dialogs.Markup);
    }
}
