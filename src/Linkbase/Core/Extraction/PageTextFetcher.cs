using System.Net;
using SmartReader;

namespace Linkbase.Core.Extraction;

public sealed record PageText(string? Title, string Text);

public sealed class PageFetchFailedException(string message) : Exception(message);

// Fetches a page and pulls out its main text. Many sites block this or forbid scraping, which is why every
// failure points back to pasting the text instead.
public sealed class PageTextFetcher(HttpClient httpClient)
{
    public const string UserAgent = "Linkbase/1.0 (one page at a time, on request)";
    private const long MaxBytes = 5 * 1024 * 1024;
    private const string PasteHint = "Copy the text from your browser and paste it instead.";

    public async Task<PageText> FetchAsync(string url, CancellationToken cancellationToken)
    {
        if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
        {
            throw new PageFetchFailedException("Enter a full web address starting with http:// or https://.");
        }

        if (uri.IsLoopback || (IPAddress.TryParse(uri.Host.Trim('[', ']'), out var literal) && !PublicAddress.IsAllowed(literal)))
        {
            throw new PageFetchFailedException("Addresses inside a private network can't be imported.");
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

            html = await ReadLimitedAsync(response.Content, cancellationToken);
        }
        catch (HttpRequestException)
        {
            // The reason is left out: it can say what is on the network behind the app.
            throw new PageFetchFailedException($"Couldn't fetch the page. {PasteHint}");
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new PageFetchFailedException($"The site took too long to answer. {PasteHint}");
        }

        return ExtractMainText(uri, html);
    }

    // The size limit holds even when the site sends no Content-Length.
    private static async Task<string> ReadLimitedAsync(HttpContent content, CancellationToken cancellationToken)
    {
        await using var stream = await content.ReadAsStreamAsync(cancellationToken);
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        int read;
        while ((read = await stream.ReadAsync(chunk, cancellationToken)) > 0)
        {
            buffer.Write(chunk, 0, read);
            if (buffer.Length > MaxBytes)
            {
                throw new PageFetchFailedException($"This page is too large to fetch. {PasteHint}");
            }
        }

        var encoding = System.Text.Encoding.UTF8;
        try
        {
            if (content.Headers.ContentType?.CharSet is { Length: > 0 } charset)
            {
                encoding = System.Text.Encoding.GetEncoding(charset.Trim('"'));
            }
        }
        catch (ArgumentException)
        {
        }

        return encoding.GetString(buffer.GetBuffer(), 0, (int)buffer.Length);
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
