using System.Net;
using System.Text.Json.Nodes;
using Linkbase.Core.Extraction;
using Microsoft.Extensions.Configuration;

namespace Linkbase.Tests;

public class OpenAiCompatibleClientTests
{
    private sealed class FakeHandler(HttpStatusCode status, string body) : HttpMessageHandler
    {
        public HttpRequestMessage? Request { get; private set; }
        public string? RequestBody { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Request = request;
            RequestBody = await request.Content!.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(status) { Content = new StringContent(body) };
        }
    }

    private static IConfiguration Config(params (string Key, string Value)[] values) =>
        new ConfigurationBuilder().AddInMemoryCollection(values.ToDictionary(v => v.Key, v => (string?)v.Value)).Build();

    private static Task<ToolCallResult> Call(OpenAiCompatibleClient client) =>
        client.CallToolAsync("system", "user", "record_data", "Records.", """{"type":"object"}""", 100, CancellationToken.None);

    private const string ToolAnswer = """
        {"choices":[{"finish_reason":"tool_calls","message":{"tool_calls":[{"function":{"name":"record_data","arguments":"{\"records\":[]}"}}]}}]}
        """;

    [Fact]
    public async Task Sends_a_chat_completion_with_one_tool_and_reads_the_call()
    {
        var handler = new FakeHandler(HttpStatusCode.OK, ToolAnswer);
        var client = new OpenAiCompatibleClient(
            Config(("Llm:Model", "some-model"), ("Llm:BaseUrl", "http://localhost:11434/v1/"), ("Llm:ApiKey", "k")), new HttpClient(handler));

        var result = await Call(client);

        Assert.Equal("http://localhost:11434/v1/chat/completions", handler.Request!.RequestUri!.ToString());
        Assert.Equal("Bearer k", handler.Request.Headers.Authorization!.ToString());
        var body = JsonNode.Parse(handler.RequestBody!)!;
        Assert.Equal("some-model", body["model"]!.GetValue<string>());
        Assert.Equal(100, body["max_completion_tokens"]!.GetValue<int>());
        Assert.Equal("auto", body["tool_choice"]!.GetValue<string>());
        Assert.Equal("record_data", body["tools"]![0]!["function"]!["name"]!.GetValue<string>());
        Assert.Null(body["tools"]![0]!["function"]!["strict"]);
        Assert.Equal("tool_calls", result.StopReason);
        Assert.True(result.ToolInput!.ContainsKey("records"));
    }

    [Fact]
    public async Task Honours_the_max_tokens_field_and_strict_settings_and_needs_no_key()
    {
        var handler = new FakeHandler(HttpStatusCode.OK, ToolAnswer);
        var client = new OpenAiCompatibleClient(
            Config(("Llm:Model", "m"), ("Llm:MaxTokensField", "max_tokens"), ("Llm:Strict", "true")), new HttpClient(handler));

        await Call(client);

        var body = JsonNode.Parse(handler.RequestBody!)!;
        Assert.Equal(100, body["max_tokens"]!.GetValue<int>());
        Assert.Null(body["max_completion_tokens"]);
        Assert.True(body["tools"]![0]!["function"]!["strict"]!.GetValue<bool>());
        Assert.Null(handler.Request!.Headers.Authorization);
        Assert.StartsWith(OpenAiCompatibleClient.DefaultBaseUrl, handler.Request.RequestUri!.ToString());
    }

    [Fact]
    public async Task Fails_with_a_message_that_says_what_to_check()
    {
        var noModel = await Assert.ThrowsAsync<ExtractionFailedException>(() => Call(new OpenAiCompatibleClient(Config(), new HttpClient(new FakeHandler(HttpStatusCode.OK, "{}")))));
        Assert.Contains("Llm:Model", noModel.Message);

        var badUrl = await Assert.ThrowsAsync<ExtractionFailedException>(() => Call(new OpenAiCompatibleClient(Config(("Llm:Model", "m"), ("Llm:BaseUrl", "ftp://x")), new HttpClient(new FakeHandler(HttpStatusCode.OK, "{}")))));
        Assert.Contains("Llm:BaseUrl", badUrl.Message);

        var rejected = await Assert.ThrowsAsync<ExtractionFailedException>(() => Call(new OpenAiCompatibleClient(Config(("Llm:Model", "m")), new HttpClient(new FakeHandler(HttpStatusCode.Unauthorized, "{}")))));
        Assert.Contains("Llm:ApiKey", rejected.Message);

        var badRequest = await Assert.ThrowsAsync<ExtractionFailedException>(() => Call(new OpenAiCompatibleClient(Config(("Llm:Model", "m")), new HttpClient(new FakeHandler(HttpStatusCode.BadRequest, """{"error":{"message":"no tools here"}}""")))));
        Assert.Contains("no tools here", badRequest.Message);
        Assert.Contains("Llm:Model", badRequest.Message);
    }

    [Fact]
    public void Maps_stop_reasons_and_treats_missing_or_broken_calls_as_failures_not_crashes()
    {
        Assert.Equal("max_tokens", OpenAiCompatibleClient.ReadResult("""{"choices":[{"finish_reason":"length","message":{}}]}""").StopReason);
        Assert.Equal("refusal", OpenAiCompatibleClient.ReadResult("""{"choices":[{"finish_reason":"stop","message":{"refusal":"No."}}]}""").StopReason);

        var text = OpenAiCompatibleClient.ReadResult("""{"choices":[{"finish_reason":"stop","message":{"content":"Here you go"}}]}""");
        Assert.Null(text.ToolInput);

        Assert.Throws<ExtractionFailedException>(() => OpenAiCompatibleClient.ReadResult("""{"choices":[]}"""));
        Assert.Throws<ExtractionFailedException>(() => OpenAiCompatibleClient.ReadResult("not json"));
        Assert.Throws<ExtractionFailedException>(() => OpenAiCompatibleClient.ReadResult(
            """{"choices":[{"finish_reason":"tool_calls","message":{"tool_calls":[{"function":{"arguments":"{broken"}}]}}]}"""));
    }
}
