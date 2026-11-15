using Linkbase.Core.Graph;

namespace Linkbase.Core.Ask;

// Answers a question with a checked pattern. The question's text is the only thing sent out: no records, no values from the data.
public sealed class QuestionService(IQuestionCompiler compiler, TimeProvider time)
{
    public const int MaxQuestionLength = 300;

    public async Task<GraphPattern> AskAsync(GraphSchema schema, string question, CancellationToken cancellationToken)
    {
        question = question.Trim();
        if (question.Length == 0)
        {
            throw new QuestionFailedException("Type a question first.");
        }

        if (question.Length > MaxQuestionLength)
        {
            throw new QuestionFailedException($"Keep the question under {MaxQuestionLength} characters.");
        }

        var raw = await compiler.CompileAsync(schema, question, DateOnly.FromDateTime(time.GetLocalNow().Date), cancellationToken);
        return PatternChecker.Check(raw, schema, question);
    }
}
