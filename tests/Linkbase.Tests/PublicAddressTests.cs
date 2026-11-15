using System.Net;
using Linkbase.Core.Extraction;

namespace Linkbase.Tests;

public class PublicAddressTests
{
    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("10.1.2.3")]
    [InlineData("172.16.0.1")]
    [InlineData("172.31.255.255")]
    [InlineData("192.168.1.1")]
    [InlineData("169.254.169.254")]
    [InlineData("100.64.0.1")]
    [InlineData("0.0.0.0")]
    [InlineData("224.0.0.1")]
    [InlineData("::1")]
    [InlineData("fe80::1")]
    [InlineData("fd00::1")]
    [InlineData("::ffff:10.0.0.1")]
    [InlineData("::ffff:169.254.169.254")]
    public void Private_and_special_addresses_are_refused(string address) =>
        Assert.False(PublicAddress.IsAllowed(IPAddress.Parse(address)));

    [Theory]
    [InlineData("8.8.8.8")]
    [InlineData("172.32.0.1")]
    [InlineData("93.184.216.34")]
    [InlineData("2606:4700:4700::1111")]
    public void Public_addresses_are_allowed(string address) =>
        Assert.True(PublicAddress.IsAllowed(IPAddress.Parse(address)));

    [Theory]
    [InlineData("http://169.254.169.254/latest/meta-data")]
    [InlineData("http://10.0.0.5/")]
    [InlineData("http://[fd00::1]/")]
    [InlineData("http://localhost/")]
    public async Task The_fetcher_refuses_private_addresses_before_connecting(string url)
    {
        var fetcher = new PageTextFetcher(new HttpClient(new FailingHandler()));
        var ex = await Assert.ThrowsAsync<PageFetchFailedException>(() => fetcher.FetchAsync(url, CancellationToken.None));
        Assert.Contains("private network", ex.Message);
    }

    private sealed class FailingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("The fetcher should have refused before sending.");
    }
}
