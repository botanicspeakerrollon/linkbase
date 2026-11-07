using Linkbase.Data;
using Linkbase.Domains.Climbing.Data;
using Linkbase.Core.Graph;
using Linkbase.Domains.Climbing.Import.Web;
using Microsoft.EntityFrameworkCore;

namespace Linkbase.Tests;

[Trait("Category", "Database")]
[Collection(PostgresCollection.Name)]
public class WebImportTests(PostgresFixture fixture)
{
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

    private WebImportRepository Repository() => new(fixture.ContextFactory());

    private WebImportService Service(IClimbingExtractor extractor) =>
        new(new PageTextFetcher(new HttpClient()), extractor, Repository());

    private async Task<long> CreateCountryAsync(string name)
    {
        await using var db = fixture.CreateContext();
        var area = new Area { UserId = AppUser.DefaultId, Name = name, Kind = AreaKind.Country };
        db.Areas.Add(area);
        await db.SaveChangesAsync();
        return area.Id;
    }

    private async Task<(long CragId, long RouteId)> CreateCragAndRouteAsync(string country, string crag, string route)
    {
        var areaId = await CreateCountryAsync(country);
        await using var db = fixture.CreateContext();
        var row = new Route
        {
            UserId = AppUser.DefaultId,
            Name = route,
            Crag = new Crag { UserId = AppUser.DefaultId, Name = crag, AreaId = areaId },
        };
        db.Routes.Add(row);
        await db.SaveChangesAsync();
        return (row.CragId, row.Id);
    }

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

        await using var db = fixture.CreateContext();
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
        await using var db = fixture.CreateContext();
        var row = await db.Routes.AsNoTracking().Where(r => r.Name == route)
            .Select(r => new { r.Id, r.Grade, r.GradeSystem, r.Description, r.ImportBatchId, Area = r.Crag.AreaId })
            .SingleAsync();
        Assert.Equal("VS 4c", row.Grade);
        Assert.Equal(GradeSystem.UkTrad, row.GradeSystem);
        Assert.Equal("My short summary.", row.Description);
        Assert.Equal(staged.BatchId, row.ImportBatchId);
        Assert.Equal(countryId, row.Area);

        var routeRef = await db.ExternalRefs.AsNoTracking().SingleAsync(r => r.EntityType == ExternalEntityType.Route && r.EntityId == row.Id);
        Assert.Equal(DataSource.WebImport, routeRef.Source);
        Assert.Equal(url, routeRef.Url);

        var rerun = await StageAsync(Text(crag, country, route, "VS 4c"), answer, url);
        Assert.Equal(StagedMatch.Existing, rerun.Crags.Single().Match);
        Assert.Equal(row.Id, rerun.Crags.Single().Routes.Single().Choice);
        var second = await Repository().CommitAsync(rerun.BatchId, rerun.Crags);
        Assert.Equal(new CommitSummary(0, 0, 1, 0, 1), second);
        Assert.Equal(1, await db.Routes.CountAsync(r => r.Name == route));
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
        await using var db = fixture.CreateContext();
        Assert.Equal("E1 5b", await db.Routes.AsNoTracking().Where(r => r.Name == renamed).Select(r => r.Grade).SingleAsync());
        Assert.False(await db.Routes.AnyAsync(r => r.Name == skip));
        Assert.False(await db.Crags.AnyAsync(c => c.Name == skippedCrag));
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

