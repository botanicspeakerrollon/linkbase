using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Linkbase.Core.Llm.Providers;

// Calls any server that speaks the OpenAI chat completions format (OpenAI, Ollama, LM Studio, vLLM, OpenRouter and most
// gateways) and makes the model answer through one tool. Every way it can fail becomes an LlmCallFailedException.
//
// Llm:BaseUrl  where the server is, such as https://api.openai.com/v1 or http://localhost:11434/v1
// Llm:ApiKey   optional, because local servers don't ask for one
// Llm:Model    required
// Llm:MaxTokensField  "max_completion_tokens" (default) or "max_tokens", which some local servers want
// Llm:Strict   "true" asks the server to enforce the tool's schema, which not every server supports
public sealed class OpenAiToolClient(IConfiguration configuration, HttpClient? httpClient = null) : LlmToolClientBase
{
    public const string DefaultBaseUrl = "https://api.openai.com/v1";

    public override string Provider => "openai";

    public override string Model => configuration["Llm:Model"] ?? "";

    protected override async Task<ToolCallResult> SendAsync(ToolCallRequest request, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(Model))
        {
            throw Fail("Llm:Model isn't set. See the Configuration section of README.md.");
        }

        var baseUrl = configuration["Llm:BaseUrl"] is { Length: > 0 } url ? url : DefaultBaseUrl;
        if (!Uri.TryCreate(baseUrl.TrimEnd('/') + "/chat/completions", UriKind.Absolute, out var endpoint)
            || (endpoint.Scheme != Uri.UriSchemeHttp && endpoint.Scheme != Uri.UriSchemeHttps))
        {
            throw Fail($"Llm:BaseUrl isn't a web address: {baseUrl}");
        }

        var maxTokensField = configuration["Llm:MaxTokensField"] is { Length: > 0 } field ? field : "max_completion_tokens";
        var function = new JsonObject
        {
            ["name"] = request.ToolName,
            ["description"] = request.ToolDescription,
            ["parameters"] = JsonNode.Parse(request.SchemaJson),
        };
        if (string.Equals(configuration["Llm:Strict"], "true", StringComparison.OrdinalIgnoreCase))
        {
            function["strict"] = true;
        }

        var body = new JsonObject
        {
            ["model"] = Model,
            [maxTokensField] = request.MaxTokens,
            ["messages"] = new JsonArray(
                new JsonObject { ["role"] = "system", ["content"] = request.System },
                new JsonObject { ["role"] = "user", ["content"] = request.User }),
            ["tools"] = new JsonArray(new JsonObject { ["type"] = "function", ["function"] = function }),
            ["tool_choice"] = "auto",
        };

        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"),
        };
        if (configuration["Llm:ApiKey"] is { Length: > 0 } apiKey)
        {
            httpRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        }

        var client = httpClient ?? new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        string text;
        try
        {
            using var response = await client.SendAsync(httpRequest, cancellationToken);
            text = await response.Content.ReadAsStringAsync(cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                throw Failure(response.StatusCode, text);
            }
        }
        catch (HttpRequestException)
        {
            throw Fail($"Couldn't reach the model server at {baseUrl}. Check Llm:BaseUrl and that it is running.");
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw Fail("The model server took too long to answer.");
        }

        return ReadResult(text);
    }

    private static LlmCallFailedException Failure(HttpStatusCode status, string text) => status switch
    {
        HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => Fail("The model server rejected the API key. Check Llm:ApiKey."),
        HttpStatusCode.TooManyRequests => Fail("The model server is rate limiting this key. Try again in a minute."),
        >= HttpStatusCode.InternalServerError => Fail("The model server had an error. Try again shortly."),
        _ => Fail($"The model server rejected the request. Check Llm:Model and that it supports tool calls. {ErrorMessage(text)}"),
    };

    private static string ErrorMessage(string text)
    {
        try
        {
            var message = JsonNode.Parse(text)?["error"]?["message"]?.GetValue<string>();
            return message is null ? "" : message.Length > 300 ? message[..300] : message;
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException)
        {
            return "";
        }
    }

    // The first choice's first call to a tool. The stop reason is mapped to the words the callers already check for:
    // "length" is "max_tokens" and "content_filter" is "refusal".
    public static ToolCallResult ReadResult(string json)
    {
        try
        {
            var choice = JsonNode.Parse(json)?["choices"] is JsonArray { Count: > 0 } choices ? choices[0] : null;
            if (choice is null)
            {
                throw Fail("The model server's answer had no choices.");
            }

            var stop = choice["finish_reason"]?.GetValue<string>() switch
            {
                "length" => StopReasons.MaxTokens,
                "content_filter" => StopReasons.Refusal,
                var other => other,
            };
            if (choice["message"]?["refusal"] is { } refusal && !string.IsNullOrEmpty(refusal.GetValue<string>()))
            {
                stop = StopReasons.Refusal;
            }

            Dictionary<string, JsonElement>? input = null;
            if (choice["message"]?["tool_calls"] is JsonArray calls && calls.Count > 0
                && calls[0]?["function"]?["arguments"]?.GetValue<string>() is { } arguments)
            {
                input = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(arguments);
            }

            return new ToolCallResult(stop, input);
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException)
        {
            // Models do sometimes write arguments that aren't valid JSON. That is a failed answer, not a crash.
            throw Fail("The model's answer wasn't in the expected format. Try again.");
        }
    }
}

public sealed class OpenAiProvider : LlmProvider
{
    public override string Id => "openai";

    public override void AddServices(IServiceCollection services, IConfiguration configuration) =>
        services.AddHttpClient<ILlmToolClient, OpenAiToolClient>(client => client.Timeout = TimeSpan.FromMinutes(5));
}
