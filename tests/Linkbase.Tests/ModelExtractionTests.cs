using System.Text.Json;
using Linkbase.Core;
using Linkbase.Core.Extraction;
using Linkbase.Core.Models;
using Linkbase.Core.Records;
using Linkbase.Core.Staging;
using Linkbase.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;

namespace Linkbase.Tests;

public class ModelExtractionValidatorTests
{
    private static readonly ExtractionSchema Schema = ExtractionSchema.From(
        [
            new RecordType("Project", null, null,
            [
                new RecordField("budget", FieldType.Number),
                new RecordField("started", FieldType.Date),
                new RecordField("status", FieldType.Choice, ["proposed", "active"]) { Required = true },
                new RecordField("tags", FieldType.List),
                new RecordField("note", FieldType.Text),
            ]),
            new RecordType("Client", null, null, [new RecordField("city", FieldType.Text)]),
            new RecordType("Trip", null, null, [], Owner: "Climbing"),
        ],
        [new RelationshipDefinition("Project", "FOR", "Client"), new RelationshipDefinition("Trip", "TO", "Client")]);

    private const string Text = "Project Atlas, started 2025-03-01 with a budget of 12,000, is active. Tags: web, mobile. It is for Northwind in Leeds.";

    private static ExtractedRecord Project(params (string Field, string Value)[] fields) =>
        new("Project", "Atlas", fields.Select(f => new ExtractedValue(f.Field, f.Value)).ToList());

    [Fact]
    public void Keeps_what_the_text_says_and_types_each_value()
    {
        var data = new ExtractedData(
            [
                Project(("budget", "12,000"), ("started", "2025-03-01"), ("status", "active"), ("tags", "web|mobile")),
                new ExtractedRecord("Client", "Northwind", [new ExtractedValue("city", "Leeds")]),
            ],
            [new ExtractedLink("FOR", "Project", "Atlas", "Client", "Northwind")]);

        var result = ModelExtractionValidator.Validate(Schema, data, Text);

        Assert.Empty(result.Warnings);
        Assert.Equal(["project:atlas", "client:northwind"], result.Records.Select(r => r.Id));
        var props = JsonDocument.Parse(result.Records[0].Props).RootElement;
        Assert.Equal(12000, props.GetProperty("budget").GetDecimal());
        Assert.Equal("2025-03-01", props.GetProperty("started").GetString());
        Assert.Equal("active", props.GetProperty("status").GetString());
        Assert.Equal(["web", "mobile"], props.GetProperty("tags").EnumerateArray().Select(t => t.GetString()));
        Assert.Single(result.Links);
    }

    [Fact]
    public void Drops_names_and_values_the_text_does_not_contain()
    {
        var data = new ExtractedData(
            [
                Project(("budget", "99"), ("status", "active"), ("note", "Went well")),
                new ExtractedRecord("Project", "Invented", [new ExtractedValue("status", "active")]),
                new ExtractedRecord("Client", "Northwind", [new ExtractedValue("city", "Paris")]),
            ],
            [new ExtractedLink("FOR", "Project", "Atlas", "Client", "Elsewhere")]);

        var result = ModelExtractionValidator.Validate(Schema, data, Text);

        Assert.Equal(["project:atlas", "client:northwind"], result.Records.Select(r => r.Id));
        Assert.DoesNotContain("budget", result.Records[0].Props, StringComparison.Ordinal);
        Assert.DoesNotContain("note", result.Records[0].Props, StringComparison.Ordinal);
        Assert.DoesNotContain("city", result.Records[1].Props, StringComparison.Ordinal);
        Assert.Empty(result.Links);
        Assert.Contains(result.Warnings, w => w.Contains("Invented", StringComparison.Ordinal));
        Assert.Contains(result.Warnings, w => w.Contains("budget", StringComparison.Ordinal));
        Assert.Contains(result.Warnings, w => w.Contains("Elsewhere", StringComparison.Ordinal));
    }

