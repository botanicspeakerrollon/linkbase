using Bunit;
using Linkbase.Core.Extraction;
using Linkbase.Core.Import;
using Linkbase.Core.Staging;
using Linkbase.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Linkbase.Tests;

// The Read text panel: what happens when the reading works, and when it doesn't.
[Trait("Category", "Database")]
public class ExtractionPanelTests(PostgresFixture fixture) : UiTestBase(fixture), IClassFixture<PostgresFixture>
{
    private sealed class FakeExtractor(Func<ExtractedData> answer) : IModelExtractor
    {
        public Task<ExtractedData> ExtractAsync(ExtractionSchema schema, string text, IReadOnlyList<string> knownRecords, CancellationToken cancellationToken) =>
            Task.FromResult(answer());
    }

    private async Task<IRenderedComponent<ExtractionPanel>> PanelAsync(Func<ExtractedData> answer)
    {
        var store = await ClimbingStore.CreateAsync(Fixture);
        Services.AddSingleton<IDbContextFactory<LinkbaseDbContext>>(store);
        Services.AddSingleton<IModelExtractor>(new FakeExtractor(answer));
        Services.AddSingleton(new PageTextFetcher(new HttpClient()));
        Services.AddSingleton<ImportStagingService>();
        Services.AddSingleton<ModelExtractionService>();
        Services.AddSingleton<Linkbase.Core.CsvImport.CsvImportService>();
        return RenderPage<ExtractionPanel>();
    }

    private static void TypeText(IRenderedComponent<ExtractionPanel> cut, string text) =>
        cut.Find("textarea").Input(text);

    [Fact]
    public async Task Reading_needs_some_text_first()
    {
        var cut = await PanelAsync(() => ExtractedData.Empty);

        Assert.True(Control(cut, "Read").HasAttribute("disabled"));

        TypeText(cut, "Alpha Wall is a crag.");
        cut.WaitForAssertion(() => Assert.False(Control(cut, "Read").HasAttribute("disabled")));
    }

    [Fact]
    public async Task A_successful_reading_opens_the_review_of_what_it_staged()
    {
        var cut = await PanelAsync(() => new ExtractedData([ClimbingExtraction.Rec("Crag", "Alpha Wall"), ClimbingExtraction.Rec("Area", "Peakland", ("kind", "area"))], [ClimbingExtraction.In("Alpha Wall", "Peakland")]));

        TypeText(cut, "Alpha Wall is a crag in Peakland with good routes.");
        Press(cut, "Read");

        WaitUntil(() => CurrentPath.StartsWith("/import/review/", StringComparison.Ordinal), $"the review, at {CurrentPath}");
    }

    [Fact]
    public async Task Updating_records_already_here_is_offered_and_on_by_default()
    {
        var cut = await PanelAsync(() => ExtractedData.Empty);

        Sees(cut, "Update records that are already here");
        Assert.True(cut.Find("input[type=checkbox]").HasAttribute("checked"));
    }

    [Fact]
    public async Task A_failed_reading_says_why_and_stays_on_the_page()
    {
        var cut = await PanelAsync(() => throw new ExtractionFailedException("The reader declined this text."));

        TypeText(cut, "Some text to read.");
        Press(cut, "Read");

        Sees(cut, "The reader declined this text.");
        Assert.DoesNotContain("/import/review/", CurrentPath, StringComparison.Ordinal);
        cut.WaitForAssertion(() => Assert.False(Control(cut, "Read").HasAttribute("disabled")));
    }
}
