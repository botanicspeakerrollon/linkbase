using System.Text.Json;

namespace Linkbase.Core.Llm;

// The one contract a language-model provider has to meet. Everything the app asks of a model (reading text into records,
// compiling a question into a search) is "send this prompt, make the model answer through this one tool, give me the tool's
// arguments". The prompts, the tool schemas and the checking of the answer are provider-neutral and live with the feature
// that asks (ExtractionPrompts, QuestionPrompts), so a provider only has to speak its own wire format.
//
// To add a provider, see "Adding a provider" in docs/models.md. In short: derive from LlmToolClientBase, derive from
// LlmProvider, and add the provider to LlmProviders.All.
public interface ILlmToolClient
{
    // Shown in messages and used as the Llm:Provider value, such as "anthropic".
    string Provider { get; }

    // The model name sent to the provider.
    string Model { get; }

    // Throws LlmCallFailedException, with a message that says what to do, for every way the call can fail.
    Task<ToolCallResult> CallToolAsync(ToolCallRequest request, CancellationToken cancellationToken);
}

// How hard the model should think. A provider that has no such setting ignores it.
public enum LlmEffort
{
    Low,
    Medium,
}

public sealed record ToolCallRequest(
    string System,
    string User,
    string ToolName,
    string ToolDescription,
    string SchemaJson,
    int MaxTokens,
    LlmEffort Effort = LlmEffort.Low);

// What came back: the tool's arguments (null when the model never called the tool), and why it stopped. A provider maps its
// own stop reasons onto the ones in StopReasons, which the callers check for.
public sealed record ToolCallResult(string? StopReason, IReadOnlyDictionary<string, JsonElement>? ToolInput);

public static class StopReasons
{
    public const string ToolUse = "tool_use";
    public const string Refusal = "refusal";
    public const string MaxTokens = "max_tokens";
}

// A call to a provider failed. The message is meant to be shown to the person, so it says what to check.
public sealed class LlmCallFailedException(string message) : Exception(message);
