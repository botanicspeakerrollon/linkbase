using System.Text.Json;

namespace Linkbase.Core.Extraction;

// Returns canned results instead of calling the API, when Extraction:Fixture names a JSON file. It lets the whole import be
// demonstrated without an API key, and the usual checks still run on what it returns.
public sealed class FixtureModelExtractor(string path) : IModelExtractor
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower, PropertyNameCaseInsensitive = true };

    public async Task<ExtractedData> ExtractAsync(ExtractionSchema schema, string text, IReadOnlyList<string> knownRecords, CancellationToken cancellationToken)
    {
        var json = await File.ReadAllTextAsync(path, cancellationToken);
        return JsonSerializer.Deserialize<ExtractedData>(json, JsonOptions) ?? ExtractedData.Empty;
    }
}
