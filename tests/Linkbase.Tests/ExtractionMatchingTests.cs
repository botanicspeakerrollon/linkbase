using System.Text.Json.Nodes;
using Linkbase.Core;
using Linkbase.Core.Extraction;
using Linkbase.Core.Records;
using Linkbase.Core.Staging;
using Linkbase.Data;
using Linkbase.Domains.Climbing.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace Linkbase.Tests;

internal static class ClimbingExtraction
{
    // The hook only checks values and writes notes here, so it needs none of its services.
    public static ClimbingRecordHook Hook() => new(null!, null!, () => "", NullLogger<ClimbingRecordHook>.Instance);

    public static ExtractedRecord Rec(string type, string name, params (string Field, string Value)[] fields) =>
        new(type, name, fields.Select(f => new ExtractedValue(f.Field, f.Value)).ToList());

    public static ExtractedLink In(string crag, string area) => new("IN", "Crag", crag, "Area", area);

    public static ExtractedLink At(string route, string crag) => new("AT", "Route", route, "Crag", crag);
}

public class ClimbingExtractionRulesTests
{
    private static JsonObject Props(params (string Key, JsonNode? Value)[] values)
    {
        var props = new JsonObject();
        foreach (var (key, value) in values)
        {
            props[key] = value;
        }

        return props;
    }

    [Fact]
    public void Believable_values_are_kept()
    {
        var route = Props(("grade", "E1 5b"), ("grade_system", "uk_trad"), ("length_m", 25), ("pitches", 2));
        Assert.Empty(ClimbingExtraction.Hook().CheckExtracted("Route", "Quiet Harbour", route, "E1 5b 25m"));
        Assert.Equal(4, route.Count);

        var crag = Props(("lat", 54.1234), ("lon", -2.5678));
        Assert.Empty(ClimbingExtraction.Hook().CheckExtracted("Crag", "Edge", crag, "It sits at 54.1234, -2.5678."));
        Assert.Equal(2, crag.Count);
    }

    [Fact]
    public void Impossible_lengths_and_a_grade_system_with_no_grade_are_removed()
    {
        var route = Props(("grade_system", "french"), ("length_m", 90_000), ("pitches", 0));

        var warnings = ClimbingExtraction.Hook().CheckExtracted("Route", "Odd", route, "Odd");

        Assert.Empty(route);
        Assert.Equal(3, warnings.Count);
    }

    [Theory]
    [InlineData(95.0, 10.0, "at 95, 10")]
    [InlineData(54.1, 10.0, "at 54.1 only")]
    [InlineData(54.1, 10.0, "at 54.12, 10")]
    public void Coordinates_outside_the_globe_or_missing_from_the_text_are_removed(double lat, double lon, string text)
    {
        var crag = Props(("lat", lat), ("lon", lon), ("rock_type", "grit"));

        var warnings = ClimbingExtraction.Hook().CheckExtracted("Crag", "Edge", crag, text);

        Assert.Single(warnings);
        Assert.Null(crag["lat"]);
        Assert.Null(crag["lon"]);
        Assert.NotNull(crag["rock_type"]);
    }

    [Fact]
    public void The_prompt_carries_the_modules_notes_only_when_the_model_has_its_types()
    {
        var hooks = new RecordHooks([ClimbingExtraction.Hook()]);
        var withRoutes = ExtractionSchema.From([new RecordType("Route", null, null, [])], [], hooks);
        var without = ExtractionSchema.From([new RecordType("Client", null, null, [])], [], hooks);

        Assert.Contains("uk_trad", ExtractionPrompts.SystemPrompt(withRoutes), StringComparison.Ordinal);
        Assert.DoesNotContain("uk_trad", ExtractionPrompts.SystemPrompt(without), StringComparison.Ordinal);
    }
}

