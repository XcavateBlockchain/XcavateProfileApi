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

    [Test]
    public async Task GetSpvCandidacy_queries_the_exact_listing_round_and_lawyer()
    {
        var handler = new CandidacyHandler();
        var client = new IndexerClient(
            new StubHttpClientFactory(handler),
            new IndexerOptions { BaseUrl = "https://indexer.test" },
            NullLogger<IndexerClient>.Instance);

        var result = await client.GetSpvCandidacyAsync(12, 2, "LawyerWallet111", CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(result, Is.Not.Null);
            Assert.That(result!.Id, Is.EqualTo("CandidacyPDA111"));
            Assert.That(result.Round, Is.EqualTo(2));
            Assert.That(result.Lawyer, Is.EqualTo("LawyerWallet111"));
            Assert.That(handler.RequestBody,
                Does.Contain("lawyerCandidacies(listingId: $listingId, round: $round, lawyer: $lawyer"));
            Assert.That(handler.RequestBody, Does.Contain("\"listingId\":\"12\""));
            Assert.That(handler.RequestBody, Does.Contain("\"round\":\"2\""));
            Assert.That(handler.RequestBody, Does.Contain("\"lawyer\":\"LawyerWallet111\""));
        });
    }

    [Test]
    public async Task GetListingLawyerReview_queries_and_maps_the_complete_two_seat_state()
    {
        var handler = new ListingReviewHandler();
        var client = new IndexerClient(
            new StubHttpClientFactory(handler),
            new IndexerOptions { BaseUrl = "https://indexer.test" },
            NullLogger<IndexerClient>.Instance);

        var result = await client.GetListingLawyerReviewAsync(12, CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(result, Is.Not.Null);
            Assert.That(result!.SpvLawyer, Is.EqualTo("SpvLawyerWallet111"));
            Assert.That(result.SpvDocumentStatus, Is.EqualTo("APPROVED"));
            Assert.That(result.SpvDocumentsHash, Is.EqualTo(new string('a', 64)));
            Assert.That(result.DeveloperLawyer, Is.EqualTo("DeveloperLawyerWallet111"));
            Assert.That(result.DeveloperDocumentStatus, Is.EqualTo("PENDING"));
            Assert.That(result.DeveloperDocumentsHash, Is.EqualTo(new string('0', 64)));
            Assert.That(result.SecondAttempt, Is.True);
            Assert.That(result.Status, Is.EqualTo("SOLD_OUT"));
            Assert.That(result.LegalDeadline, Is.EqualTo(1_790_000_000));
            Assert.That(handler.RequestBody, Does.Contain("listings(listingId: $id, first: 1, offset: 0)"));
            Assert.That(handler.RequestBody, Does.Not.Contain("active: true"));
            Assert.That(handler.RequestBody, Does.Contain("spvLawyerDocStatus"));
            Assert.That(handler.RequestBody, Does.Contain("spvLawyerDocumentsHash"));
            Assert.That(handler.RequestBody, Does.Contain("developerLawyerDocStatus"));
            Assert.That(handler.RequestBody, Does.Contain("developerLawyerDocumentsHash"));
            Assert.That(handler.RequestBody, Does.Contain("secondAttempt status legalDeadline"));
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

    private sealed class ListingReviewHandler : HttpMessageHandler
    {
        public string RequestBody { get; private set; } = string.Empty;

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestBody = await request.Content!.ReadAsStringAsync(cancellationToken);
            const string json = """{"data":{"listings":{"nodes":[{"spvLawyer":"SpvLawyerWallet111","spvLawyerDocStatus":"APPROVED","spvLawyerDocumentsHash":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa","developerLawyer":"DeveloperLawyerWallet111","developerLawyerDocStatus":"PENDING","developerLawyerDocumentsHash":"0000000000000000000000000000000000000000000000000000000000000000","secondAttempt":true,"status":"SOLD_OUT","legalDeadline":"1790000000"}]}}}""";
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            };
        }
    }

    private sealed class CandidacyHandler : HttpMessageHandler
    {
        public string RequestBody { get; private set; } = string.Empty;

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestBody = await request.Content!.ReadAsStringAsync(cancellationToken);
            const string json = """{"data":{"lawyerCandidacies":{"nodes":[{"id":"CandidacyPDA111","round":"2","lawyer":"LawyerWallet111"}]}}}""";
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            };
        }
    }
}
