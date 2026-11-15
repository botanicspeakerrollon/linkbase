using Linkbase.Core.Graph;
using Linkbase.Core.Llm;

namespace Linkbase.Core.Ask;

// Compiles a question with whichever provider is configured, using QuestionPrompts' prompt and schema. PatternChecker still
// checks the answer against the model, so a weaker model can only fail, not search for something that isn't there.
public sealed class LlmQuestionCompiler(ILlmToolClient client) : IQuestionCompiler
{
    public async Task<GraphPattern> CompileAsync(GraphSchema schema, string question, DateOnly today, CancellationToken cancellationToken)
    {
        ToolCallResult result;
        try
        {
            result = await client.CallToolAsync(QuestionPrompts.BuildRequest(schema, question, today), cancellationToken);
        }
        catch (LlmCallFailedException ex)
        {
            throw new QuestionFailedException(ex.Message);
        }

        return QuestionPrompts.ReadResponse(result.StopReason, result.ToolInput);
    }
}
