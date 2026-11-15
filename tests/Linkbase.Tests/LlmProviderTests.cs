using Linkbase.Core.Extraction;
using Linkbase.Core.Llm;
using Linkbase.Core.Llm.Providers;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Linkbase.Tests;

public class LlmProviderTests
{
    private static IConfiguration Config(string? provider) =>
        new ConfigurationBuilder().AddInMemoryCollection(provider is null ? [] : new Dictionary<string, string?> { ["Llm:Provider"] = provider }).Build();

    private static ILlmToolClient ClientFor(string? provider)
    {
        var services = new ServiceCollection().AddLogging();
        var config = Config(provider);
        services.AddSingleton(config);
        services.AddLlm(config);
        return services.BuildServiceProvider().GetRequiredService<ILlmToolClient>();
    }

    [Fact]
    public void Anthropic_is_the_default_and_openai_is_picked_by_name()
    {
        Assert.IsType<AnthropicToolClient>(ClientFor(null));
        Assert.IsType<AnthropicToolClient>(ClientFor("Anthropic"));
        Assert.IsType<OpenAiToolClient>(ClientFor(" OpenAI "));
    }

    [Fact]
    public void An_unknown_provider_lists_the_ones_there_are()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => ClientFor("gemini"));
        Assert.Contains("anthropic, openai", ex.Message);
    }

    [Fact]
    public void Every_provider_has_its_own_id()
    {
        Assert.Equal(LlmProviders.All.Count, LlmProviders.All.Select(p => p.Id).Distinct().Count());
    }

    private sealed class FakeClient(ToolCallResult result) : LlmToolClientBase
    {
        public ToolCallRequest? Seen { get; private set; }

        public override string Provider => "fake";

        public override string Model => "fake-1";

        protected override Task<ToolCallResult> SendAsync(ToolCallRequest request, CancellationToken cancellationToken)
        {
            Seen = request;
            return Task.FromResult(result);
        }
    }

    // A new provider needs nothing but a client: the extractor works with it as it is.
    [Fact]
    public async Task A_new_provider_is_enough_to_extract_with()
    {
        var input = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, System.Text.Json.JsonElement>>(
            """{"records":[{"type":"Crag","name":"Larkfield","fields":[]}],"links":[]}""")!;
        var client = new FakeClient(new ToolCallResult(StopReasons.ToolUse, input));
        var schema = ExtractionSchema.From([new Linkbase.Core.Records.RecordType("Crag", null, null, [])], []);

        var data = await new LlmModelExtractor(client).ExtractAsync(schema, "Larkfield", [], CancellationToken.None);

        Assert.Equal("Larkfield", Assert.Single(data.Records).Name);
        Assert.Equal(ExtractionPrompts.ToolName, client.Seen!.ToolName);
        Assert.Contains("<page_text>", client.Seen.User);
    }

    [Fact]
    public async Task A_failed_call_reaches_the_caller_as_that_features_own_exception()
    {
        var failing = new Failing();
        var schema = ExtractionSchema.From([new Linkbase.Core.Records.RecordType("Crag", null, null, [])], []);

        var ex = await Assert.ThrowsAsync<ExtractionFailedException>(() => new LlmModelExtractor(failing).ExtractAsync(schema, "x", [], CancellationToken.None));
        Assert.Equal("nope", ex.Message);
    }

    private sealed class Failing : LlmToolClientBase
    {
        public override string Provider => "failing";

        public override string Model => "";

        protected override Task<ToolCallResult> SendAsync(ToolCallRequest request, CancellationToken cancellationToken) => throw Fail("nope");
    }
}
