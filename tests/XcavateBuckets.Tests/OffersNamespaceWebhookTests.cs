using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using XcavateBuckets.Domain.Entities;
using XcavateBuckets.Domain.Services;

namespace XcavateBuckets.Tests;

/// <summary>
/// The backend-driven offers namespace creation webhook: the indexer posts
/// <c>offers_namespace_created</c> after <c>claim_shares</c> or <c>relist_shares</c>, and the
/// profile API idempotently creates the property's <c>"&lt;property&gt; offers"</c> namespace.
/// </summary>
[TestFixture]
public class OffersNamespaceWebhookTests
{
    private const long ListingId = 42;
    private const string AssetPda = "AssetPDA222";
    private const long AssetSlot = 488_000_000;
    private const string WebhookSecret = "super-secret-indexer-webhook-token";

    private static async Task<(HttpStatusCode Status, JsonElement Body)> PostAsync(
        HttpClient client, string path, string json, string? secret = null)
    {
        var url = secret is null ? path : $"{path}?secret={Uri.EscapeDataString(secret)}";
        using var response = await client.PostAsync(
            url, new StringContent(json, Encoding.UTF8, "application/json"));
        var text = await response.Content.ReadAsStringAsync();

        JsonElement body;
        try
        {
            body = string.IsNullOrWhiteSpace(text)
                ? default
                : JsonSerializer.Deserialize<JsonElement>(text);
        }
        catch (JsonException)
        {
            body = default;
        }

        return ((HttpStatusCode)response.StatusCode, body);
    }

    private static string AssetRegisteredJson()
    {
        return new JsonObject
        {
            ["event"] = "property_asset_registered",
            ["pubkey"] = AssetPda,
            ["listing_id"] = ListingId,
            ["asset_id"] = 42,
            ["name"] = "Villa Sol",
            ["metadata_uri"] = "https://metadata.test/villa-sol.json",
            ["share_mint"] = "ShareMint22222222222222222222222222222222222",
            ["slot"] = AssetSlot,
            ["tx_signature"] = "AssetTx2222222222222222222222222222222222222222222",
            ["block_time"] = "2026-08-26T10:12:34+00:00",
            ["program"] = "marketplace"
        }.ToJsonString();
    }

    private static string OffersNamespaceCreatedJson(
        string? txSignature = null,
        long? assetId = 42,
        long? listingId = ListingId)
    {
        return new JsonObject
        {
            ["event"] = "offers_namespace_created",
            ["listing_id"] = listingId,
            ["listing"] = "ListingPDA222",
            ["property"] = AssetPda,
            ["asset_id"] = assetId,
            ["cluster"] = "devnet",
            ["tx_signature"] = txSignature ?? "OfferTx2222222222222222222222222222222222222222222",
            ["slot"] = 488_100_000,
            ["block_time"] = "2026-08-27T09:00:00+00:00"
        }.ToJsonString();
    }

    private static async Task SeedPropertyNamespaceAsync(HttpClient client)
    {
        var (status, _) = await PostAsync(
            client, "/webhooks/init_property_asset_devnet", AssetRegisteredJson(), secret: WebhookSecret);
        Assert.That(status, Is.EqualTo(HttpStatusCode.OK), "the asset webhook seed must succeed");
    }

    [Test]
    public async Task Creates_the_offers_namespace_on_first_delivery()
    {
        await using var host = await WebhooksHost.StartAsync(webhookSecret: WebhookSecret);
        await SeedPropertyNamespaceAsync(host.Client);

        var (status, body) = await PostAsync(
            host.Client,
            "/webhooks/offers_namespace_created_devnet",
            OffersNamespaceCreatedJson(),
            secret: WebhookSecret);

        var namespaces = await host.NamespacesAsync();
        var offersNs = namespaces.Single(n => n.Category == BucketService.OffersNamespaceCategory);

        Assert.Multiple(() =>
        {
            Assert.That(status, Is.EqualTo(HttpStatusCode.OK));
            Assert.That(body.GetProperty("status").GetString(), Is.EqualTo("created"));
            Assert.That(body.GetProperty("namespaceId").GetInt64(), Is.EqualTo(offersNs.NamespaceId));
            Assert.That(offersNs.Name, Is.EqualTo("Villa Sol offers"));
            Assert.That(offersNs.Creator, Is.EqualTo(AssetPda));
            Assert.That(offersNs.Cluster, Is.EqualTo("devnet"));
            Assert.That(OffersNamespaceService.ReadAssetId(offersNs.Properties), Is.EqualTo("42"));
        });
    }

