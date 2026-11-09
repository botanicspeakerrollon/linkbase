using Linkbase.Core.Extraction;
using Linkbase.Data;
using Linkbase.Domains.Climbing.Data;

namespace Linkbase.Domains.Climbing.Import.Web;

// Input to staged results: fetch or take pasted text, extract part by part, merge, validate, store.
public sealed class WebImportService(
    PageTextFetcher fetcher,
    IClimbingExtractor extractor,
    WebImportRepository repository)
{
    public async Task<long> ExtractFromUrlAsync(string url, IProgress<ExtractionProgress>? progress, CancellationToken cancellationToken)
    {
        var batchId = await repository.CreateBatchAsync(url.Trim(), cancellationToken);
        return await RunAsync(batchId, progress, async () =>
        {
            progress?.Report(new ExtractionProgress("Fetching the page", 0, 1));
            return await fetcher.FetchAsync(url, cancellationToken);
        }, cancellationToken);
    }

    public async Task<long> ExtractFromTextAsync(string text, string? sourceUrl, IProgress<ExtractionProgress>? progress, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            throw new ExtractionFailedException("There's no text to import.");
        }

        var batchId = await repository.CreateBatchAsync(string.IsNullOrWhiteSpace(sourceUrl) ? null : sourceUrl.Trim(), cancellationToken);
        return await RunAsync(batchId, progress, () => Task.FromResult(new PageText(null, text)), cancellationToken);
    }

    private async Task<long> RunAsync(
        long batchId,
        IProgress<ExtractionProgress>? progress,
        Func<Task<PageText>> getText,
        CancellationToken cancellationToken)
    {
        try
        {
            var page = await getText();
            var chunks = TextChunker.Split(page.Text);
            var results = new List<ExtractedPage>();
            var knownCrags = new List<string>();

            for (var i = 0; i < chunks.Count; i++)
            {
                progress?.Report(new ExtractionProgress($"Reading part {i + 1} of {chunks.Count}", i, chunks.Count));
                var result = await extractor.ExtractAsync(chunks[i], knownCrags, cancellationToken);
                results.Add(result);
                knownCrags.AddRange(result.Crags.Select(c => c.Name).Except(knownCrags, StringComparer.OrdinalIgnoreCase));
            }

            var (validated, warnings) = ExtractionValidator.Validate(ExtractionMerger.Merge(results), page.Text);
            await repository.StageAsync(batchId, new ImportExtraction(page.Title, validated, warnings), CancellationToken.None);
            progress?.Report(new ExtractionProgress("Done", chunks.Count, chunks.Count));
            return batchId;
        }
        catch (Exception ex) when (ex is ExtractionFailedException or PageFetchFailedException or OperationCanceledException)
        {
            await repository.MarkFailedAsync(batchId, ex is OperationCanceledException ? "Cancelled." : ex.Message, CancellationToken.None);
            throw;
        }
    }
}
