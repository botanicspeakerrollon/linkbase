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

public class PageTextFetcherFailureTests
{
    private sealed class StubHandler(Func<HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(respond());
    }

    private static PageTextFetcher Fetcher(Func<HttpResponseMessage> respond) => new(new HttpClient(new StubHandler(respond)));

    [Fact]
    public async Task A_page_too_large_suggests_pasting()
    {
        var ex = await Assert.ThrowsAsync<PageFetchFailedException>(() => Fetcher(() =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("x", Encoding.UTF8, "text/html") };
            response.Content.Headers.ContentLength = 50_000_000;
            return response;
        }).FetchAsync("https://example.com/big", CancellationToken.None));
        Assert.Contains("too large", ex.Message);
    }

    [Fact]
    public async Task A_network_failure_suggests_pasting()
    {
        var ex = await Assert.ThrowsAsync<PageFetchFailedException>(() =>
            Fetcher(() => throw new HttpRequestException("no route")).FetchAsync("https://example.com/x", CancellationToken.None));
        Assert.DoesNotContain("no route", ex.Message);
        Assert.Contains("paste", ex.Message);
    }

    [Fact]
    public async Task A_timeout_suggests_pasting()
    {
        var ex = await Assert.ThrowsAsync<PageFetchFailedException>(() =>
            Fetcher(() => throw new TaskCanceledException()).FetchAsync("https://example.com/x", CancellationToken.None));
        Assert.Contains("too long", ex.Message);
    }

    [Fact]
    public void A_page_with_no_main_text_suggests_pasting() =>
        Assert.Throws<PageFetchFailedException>(() =>
            PageTextFetcher.ExtractMainText(new Uri("https://example.com"), "<html><body></body></html>"));
}