[Trait("Category", "Database")]
public sealed class ExtractionMatchingTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    private sealed class FakeExtractor(ExtractedData data) : IModelExtractor
    {
        public Task<ExtractedData> ExtractAsync(ExtractionSchema schema, string text, IReadOnlyList<string> knownRecords, CancellationToken cancellationToken) =>
            Task.FromResult(data);
    }

    private async Task<(ModelFactory Store, ModelExtractionService Service, ImportStagingService Staging)> SetupAsync(IModelExtractor extractor, bool hooks = false)
    {
        var store = await ClimbingStore.CreateAsync(fixture);
        var staging = new ImportStagingService(store);
        var service = new ModelExtractionService(
            new RecordRepository(store), extractor, new PageTextFetcher(new HttpClient()), store, staging, hooks ? new RecordHooks([ClimbingExtraction.Hook()]) : null);
        return (store, service, staging);
    }

    private Task<(ModelFactory Store, ModelExtractionService Service, ImportStagingService Staging)> SetupAsync(ExtractedData data) =>
        SetupAsync(new FakeExtractor(data));

    // England with two crags, and a route called Hope at the first.
    private static async Task<(string England, string Alpha, string Beta)> SeedAsync(ModelFactory store)
    {
        var england = await ClimbingStore.AddCountryAsync(store, "England");
        var alpha = await ClimbingStore.AddAsync(store, "Crag", "Alpha Wall", null, "IN", england);
        var beta = await ClimbingStore.AddAsync(store, "Crag", "Beta Wall", null, "IN", england);
        await ClimbingStore.AddAsync(store, "Route", "Hope", null, "AT", alpha);
        return (england, alpha, beta);
    }

    [Fact]
    public async Task A_similar_name_is_the_record_already_there_but_a_numbered_one_is_not()
    {
        var (store, service, staging) = await SetupAsync(new ExtractedData(
            [ClimbingExtraction.Rec("Crag", "Alpha Walls"), ClimbingExtraction.Rec("Crag", "Boulder 2")],
            [ClimbingExtraction.In("Alpha Walls", "England"), ClimbingExtraction.In("Boulder 2", "England")]));
        var (england, _, _) = await SeedAsync(store);
        await ClimbingStore.AddAsync(store, "Crag", "Boulder 1", null, "IN", england);

        var batch = await service.ExtractFromTextAsync("Alpha Walls and Boulder 2 are in England.", null, null, CancellationToken.None);

        var staged = (await staging.GetAsync(batch))!;
        Assert.Equal(1, staged.Records);
        Assert.Equal(1, staged.AlreadyThere);
        Assert.Contains(staged.Notes, n => n.Contains("Treated Crag 'Alpha Walls' as the Crag already here, 'Alpha Wall'", StringComparison.Ordinal));
        Assert.Equal("Boulder 2", (await staging.ReviewAsync(batch, StagedKind.Record, 0, 10)).Rows.Single().Title);
    }

    [Fact]
    public async Task Names_that_could_be_several_records_are_added_as_new_with_a_note()
    {
        var (store, service, staging) = await SetupAsync(new ExtractedData(
            [ClimbingExtraction.Rec("Crag", "Granite Buttressy")], [ClimbingExtraction.In("Granite Buttressy", "England")]));
        var (england, _, _) = await SeedAsync(store);
        await ClimbingStore.AddAsync(store, "Crag", "Granite Buttress", null, "IN", england);
        await ClimbingStore.AddAsync(store, "Crag", "Granite Buttresses", null, "IN", england);

        var batch = await service.ExtractFromTextAsync("Granite Buttressy is in England.", null, null, CancellationToken.None);

        var staged = (await staging.GetAsync(batch))!;
        Assert.Equal(1, staged.Records);
        Assert.Contains(staged.Notes, n => n.Contains("is like several already here", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_route_is_the_same_only_at_the_crag_the_text_puts_it_at()
    {
        var (store, service, staging) = await SetupAsync(new ExtractedData(
            [ClimbingExtraction.Rec("Crag", "Beta Wall"), ClimbingExtraction.Rec("Route", "Hope")], [ClimbingExtraction.At("Hope", "Beta Wall")]));
        var (_, _, beta) = await SeedAsync(store);

        // Hope is a route at Alpha Wall, not Beta Wall, so this is another one.
        var batch = await service.ExtractFromTextAsync("Hope is at Beta Wall.", null, null, CancellationToken.None);
        var staged = (await staging.GetAsync(batch))!;
        Assert.Equal((1, 1), (staged.Records, staged.AlreadyThere));

        await staging.CommitAsync(batch);
        await using var db = store.CreateDbContext();
        Assert.Equal(2, await db.Nodes.CountAsync(n => n.Type == "Route" && n.Name == "Hope"));
        Assert.True(await db.NodeLinks.AnyAsync(l => l.RelType == "AT" && l.Source.Name == "Hope" && l.TargetId == beta && l.Source.ImportBatchId == batch));

        // The same text again finds it, now that Beta Wall has a Hope.
        var again = await service.ExtractFromTextAsync("Hope is at Beta Wall.", null, null, CancellationToken.None);
        Assert.Equal((0, 2), ((await staging.GetAsync(again))!.Records, (await staging.GetAsync(again))!.AlreadyThere));
    }

    [Fact]
    public async Task A_page_address_is_kept_for_what_was_read_from_it_and_goes_with_an_undo()
    {
        var (store, service, staging) = await SetupAsync(new ExtractedData(
            [ClimbingExtraction.Rec("Crag", "Alpha Wall"), ClimbingExtraction.Rec("Crag", "Gamma Crag")],
            [ClimbingExtraction.In("Gamma Crag", "England")]));
        var (_, alpha, _) = await SeedAsync(store);

        var batch = await service.ExtractFromTextAsync("Alpha Wall and Gamma Crag are in England.", "https://Example.test/guide/?page=2#top", null, CancellationToken.None);
        await staging.CommitAsync(batch);

        await using var db = store.CreateDbContext();
        var refs = await db.ExternalRefs.AsNoTracking().Where(r => r.ImportBatchId == batch).ToListAsync();
        Assert.Equal(2, refs.Count);
        Assert.All(refs, r => Assert.Equal((DataSource.Ai, "https://Example.test/guide/?page=2#top"), (r.Source, r.Url)));
        Assert.Contains(refs, r => r.NodeId == alpha && r.ExternalId == "https://example.test/guide?page=2|Crag|alphawall");
        Assert.Contains(refs, r => r.ExternalId == "https://example.test/guide?page=2|Crag|gammacrag");

        await staging.UndoAsync(batch);
        Assert.Empty(await db.ExternalRefs.AsNoTracking().Where(r => r.ImportBatchId == batch).ToListAsync());
        Assert.True(await db.Nodes.AnyAsync(n => n.Id == alpha));
    }

    [Fact]
    public async Task Pasted_text_has_no_address_to_keep()
    {
        var (store, service, staging) = await SetupAsync(new ExtractedData(
            [ClimbingExtraction.Rec("Crag", "Gamma Crag")], [ClimbingExtraction.In("Gamma Crag", "England")]));
        await SeedAsync(store);

        await staging.CommitAsync(await service.ExtractFromTextAsync("Gamma Crag is in England.", null, null, CancellationToken.None));

        await using var db = store.CreateDbContext();
        Assert.Empty(await db.ExternalRefs.ToListAsync());
    }

    private async Task<(ModelFactory Store, ImportStagingService Staging, long Batch)> CommittedAsync()
    {
        var (store, service, staging) = await SetupAsync(new ExtractedData(
            [ClimbingExtraction.Rec("Crag", "Gamma Crag"), ClimbingExtraction.Rec("Route", "Fresh Line")],
            [ClimbingExtraction.In("Gamma Crag", "England"), ClimbingExtraction.At("Fresh Line", "Gamma Crag")]));
        await SeedAsync(store);
        var batch = await service.ExtractFromTextAsync("Fresh Line is at Gamma Crag in England.", null, null, CancellationToken.None);
        await staging.CommitAsync(batch);
        return (store, staging, batch);
    }

    private static async Task EditAsync(ModelFactory store, string name)
    {
        await using var db = store.CreateDbContext();
        await db.Nodes.Where(n => n.Name == name).ExecuteUpdateAsync(s => s.SetProperty(n => n.UpdatedAt, n => n.CreatedAt.AddMinutes(5)));
    }

    private static async Task AssertLinkCountsAsync(ModelFactory store)
    {
        await using var db = store.CreateDbContext();
        foreach (var definition in await db.LinkDefinitions.AsNoTracking().ToListAsync())
        {
            var actual = await db.NodeLinks.CountAsync(l => l.RelType == definition.RelType && l.Source.Type == definition.SourceType && l.Target.Type == definition.TargetType);
            Assert.Equal(actual, definition.Links);
        }
    }

    [Fact]
    public async Task Undo_removes_what_was_not_touched_and_leaves_the_counts_right()
    {
        var (store, staging, batch) = await CommittedAsync();
        await AssertLinkCountsAsync(store);

        var undone = await staging.UndoAsync(batch);

        Assert.True(undone.Complete);
        Assert.Equal(2, undone.Records);
        await using var db = store.CreateDbContext();
        Assert.False(await db.Nodes.AnyAsync(n => n.Name == "Gamma Crag" || n.Name == "Fresh Line"));
        await AssertLinkCountsAsync(store);
    }

    [Fact]
    public async Task Undo_keeps_a_record_edited_since_and_the_link_its_type_requires()
    {
        var (store, staging, batch) = await CommittedAsync();
        await EditAsync(store, "Gamma Crag");

        var undone = await staging.UndoAsync(batch);

        Assert.False(undone.Complete);
        Assert.Equal(1, undone.Records);
        Assert.Equal("Crag 'Gamma Crag': edited since the import.", Assert.Single(undone.Kept));
        await using var db = store.CreateDbContext();
        Assert.False(await db.Nodes.AnyAsync(n => n.Name == "Fresh Line"));
        Assert.True(await db.NodeLinks.AnyAsync(l => l.RelType == "IN" && l.Source.Name == "Gamma Crag" && l.Target.Name == "England"));
        Assert.Equal(ImportStatus.Committed, (await db.ImportBatches.SingleAsync(b => b.Id == batch)).Status);
        await AssertLinkCountsAsync(store);

        // Once it is no longer edited, the rest can go.
        await db.Nodes.Where(n => n.Name == "Gamma Crag").ExecuteUpdateAsync(s => s.SetProperty(n => n.UpdatedAt, n => n.CreatedAt));
        Assert.True((await staging.UndoAsync(batch)).Complete);
        Assert.False(await db.Nodes.AnyAsync(n => n.Name == "Gamma Crag"));
        await AssertLinkCountsAsync(store);
    }

    [Fact]
    public async Task Undo_keeps_what_a_record_that_stays_needs()
    {
        var (store, staging, batch) = await CommittedAsync();
        await EditAsync(store, "Fresh Line");

        var undone = await staging.UndoAsync(batch);

        Assert.Equal(
            ["Crag 'Gamma Crag': needed by a record that stays.", "Route 'Fresh Line': edited since the import."],
            undone.Kept);
        Assert.Equal(0, undone.Records);
        await using var db = store.CreateDbContext();
        Assert.True(await db.NodeLinks.AnyAsync(l => l.RelType == "AT" && l.Source.Name == "Fresh Line" && l.Target.Name == "Gamma Crag"));
        await AssertLinkCountsAsync(store);
    }

    [Fact]
    public async Task Undo_keeps_a_record_something_links_to_now()
    {
        var (store, staging, batch) = await CommittedAsync();
        string route;
        await using (var db = store.CreateDbContext())
        {
            route = await db.Nodes.Where(n => n.Name == "Fresh Line").Select(n => n.Id).SingleAsync();
        }

        var ascent = await ClimbingStore.AddAsync(store, "Ascent", "Fresh Line, today", null, "OF", route);

        var undone = await staging.UndoAsync(batch);

        Assert.Contains("Route 'Fresh Line': linked to since the import.", undone.Kept);
        await using var check = store.CreateDbContext();
        Assert.True(await check.NodeLinks.AnyAsync(l => l.SourceId == ascent && l.TargetId == route));
        await AssertLinkCountsAsync(store);
    }

    [Fact]
    public async Task A_whole_article_flows_from_the_models_answer_to_records_without_the_bad_values()
    {
        var fixtureDir = RepoPaths.Resolve("tests/Linkbase.Tests/Fixtures/web");
        var (store, service, staging) = await SetupAsync(new FixtureModelExtractor(Path.Combine(fixtureDir, "crag-article.extraction.json")), hooks: true);
        var text = await File.ReadAllTextAsync(Path.Combine(fixtureDir, "crag-article.txt"));

        var batch = await service.ExtractFromTextAsync(text, null, null, CancellationToken.None);

        var staged = (await staging.GetAsync(batch))!;
        Assert.Equal((9, 8), (staged.Records, staged.Links));
        Assert.Contains(staged.Notes, n => n.Contains("Crag The Page Never Mentions", StringComparison.Ordinal));
        Assert.Contains(staged.Notes, n => n.Contains("Route The Page Never Mentions", StringComparison.Ordinal));
        Assert.Contains(staged.Notes, n => n.Contains("E9 7a", StringComparison.Ordinal));
        Assert.Contains(staged.Notes, n => n.Contains("Long Way Round's grade_system", StringComparison.Ordinal));

        await staging.CommitAsync(batch);
        await using var db = store.CreateDbContext();
        var crag = JsonNode.Parse(await db.Nodes.Where(n => n.Name == "Zzq Sandstone Edge").Select(n => n.Props).SingleAsync())!;
        Assert.Equal(54.1234, (double?)crag["lat"]);
        var harbour = JsonNode.Parse(await db.Nodes.Where(n => n.Name == "Quiet Harbour").Select(n => n.Props).SingleAsync())!;
        Assert.NotNull(harbour["grade_rank"]);
        Assert.Equal(25, (int?)harbour["length_m"]);
        var longWay = JsonNode.Parse(await db.Nodes.Where(n => n.Name == "Long Way Round").Select(n => n.Props).SingleAsync())!;
        Assert.Null(longWay["grade"]);
        Assert.Null(longWay["grade_system"]);
        await AssertLinkCountsAsync(store);
    }
}