    [Fact]
    public void A_record_missing_a_required_field_is_dropped()
    {
        var result = ModelExtractionValidator.Validate(Schema, new ExtractedData([Project(("status", "finished"))], []), Text);

        Assert.Empty(result.Records);
        Assert.Contains(result.Warnings, w => w.Contains("status is required", StringComparison.Ordinal));
    }

    [Fact]
    public void Types_a_module_derives_and_links_outside_the_model_are_refused()
    {
        var data = new ExtractedData(
            [new ExtractedRecord("Trip", "Atlas", []), new ExtractedRecord("Nothing", "Atlas", [])],
            [new ExtractedLink("FOR", "Client", "Northwind", "Project", "Atlas"), new ExtractedLink("TO", "Trip", "Atlas", "Client", "Northwind")]);

        var result = ModelExtractionValidator.Validate(Schema, data, Text);

        Assert.Empty(result.Records);
        Assert.Empty(result.Links);
        Assert.Equal(4, result.Warnings.Count);
    }

    [Fact]
    public void Long_values_are_not_kept_because_only_facts_are_stored()
    {
        var long_ = new string('a', ModelExtractionValidator.MaxValueLength + 1);
        var data = new ExtractedData([Project(("status", "active"), ("note", long_))], []);

        var result = ModelExtractionValidator.Validate(Schema, data, Text + " " + long_);

        Assert.DoesNotContain("note", result.Records[0].Props, StringComparison.Ordinal);
    }
}

public class ExtractionPromptsTests
{
    private static readonly ExtractionSchema Schema = ExtractionSchema.From(
        [new RecordType("Project", null, null, [new RecordField("status", FieldType.Choice, ["proposed", "active"]) { Required = true }]), new RecordType("Client", null, null, [])],
        [new RelationshipDefinition("Project", "FOR", "Client")]);

    [Fact]
    public void The_tool_schema_only_allows_the_models_own_names()
    {
        var schema = JsonDocument.Parse(ExtractionPrompts.InputSchemaJson(Schema)).RootElement;

        var record = schema.GetProperty("properties").GetProperty("records").GetProperty("items").GetProperty("properties");
        Assert.Equal(["Project", "Client"], record.GetProperty("type").GetProperty("enum").EnumerateArray().Select(e => e.GetString()));
        Assert.Equal(["status"], record.GetProperty("fields").GetProperty("items").GetProperty("properties").GetProperty("field").GetProperty("enum").EnumerateArray().Select(e => e.GetString()));
        var link = schema.GetProperty("properties").GetProperty("links").GetProperty("items").GetProperty("properties");
        Assert.Equal(["FOR"], link.GetProperty("rel_type").GetProperty("enum").EnumerateArray().Select(e => e.GetString()));
        Assert.False(schema.GetProperty("additionalProperties").GetBoolean());
    }

    [Fact]
    public void A_model_with_no_relationships_has_no_links_in_the_schema()
    {
        var schema = ExtractionSchema.From([new RecordType("Note", null, null, [])], []);

        var json = JsonDocument.Parse(ExtractionPrompts.InputSchemaJson(schema)).RootElement;

        Assert.False(json.GetProperty("properties").TryGetProperty("links", out _));
        Assert.Equal(["records"], json.GetProperty("required").EnumerateArray().Select(e => e.GetString()));
    }

    [Fact]
    public void The_prompt_describes_the_model_and_the_request_uses_a_strict_tool()
    {
        var client = new Linkbase.Core.Llm.Providers.AnthropicToolClient(new ConfigurationBuilder().Build());

        var prompt = ExtractionPrompts.SystemPrompt(Schema);
        var request = client.BuildRequest(ExtractionPrompts.BuildRequest(Schema, "Atlas is for Northwind", ["Earlier"]));

        Assert.Contains("status (one of proposed, active, required)", prompt, StringComparison.Ordinal);
        Assert.Contains("Project FOR Client", prompt, StringComparison.Ordinal);
        Assert.Equal("record_data", request.Tools![0].Value is { } tool ? ((Anthropic.Models.Beta.Messages.BetaTool)tool).Name : null);
    }

