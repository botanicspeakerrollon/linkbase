using SmartReader;

namespace Linkbase.Domains.Climbing.Import.Web;

public sealed record PageText(string? Title, string Text);

public sealed class PageFetchFailedException(string message) : Exception(message);

// Fetches a page and pulls out its main text. Many sites block this or forbid scraping, which is why every
// failure points back to pasting the text instead.
public sealed class PageTextFetcher(HttpClient httpClient)
{
    public const string UserAgent = "Linkbase/1.0 (personal climbing logbook; one page at a time, on request)";
    private const long MaxBytes = 5 * 1024 * 1024;
    private const string PasteHint = "Copy the text from your browser and paste it instead.";

    public async Task<PageText> FetchAsync(string url, CancellationToken cancellationToken)
    {
        if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
        {
            throw new PageFetchFailedException("Enter a full web address starting with http:// or https://.");
        }

        if (uri.IsLoopback)
        {
            throw new PageFetchFailedException("Addresses on this computer can't be imported.");
        }

        string html;
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            request.Headers.TryAddWithoutValidation("User-Agent", UserAgent);
            request.Headers.TryAddWithoutValidation("Accept", "text/html,application/xhtml+xml");

            using var response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                throw new PageFetchFailedException($"The site answered {(int)response.StatusCode} {response.ReasonPhrase}. {PasteHint}");
            }

            if (response.Content.Headers.ContentType?.MediaType is { } type && !type.Contains("html", StringComparison.OrdinalIgnoreCase))
            {
                throw new PageFetchFailedException($"This address returned {type}, not a web page. {PasteHint}");
            }

            if (response.Content.Headers.ContentLength > MaxBytes)
            {
                throw new PageFetchFailedException($"This page is too large to fetch. {PasteHint}");
            }

            html = await response.Content.ReadAsStringAsync(cancellationToken);
        }
        catch (HttpRequestException ex)
        {
            throw new PageFetchFailedException($"Couldn't fetch the page: {ex.Message} {PasteHint}");
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new PageFetchFailedException($"The site took too long to answer. {PasteHint}");
        }

        return ExtractMainText(uri, html);
    }

    public static PageText ExtractMainText(Uri uri, string html)
    {
        var article = new Reader(uri.ToString(), html).GetArticle();
        var text = article.TextContent?.Trim();
        if (!article.IsReadable || string.IsNullOrEmpty(text))
        {
            throw new PageFetchFailedException($"Couldn't find the main text of this page. {PasteHint}");
        }

        return new PageText(string.IsNullOrWhiteSpace(article.Title) ? null : article.Title.Trim(), text);
    }
}
