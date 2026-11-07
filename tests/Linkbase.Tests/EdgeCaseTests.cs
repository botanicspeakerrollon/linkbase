using Linkbase.Data;
using Linkbase.Domains.Climbing.Data;
using Linkbase.Core.Graph;
using Linkbase.Domains.Climbing.Import;
using Linkbase.Domains.Climbing.Import.Web;
using Microsoft.EntityFrameworkCore;

namespace Linkbase.Tests;

public class GraphPatternEdgeCaseTests
{
    private static readonly GraphSchema Schema = new(
        [],
        [
            new GraphPropertyInfo("Trip", "abroad", GraphValueType.Boolean),
            new GraphPropertyInfo("Route", "length_m", GraphValueType.Number),
        ]);

    private static string Sql(string label, string key, FilterOperator op, string value) =>
        GraphPatternSql.Build(new GraphPattern([new PatternNode(label, [new PatternFilter(key, op, value)])], []), Schema, 10, 0).Sql;

    [Fact]
    public void An_unfinished_pattern_is_refused()
    {
        Assert.Throws<LinkbaseValidationException>(() => GraphPatternSql.Build(new GraphPattern([], []), Schema, 10, 0));
        Assert.Throws<LinkbaseValidationException>(() => GraphPatternSql.Build(new GraphPattern([new PatternNode("Crag", []), new PatternNode("Route", [])], []), Schema, 10, 0));
    }

    [Theory]
    [InlineData(FilterOperator.Equals, " = ")]
    [InlineData(FilterOperator.LessOrEqual, " <= ")]
    public void Numbers_compare_as_numbers(FilterOperator op, string expected) =>
        Assert.Contains(expected, Sql("Route", "length_m", op, "20"));

    [Fact]
    public void Booleans_compare_as_text_from_the_view() =>
        Assert.Contains("= ", Sql("Trip", "abroad", FilterOperator.Equals, " TRUE "));
}

public class ExtractionEdgeCaseTests
{
    [Fact]
    public void A_paragraph_with_no_spaces_is_still_cut_to_length()
    {
        var chunks = TextChunker.Split(new string('x', 2500), 1000);
        Assert.True(chunks.Count >= 3);
        Assert.All(chunks, c => Assert.True(c.Length <= 1000));
        Assert.Equal(2500, chunks.Sum(c => c.Length));
    }

    [Fact]
    public void Drops_invented_areas_and_routes_at_invented_crags()
    {
        var page = new ExtractedPage(
            [new ExtractedArea("Atlantis", null, null)],
            [],
            [new ExtractedRoute("Real Route", "Invented Crag", null, null, null, null, null, null)]);

        var (valid, warnings) = ExtractionValidator.Validate(page, "Real Route is good");

        Assert.Empty(valid.Areas);
        Assert.Empty(valid.Routes);
        Assert.Contains(warnings, w => w.Contains("Atlantis", StringComparison.Ordinal));
        Assert.Contains(warnings, w => w.Contains("Invented Crag", StringComparison.Ordinal));
    }

    [Fact]
    public void A_boulder_tick_is_bouldering()
    {
        const string header = "Date,Route,Rating,Notes,URL,Pitches,Location,\"Avg Stars\",\"Your Stars\",Style,\"Lead Style\",\"Route Type\",\"Your Rating\",Length,\"Rating Code\"\n";
        var row = MountainProjectParser.Parse(new StringReader(header + "2023-01-01,R,V3,,https://www.mountainproject.com/route/1/r,1,\"Utah > Crag\",1,-1,Send,,Boulder,,,1\n")).Single();
        Assert.Equal(Discipline.Boulder, row.Discipline);
    }
}

[Trait("Category", "Database")]
[Collection(PostgresCollection.Name)]
public class WebImportEdgeCaseTests(PostgresFixture fixture)
{
    private sealed class StubExtractor(ExtractedPage page) : IClimbingExtractor
    {
        public Task<ExtractedPage> ExtractAsync(string text, IReadOnlyList<string> knownCrags, CancellationToken cancellationToken) =>
            Task.FromResult(page);
    }

    private static string Word(string prefix) => $"{prefix}{Guid.NewGuid():N}";

    private WebImportRepository Repository() => new(fixture.ContextFactory());

    private async Task<StagedImport> StageAsync(string text, ExtractedPage page)
    {
        var service = new WebImportService(new PageTextFetcher(new HttpClient()), new StubExtractor(page), Repository());
        return (await Repository().LoadStagedAsync(await service.ExtractFromTextAsync(text, null, null, CancellationToken.None)))!;
    }

    private static ExtractedPage Page(string crag, string route, string? system = null) => new(
        [],
        [new ExtractedCrag(crag, null, null, null, null)],
        [new ExtractedRoute(route, crag, null, system, null, null, null, null)]);

    private async Task<long> CountryAsync(string name)
    {
        await using var db = fixture.CreateContext();
        var area = new Area { UserId = AppUser.DefaultId, Name = name, Kind = AreaKind.Country };
        db.Areas.Add(area);
        await db.SaveChangesAsync();
        return area.Id;
    }

    [Fact]
    public async Task Blank_text_is_refused()
    {
        var service = new WebImportService(new PageTextFetcher(new HttpClient()), new StubExtractor(ExtractedPage.Empty), Repository());
        await Assert.ThrowsAsync<ExtractionFailedException>(() => service.ExtractFromTextAsync("   ", null, null, CancellationToken.None));
    }

