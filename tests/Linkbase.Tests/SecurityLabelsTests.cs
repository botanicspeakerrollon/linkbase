using Linkbase.Core;
using Linkbase.Core.History;
using Linkbase.Core.Models;
using Linkbase.Core.Security;
using Linkbase.Data;
using Microsoft.EntityFrameworkCore;

namespace Linkbase.Tests;

// Label columns, the saved security setting, labels in CSV files, and changing labels in bulk. See docs/security-design.md.
[Trait("Category", "Database")]
public sealed class SecurityLabelsTests(PostgresFixture fixture) : IDisposable, IClassFixture<PostgresFixture>
{
    private static readonly string Consultancy = RepoPaths.Resolve("models/consultancy/model.json");
    private readonly string _folder = Path.Combine(Path.GetTempPath(), $"linkbase-labels-{Guid.NewGuid():N}");

    private sealed class Factory(string connectionString) : IDbContextFactory<LinkbaseDbContext>
    {
        public LinkbaseDbContext CreateDbContext() =>
            new(new DbContextOptionsBuilder<LinkbaseDbContext>().UseNpgsql(connectionString).Options);
    }

    private async Task<Factory> NewDatabaseAsync() => new(await fixture.CreateModelDatabaseAsync("SELECT 1"));

    private async Task<Factory> ConsultancyAsync()
    {
        var factory = await NewDatabaseAsync();
        await TestModels.LoadAsync(factory, Consultancy, TextWriter.Null);
        return factory;
    }

    private string WriteModel(string security, string typeExtras, string things, string? linksCsv = null, string relExtras = "")
    {
        Directory.CreateDirectory(_folder);
        File.WriteAllText(Path.Combine(_folder, "things.csv"), things);
        File.WriteAllText(Path.Combine(_folder, "links.csv"), linksCsv ?? "a,b,lvl\n");
        var path = Path.Combine(_folder, "model.json");
        File.WriteAllText(path, $$"""
            {
              "name": "Labels",
              "history": { "requireReason": false },
              "security": {{security}},
              "types": [ { "name": "Thing", "source": { "file": "things.csv", "key": "id"{{typeExtras}} }, "fields": [] } ],
              "relationships": [ { "from": "Thing", "type": "NEXT", "to": "Thing", "source": { "file": "links.csv", "fromColumn": "a", "toColumn": "b"{{relExtras}} } } ]
            }
            """);
        return path;
    }

    private const string Parts = """
        { "level": { "name": "Sensitivity", "values": ["public", "internal", "confidential"] },
          "anyOf": { "name": "Practice", "values": ["Design", "Engineering"] },
          "allOf": { "name": "NDA" } }
        """;

    private static async Task<List<(string Id, short Level, string[] Any, string[] All)>> NodeLabelsAsync(IDbContextFactory<LinkbaseDbContext> factory)
    {
        await using var db = factory.CreateDbContext();
        return (await db.Nodes.AsNoTracking().OrderBy(n => n.Id).Select(n => new { n.Id, n.LabelLevel, n.LabelAny, n.LabelAll }).ToListAsync())
            .Select(n => (n.Id, n.LabelLevel, n.LabelAny, n.LabelAll)).ToList();
    }

    private static string Show(int level, IEnumerable<string> any, IEnumerable<string> all) => $"{level} | {string.Join(",", any)} | {string.Join(",", all)}";

    [Fact]
    public async Task A_model_without_security_leaves_every_label_at_its_default_and_saves_no_setting()
    {
        var factory = await NewDatabaseAsync();
        await TestModels.LoadAsync(factory, RepoPaths.Resolve("models/climbing-demo/model.json"), TextWriter.Null);

        Assert.All(await NodeLabelsAsync(factory), n =>
        {
            Assert.Equal(0, n.Level);
            Assert.Equal(["*"], n.Any);
            Assert.Empty(n.All);
        });
        Assert.Null(await new LabelRepository(factory).SettingsAsync());
    }

    [Fact]
    public async Task The_consultancy_example_loads_each_projects_label_from_its_row_and_the_default()
    {
        var factory = await ConsultancyAsync();

        var labels = (await NodeLabelsAsync(factory)).Where(n => n.Id.StartsWith("project:", StringComparison.Ordinal)).ToDictionary(n => n.Id);

        string Of(string id) => Show(labels[id].Level, labels[id].Any, labels[id].All);
        Assert.Equal("0 | * | ", Of("project:pr1")); // public, from the row
        Assert.Equal("1 | Delivery,Design | Harbourline", Of("project:pr2"));
        Assert.Equal("1 | * | Greenleaf", Of("project:pr3")); // blank level takes the type's: internal
        Assert.Equal("2 | Delivery,Engineering | Copperfield", Of("project:pr4"));

        // Records of types with no label are untouched.
        Assert.All((await NodeLabelsAsync(factory)).Where(n => n.Id.StartsWith("person:", StringComparison.Ordinal)), n => Assert.Equal(0, n.Level));
    }

