using Linkbase.Core;
using Linkbase.Core.CsvImport;
using Linkbase.Core.Extraction;
using Linkbase.Core.Graph;
using Linkbase.Core.Models;
using Linkbase.Core.Records;
using Linkbase.Core.Staging;
using Linkbase.Data;
using Microsoft.EntityFrameworkCore;

namespace Linkbase.Tests;

// Notes every change to people and projects, to see what a module would hear of.
internal sealed class LinkWatcher : IRecordHook
{
    public string Domain => "Climbing";

    public IReadOnlyCollection<string> Types { get; } = ["Person", "Project"];

    public List<RecordChange> Changes { get; } = [];

    public void BeforeSave(RecordSaving record)
    {
    }

    public Task AfterChangeAsync(IReadOnlyList<RecordChange> changes, CancellationToken cancellationToken)
    {
        Changes.AddRange(changes);
        return Task.CompletedTask;
    }
}

// Link attributes beyond the record page: Explore, the new record form, hooks, the CSV import and reading text. They're
// tried on the example consultancy's Person WORKS_ON Project, which has a role and days a week.
[Trait("Category", "Database")]
public sealed class LinkAttributeTests(PostgresFixture fixture) : IDisposable, IClassFixture<PostgresFixture>
{
    private readonly List<string> _files = [];

    private async Task<ModelFactory> ExampleAsync()
    {
        var factory = new ModelFactory(await fixture.CreateModelDatabaseAsync("SELECT 1"));
        await TestModels.LoadAsync(factory, RepoPaths.Resolve("models/consultancy/model.json"), TextWriter.Null);
        return factory;
    }

    private CsvFile Csv(string content)
    {
        var path = Path.Combine(Path.GetTempPath(), $"linkbase-link-attributes-{Guid.NewGuid():N}.csv");
        File.WriteAllText(path, content);
        _files.Add(path);
        return CsvImportService.Read(path, "works-on.csv");
    }

    public void Dispose()
    {
        foreach (var file in _files)
        {
            File.Delete(file);
        }
    }

    private static Dictionary<string, string?> Values(string role, string days) => new() { ["role"] = role, ["days_a_week"] = days };

    private static async Task<IReadOnlyList<RecordLink>> WorksOnAsync(RecordRepository records, string id) =>
        (await records.LinksAsync(id)).Single(g => g.RelType == "WORKS_ON" && g.Outgoing).Links;

    [Fact]
    public async Task Explore_gets_each_links_values_and_tells_repeats_apart()
    {
        var graph = new GraphRepository(await ExampleAsync(), Linkbase.Domains.RegisteredDomains.Registry, Linkbase.Core.Security.FieldPolicy.Unrestricted);

        var edges = (await graph.SubgraphAsync(["person:p4", "project:pr4", "team:t2"])).Edges;

        var worksOn = edges.Where(e => e.RelType == "WORKS_ON").ToList();
        Assert.Equal(2, worksOn.Count);
        Assert.Equal(["Engineer", "Reviewer"], worksOn.Select(e => e.Values!["role"]).Order());
        Assert.Equal(2, worksOn.Select(e => e.Key).Distinct().Count());
        Assert.Contains("WORKS_ON · Reviewer · 1", worksOn.Select(e => e.Label));
        Assert.Null(edges.Single(e => e.RelType == "MEMBER_OF").Values);
    }

    [Fact]
    public async Task Explore_filters_on_a_links_own_value()
    {
        var factory = await ExampleAsync();
        var graph = new GraphRepository(factory, Linkbase.Domains.RegisteredDomains.Registry, Linkbase.Core.Security.FieldPolicy.Unrestricted);
        var schema = await graph.SchemaAsync();
        Assert.Contains(new GraphLinkPropertyInfo("WORKS_ON", "role", GraphValueType.Text), schema.LinkProperties);
        Assert.Contains(new GraphLinkPropertyInfo("WORKS_ON", "days_a_week", GraphValueType.Number), schema.LinkProperties);

        var pattern = new GraphPattern(
            [new PatternNode("Person", []), new PatternNode("Project", [])],
            [new PatternRel("WORKS_ON", true) { Filters = [new PatternFilter("role", FilterOperator.Equals, "Reviewer")] }]);

        var result = await graph.RunPatternAsync(pattern, schema, 50, 0);

        Assert.Equal(["person:p4", "project:pr4"], result.NodeIds);
    }

