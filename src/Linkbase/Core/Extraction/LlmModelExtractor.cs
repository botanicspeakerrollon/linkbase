using Linkbase.Core.Llm;

namespace Linkbase.Core.Extraction;

// Extracts records and links for any model with whichever provider is configured. The prompt, the tool's schema and the reading
// of the answer are ExtractionPrompts', the same for every provider, so ModelExtractionValidator and the record hooks check it
// just the same.
public sealed class LlmModelExtractor(ILlmToolClient client) : IModelExtractor
{
    public async Task<ExtractedData> ExtractAsync(ExtractionSchema schema, string text, IReadOnlyList<string> knownRecords, CancellationToken cancellationToken)
    {
        if (schema.Types.Count == 0)
        {
            throw new ExtractionFailedException("This model has no types that can be filled from text.");
        }

        ToolCallResult result;
        try
        {
            result = await client.CallToolAsync(ExtractionPrompts.BuildRequest(schema, text, knownRecords), cancellationToken);
        }
        catch (LlmCallFailedException ex)
        {
            throw new ExtractionFailedException(ex.Message);
        }

        return ExtractionPrompts.ReadResponse(result.StopReason, result.ToolInput);
    }
}