    [Fact]
    public async Task The_consultancy_example_shows_each_person_the_projects_the_models_doc_lists()
    {
        var factory = await ConsultancyAsync();
        var repository = new LabelRepository(factory);
        var security = (await repository.SettingsAsync())!;
        var directory = ViewerDirectory.Create(security);
        var people = new[] { "sam.partner", "ava.lindqvist", "chloe.marsh", "guest", "someone.else" };
        var viewers = new List<Viewer>();
        foreach (var person in people)
        {
            viewers.Add(await directory.ResolveAsync(person));
        }

        var projects = (await NodeLabelsAsync(factory)).Where(n => n.Id.StartsWith("project:", StringComparison.Ordinal)).ToList();
        string Seen(Viewer viewer) => string.Join(",", projects.Where(p => VisibilityRule.CanSee(security, viewer, LabelCodec.ToLabel(p.Level, p.Any, p.All))).Select(p => p.Id[8..]));

        Assert.Equal(["pr1,pr2,pr3,pr4,pr5", "pr1,pr2", "pr1,pr3", "pr1", ""], viewers.Select(Seen));
    }

    [Fact]
    public async Task The_security_setting_is_saved_with_the_model_and_cleared_when_the_model_stops_using_it()
    {
        var factory = await ConsultancyAsync();

        var saved = await new LabelRepository(factory).SettingsAsync();

        Assert.Equal(["Sensitivity", "Practice", "NDA"], saved!.Parts.Select(p => p.Part.Name));
        Assert.Equal(["public", "internal", "confidential"], saved.Level!.Values);
        Assert.Equal(["Design", "Engineering", "Delivery"], saved.AnyOf!.Values);

        await TestModels.LoadAsync(factory, WriteNoSecurityModel(), TextWriter.Null);

        Assert.Null(await new LabelRepository(factory).SettingsAsync());
    }

    private string WriteNoSecurityModel()
    {
        Directory.CreateDirectory(_folder);
        File.WriteAllText(Path.Combine(_folder, "plain.csv"), "id\nx\n");
        var path = Path.Combine(_folder, "plain.json");
        File.WriteAllText(path, """{ "name": "Plain", "types": [ { "name": "Thing", "source": { "file": "plain.csv", "key": "id" }, "fields": [] } ] }""");
        return path;
    }

    [Fact]
    public async Task Labels_come_from_a_types_default_a_csv_column_and_a_relationships_own_columns()
    {
        var path = WriteModel(Parts, """, "label": { "level": "sens", "anyOf": "dept", "allOf": "nda" }""",
            "id,sens,dept,nda\na,confidential,Design| Engineering ,X|Y\nb,,,\nc,public,,Z\n",
            "a,b,lvl\na,b,confidential\nb,c,\n", """, "label": { "level": "lvl" }""");
        var json = File.ReadAllText(path)
            .Replace("\"fields\": [] } ]", "\"fields\": [], \"label\": { \"level\": \"internal\", \"anyOf\": [\"Design\"] } } ]")
            .Replace("\"to\": \"Thing\",", "\"to\": \"Thing\", \"label\": { \"level\": \"internal\" },");
        File.WriteAllText(path, json);
        var factory = await NewDatabaseAsync();

        await TestModels.LoadAsync(factory, path, TextWriter.Null);

        var nodes = (await NodeLabelsAsync(factory)).ToDictionary(n => n.Id);
        string Of(string id) => Show(nodes[id].Level, nodes[id].Any, nodes[id].All);
        Assert.Equal("2 | Design,Engineering | X,Y", Of("thing:a"));
        Assert.Equal("1 | Design | ", Of("thing:b")); // all defaults
        Assert.Equal("0 | Design | Z", Of("thing:c"));

        await using var db = factory.CreateDbContext();
        var links = await db.NodeLinks.AsNoTracking().OrderBy(l => l.SourceId).Select(l => new { l.SourceId, l.LabelLevel }).ToListAsync();
        Assert.Equal([("thing:a", (short)2), ("thing:b", (short)1)], links.Select(l => (l.SourceId, l.LabelLevel)));
    }

