using System.Text.Json.Nodes;
using Linkbase.Core;
using Linkbase.Core.Models;
using Linkbase.Data;
using Linkbase.Domains.Climbing.Data;
using Linkbase.Domains.Climbing.Import.Web;
using Microsoft.EntityFrameworkCore;

namespace Linkbase.Tests;

// The web import over the generic store. Each test gets a database of its own with the climbing model loaded and no records.
[Trait("Category", "Database")]
[Collection(PostgresCollection.Name)]
public class WebImportTests(PostgresFixture fixture) : IAsyncLifetime
{
    private ModelFactory _store = null!;
    private int _next;

    public async Task InitializeAsync()
    {
        _store = new ModelFactory(await fixture.CreateModelDatabaseAsync("SELECT 1"));
        await new ModelLoader(_store).LoadAsync(RepoPaths.Resolve("models/climbing/model.json"), TextWriter.Null);
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private static string Word() => Guid.NewGuid().ToString("N")[..12];

    private sealed class FakeExtractor(params ExtractedPage[] answers) : IClimbingExtractor
    {
        private int _next;
        public List<(string Text, List<string> Known)> Calls { get; } = [];

        public Task<ExtractedPage> ExtractAsync(string text, IReadOnlyList<string> knownCrags, CancellationToken cancellationToken)
        {
            Calls.Add((text, knownCrags.ToList()));
            return Task.FromResult(answers[Math.Min(_next++, answers.Length - 1)]);
        }
    }

    private WebImportRepository Repository() => new(_store);

    private WebImportService Service(IClimbingExtractor extractor) =>
        new(new PageTextFetcher(new HttpClient()), extractor, Repository());

    // Adds a record, and a link from it if given. Returns its node id.
    private async Task<string> AddAsync(string type, string name, JsonObject? props = null, string? rel = null, string? target = null)
    {
        var key = (++_next).ToString();
        var id = $"{type.ToLowerInvariant()}:{key}";
        await using var db = _store.CreateDbContext();
        db.Nodes.Add(new Node { Id = id, UserId = AppUser.DefaultId, Type = type, Key = key, Name = name, Props = (props ?? []).ToJsonString() });
        await db.SaveChangesAsync();
        if (rel is not null)
        {
            db.NodeLinks.Add(new NodeLink { SourceId = id, RelType = rel, TargetId = target! });
            await db.SaveChangesAsync();
        }

        return id;
    }

    private Task<string> CreateCountryAsync(string name) => AddAsync("Area", name, new JsonObject { ["kind"] = "country" });

    private async Task<(string CragId, string RouteId)> CreateCragAndRouteAsync(string country, string crag, string route)
    {
        var areaId = await CreateCountryAsync(country);
        var cragId = await AddAsync("Crag", crag, null, "IN", areaId);
        return (cragId, await AddAsync("Route", route, null, "AT", cragId));
    }

    private async Task<Node> NodeAsync(string type, string name)
    {
        await using var db = _store.CreateDbContext();
        return await db.Nodes.AsNoTracking().SingleAsync(n => n.Type == type && n.Name == name);
    }

    private async Task<bool> ExistsAsync(string type, string name)
    {
        await using var db = _store.CreateDbContext();
        return await db.Nodes.AnyAsync(n => n.Type == type && n.Name == name);
    }

    private async Task<string?> TargetAsync(string sourceId, string rel)
    {
        await using var db = _store.CreateDbContext();
        return await db.NodeLinks.AsNoTracking().Where(l => l.SourceId == sourceId && l.RelType == rel).Select(l => l.TargetId).SingleOrDefaultAsync();
    }

    private static string Prop(Node node, string key) => (string)JsonNode.Parse(node.Props)![key]!;

    private static string Text(params string[] names) => string.Join("\n\n", names);

    private static ExtractedPage Page(string crag, string? area, params string[] routes) => new(
        [],
        [new ExtractedCrag(crag, area, null, null, null)],
        routes.Select(r => new ExtractedRoute(r, crag, null, null, null, null, null, null)).ToList());

    private async Task<StagedImport> StageAsync(string text, ExtractedPage answer, string? url = null)
    {
        var batchId = await Service(new FakeExtractor(answer)).ExtractFromTextAsync(text, url, null, CancellationToken.None);
        return (await Repository().LoadStagedAsync(batchId))!;
    }

    [Fact]
    public async Task Extraction_stores_results_but_not_the_page_text()
    {
        var (crag, route, secret) = ($"Crag {Word()}", $"Route {Word()}", $"Guidebook prose {Word()}");
        var staged = await StageAsync(Text(crag, route, secret), Page(crag, null, route));

        Assert.Equal(ImportStatus.Staged, staged.Status);
        Assert.Equal(route, staged.Crags.Single().Routes.Single().Name);

        await using var db = _store.CreateDbContext();
        var payload = await db.ImportBatches.AsNoTracking().Where(b => b.Id == staged.BatchId).Select(b => b.RawPayload).SingleAsync();
        Assert.Contains(route, payload);
        Assert.DoesNotContain(secret, payload);
    }

    [Fact]
    public async Task Long_text_is_chunked_merged_and_passes_earlier_crags_on()
    {
        var (crag, one, two) = ($"Crag {Word()}", $"Route {Word()}", $"Route {Word()}");
        var padding = new string('x', 500);
        var text = string.Join("\n\n", Enumerable.Repeat($"{crag} {one} {two} {padding}", 80));
        var extractor = new FakeExtractor(Page(crag, null, one), Page(crag, null, one, two));

        var batchId = await Service(extractor).ExtractFromTextAsync(text, null, null, CancellationToken.None);

        Assert.True(extractor.Calls.Count > 1);
        Assert.Empty(extractor.Calls[0].Known);
        Assert.Equal([crag], extractor.Calls[1].Known);

        var staged = await Repository().LoadStagedAsync(batchId);
        Assert.Equal(2, staged!.Crags.Single().Routes.Count);
    }

    [Fact]
    public async Task Progress_reports_each_part()
    {
        var crag = $"Crag {Word()}";
        var reports = new List<ExtractionProgress>();
        await Service(new FakeExtractor(Page(crag, null))).ExtractFromTextAsync(crag, null, new InlineProgress(reports), CancellationToken.None);
        Assert.Contains(reports, r => r.Message == "Reading part 1 of 1");
        Assert.Equal("Done", reports[^1].Message);
    }

    private sealed class InlineProgress(List<ExtractionProgress> reports) : IProgress<ExtractionProgress>
    {
        public void Report(ExtractionProgress value) => reports.Add(value);
    }

    [Fact]
    public async Task A_failed_extraction_marks_the_batch_failed_with_the_reason()
    {
        var word = Word();
        var extractor = new ThrowingExtractor($"Broken {word}");

        await Assert.ThrowsAsync<ExtractionFailedException>(() =>
            Service(extractor).ExtractFromTextAsync("some text", null, null, CancellationToken.None));

        var batch = (await Repository().RecentBatchesAsync()).First(b => b.Error?.Contains(word) == true);
        Assert.Equal(ImportStatus.Failed, batch.Status);
    }

    private sealed class ThrowingExtractor(string message) : IClimbingExtractor
    {
        public Task<ExtractedPage> ExtractAsync(string text, IReadOnlyList<string> knownCrags, CancellationToken cancellationToken) =>
            throw new ExtractionFailedException(message);
    }

    [Fact]
    public async Task Rows_are_marked_new_existing_or_ambiguous()
    {
        var (word, country) = (Word(), $"Country {Word()}");
        var (cragName, routeName, similar, fresh) = ($"Stanage {word}", $"Hanging Groove {word}", $"Hanging Groove {word} Direct", $"Totally Unrelated {Word()}");
        var existing = await CreateCragAndRouteAsync(country, cragName, routeName);

        var staged = await StageAsync(
            Text(cragName, routeName, similar, fresh),
            Page(cragName.ToUpperInvariant(), null, routeName.ToUpperInvariant(), similar, fresh));

        var crag = staged.Crags.Single();
        Assert.Equal(StagedMatch.Existing, crag.Match);
        Assert.Equal(existing.CragId, crag.Choice);

        var routes = crag.Routes.ToDictionary(r => r.Name);
        Assert.Equal(StagedMatch.Existing, routes[routeName.ToUpperInvariant()].Match);
        Assert.Equal(existing.RouteId, routes[routeName.ToUpperInvariant()].Choice);

        Assert.Equal(StagedMatch.Ambiguous, routes[similar].Match);
        Assert.Equal(StagedRow.Undecided, routes[similar].Choice);
        Assert.Equal(existing.RouteId, routes[similar].Candidates.Single().Id);

        Assert.Equal(StagedMatch.New, routes[fresh].Match);
        Assert.Equal(StagedRow.CreateNew, routes[fresh].Choice);
    }

    [Fact]
    public async Task A_similar_crag_is_ambiguous_and_a_numbered_series_is_not()
    {
        var word = Word();
        await CreateCragAndRouteAsync($"Country {Word()}", $"Fawr Ridge {word}", $"R {Word()}");
        var staged = await StageAsync($"Fach Ridge {word}", Page($"Fach Ridge {word}", null));
        Assert.Equal(StagedMatch.Ambiguous, staged.Crags.Single().Match);

        await CreateCragAndRouteAsync($"Country {Word()}", $"Wall {word} A", $"R {Word()}");
        var series = await StageAsync($"Wall {word} B", Page($"Wall {word} B", null));
        Assert.Equal(StagedMatch.New, series.Crags.Single().Match);
    }

    [Fact]
    public async Task Choosing_a_different_crag_rematches_its_routes()
    {
        var (word, routeName) = (Word(), $"Route {Word()}");
        var existing = await CreateCragAndRouteAsync($"Country {Word()}", $"Other Place {word}", routeName);
        var cragName = $"Brand New {Word()}";
        var staged = await StageAsync(Text(cragName, routeName), Page(cragName, null, routeName));
        var crag = staged.Crags.Single();
        Assert.Equal(StagedMatch.New, crag.Routes.Single().Match);

        crag.Choice = existing.CragId;
        await Repository().RematchRoutesAsync(staged.BatchId, null, crag);

        Assert.Equal(existing.RouteId, crag.Routes.Single().Choice);
    }

    [Fact]
    public async Task Commit_creates_rows_with_the_batch_and_source_url_and_a_rerun_matches_them()
    {
        var country = $"Country {Word()}";
        var countryId = await CreateCountryAsync(country);
        var (crag, route) = ($"Crag {Word()}", $"Route {Word()}");
        var url = $"https://example.com/{Word()}";
        var answer = new ExtractedPage(
            [],
            [new ExtractedCrag(crag, country, null, null, null)],
            [new ExtractedRoute(route, crag, "VS 4c", "uk_trad", "trad", 20, 1, "My short summary.")]);
        var staged = await StageAsync(Text(crag, country, route, "VS 4c"), answer, url);
        Assert.Equal(countryId, staged.Crags.Single().AreaId);

        var summary = await Repository().CommitAsync(staged.BatchId, staged.Crags);

        Assert.Equal(new CommitSummary(0, 1, 0, 1, 0), summary);
        var row = await NodeAsync("Route", route);
        var props = JsonNode.Parse(row.Props)!;
        Assert.Equal(("VS 4c", "uk_trad", "trad", 20, 1), ((string)props["grade"]!, (string)props["grade_system"]!, (string)props["discipline"]!, (int)props["length_m"]!, (int)props["pitches"]!));
        Assert.Equal(GradeRanking.Rank(GradeSystem.UkTrad, "VS 4c"), (double?)props["grade_rank"]);
        Assert.Equal(staged.BatchId, row.ImportBatchId);
        var cragNode = await NodeAsync("Crag", crag);
        Assert.Equal(cragNode.Id, await TargetAsync(row.Id, "AT"));
        Assert.Equal(countryId, await TargetAsync(cragNode.Id, "IN"));

        await using var db = _store.CreateDbContext();
        var routeRef = await db.ExternalRefs.AsNoTracking().SingleAsync(r => r.NodeId == row.Id);
        Assert.Equal((DataSource.WebImport, url, "Route"), (routeRef.Source, routeRef.Url, routeRef.NodeType));

        // The relationship counts the home page shows follow.
        Assert.Equal(1, (await db.LinkDefinitions.SingleAsync(d => d.SourceType == "Route" && d.RelType == "AT")).Links);
        Assert.Equal(1, (await db.LinkDefinitions.SingleAsync(d => d.SourceType == "Crag" && d.RelType == "IN")).Links);

        var rerun = await StageAsync(Text(crag, country, route, "VS 4c"), answer, url);
        Assert.Equal(StagedMatch.Existing, rerun.Crags.Single().Match);
        Assert.Equal(row.Id, rerun.Crags.Single().Routes.Single().Choice);
        var second = await Repository().CommitAsync(rerun.BatchId, rerun.Crags);
        Assert.Equal(new CommitSummary(0, 0, 1, 0, 1), second);
        Assert.Equal(1, await db.Nodes.CountAsync(n => n.Type == "Route" && n.Name == route));
    }

    [Fact]
    public async Task Commit_honours_edits_and_unticked_rows()
    {
        var country = $"Country {Word()}";
        await CreateCountryAsync(country);
        var (crag, keep, skip, skippedCrag) = ($"Crag {Word()}", $"Route {Word()}", $"Route {Word()}", $"Crag {Word()}");
        var answer = new ExtractedPage(
            [],
            [new ExtractedCrag(crag, country, null, null, null), new ExtractedCrag(skippedCrag, country, null, null, null)],
            [new ExtractedRoute(keep, crag, null, null, null, null, null, null), new ExtractedRoute(skip, crag, null, null, null, null, null, null)]);
        var staged = await StageAsync(Text(crag, country, keep, skip, skippedCrag), answer);

        var stagedCrag = staged.Crags.Single(c => c.Name == crag);
        var renamed = $"Renamed {Word()}";
        var kept = stagedCrag.Routes.Single(r => r.Name == keep);
        kept.Name = renamed;
        kept.Grade = "E1 5b";
        stagedCrag.Routes.Single(r => r.Name == skip).Include = false;
        staged.Crags.Single(c => c.Name == skippedCrag).Include = false;

        var summary = await Repository().CommitAsync(staged.BatchId, staged.Crags);

        Assert.Equal(new CommitSummary(0, 1, 0, 1, 0), summary);
        Assert.Equal("E1 5b", Prop(await NodeAsync("Route", renamed), "grade"));
        Assert.False(await ExistsAsync("Route", skip));
        Assert.False(await ExistsAsync("Crag", skippedCrag));
    }

    [Fact]
    public async Task Commit_refuses_undecided_rows_and_missing_areas_and_writes_nothing()
    {
        var word = Word();
        var existing = await CreateCragAndRouteAsync($"Country {Word()}", $"Stanage {word}", $"R {Word()}");

        var noArea = $"Crag {Word()}";
        var missing = await StageAsync(noArea, Page(noArea, null));
        var areaError = await Assert.ThrowsAsync<LinkbaseValidationException>(() => Repository().CommitAsync(missing.BatchId, missing.Crags));
        Assert.Contains("area", areaError.Message);

        var similar = $"Stanages {word}";
        var ambiguous = await StageAsync(similar, Page(similar, null));
        var choiceError = await Assert.ThrowsAsync<LinkbaseValidationException>(() => Repository().CommitAsync(ambiguous.BatchId, ambiguous.Crags));
        Assert.Contains("Choose a match", choiceError.Message);

        Assert.False(await ExistsAsync("Crag", noArea) || await ExistsAsync("Crag", similar));
        Assert.Equal(existing.CragId, ambiguous.Crags.Single().Candidates.Single().Id);
    }

    [Fact]
    public async Task Commit_rolls_back_entirely_if_a_row_fails()
    {
        var country = await CreateCountryAsync($"Country {Word()}");
        var (good, bad) = ($"Crag {Word()}", $"Crag {Word()}");
        var staged = await StageAsync(Text(good, bad), new ExtractedPage([], [new ExtractedCrag(good, null, null, null, null), new ExtractedCrag(bad, null, null, null, null)], []));
        staged.Crags[0].AreaId = country;
        staged.Crags[1].AreaId = "area:missing";

        await Assert.ThrowsAnyAsync<DbUpdateException>(() => Repository().CommitAsync(staged.BatchId, staged.Crags));

        await using var db = _store.CreateDbContext();
        Assert.False(await ExistsAsync("Crag", good));
        Assert.Equal(ImportStatus.Staged, await db.ImportBatches.AsNoTracking().Where(b => b.Id == staged.BatchId).Select(b => b.Status).SingleAsync());
    }

    [Fact]
    public async Task A_batch_can_only_be_committed_once()
    {
        var country = await CreateCountryAsync($"Country {Word()}");
        var crag = $"Crag {Word()}";
        var staged = await StageAsync(crag, Page(crag, null));
        staged.Crags[0].AreaId = country;
        await Repository().CommitAsync(staged.BatchId, staged.Crags);

        await Assert.ThrowsAsync<LinkbaseValidationException>(() => Repository().CommitAsync(staged.BatchId, staged.Crags));
    }

    [Fact]
    public async Task A_new_area_is_created_under_its_parent_and_a_top_level_area_must_be_a_country()
    {
        var parent = $"Country {Word()}";
        var parentId = await CreateCountryAsync(parent);
        var (region, crag) = ($"Region {Word()}", $"Crag {Word()}");
        var answer = new ExtractedPage([new ExtractedArea(region, parent, "region")], [new ExtractedCrag(crag, region, null, null, null)], []);

        var staged = await StageAsync(Text(region, parent, crag), answer);
        var row = staged.Crags.Single();
        Assert.Equal(region, row.NewAreaName);
        Assert.Equal(parentId, row.NewAreaParentId);

        var summary = await Repository().CommitAsync(staged.BatchId, staged.Crags);

        Assert.Equal(1, summary.AreasCreated);
        var area = await NodeAsync("Area", region);
        Assert.Equal(parentId, await TargetAsync(area.Id, "IN"));
        Assert.Equal("region", Prop(area, "kind"));

        var (orphanRegion, orphanCrag) = ($"Region {Word()}", $"Crag {Word()}");
        var orphan = await StageAsync(Text(orphanRegion, orphanCrag), new ExtractedPage([], [new ExtractedCrag(orphanCrag, orphanRegion, null, null, null)], []));
        var error = await Assert.ThrowsAsync<LinkbaseValidationException>(() => Repository().CommitAsync(orphan.BatchId, orphan.Crags));
        Assert.Contains(orphanRegion, error.Message);
    }

    private async Task<(StagedImport Staged, string Region, string Crag, string Route)> CommitNewAsync()
    {
        var parent = $"Country {Word()}";
        await CreateCountryAsync(parent);
        var (region, crag, route) = ($"Region {Word()}", $"Crag {Word()}", $"Route {Word()}");
        var answer = new ExtractedPage(
            [new ExtractedArea(region, parent, "region")],
            [new ExtractedCrag(crag, region, null, null, null)],
            [new ExtractedRoute(route, crag, null, null, null, null, null, null)]);
        var staged = await StageAsync(Text(region, parent, crag, route), answer);
        await Repository().CommitAsync(staged.BatchId, staged.Crags);
        return (staged, region, crag, route);
    }

    [Fact]
    public async Task Undo_removes_everything_the_batch_created()
    {
        var (staged, region, crag, route) = await CommitNewAsync();

        var undo = await Repository().UndoAsync(staged.BatchId);

        Assert.Equal(new UndoSummary(1, 1, 1, []), undo with { Skipped = [] });
        Assert.Empty(undo.Skipped);
        await using var db = _store.CreateDbContext();
        Assert.False(await ExistsAsync("Route", route));
        Assert.False(await ExistsAsync("Crag", crag));
        Assert.False(await ExistsAsync("Area", region));

        // Every link went with its records, and the counts are back to nothing.
        Assert.Empty(await db.NodeLinks.ToListAsync());
        Assert.All(await db.LinkDefinitions.ToListAsync(), d => Assert.Equal(0, d.Links));
        Assert.False(await db.ExternalRefs.AnyAsync(r => r.ImportBatchId == staged.BatchId));
        Assert.Equal(ImportStatus.Undone, await db.ImportBatches.AsNoTracking().Where(b => b.Id == staged.BatchId).Select(b => b.Status).SingleAsync());
    }

    [Fact]
    public async Task Undo_keeps_a_route_with_an_ascent_and_everything_above_it_and_can_run_again()
    {
        var (staged, region, crag, route) = await CommitNewAsync();
        var routeId = (await NodeAsync("Route", route)).Id;
        var ascent = await AddAsync("Ascent", route, new JsonObject { ["date"] = "2020-01-01", ["date_precision"] = "day", ["style"] = "lead", ["source"] = "manual" }, "OF", routeId);

        var undo = await Repository().UndoAsync(staged.BatchId);

        await using var db = _store.CreateDbContext();
        Assert.Equal(0, undo.RoutesDeleted);
        Assert.Contains(undo.Skipped, s => s.Contains(route) && s.Contains("1 ascent"));
        Assert.Contains(undo.Skipped, s => s.Contains(crag));
        Assert.Contains(undo.Skipped, s => s.Contains(region));
        Assert.True(await db.Nodes.AnyAsync(n => n.Id == routeId));
        Assert.True(await db.ExternalRefs.AnyAsync(r => r.NodeId == routeId));
        Assert.Equal(ImportStatus.Committed, await db.ImportBatches.AsNoTracking().Where(b => b.Id == staged.BatchId).Select(b => b.Status).SingleAsync());

        await db.NodeLinks.Where(l => l.SourceId == ascent).ExecuteDeleteAsync();
        await db.Nodes.Where(n => n.Id == ascent).ExecuteDeleteAsync();
        var again = await Repository().UndoAsync(staged.BatchId);
        Assert.Empty(again.Skipped);
        Assert.False(await db.Nodes.AnyAsync(n => n.Id == routeId));
    }

    [Fact]
    public async Task Undo_keeps_a_row_edited_since_the_import()
    {
        var (staged, _, _, route) = await CommitNewAsync();
        await using (var db = _store.CreateDbContext())
        {
            await db.Nodes.Where(n => n.Type == "Route" && n.Name == route).ExecuteUpdateAsync(u => u.SetProperty(n => n.UpdatedAt, DateTimeOffset.UtcNow.AddMinutes(1)));
        }

        var undo = await Repository().UndoAsync(staged.BatchId);

        Assert.Contains(undo.Skipped, s => s.Contains(route) && s.Contains("edited"));
        Assert.True(await ExistsAsync("Route", route));
    }

    [Fact]
    public async Task Undo_leaves_rows_that_were_already_there_but_removes_the_refs_it_added()
    {
        var (cragName, routeName) = ($"Crag {Word()}", $"Route {Word()}");
        var existing = await CreateCragAndRouteAsync($"Country {Word()}", cragName, routeName);
        var staged = await StageAsync(Text(cragName, routeName), Page(cragName, null, routeName), $"https://example.com/{Word()}");
        var summary = await Repository().CommitAsync(staged.BatchId, staged.Crags);
        Assert.Equal(new CommitSummary(0, 0, 1, 0, 1), summary);

        var undo = await Repository().UndoAsync(staged.BatchId);

        Assert.Empty(undo.Skipped);
        await using var db = _store.CreateDbContext();
        Assert.True(await db.Nodes.AnyAsync(n => n.Id == existing.RouteId));
        Assert.False(await db.ExternalRefs.AnyAsync(r => r.ImportBatchId == staged.BatchId));
    }

    [Fact]
    public async Task Only_committed_batches_can_be_undone()
    {
        var crag = $"Crag {Word()}";
        var staged = await StageAsync(crag, Page(crag, null));
        await Assert.ThrowsAsync<LinkbaseValidationException>(() => Repository().UndoAsync(staged.BatchId));
    }

    [Fact]
    public async Task The_whole_fixture_flows_from_model_output_to_staged_rows_without_the_bad_values()
    {
        var text = await File.ReadAllTextAsync(Path.Combine("Fixtures", "web", "crag-article.txt"));
        var path = Path.Combine("Fixtures", "web", "crag-article.extraction.json");

        var batchId = await Service(new FixtureClimbingExtractor(path)).ExtractFromTextAsync(text, null, null, CancellationToken.None);

        var staged = await Repository().LoadStagedAsync(batchId);
        Assert.Equal(2, staged!.Crags.Count);
        var routes = staged.Crags.SelectMany(c => c.Routes).ToList();
        Assert.Equal(5, routes.Count);
        Assert.DoesNotContain(routes, r => r.Grade == "E9 7a");
        Assert.Equal(4, staged.Warnings.Count);
    }
}
