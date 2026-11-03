using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace XcavateBuckets.Tests;

[TestFixture]
public class PropertyLifecycleWebhookTests
{
    private const long ListingId = 12;
    private const string AssetPda = "AssetPDA111";
    private const string WebhookSecret = "property-lifecycle-test-secret";

    [Test]
    public async Task Claim_started_does_not_create_a_legal_process_bucket()
    {
        await using var host = await WebhooksHost.StartAsync(webhookSecret: WebhookSecret);
        var asset = new JsonObject
        {
            ["event"] = "property_asset_registered",
            ["pubkey"] = AssetPda,
            ["listing_id"] = ListingId,
            ["name"] = "42 Main Street",
            ["metadata_uri"] = "https://metadata.test/42-main-street.json",
            ["share_mint"] = "ShareMint11111111111111111111111111111111111",
            ["slot"] = 487_500_123,
            ["tx_signature"] = "TxSignature11111111111111111111111111111111111111111",
            ["program"] = "marketplace"
        }.ToJsonString();
        using (var response = await PostAsync(host.Client, "/webhooks/init_property_asset_devnet", asset))
        {
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        }

        var claim = new JsonObject
        {
            ["event"] = "property_claim_started",
            ["listing"] = "ListingPDA111",
            ["property"] = AssetPda,
            ["listing_id"] = ListingId,
            ["investor"] = "InvestorWallet111",
            ["slot"] = 487_600_000,
            ["tx_signature"] = "ClaimTx1111111111111111111111111111111111111111111",
            ["program"] = "marketplace"
        }.ToJsonString();
        using var claimResponse = await PostAsync(
            host.Client, "/webhooks/property_claim_started_devnet", claim);
        var buckets = await host.BucketsAsync();

        Assert.Multiple(() =>
        {
            Assert.That(claimResponse.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
            Assert.That(buckets, Is.Empty);
        });
    }

    private static async Task<HttpResponseMessage> PostAsync(
        HttpClient client, string path, string json) =>
        await client.PostAsync(
            $"{path}?secret={Uri.EscapeDataString(WebhookSecret)}",
            new StringContent(json, Encoding.UTF8, "application/json"));
}