    [Theory]
    [InlineData("""{ "level": { "name": "S", "values": ["low", "high"] } }""", "id,sens\na,medium\n", "'medium'")]
    [InlineData("""{ "anyOf": { "name": "Practice", "values": ["Design"] } }""", "id,dept\na,Design|Legal\n", "'Legal'")]
    [InlineData("""{ "allOf": { "name": "NDA" } }""", "id,nda\na,\"X|bad,value\"\n", "'bad,value'")]
    [InlineData("""{ "allOf": { "name": "NDA" } }""", "id,nda\na,*\n", "'*'")]
    public async Task A_label_value_the_model_does_not_allow_stops_the_load_and_names_it(string security, string csv, string expected)
    {
        var column = csv.Contains("sens", StringComparison.Ordinal) ? "\"level\": \"sens\"" : csv.Contains("dept", StringComparison.Ordinal) ? "\"anyOf\": \"dept\"" : "\"allOf\": \"nda\"";
        var path = WriteModel(security, $$""", "label": { {{column}} }""", csv);

        var ex = await Assert.ThrowsAsync<LinkbaseValidationException>(async () => await new ModelLoader(await NewDatabaseAsync(), Linkbase.Domains.RegisteredDomains.Registry).LoadAsync(path, TextWriter.Null));

        Assert.Contains(expected, ex.Message);
    }

    [Fact]
    public async Task A_label_in_a_model_without_security_is_refused_by_the_model_file()
    {
        var path = WriteModel(Parts, ", \"label\": { \"level\": \"sens\" }", "id,sens\na,public\n");
        File.WriteAllText(path, File.ReadAllText(path).Replace($"\"security\": {Parts},", ""));

        var ex = Assert.Throws<LinkbaseValidationException>(() => ModelFile.Read(path, Linkbase.Domains.RegisteredDomains.Registry));

        Assert.Contains("no security section", ex.Message);
    }

    // Changing labels in bulk.

    [Fact]
    public async Task Bulk_edits_set_the_level_and_add_or_remove_values_and_count_only_what_changed()
    {
        var factory = await ConsultancyAsync();
        var repository = new LabelRepository(factory);
        string[] all = ["project:pr1", "project:pr2", "project:pr3", "project:pr4", "project:pr5"];

        // Everything to confidential: pr4 already is.
        Assert.Equal(4, await repository.EditNodesAsync(all, new LabelEdit(Level: "confidential"), null));
        Assert.Equal("confidential", (await repository.GetNodeAsync("project:pr1"))!.Level);

        // pr2 already has Design.
        Assert.Equal(4, await repository.EditNodesAsync(all, new LabelEdit(AnyOf: new SetEdit(SetEditMode.Add, ["Design"])), null));
        Assert.Equal(["Delivery", "Design"], (await repository.GetNodeAsync("project:pr2"))!.AnyOf);
        Assert.Equal(["Delivery", "Design", "Engineering"], (await repository.GetNodeAsync("project:pr4"))!.AnyOf);

        Assert.Equal(1, await repository.EditNodesAsync(["project:pr2"], new LabelEdit(AnyOf: new SetEdit(SetEditMode.Remove, ["Delivery", "Design"])), null));
        Assert.Empty((await repository.GetNodeAsync("project:pr2"))!.AnyOf);

        Assert.Equal(1, await repository.EditNodesAsync(["project:pr2"], new LabelEdit(AllOf: new SetEdit(SetEditMode.Replace, ["Harbourline", "Greenleaf"])), null));
        Assert.Equal(["Greenleaf", "Harbourline"], (await repository.GetNodeAsync("project:pr2"))!.AllOf);
    }

    [Fact]
    public async Task A_bulk_edit_is_one_change_set_in_the_history_with_the_reason()
    {
        var factory = await ConsultancyAsync();
        var repository = new LabelRepository(factory);

        await repository.EditNodesAsync(["project:pr1", "project:pr2"], new LabelEdit(Level: "confidential"), new ChangeReason("Budgets became sensitive"));

        await using var db = factory.CreateDbContext();
        var changes = await db.Changes.AsNoTracking().Where(c => c.EntityKind == ChangeEntityKind.Label).OrderBy(c => c.EntityId).ToListAsync();
        Assert.Equal(2, changes.Count);
        Assert.Single(changes.Select(c => c.ChangeSetId).Distinct());
        Assert.Equal(("project:pr1", "Sensitivity", "public", "confidential"), (changes[0].EntityId, changes[0].FieldKey, changes[0].OldValue, changes[0].NewValue));

        var history = await new HistoryRepository(factory).ForRecordAsync("project:pr1");
        var entry = history.First(e => e.Changes.Any(c => c.Kind == ChangeEntityKind.Label));
        Assert.Equal("Budgets became sensitive", entry.Note);
    }

