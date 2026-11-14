using Linkbase.Core.Extraction;
using Linkbase.Core.Graph;

namespace Linkbase.Core.Ask;

// Compiles a question through any OpenAI-compatible server, with the same prompt and schema as for Claude. PatternChecker
// still checks the answer against the model, so a weaker model can only fail, not search for something that isn't there.
public sealed class OpenAiQuestionCompiler(OpenAiCompatibleClient client) : IQuestionCompiler
{
    private const int MaxTokens = 2_000;

    public async Task<GraphPattern> CompileAsync(GraphSchema schema, string question, DateOnly today, CancellationToken cancellationToken)
    {
        var result = await client.CallToolAsync(
            AnthropicQuestionCompiler.SystemPrompt(schema, today),
            $"<question>\n{question}\n</question>",
            AnthropicQuestionCompiler.ToolName,
            "Builds the search that answers the question. Call this once.",
            AnthropicQuestionCompiler.InputSchemaJson(schema),
            MaxTokens,
            cancellationToken);
        return AnthropicQuestionCompiler.ReadResponse(result.StopReason, result.ToolInput);
    }
}
