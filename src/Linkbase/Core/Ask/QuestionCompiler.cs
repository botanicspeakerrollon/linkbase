using Linkbase.Core.Graph;

namespace Linkbase.Core.Ask;

public sealed class QuestionFailedException(string message) : Exception(message);

// Turns a plain-English question into a search pattern. What comes back is untrusted: PatternChecker decides whether it's usable.
public interface IQuestionCompiler
{
    Task<GraphPattern> CompileAsync(GraphSchema schema, string question, DateOnly today, CancellationToken cancellationToken);
}

// Returns the pattern in a JSON file instead of calling the API, when Ask:Fixture names one. The usual checks still run.
public sealed class FixtureQuestionCompiler(string path) : IQuestionCompiler
{
    private static readonly System.Text.Json.JsonSerializerOptions JsonOptions = new(System.Text.Json.JsonSerializerDefaults.Web)
    {
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter(System.Text.Json.JsonNamingPolicy.CamelCase) },
    };

    public async Task<GraphPattern> CompileAsync(GraphSchema schema, string question, DateOnly today, CancellationToken cancellationToken) =>
        System.Text.Json.JsonSerializer.Deserialize<GraphPattern>(await File.ReadAllTextAsync(path, cancellationToken), JsonOptions)
            ?? throw new QuestionFailedException("The fixture file is empty.");
}