    [Test]
    public async Task Redelivery_with_same_event_key_returns_duplicate()
    {
        await using var host = await WebhooksHost.StartAsync(webhookSecret: WebhookSecret);
        await SeedPropertyNamespaceAsync(host.Client);

        var json = OffersNamespaceCreatedJson();
        await PostAsync(host.Client, "/webhooks/offers_namespace_created_devnet", json, secret: WebhookSecret);
        var (status, body) = await PostAsync(
            host.Client, "/webhooks/offers_namespace_created_devnet", json, secret: WebhookSecret);

        var namespaces = await host.NamespacesAsync();

        Assert.Multiple(() =>
        {
            Assert.That(status, Is.EqualTo(HttpStatusCode.OK));
            Assert.That(body.GetProperty("status").GetString(), Is.EqualTo("duplicate"));
            Assert.That(namespaces.Count(n => n.Category == BucketService.OffersNamespaceCategory), Is.EqualTo(1));
        });
    }

    [Test]
    public async Task Returns_ignored_when_the_offers_namespace_already_existed()
    {
        await using var host = await WebhooksHost.StartAsync(webhookSecret: WebhookSecret);
        await SeedPropertyNamespaceAsync(host.Client);

        await host.WithDbAsync(async db =>
        {
            db.Namespaces.Add(new Namespace
            {
                Name = "Villa Sol offers",
                Creator = AssetPda,
                Category = BucketService.OffersNamespaceCategory,
                Cluster = "devnet",
                Properties = """{"assetId":"42"}""",
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            });
            await db.SaveChangesAsync();
            return true;
        });

        var (status, body) = await PostAsync(
            host.Client,
            "/webhooks/offers_namespace_created_devnet",
            OffersNamespaceCreatedJson(txSignature: "DifferentTx22222222222222222222222222222222222222"),
            secret: WebhookSecret);

        var namespaces = await host.NamespacesAsync();

        Assert.Multiple(() =>
        {
            Assert.That(status, Is.EqualTo(HttpStatusCode.OK));
            Assert.That(body.GetProperty("status").GetString(), Is.EqualTo("ignored"));
            Assert.That(namespaces.Count(n => n.Category == BucketService.OffersNamespaceCategory), Is.EqualTo(1));
        });
    }

    [Test]
    public async Task Returns_404_when_the_property_namespace_is_missing_so_the_indexer_retries()
    {
        await using var host = await WebhooksHost.StartAsync(webhookSecret: WebhookSecret);

        var (status, _) = await PostAsync(
            host.Client,
            "/webhooks/offers_namespace_created_devnet",
            OffersNamespaceCreatedJson(),
            secret: WebhookSecret);

        var namespaces = await host.NamespacesAsync();

        Assert.Multiple(() =>
        {
            Assert.That(status, Is.EqualTo(HttpStatusCode.NotFound));
            Assert.That(namespaces, Is.Empty);
        });
    }

    [Test]
    public async Task Rejects_a_wrong_secret_with_401()
    {
        await using var host = await WebhooksHost.StartAsync(webhookSecret: WebhookSecret);
        await SeedPropertyNamespaceAsync(host.Client);

        var (status, _) = await PostAsync(
            host.Client,
            "/webhooks/offers_namespace_created_devnet",
            OffersNamespaceCreatedJson(),
            secret: "wrong-secret");

        Assert.That(status, Is.EqualTo(HttpStatusCode.Unauthorized));
    }

    [Test]
    public async Task Returns_503_when_the_shared_secret_is_not_configured()
    {
        await using var host = await WebhooksHost.StartAsync();

        var (status, _) = await PostAsync(
            host.Client,
            "/webhooks/offers_namespace_created_devnet",
            OffersNamespaceCreatedJson());

        Assert.That(status, Is.EqualTo(HttpStatusCode.ServiceUnavailable));
    }
}
