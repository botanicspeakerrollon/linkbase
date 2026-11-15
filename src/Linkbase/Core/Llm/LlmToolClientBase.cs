namespace Linkbase.Core.Llm;

// What every provider's client shares. A provider writes SendAsync, which makes the call and reads the answer, and throws
// through Fail for anything that goes wrong, so every failure reaches the person as the same kind of exception.
public abstract class LlmToolClientBase : ILlmToolClient
{
    public abstract string Provider { get; }

    public abstract string Model { get; }

    public async Task<ToolCallResult> CallToolAsync(ToolCallRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        return await SendAsync(request, cancellationToken);
    }

    // One call to the provider. The tool choice stays on "auto" because newer models reject a forced one, so an answer without
    // the tool call comes back as a null ToolInput, which the caller treats as a failure.
    protected abstract Task<ToolCallResult> SendAsync(ToolCallRequest request, CancellationToken cancellationToken);

    protected static LlmCallFailedException Fail(string message) => new(message);
}
