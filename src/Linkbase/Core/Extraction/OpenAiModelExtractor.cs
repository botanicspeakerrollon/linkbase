namespace Linkbase.Core.Extraction;

// Extracts records and links through any OpenAI-compatible server. The prompt, the tool's schema and the reading of the answer
// are the same as for Claude, so ModelExtractionValidator and the record hooks check it just the same.
public sealed class OpenAiModelExtractor(OpenAiCompatibleClient client) : IModelExtractor
{
    private const int MaxTokens = 16_000;

    public async Task<ExtractedData> ExtractAsync(ExtractionSchema schema, string text, IReadOnlyList<string> knownRecords, CancellationToken cancellationToken)
    {
        if (schema.Types.Count == 0)
        {
            throw new ExtractionFailedException("This model has no types that can be filled from text.");
        }

        var result = await client.CallToolAsync(
            AnthropicModelExtractor.SystemPrompt(schema),
            AnthropicModelExtractor.BuildUserMessage(text, knownRecords),
            AnthropicModelExtractor.ToolName,
            "Records the records and links the text states. Call this once.",
            AnthropicModelExtractor.InputSchemaJson(schema),
            MaxTokens,
            cancellationToken);
        return AnthropicModelExtractor.ReadResponse(result.StopReason, result.ToolInput);
    }
}
