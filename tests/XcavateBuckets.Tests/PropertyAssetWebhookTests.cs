using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using XcavateBuckets.Domain.Entities;

namespace XcavateBuckets.Tests;

/// <summary>
/// The <c>init_property_asset_devnet</c> webhook driven against the real controller and bucket
/// domain services over SQLite — no docker, unlike the E2E suite. It covers the create path and
/// the at-least-once contract the indexer relies on: a redelivered asset is ignored, not
/// duplicated.
/// </summary>
[TestFixture]
public class PropertyAssetWebhookTests
{
    private const string Path = "/webhooks/init_property_asset_devnet";

    /// <summary>Builds a payload shaped exactly like the indexer's <c>property_asset_registered</c>.</summary>
    private static string Payload(
        string pubkey, long slot, string name = "42 Main Street", long listingId = 12)
    {
        // JsonObject, not an anonymous type: "event" is a C# keyword and cannot be a member name,
        // but it is an ordinary JSON field the indexer sends.
        var payload = new JsonObject
        {
            ["event"] = "property_asset_registered",
            ["pubkey"] = pubkey,
            ["listing_id"] = listingId,
            ["name"] = name,
            ["metadata_uri"] = $"https://metadata.test/{name.Replace(' ', '-').ToLowerInvariant()}.json",
            ["share_mint"] = "ShareMint11111111111111111111111111111111111",
            ["slot"] = slot,
            ["tx_signature"] = "TxSignature11111111111111111111111111111111111111111",
            ["block_time"] = "2026-08-26T10:12:34+00:00",
            ["program"] = "marketplace"
        };

        return payload.ToJsonString();
    }

    /// <summary>POSTs the raw JSON and returns the status plus the parsed body (default when empty).</summary>
    private static async Task<(HttpStatusCode Status, JsonElement Body)> PostAsync(
        HttpClient client, string json)
    {
        using var response = await client.PostAsync(
            Path, new StringContent(json, Encoding.UTF8, "application/json"));
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
            // The 400 path answers with a plain-text reason, not JSON; leave the body unpopulated.
            body = default;
        }

        return ((HttpStatusCode)response.StatusCode, body);
    }

    [Test]
    public async Task Webhook_creates_a_namespace_for_a_new_property_asset()
    {
        await using var host = await WebhooksHost.StartAsync();
        using var client = host.Client;

        var (status, body) = await PostAsync(client, Payload(pubkey: "AssetPDA111", slot: 487_500_123));

        var stored = await host.NamespacesAsync();
        var ns = stored.Single(n => n.Creator == "AssetPDA111");

        Assert.Multiple(() =>
        {
            Assert.That(status, Is.EqualTo(HttpStatusCode.OK));
            Assert.That(body.GetProperty("status").GetString(), Is.EqualTo("created"));

            // The on-chain details land on the namespace and in its properties map.
            Assert.That(ns.Name, Is.EqualTo("42 Main Street"));
            Assert.That(ns.SchemaUri, Is.EqualTo("https://metadata.test/42-main-street.json"));
            Assert.That(ns.Slot, Is.EqualTo(487_500_123));
            Assert.That(ns.PropertyId, Is.EqualTo(12));
            Assert.That(ns.Properties, Does.Contain("AssetPDA111"));
            Assert.That(ns.Properties, Does.Contain("ShareMint11111111111111111111111111111111111"));
            Assert.That(ns.Properties, Does.Contain("marketplace"));
        });
    }

    [Test]
    public async Task Webhook_ignores_a_redelivered_property_asset()
    {
        await using var host = await WebhooksHost.StartAsync();
        using var client = host.Client;

        const string pubkey = "AssetPDA222";
        const long slot = 500_000_001;

        var (firstStatus, firstBody) = await PostAsync(client, Payload(pubkey, slot));
        var (secondStatus, secondBody) = await PostAsync(client, Payload(pubkey, slot));

        var stored = await host.NamespacesAsync();

        Assert.Multiple(() =>
        {
            // Both deliveries answer 2xx, so the indexer stops retrying either one.
            Assert.That(firstStatus, Is.EqualTo(HttpStatusCode.OK));
            Assert.That(firstBody.GetProperty("status").GetString(), Is.EqualTo("created"));
            Assert.That(secondStatus, Is.EqualTo(HttpStatusCode.OK));
            Assert.That(secondBody.GetProperty("status").GetString(), Is.EqualTo("ignored"));
            Assert.That(
                stored.Count(n => n.Creator == pubkey),
                Is.EqualTo(1),
                "the redelivery must not create a second namespace");
        });
    }

    [Test]
    public async Task Webhook_creates_a_namespace_for_each_distinct_asset()
    {
        await using var host = await WebhooksHost.StartAsync();
        using var client = host.Client;

        await PostAsync(client, Payload(pubkey: "AssetPDAAAA", slot: 1));
        await PostAsync(
            client,
            Payload(pubkey: "AssetPDBBBB", slot: 1, name: "43 Main Street", listingId: 99));

        var stored = await host.NamespacesAsync();

        Assert.Multiple(() =>
        {
            Assert.That(stored.Count, Is.EqualTo(2));
            Assert.That(
                stored.Where(n => n.Creator == "AssetPDAAAA").Select(n => n.Name),
                Does.Contain("42 Main Street"));
            Assert.That(
                stored.Where(n => n.Creator == "AssetPDBBBB").Select(n => n.Name),
                Does.Contain("43 Main Street"));
        });
    }

    [Test]
    public async Task Webhook_rejects_a_payload_that_is_not_property_asset_registered()
    {
        await using var host = await WebhooksHost.StartAsync();
        using var client = host.Client;

        var json = new JsonObject
        {
            ["event"] = "something_else",
            ["pubkey"] = "AssetPDA333",
            ["name"] = "42 Main Street",
            ["slot"] = 600
        }.ToJsonString();

        var (status, _) = await PostAsync(client, json);
        var stored = await host.NamespacesAsync();

        Assert.Multiple(() =>
        {
            Assert.That(status, Is.EqualTo(HttpStatusCode.BadRequest));
            Assert.That(stored, Is.Empty, "a rejected payload must not create a namespace");
        });
    }
}