    [Fact]
    public async Task A_new_records_links_take_values_and_a_required_one_blocks_the_save()
    {
        var factory = await ExampleAsync();
        var records = new RecordRepository(factory, Linkbase.Core.Records.RecordHooks.None, Linkbase.Core.Security.FieldPolicy.Unrestricted);

        var key = await records.CreateAsync("Person", null, TestModels.Person("Joiner", links:
        [
            new NewLink("WORKS_ON", "project:pr4", Values("Lead", "2")),
            new NewLink("WORKS_ON", "project:pr4", Values("Cover", "1")),
            new NewLink("WORKS_ON", "project:pr4", Values("Cover", "1")),
        ]));
        var links = await WorksOnAsync(records, $"person:{key}");
        Assert.Equal(["Cover", "Lead"], links.Select(l => l.Values["role"]).Order());

        await using (var db = factory.CreateDbContext())
        {
            await db.Database.ExecuteSqlRawAsync("UPDATE link_attribute SET required = true WHERE key = 'role'");
        }

        var ex = await Assert.ThrowsAsync<LinkbaseValidationException>(() => records.CreateAsync(
            "Person", null, TestModels.Person("Other", links: [new NewLink("WORKS_ON", "project:pr4")])));
        Assert.Contains("Role is required", ex.Message);
        Assert.Null(await records.GetAsync("Person", "other"));
    }

    [Fact]
    public async Task A_module_hears_when_a_links_values_change()
    {
        var factory = await ExampleAsync();
        var hook = new LinkWatcher();
        var records = new RecordRepository(factory, new RecordHooks([hook]), Linkbase.Core.Security.FieldPolicy.Unrestricted);
        var engineer = (await WorksOnAsync(records, "person:p4")).Single(l => l.OtherId == "project:pr4" && l.Values["role"] == "Engineer");

        await records.UpdateLinkAsync(engineer.Id, Values("Engineer", "3"));

        var changes = hook.Changes.Where(c => c.Kind == RecordChangeKind.LinkUpdated).ToList();
        Assert.Equal(["person:p4", "project:pr4"], changes.Select(c => c.Id).Order());
        Assert.All(changes, c => Assert.Equal(("1", "3"), (c.Before["days_a_week"], c.After["days_a_week"])));

        // Saving the same values changes nothing, so nothing is said.
        await records.UpdateLinkAsync(engineer.Id, Values("Engineer", "3"));
        Assert.Equal(2, hook.Changes.Count(c => c.Kind == RecordChangeKind.LinkUpdated));

        await records.AddLinkAsync("person:p4", "WORKS_ON", "project:pr1", Values("Observer", "1"));
        Assert.Contains(hook.Changes, c => c.Kind == RecordChangeKind.Linked && c.Id == "person:p4" && c.After["role"] == "Observer");
    }

    [Fact]
    public async Task A_csv_of_links_fills_the_values_and_keeps_repeats_that_differ()
    {
        var factory = await ExampleAsync();
        var importer = new CsvImportService(factory);
        var records = new RecordRepository(factory, Linkbase.Core.Records.RecordHooks.None, Linkbase.Core.Security.FieldPolicy.Unrestricted);
        var file = Csv("person,project,role,days\np5,pr1,Lead,3\np5,pr1,Cover,1\np5,pr1,Cover,1\np5,pr4,,\np2,pr2,Designer,4\n");
        var mapping = new CsvLinkMapping("Person", 0, "WORKS_ON", "Project", 1, [new CsvFieldMapping(2, "role", FieldType.Text), new CsvFieldMapping(3, "days_a_week", FieldType.Number)]);

        var plan = await importer.PlanAsync(file, mapping);

        // Two rows with different values are two links, the identical repeat is one, a row with no values is a link with none,
        // and the last row is a link the model already has with the same values.
        Assert.Equal((3, 1, 1), (plan.ToAdd, plan.AlreadyThere, plan.Skipped));
        var result = await importer.ImportAsync(file, mapping);
        Assert.Equal(3, result.Added);

        var onPr1 = (await WorksOnAsync(records, "person:p5")).Where(l => l.OtherId == "project:pr1").ToList();
        Assert.Equal(["Cover", "Lead"], onPr1.Select(l => l.Values["role"]).Order());
        Assert.Equal("3", onPr1.Single(l => l.Values["role"] == "Lead").Values["days_a_week"]);
        Assert.Empty((await WorksOnAsync(records, "person:p5")).Single(l => l.OtherId == "project:pr4").Values);

        // Importing it again adds nothing, and undoing the first removes what it added.
        Assert.Equal(0, (await importer.PlanAsync(file, mapping)).ToAdd);
        await importer.UndoAsync(result.BatchId);
        Assert.DoesNotContain(await WorksOnAsync(records, "person:p5"), l => l.OtherId is "project:pr1" or "project:pr4");
    }

    [Fact]
    public async Task A_csv_of_links_is_refused_for_values_the_model_lacks_or_that_dont_fit()
    {
        var factory = await ExampleAsync();
        var importer = new CsvImportService(factory);
        var file = Csv("person,project,role,days,mood\np5,pr1,Lead,lots,happy\n");

        var unknown = new CsvLinkMapping("Person", 0, "WORKS_ON", "Project", 1, [new CsvFieldMapping(4, "mood", FieldType.Text)]);
        Assert.Contains("no value called mood", (await importer.PlanAsync(file, unknown)).Problems.Single());

        var wrong = new CsvLinkMapping("Person", 0, "WORKS_ON", "Project", 1, [new CsvFieldMapping(3, "days_a_week", FieldType.Number)]);
        Assert.Contains("Row 2: Days a week must be a number", (await importer.PlanAsync(file, wrong)).Problems.Single());

        // A value the relationship requires needs a column.
        await using (var db = factory.CreateDbContext())
        {
            await db.Database.ExecuteSqlRawAsync("UPDATE link_attribute SET required = true WHERE key = 'role'");
        }

        Assert.Contains("Role is required on every Person WORKS_ON Project link, so it needs a column", (await importer.PlanAsync(file, wrong)).Problems.Single());
    }

