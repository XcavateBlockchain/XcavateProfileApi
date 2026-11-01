using System.Net;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using XcavateProfileApi.Services.Indexer;

namespace XcavateBuckets.Tests;

[TestFixture]
public class IndexerClientLawyersTests
{
    [Test]
    public async Task GetListingLawyers_queries_and_maps_both_wallets()
    {
        var handler = new LawyerPairHandler();
        var client = new IndexerClient(
            new StubHttpClientFactory(handler),
            new IndexerOptions { BaseUrl = "https://indexer.test" },
            NullLogger<IndexerClient>.Instance);

        var result = await client.GetListingLawyersAsync(12, CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(result, Is.Not.Null);
            Assert.That(result!.SpvLawyer, Is.EqualTo("SpvLawyerWallet111"));
            Assert.That(result.DeveloperLawyer, Is.EqualTo("DeveloperLawyerWallet111"));
            Assert.That(handler.RequestBody, Does.Contain("nodes { spvLawyer developerLawyer }"));
            Assert.That(handler.RequestBody, Does.Contain("\"id\":\"12\""));
        });
    }

    private sealed class StubHttpClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private sealed class LawyerPairHandler : HttpMessageHandler
    {
        public string RequestBody { get; private set; } = string.Empty;

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestBody = await request.Content!.ReadAsStringAsync(cancellationToken);
            var requestedPair = RequestBody.Contains("spvLawyer", StringComparison.Ordinal)
                && RequestBody.Contains("developerLawyer", StringComparison.Ordinal);
            var json = requestedPair
                ? """{"data":{"listings":{"nodes":[{"spvLawyer":"SpvLawyerWallet111","developerLawyer":"DeveloperLawyerWallet111"}]}}}"""
                : """{"errors":[{"message":"Both lawyer fields must be requested"}]}""";
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            };
        }
    }
}