    [Fact]
    public async Task A_bulk_edit_needs_a_reason_where_the_model_asks_for_one_and_changes_nothing_without_it()
    {
        var factory = await NewDatabaseAsync();
        await new ModelLoader(factory, Linkbase.Domains.RegisteredDomains.Registry).LoadAsync(Consultancy, TextWriter.Null); // the loader leaves reasons required
        var repository = new LabelRepository(factory);

        await Assert.ThrowsAsync<LinkbaseValidationException>(() => repository.EditNodesAsync(["project:pr1"], new LabelEdit(Level: "confidential"), null));

        Assert.Equal("public", (await repository.GetNodeAsync("project:pr1"))!.Level);
    }

    [Fact]
    public async Task A_bulk_edit_with_a_value_the_model_does_not_have_is_refused_before_anything_changes()
    {
        var factory = await ConsultancyAsync();
        var repository = new LabelRepository(factory);

        var ex = await Assert.ThrowsAsync<LinkbaseValidationException>(() =>
            repository.EditNodesAsync(["project:pr1"], new LabelEdit(Level: "secret", AnyOf: new SetEdit(SetEditMode.Add, ["Legal", "a,b"])), null));

        Assert.Contains("'secret' isn't one of public, internal, confidential", ex.Message);
        Assert.Contains("'Legal' isn't one of Design, Engineering, Delivery", ex.Message);
        Assert.Contains("'a,b' isn't a valid Practice value", ex.Message);
        Assert.Equal("public", (await repository.GetNodeAsync("project:pr1"))!.Level);
    }

    [Fact]
    public async Task A_model_without_security_has_no_labels_to_edit()
    {
        var factory = await NewDatabaseAsync();
        await TestModels.LoadAsync(factory, WriteNoSecurityModel(), TextWriter.Null);

        var ex = await Assert.ThrowsAsync<LinkbaseValidationException>(() => new LabelRepository(factory).EditNodesAsync(["thing:x"], new LabelEdit(Level: "high"), null));

        Assert.Contains("doesn't use security", ex.Message);
    }

    [Fact]
    public async Task Links_can_be_labelled_in_bulk_too_and_the_history_names_the_link()
    {
        var factory = await ConsultancyAsync();
        var repository = new LabelRepository(factory);
        await using var db = factory.CreateDbContext();
        var link = await db.NodeLinks.AsNoTracking().Where(l => l.RelType == "WORKS_ON").OrderBy(l => l.Id).FirstAsync();

        Assert.Equal(1, await repository.EditLinksAsync([link.Id], new LabelEdit(Level: "internal", AllOf: new SetEdit(SetEditMode.Add, ["Harbourline"])), null));

        var label = await repository.GetLinkAsync(link.Id);
        Assert.Equal("internal", label!.Level);
        Assert.Equal(["Harbourline"], label.AllOf);
        var history = await new HistoryRepository(factory).ForLinkAsync(link.SourceId, link.RelType, link.TargetId);
        Assert.Equal(2, history.SelectMany(e => e.Changes).Count(c => c.Kind == ChangeEntityKind.Label));
    }

    [Fact]
    public async Task A_label_change_cannot_be_reverted_automatically_for_now()
    {
        var factory = await ConsultancyAsync();
        await new LabelRepository(factory).EditNodesAsync(["project:pr1"], new LabelEdit(Level: "internal"), null);
        await using var db = factory.CreateDbContext();
        var set = await db.Changes.AsNoTracking().Where(c => c.EntityKind == ChangeEntityKind.Label).Select(c => c.ChangeSetId).FirstAsync();

        var ex = await Assert.ThrowsAsync<LinkbaseValidationException>(() => new ChangeReverter(factory, new Linkbase.Core.Records.RecordRepository(factory)).RevertAsync(set, null));

        Assert.Contains("can't be reverted", ex.Message);
    }

    public void Dispose()
    {
        if (Directory.Exists(_folder))
        {
            Directory.Delete(_folder, recursive: true);
        }
    }
}