    private sealed class FakeExtractor(ExtractedData answer) : IModelExtractor
    {
        public Task<ExtractedData> ExtractAsync(ExtractionSchema schema, string text, IReadOnlyList<string> knownRecords, CancellationToken cancellationToken) =>
            Task.FromResult(answer);
    }

    private const string Text = "Zed Quinn joined. Project Atlas is active. Quinn works on Atlas as Lead 3 days a week, and as Cover 1 day a week.";

    [Fact]
    public async Task Reading_text_checks_a_links_values_against_the_text_and_stages_them()
    {
        var factory = await ExampleAsync();
        var records = new RecordRepository(factory, Linkbase.Core.Records.RecordHooks.None, Linkbase.Core.Security.FieldPolicy.Unrestricted);
        var link = (string Role, string Days) => new ExtractedLink("WORKS_ON", "Person", "Zed Quinn", "Project", "Atlas", [new ExtractedValue("role", Role), new ExtractedValue("days_a_week", Days)]);
        var answer = new ExtractedData(
            [new ExtractedRecord("Person", "Zed Quinn", []), new ExtractedRecord("Project", "Atlas", [new ExtractedValue("status", "active")])],
            [link("Lead", "3"), link("Cover", "1"), link("Cover", "1")]);
        var service = new ModelExtractionService(records, new FakeExtractor(answer), new PageTextFetcher(new HttpClient()), factory, new ImportStagingService(factory));

        var batch = await service.ExtractFromTextAsync(Text, null, null, CancellationToken.None);
        await new ImportStagingService(factory).CommitAsync(batch);

        // Lead and Cover are in the text, and the repeated Cover is one link.
        var links = await WorksOnAsync(records, "person:zed-quinn");
        Assert.Equal(["Cover", "Lead"], links.Select(l => l.Values["role"]).Order());
        Assert.Equal("3", links.Single(l => l.Values["role"] == "Lead").Values["days_a_week"]);

        var schema = ExtractionSchema.From(await records.TypesAsync(), await records.RelationshipsAsync(), null,
            new Dictionary<(string, string, string), IReadOnlyList<RecordField>> { [("Person", "WORKS_ON", "Project")] = await records.LinkAttributesAsync("Person", "WORKS_ON", "Project") });
        var tool = ExtractionPrompts.InputSchemaJson(schema);
        Assert.Contains("days_a_week", tool, StringComparison.Ordinal);
        Assert.Contains("Each link can carry these values: role (text); days_a_week (a number)", ExtractionPrompts.SystemPrompt(schema), StringComparison.Ordinal);
    }
}

public class LinkValueValidatorTests
{
    private static ExtractionSchema Schema(bool roleRequired) => ExtractionSchema.From(
        [new RecordType("Person", null, null, []), new RecordType("Project", null, null, [])],
        [new RelationshipDefinition("Person", "WORKS_ON", "Project")],
        null,
        new Dictionary<(string, string, string), IReadOnlyList<RecordField>>
        {
            [("Person", "WORKS_ON", "Project")] = [new RecordField("role", FieldType.Text) { Required = roleRequired }, new RecordField("days_a_week", FieldType.Number)],
        });

    private const string Text = "Zed Quinn works on Atlas as Lead.";

    private static ExtractedData Data(params (string Field, string Value)[] values) => new(
        [],
        [new ExtractedLink("WORKS_ON", "Person", "Zed Quinn", "Project", "Atlas", values.Select(v => new ExtractedValue(v.Field, v.Value)).ToList())]);

    [Fact]
    public void A_value_the_text_doesnt_have_is_dropped_but_the_link_stays()
    {
        var result = ModelExtractionValidator.Validate(Schema(false), Data(("role", "Lead"), ("days_a_week", "9"), ("mood", "happy")), Text);

        Assert.Equal("""{"role":"Lead"}""", Assert.Single(result.Links).Props);
        Assert.Contains(result.Warnings, w => w.Contains("days_a_week", StringComparison.Ordinal));
        Assert.Contains(result.Warnings, w => w.Contains("no such value", StringComparison.Ordinal));
    }

    [Fact]
    public void A_link_missing_a_required_value_is_dropped()
    {
        var result = ModelExtractionValidator.Validate(Schema(true), Data(("role", "Mentor")), Text);

        Assert.Empty(result.Links);
        Assert.Contains(result.Warnings, w => w.Contains("role is required", StringComparison.Ordinal));
    }
}