        await using var db = fixture.CreateContext();
        Assert.False(await db.Crags.AnyAsync(c => c.Name == noArea || c.Name == similar));
        Assert.Equal(existing.CragId, ambiguous.Crags.Single().Candidates.Single().Id);
    }

    [Fact]
    public async Task Commit_rolls_back_entirely_if_a_row_fails()
    {
        var country = await CreateCountryAsync($"Country {Word()}");
        var (good, bad) = ($"Crag {Word()}", $"Crag {Word()}");
        var staged = await StageAsync(Text(good, bad), new ExtractedPage([], [new ExtractedCrag(good, null, null, null, null), new ExtractedCrag(bad, null, null, null, null)], []));
        staged.Crags[0].AreaId = country;
        staged.Crags[1].AreaId = -1;

        await Assert.ThrowsAnyAsync<DbUpdateException>(() => Repository().CommitAsync(staged.BatchId, staged.Crags));

        await using var db = fixture.CreateContext();
        Assert.False(await db.Crags.AnyAsync(c => c.Name == good));
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
        await using var db = fixture.CreateContext();
        var area = await db.Areas.AsNoTracking().SingleAsync(a => a.Name == region);
        Assert.Equal(parentId, area.ParentId);
        Assert.Equal(AreaKind.Region, area.Kind);

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
        await using var db = fixture.CreateContext();
        Assert.False(await db.Routes.AnyAsync(r => r.Name == route));
        Assert.False(await db.Crags.AnyAsync(c => c.Name == crag));
        Assert.False(await db.Areas.AnyAsync(a => a.Name == region));
        Assert.False(await db.ExternalRefs.AnyAsync(r => r.ImportBatchId == staged.BatchId));
        Assert.Equal(ImportStatus.Undone, await db.ImportBatches.AsNoTracking().Where(b => b.Id == staged.BatchId).Select(b => b.Status).SingleAsync());
    }

    [Fact]
    public async Task Undo_keeps_a_route_with_an_ascent_and_everything_above_it_and_can_run_again()
    {
        var (staged, region, crag, route) = await CommitNewAsync();
        await using var db = fixture.CreateContext();
        var routeId = await db.Routes.AsNoTracking().Where(r => r.Name == route).Select(r => r.Id).SingleAsync();
        var ascent = new Ascent { UserId = AppUser.DefaultId, RouteId = routeId, Date = new DateOnly(2020, 1, 1), DatePrecision = DatePrecision.Day, Style = AscentStyle.Lead, Source = DataSource.Manual };
        db.Ascents.Add(ascent);
        await db.SaveChangesAsync();

        var undo = await Repository().UndoAsync(staged.BatchId);

        Assert.Equal(0, undo.RoutesDeleted);
        Assert.Contains(undo.Skipped, s => s.Contains(route) && s.Contains("1 ascent"));
        Assert.Contains(undo.Skipped, s => s.Contains(crag));
        Assert.Contains(undo.Skipped, s => s.Contains(region));
        Assert.True(await db.Routes.AnyAsync(r => r.Id == routeId));
        Assert.True(await db.ExternalRefs.AnyAsync(r => r.EntityType == ExternalEntityType.Route && r.EntityId == routeId));
        Assert.Equal(ImportStatus.Committed, await db.ImportBatches.AsNoTracking().Where(b => b.Id == staged.BatchId).Select(b => b.Status).SingleAsync());

        await db.Ascents.Where(a => a.Id == ascent.Id).ExecuteDeleteAsync();
        var again = await Repository().UndoAsync(staged.BatchId);
        Assert.Empty(again.Skipped);
        Assert.False(await db.Routes.AnyAsync(r => r.Id == routeId));
    }

    [Fact]
    public async Task Undo_keeps_a_row_edited_since_the_import()
    {
        var (staged, _, _, route) = await CommitNewAsync();
        await using (var db = fixture.CreateContext())
        {
            var row = await db.Routes.SingleAsync(r => r.Name == route);
            row.Grade = "Changed";
            await db.SaveChangesAsync();
        }

        var undo = await Repository().UndoAsync(staged.BatchId);

        Assert.Contains(undo.Skipped, s => s.Contains(route) && s.Contains("edited"));
        await using var check = fixture.CreateContext();
        Assert.True(await check.Routes.AnyAsync(r => r.Name == route));
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
        await using var db = fixture.CreateContext();
        Assert.True(await db.Routes.AnyAsync(r => r.Id == existing.RouteId));
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
        Assert.DoesNotContain(routes, r => r.Summary == "takes the obvious crack for 20m");
        Assert.Equal(4, staged.Warnings.Count);
    }
}