    [Fact]
    public async Task An_unknown_grade_system_from_the_model_becomes_none()
    {
        var (crag, route) = (Word("Crag"), Word("Route"));
        var staged = await StageAsync($"{crag} {route}", Page(crag, route, "made_up"));
        Assert.Null(staged.Crags.Single().Routes.Single().GradeSystem);
    }

    [Fact]
    public async Task Commit_refuses_rows_that_arent_ready()
    {
        var (crag, route) = (Word("Crag"), Word("Route"));
        var staged = await StageAsync($"{crag} {route}", Page(crag, route));
        var row = staged.Crags.Single();
        row.AreaId = await CountryAsync(Word("Country"));

        async Task RefusedAsync(string message)
        {
            var ex = await Assert.ThrowsAsync<LinkbaseValidationException>(() => Repository().CommitAsync(staged.BatchId, staged.Crags));
            Assert.Contains(message, ex.Message);
        }

        row.Name = " ";
        await RefusedAsync("has no name");
        row.Name = crag;

        row.Routes[0].Name = " ";
        await RefusedAsync("has no name");
        row.Routes[0].Name = route;

        row.Routes[0].Choice = StagedRow.Undecided;
        await RefusedAsync("Choose a match");
        row.Routes[0].Choice = StagedRow.CreateNew;

        row.Routes[0].LengthM = -1;
        await RefusedAsync("can't be negative");
    }

    [Fact]
    public async Task New_areas_take_their_kind_from_their_parent_and_reuse_an_existing_name()
    {
        var (country, region, place) = (Word("Country"), Word("Region"), Word("Place"));

        async Task<long> CommitAsync(string areaName, long? parentId)
        {
            var (crag, route) = (Word("Crag"), Word("Route"));
            var staged = await StageAsync($"{crag} {route}", Page(crag, route));
            staged.Crags[0].NewAreaName = areaName;
            staged.Crags[0].NewAreaParentId = parentId;
            staged.Crags[0].NewAreaKind = parentId is null ? AreaKind.Country : null;
            await Repository().CommitAsync(staged.BatchId, staged.Crags);
            await using var db = fixture.CreateContext();
            return await db.Crags.Where(c => c.Name == crag).Select(c => c.AreaId).SingleAsync();
        }

        var countryId = await CommitAsync(country, null);
        var regionId = await CommitAsync(region, countryId);
        var placeId = await CommitAsync(place, regionId);

        await using var check = fixture.CreateContext();
        Assert.Equal(AreaKind.Country, (await check.Areas.FindAsync(countryId))!.Kind);
        Assert.Equal(AreaKind.Region, (await check.Areas.FindAsync(regionId))!.Kind);
        Assert.Equal(AreaKind.Area, (await check.Areas.FindAsync(placeId))!.Kind);

        Assert.Equal(regionId, await CommitAsync(region, countryId));
    }

    [Fact]
    public async Task Undo_keeps_crags_edited_since_the_import()
    {
        var (crag, route) = (Word("Crag"), Word("Route"));
        var staged = await StageAsync($"{crag} {route}", Page(crag, route));
        staged.Crags[0].AreaId = await CountryAsync(Word("Country"));
        await Repository().CommitAsync(staged.BatchId, staged.Crags);
        await using (var db = fixture.CreateContext())
        {
            await db.Crags.Where(c => c.Name == crag).ExecuteUpdateAsync(u => u.SetProperty(c => c.UpdatedAt, DateTimeOffset.UtcNow.AddMinutes(1)));
        }

        var undo = await Repository().UndoAsync(staged.BatchId);

        Assert.Contains(undo.Skipped, line => line.Contains($"Crag '{crag}': edited", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Undo_keeps_areas_edited_since_the_import()
    {
        var (crag, route, area) = (Word("Crag"), Word("Route"), Word("Area"));
        var staged = await StageAsync($"{crag} {route}", Page(crag, route));
        staged.Crags[0].NewAreaName = area;
        staged.Crags[0].NewAreaKind = AreaKind.Country;
        await Repository().CommitAsync(staged.BatchId, staged.Crags);
        await using (var db = fixture.CreateContext())
        {
            await db.Areas.Where(a => a.Name == area).ExecuteUpdateAsync(u => u.SetProperty(a => a.UpdatedAt, DateTimeOffset.UtcNow.AddMinutes(1)));
        }

        var undo = await Repository().UndoAsync(staged.BatchId);

        Assert.Contains(undo.Skipped, line => line.Contains($"Area '{area}': edited", StringComparison.Ordinal));
    }
}

[Trait("Category", "Database")]
[Collection(PostgresCollection.Name)]
public class ContextEdgeCaseTests(PostgresFixture fixture)
{
    [Fact]
    public void Saving_synchronously_stamps_timestamps_too()
    {
        using var db = fixture.CreateContext();
        var partner = new Partner { UserId = AppUser.DefaultId, Name = $"Partner {Guid.NewGuid():N}" };
        db.Partners.Add(partner);

        db.SaveChanges();

        Assert.NotEqual(default, partner.CreatedAt);
        Assert.Equal(partner.CreatedAt, partner.UpdatedAt);
    }

    [Fact]
    public async Task Nothing_typed_matches_nothing() =>
        Assert.Empty(await new GraphRepository(fixture.ContextFactory()).MatchPropertiesAsync(null, " "));
}
