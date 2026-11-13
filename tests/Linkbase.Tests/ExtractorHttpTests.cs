using Linkbase.Core.Extraction;
using Linkbase.Core;
using System.Net;
using System.Text;
using System.Text.Json;
using Linkbase.Data;
using Linkbase.Domains.Climbing.Data;
using Linkbase.Core.Graph;
using Linkbase.Domains.Climbing.Features;
using Linkbase.Core.Explore;
using Linkbase.Domains.Climbing.Import;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace Linkbase.Tests;

public class ExtractorHttpTests
{
    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public string? RequestBody { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestBody = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            return respond(request);
        }
    }

    private static readonly ExtractionSchema Schema = ExtractionSchema.From(
        [
            new Linkbase.Core.Records.RecordType("Crag", null, null, []),
            new Linkbase.Core.Records.RecordType("Route", null, null, [new Linkbase.Core.Records.RecordField("grade", Linkbase.Data.FieldType.Text)]),
        ],
        [new Linkbase.Core.Records.RelationshipDefinition("Route", "AT", "Crag")]);

    private static IConfiguration Config() => new ConfigurationBuilder()
        .AddInMemoryCollection(new Dictionary<string, string?> { ["Anthropic:ApiKey"] = "test-key", ["Anthropic:Model"] = "claude-haiku-5-5" })
        .Build();

    private static HttpResponseMessage Json(HttpStatusCode status, string body) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private const string ToolResponse =
        """
        {
          "id": "msg_1", "type": "message", "role": "assistant", "model": "claude-haiku-5-5",
          "content": [
            { "type": "tool_use", "id": "tu_1", "name": "record_data", "input": {
              "records": [{ "type": "Crag", "name": "Larkfield", "fields": [] }, { "type": "Route", "name": "Slab Theory", "fields": [{ "field": "grade", "value": "7b+" }] }],
              "links": [{ "rel_type": "AT", "from_type": "Route", "from_name": "Slab Theory", "to_type": "Crag", "to_name": "Larkfield" }]
            } }
          ],
          "stop_reason": "tool_use", "stop_sequence": null, "usage": { "input_tokens": 10, "output_tokens": 20 }
        }
        """;

    [Fact]
    public async Task A_real_response_is_read_through_the_sdk()
    {
        var handler = new StubHandler(_ => Json(HttpStatusCode.OK, ToolResponse));
        var extractor = new AnthropicModelExtractor(Config(), new HttpClient(handler));

        var data = await extractor.ExtractAsync(Schema, "Slab Theory at Larkfield", ["Earlier"], CancellationToken.None);

        Assert.Equal("Slab Theory", data.Records.Single(r => r.Type == "Route").Name);
        Assert.Single(data.Links);
        Assert.Contains("claude-haiku-5-5", handler.RequestBody);
        Assert.Contains("Slab Theory at Larkfield", handler.RequestBody);
        Assert.Contains("Earlier", handler.RequestBody);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, "rejected")]
    [InlineData((HttpStatusCode)429, "rate limiting")]
    [InlineData(HttpStatusCode.BadRequest, "Check Anthropic:Model")]
    [InlineData(HttpStatusCode.InternalServerError, "server error")]
    [InlineData(HttpStatusCode.Forbidden, "returned an error")]
    public async Task Api_errors_become_clear_messages(HttpStatusCode status, string expected)
    {
        var handler = new StubHandler(_ => Json(status, """{"type":"error","error":{"type":"x","message":"nope"}}"""));
        var extractor = new AnthropicModelExtractor(Config(), new HttpClient(handler));

        var ex = await Assert.ThrowsAsync<ExtractionFailedException>(() => extractor.ExtractAsync(Schema, "text", [], CancellationToken.None));

        Assert.Contains(expected, ex.Message);
    }

    [Fact]
    public async Task A_network_failure_is_a_clear_message()
    {
        var handler = new StubHandler(_ => throw new HttpRequestException("down"));
        var extractor = new AnthropicModelExtractor(Config(), new HttpClient(handler));

        var ex = await Assert.ThrowsAsync<ExtractionFailedException>(() => extractor.ExtractAsync(Schema, "text", [], CancellationToken.None));

        Assert.Contains("Couldn't reach", ex.Message);
    }

    [Fact]
    public void Empty_results_are_a_clear_failure()
    {
        // JSON null deserialises to nothing at all, which isn't the same as empty lists.
        var input = new Dictionary<string, JsonElement> { ["records"] = JsonSerializer.SerializeToElement(1) };
        Assert.Throws<ExtractionFailedException>(() => AnthropicModelExtractor.ReadResponse("tool_use", input));
    }
}
