using Anthropic;
using Anthropic.Exceptions;

namespace Linkbase.Core.Extraction;

// Makes one call to the Anthropic API and turns every way it can fail into an ExtractionFailedException with a message
// that says what to do. Shared by the extractors, so each says the same thing.
public static class AnthropicCall
{
    public static async Task<T> RunAsync<T>(IConfiguration configuration, HttpClient? httpClient, string model, Func<AnthropicClient, Task<T>> call)
    {
        var apiKey = configuration["Anthropic:ApiKey"];
        if (string.IsNullOrEmpty(apiKey))
        {
            throw new ExtractionFailedException("Anthropic:ApiKey isn't set. See the Configuration section of README.md.");
        }

        var client = httpClient is null
            ? new AnthropicClient { ApiKey = apiKey }
            : new AnthropicClient { ApiKey = apiKey, HttpClient = httpClient, MaxRetries = 0 };
        try
        {
            return await call(client);
        }
        catch (AnthropicUnauthorizedException)
        {
            throw new ExtractionFailedException("The Anthropic API key was rejected. Check Anthropic:ApiKey.");
        }
        catch (AnthropicRateLimitException)
        {
            throw new ExtractionFailedException("The Anthropic API is rate limiting this key. Try again in a minute.");
        }
        catch (AnthropicBadRequestException)
        {
            throw new ExtractionFailedException($"The Anthropic API rejected the request. Check Anthropic:Model ({model}).");
        }
        catch (Anthropic5xxException)
        {
            throw new ExtractionFailedException("The Anthropic API had a server error. Try again shortly.");
        }
        catch (AnthropicIOException)
        {
            throw new ExtractionFailedException("Couldn't reach the Anthropic API. Check the internet connection.");
        }
        catch (AnthropicApiException ex)
        {
            throw new ExtractionFailedException($"The Anthropic API returned an error: {ex.Message}");
        }
    }
}