    [Fact]
    public void An_answer_of_the_wrong_shape_is_refused_with_a_reason_instead_of_crashing()
    {
        var input = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>("""{"records":"not a list"}""")!;

        var ex = Assert.Throws<ExtractionFailedException>(() => ExtractionPrompts.ReadResponse("tool_use", input));

        Assert.Contains("expected shape", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void The_page_text_is_fenced_and_earlier_records_are_named_only_for_a_continuation()
    {
        var first = ExtractionPrompts.BuildUserMessage("Atlas is active.", []);
        var later = ExtractionPrompts.BuildUserMessage("Atlas is for Northwind.", ["Atlas", "Northwind"]);

        Assert.StartsWith("<page_text>", first, StringComparison.Ordinal);
        Assert.DoesNotContain("continues", first, StringComparison.Ordinal);
        Assert.Contains("Records named earlier in the page: Atlas, Northwind.", later, StringComparison.Ordinal);
        Assert.EndsWith("</page_text>", later, StringComparison.Ordinal);
    }

    [Fact]
    public void Reads_the_answer_and_refuses_an_unfinished_one()
    {
        var input = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(
            """{"records":[{"type":"Project","name":"Atlas","fields":[{"field":"status","value":"active"}]}],"links":[{"rel_type":"FOR","from_type":"Project","from_name":"Atlas","to_type":"Client","to_name":"Northwind"}]}""")!;

        var data = ExtractionPrompts.ReadResponse("tool_use", input);

        Assert.Equal("Atlas", data.Records[0].Name);
        Assert.Equal("active", data.Records[0].Fields[0].Value);
        Assert.Equal("Northwind", data.Links[0].ToName);
        Assert.Throws<ExtractionFailedException>(() => ExtractionPrompts.ReadResponse("max_tokens", input));
        Assert.Throws<ExtractionFailedException>(() => ExtractionPrompts.ReadResponse("refusal", null));
        Assert.Throws<ExtractionFailedException>(() => ExtractionPrompts.ReadResponse("end_turn", null));
        Assert.Empty(ExtractionPrompts.ReadResponse("tool_use", JsonSerializer.Deserialize<Dictionary<string, JsonElement>>("""{"records":[]}""")!).Links);
    }
}

[Trait("Category", "Database")]
public sealed class ModelExtractionServiceTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    private sealed class FakeExtractor(Func<ExtractionSchema, string, ExtractedData> answer) : IModelExtractor
    {
        public Task<ExtractedData> ExtractAsync(ExtractionSchema schema, string text, IReadOnlyList<string> knownRecords, CancellationToken cancellationToken) =>
            Task.FromResult(answer(schema, text));
    }

    private async Task<(ModelFactory Factory, ModelExtractionService Service, ImportStagingService Staging)> ExampleAsync(Func<ExtractionSchema, string, ExtractedData> answer)
    {
        var factory = new ModelFactory(await fixture.CreateModelDatabaseAsync("SELECT 1"));
        await TestModels.LoadAsync(factory, RepoPaths.Resolve("models/consultancy/model.json"), TextWriter.Null);
        var staging = new ImportStagingService(factory);
        var service = new ModelExtractionService(new RecordRepository(factory, Linkbase.Core.Records.RecordHooks.None, Linkbase.Core.Security.FieldPolicy.Unrestricted), new FakeExtractor(answer), new PageTextFetcher(new HttpClient()), factory, staging);
        return (factory, service, staging);
    }

    private const string Text = "Zed Quinn joined the Design team. Project Atlas is active and is for Harbourline Ferries. Quinn works on Atlas. Ava Lindqvist is also mentioned.";

    private static ExtractedData Answer(ExtractionSchema schema, string text) => new(
        [
            new ExtractedRecord("Person", "Zed Quinn", []),
            new ExtractedRecord("Person", "Ava Lindqvist", []),
            new ExtractedRecord("Project", "Atlas", [new ExtractedValue("status", "active")]),
            new ExtractedRecord("Project", "Hallucinated", [new ExtractedValue("status", "active")]),
        ],
        [
            new ExtractedLink("MEMBER_OF", "Person", "Zed Quinn", "Team", "Design"),
            new ExtractedLink("FOR", "Project", "Atlas", "Client", "Harbourline Ferries"),
            new ExtractedLink("WORKS_ON", "Person", "Zed Quinn", "Project", "Atlas"),
        ]);

    [Fact]
    public async Task Stages_what_is_new_links_to_what_exists_and_commits_it()
    {
        var (factory, service, staging) = await ExampleAsync(Answer);

        var batch = await service.ExtractFromTextAsync(Text, "https://example.test/article", null, CancellationToken.None);

        var staged = (await staging.GetAsync(batch))!;
        Assert.Equal(DataSource.Ai, staged.Source);
        Assert.Equal("https://example.test/article", staged.File);
        Assert.Equal(2, staged.Records);
        Assert.Equal(3, staged.Links);
        Assert.Equal(1, staged.AlreadyThere);
        Assert.Contains(staged.Notes, n => n.Contains("Hallucinated", StringComparison.Ordinal));

        var links = (await staging.ReviewAsync(batch, StagedKind.Link, 0, 10)).Rows.Select(r => r.Title).ToList();
        Assert.Contains("Atlas FOR Harbourline Ferries", links);
        Assert.Contains("Zed Quinn MEMBER_OF Design", links);

        await staging.CommitAsync(batch);

        await using var db = factory.CreateDbContext();
        Assert.Equal("Zed Quinn", await db.Nodes.Where(n => n.Id == "person:zed-quinn").Select(n => n.Name).SingleAsync());
        Assert.True(await db.Nodes.AnyAsync(n => n.Id == "project:atlas" && n.ImportBatchId == batch));
        Assert.Equal(1, await db.Nodes.CountAsync(n => n.Type == "Person" && n.Name == "Ava Lindqvist"));
        Assert.True(await db.NodeLinks.AnyAsync(l => l.SourceId == "project:atlas" && l.RelType == "FOR" && l.TargetId == "client:c1"));
        Assert.True(await db.NodeLinks.AnyAsync(l => l.SourceId == "person:zed-quinn" && l.RelType == "MEMBER_OF" && l.TargetId == "team:t1"));

        await staging.UndoAsync(batch);
        Assert.False(await db.Nodes.AnyAsync(n => n.Id == "project:atlas"));
        Assert.True(await db.Nodes.AnyAsync(n => n.Id == "client:c1"));
    }

    private const string UpdateText = "Ava Lindqvist is now Principal Designer. Zed Quinn joined the Design team.";

    [Fact]
    public async Task Text_leaves_a_record_already_here_alone_unless_asked_to_update()
    {
        var (factory, service, staging) = await ExampleAsync((_, _) => new ExtractedData(
            [new ExtractedRecord("Person", "Ava Lindqvist", [new ExtractedValue("title", "Principal Designer")]), new ExtractedRecord("Person", "Zed Quinn", [])], []));

        var batch = await service.ExtractFromTextAsync(UpdateText, null, null, CancellationToken.None);
        await staging.CommitAsync(batch);

        await using var db = factory.CreateDbContext();
        Assert.Contains("Design lead", await db.Nodes.Where(n => n.Id == "person:p1").Select(n => n.Props).SingleAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Updating_changes_only_the_values_the_text_gives_on_an_exact_name_and_undo_puts_them_back()
    {
        var (factory, service, staging) = await ExampleAsync((_, _) => new ExtractedData(
            [new ExtractedRecord("Person", "Ava Lindqvist", [new ExtractedValue("title", "Principal Designer")]), new ExtractedRecord("Person", "Zed Quinn", [])], []));
        await using var db = factory.CreateDbContext();
        var before = await db.Nodes.Where(n => n.Id == "person:p1").Select(n => n.Props).SingleAsync();

        var batch = await service.ExtractFromTextAsync(UpdateText, null, null, CancellationToken.None, updateExisting: true);

        var staged = (await staging.GetAsync(batch))!;
        Assert.Equal(1, staged.Updating);
        var row = (await staging.ReviewAsync(batch, StagedKind.Record, 0, 10)).Rows.Single(r => r.Updating);
        Assert.Contains("title: Design lead → Principal Designer", row.Detail, StringComparison.Ordinal);

        var committed = await staging.CommitAsync(batch);
        Assert.Equal(1, committed.Updated);
        var after = await db.Nodes.AsNoTracking().Where(n => n.Id == "person:p1").Select(n => new { n.Name, n.Props }).SingleAsync();
        Assert.Equal("Ava Lindqvist", after.Name);
        Assert.Contains("Principal Designer", after.Props, StringComparison.Ordinal);
        Assert.Contains("Service design", after.Props, StringComparison.Ordinal);
        Assert.Equal(1, await db.Nodes.CountAsync(n => n.Type == "Person" && n.Name == "Ava Lindqvist"));

        await staging.UndoAsync(batch);
        Assert.Equal(before, await db.Nodes.AsNoTracking().Where(n => n.Id == "person:p1").Select(n => n.Props).SingleAsync());
    }

    [Fact]
    public async Task Updating_skips_a_record_that_only_has_a_similar_name_and_says_so()
    {
        var (factory, service, staging) = await ExampleAsync((_, _) => new ExtractedData(
            [new ExtractedRecord("Person", "Ava Lindqvist-Smith", [new ExtractedValue("title", "Principal Designer")]), new ExtractedRecord("Person", "Zed Quinn", [])], []));

        var batch = await service.ExtractFromTextAsync("Ava Lindqvist-Smith is now Principal Designer. Zed Quinn joined.", null, null, CancellationToken.None, updateExisting: true);

        var staged = (await staging.GetAsync(batch))!;
        Assert.Equal(0, staged.Updating);
        await staging.CommitAsync(batch);
        await using var db = factory.CreateDbContext();
        Assert.Contains("Design lead", await db.Nodes.Where(n => n.Id == "person:p1").Select(n => n.Props).SingleAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_different_record_with_the_same_key_gets_the_next_free_one()
    {
        var (factory, service, staging) = await ExampleAsync((_, _) => new ExtractedData(
            [new ExtractedRecord("Client", "Acme Co", [])], []));
        await using (var db = factory.CreateDbContext())
        {
            db.Nodes.Add(new Node { Id = "client:acme-co", UserId = AppUser.DefaultId, Type = "Client", Key = "acme-co", Name = "Zebra Holdings", Props = "{}" });
            await db.SaveChangesAsync();
        }

        var batch = await service.ExtractFromTextAsync("Acme Co", null, null, CancellationToken.None);
        await staging.CommitAsync(batch);

        await using var check = factory.CreateDbContext();
        var clients = await check.Nodes.Where(n => n.Type == "Client" && n.Key.StartsWith("acme-co")).Select(n => n.Key + "=" + n.Name).OrderBy(x => x).ToListAsync();
        Assert.Equal(["acme-co-2=Acme Co", "acme-co=Zebra Holdings"], clients);
    }

    [Fact]
    public async Task Text_with_nothing_for_the_model_is_refused_and_nothing_is_staged()
    {
        var (factory, service, _) = await ExampleAsync((_, _) => ExtractedData.Empty);

        var error = await Assert.ThrowsAsync<ExtractionFailedException>(() => service.ExtractFromTextAsync(Text, null, null, CancellationToken.None));

        Assert.Contains("Nothing in the text", error.Message, StringComparison.Ordinal);
        await using var db = factory.CreateDbContext();
        Assert.False(await db.ImportBatches.AnyAsync(b => b.Source == DataSource.Ai));
    }

    [Fact]
    public async Task Blank_text_is_refused()
    {
        var (_, service, _) = await ExampleAsync(Answer);

        await Assert.ThrowsAsync<ExtractionFailedException>(() => service.ExtractFromTextAsync("  ", null, null, CancellationToken.None));
    }
}
