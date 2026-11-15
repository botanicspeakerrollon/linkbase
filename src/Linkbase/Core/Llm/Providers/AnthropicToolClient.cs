using System.Text.Json;
using Anthropic;
using Anthropic.Exceptions;
using Anthropic.Models.Beta.Messages;

namespace Linkbase.Core.Llm.Providers;

// Claude, through the Anthropic SDK, with a strict tool.
//
// Anthropic:ApiKey   required
// Anthropic:Model    optional, claude-haiku-5-5 by default
public sealed class AnthropicToolClient(IConfiguration configuration, HttpClient? httpClient = null) : LlmToolClientBase
{
    public const string DefaultModel = "claude-haiku-5-5";
    private const string FallbackBeta = "server-side-fallback-2026-07-01";

    public override string Provider => "anthropic";

    public override string Model => configuration["Anthropic:Model"] is { Length: > 0 } model ? model : DefaultModel;

    protected override async Task<ToolCallResult> SendAsync(ToolCallRequest request, CancellationToken cancellationToken)
    {
        var apiKey = configuration["Anthropic:ApiKey"];
        if (string.IsNullOrEmpty(apiKey))
        {
            throw Fail("Anthropic:ApiKey isn't set. See the Configuration section of README.md.");
        }

        var client = httpClient is null
            ? new AnthropicClient { ApiKey = apiKey }
            : new AnthropicClient { ApiKey = apiKey, HttpClient = httpClient, MaxRetries = 0 };
        try
        {
            var response = await client.Beta.Messages.Create(BuildRequest(request), cancellationToken);

            IReadOnlyDictionary<string, JsonElement>? toolInput = null;
            foreach (var block in response.Content)
            {
                if (block.TryPickToolUse(out var toolUse) && toolUse.Name == request.ToolName)
                {
                    toolInput = toolUse.Input;
                    break;
                }
            }

            // Anthropic's stop reasons are the words the callers check for, so they pass through as they are.
            return new ToolCallResult(response.StopReason?.Raw(), toolInput);
        }
        catch (AnthropicUnauthorizedException)
        {
            throw Fail("The Anthropic API key was rejected. Check Anthropic:ApiKey.");
        }
        catch (AnthropicRateLimitException)
        {
            throw Fail("The Anthropic API is rate limiting this key. Try again in a minute.");
        }
        catch (AnthropicBadRequestException)
        {
            throw Fail($"The Anthropic API rejected the request. Check Anthropic:Model ({Model}).");
        }
        catch (Anthropic5xxException)
        {
            throw Fail("The Anthropic API had a server error. Try again shortly.");
        }
        catch (AnthropicIOException)
        {
            throw Fail("Couldn't reach the Anthropic API. Check the internet connection.");
        }
        catch (AnthropicApiException ex)
        {
            throw Fail($"The Anthropic API returned an error: {ex.Message}");
        }
    }

    public MessageCreateParams BuildRequest(ToolCallRequest request) => new()
    {
        Model = Model,
        MaxTokens = request.MaxTokens,
        System = request.System,
        OutputConfig = new BetaOutputConfig { Effort = request.Effort == LlmEffort.Medium ? Effort.Medium : Effort.Low },
        Tools =
        [
            new BetaTool
            {
                Name = request.ToolName,
                Description = request.ToolDescription,
                Strict = true,
                InputSchema = InputSchema.FromRawUnchecked(JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(request.SchemaJson)!),
            },
        ],
        Messages = [new BetaUserMessageParam { Content = request.User }],
        Betas = [FallbackBeta],
        Fallbacks = new Default(),
    };
}

public sealed class AnthropicProvider : LlmProvider
{
    public override string Id => "anthropic";

    public override void AddServices(IServiceCollection services, IConfiguration configuration) =>
        services.AddScoped<ILlmToolClient>(_ => new AnthropicToolClient(configuration));
}
