using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using XcavateBuckets.Domain.Entities;

namespace XcavateBuckets.Tests;

/// <summary>
/// The five property-lifecycle webhooks (claim started, lawyer elected, deal executed, agent
/// appointed, shares transferred) driven against the real controller and bucket domain services
/// over SQLite, like <see cref="PropertyAssetWebhookTests"/> does for the asset webhook. Covers
/// the bucket-creation and lawyer-removal paths, the placeholder acknowledgements, the 404
/// retry-me ordering contract, and idempotent redelivery.
/// </summary>
[TestFixture]
public class PropertyLifecycleWebhookTests
{
    private const long ListingId = 12;
    private const string AssetPda = "AssetPDA111";
    private const long AssetSlot = 487_500_123;

    /// <summary>POSTs the raw JSON and returns the status plus the parsed body (default when empty).</summary>
    private static async Task<(HttpStatusCode Status, JsonElement Body)> PostAsync(
        HttpClient client, string path, string json)
    {
        using var response = await client.PostAsync(
            path, new StringContent(json, Encoding.UTF8, "application/json"));
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

    /// <summary>Seeds the namespace exactly the way the running system gets it: the asset webhook.</summary>
    private static async Task SeedNamespaceAsync(HttpClient client)
    {
        var json = new JsonObject
        {
            ["event"] = "property_asset_registered",
            ["pubkey"] = AssetPda,
            ["listing_id"] = ListingId,
            ["name"] = "42 Main Street",
            ["metadata_uri"] = "https://metadata.test/42-main-street.json",
            ["share_mint"] = "ShareMint11111111111111111111111111111111111",
            ["slot"] = AssetSlot,
            ["tx_signature"] = "TxSignature11111111111111111111111111111111111111111",
            ["block_time"] = "2026-08-26T10:12:34+00:00",
            ["program"] = "marketplace"
        }.ToJsonString();

        var (status, _) = await PostAsync(client, "/webhooks/init_property_asset_devnet", json);
        Assert.That(status, Is.EqualTo(HttpStatusCode.OK), "the asset webhook seed must succeed");
    }

    private static string ClaimStartedJson()
    {
        return new JsonObject
        {
            ["event"] = "property_claim_started",
            ["listing"] = "ListingPDA111",
            ["property"] = AssetPda,
            ["listing_id"] = ListingId,
            ["investor"] = "InvestorWallet111",
            ["slot"] = 487_600_000,
            ["tx_signature"] = "ClaimTx1111111111111111111111111111111111111111111",
            ["block_time"] = "2026-08-27T09:00:00+00:00",
            ["program"] = "marketplace"
        }.ToJsonString();
    }

    /// <summary>Seeds the namespace AND the legal bucket (the claim webhook), returning the bucket id.</summary>
    private static async Task<long> SeedLegalBucketAsync(WebhooksHost host)
    {
        await SeedNamespaceAsync(host.Client);
        var (status, body) = await PostAsync(
            host.Client, "/webhooks/property_claim_started_devnet", ClaimStartedJson());
        Assert.That(status, Is.EqualTo(HttpStatusCode.OK), "the claim webhook seed must succeed");

        return body.GetProperty("bucketId").GetInt64();
    }

    // ---- property_claim_started: the legal bucket ------------------------------------------

    [Test]
    public async Task Claim_started_creates_the_legal_bucket_with_the_operator_admin()
    {
        await using var host = await WebhooksHost.StartAsync();
        await SeedNamespaceAsync(host.Client);

        var (status, body) = await PostAsync(
            host.Client, "/webhooks/property_claim_started_devnet", ClaimStartedJson());

        var buckets = await host.BucketsAsync();

        Assert.Multiple(() =>
        {
            Assert.That(status, Is.EqualTo(HttpStatusCode.OK));
            Assert.That(body.GetProperty("status").GetString(), Is.EqualTo("created"));

            var bucket = buckets.Single();
            Assert.That(bucket.Category, Is.EqualTo("legal-process"));
            Assert.That(bucket.Name, Is.EqualTo("42 Main Street - legal process"));
            Assert.That(bucket.Creator, Is.EqualTo(AssetPda));
            Assert.That(bucket.Properties, Does.Contain("legal_process"));
            Assert.That(bucket.Properties, Does.Contain("ClaimTx1111111111111111111111111111111111111111111"));
        });

        // The namespace creator is installed as the bucket admin the later membership webhooks
        // use as their caller.
        var admins = await host.WithDbAsync(async db =>
            await Task.FromResult(
                db.BucketAdmins.Where(a => a.BucketId == buckets[0].BucketId)
                    .Select(a => a.SubjectId)
                    .ToList()));
        Assert.That(admins, Is.EqualTo(new[] { AssetPda }));
    }

    [Test]
    public async Task Claim_started_redelivery_is_ignored_not_duplicated()
    {
        await using var host = await WebhooksHost.StartAsync();
        await SeedNamespaceAsync(host.Client);

        var (firstStatus, _) = await PostAsync(
            host.Client, "/webhooks/property_claim_started_devnet", ClaimStartedJson());
        var (secondStatus, secondBody) = await PostAsync(
            host.Client, "/webhooks/property_claim_started_devnet", ClaimStartedJson());

        var buckets = await host.BucketsAsync();

        Assert.Multiple(() =>
        {
            Assert.That(firstStatus, Is.EqualTo(HttpStatusCode.OK));
            Assert.That(secondStatus, Is.EqualTo(HttpStatusCode.OK));
            Assert.That(secondBody.GetProperty("status").GetString(), Is.EqualTo("ignored"));
            Assert.That(buckets.Count, Is.EqualTo(1), "the redelivery must not create a second bucket");
        });
    }

    [Test]
    public async Task Claim_started_404s_while_the_namespace_is_missing_so_the_indexer_retries()
    {
        await using var host = await WebhooksHost.StartAsync();

        var (status, _) = await PostAsync(
            host.Client, "/webhooks/property_claim_started_devnet", ClaimStartedJson());

        var buckets = await host.BucketsAsync();

        Assert.Multiple(() =>
        {
            Assert.That(status, Is.EqualTo(HttpStatusCode.NotFound));
            Assert.That(buckets, Is.Empty);
        });
    }

    [Test]
    public async Task Claim_started_rejects_a_wrong_event_name()
    {
        await using var host = await WebhooksHost.StartAsync();

        var json = new JsonObject { ["event"] = "something_else", ["listing_id"] = ListingId }
            .ToJsonString();
        var (status, _) = await PostAsync(host.Client, "/webhooks/property_claim_started_devnet", json);

        Assert.That(status, Is.EqualTo(HttpStatusCode.BadRequest));
    }

    // ---- deal_executed: the lawyers leave the bucket ----------------------------------------

    [Test]
    public async Task Deal_executed_removes_the_lawyers_but_keeps_the_operator_admin()
    {
        await using var host = await WebhooksHost.StartAsync();
        var bucketId = await SeedLegalBucketAsync(host);

        // Two lawyers with write+manage access, as the election webhook (once implemented) or a
        // human would have added them.
        await host.WithDbAsync(async db =>
        {
            db.BucketContributors.Add(new BucketContributor { BucketId = bucketId, SubjectId = "LawyerA" });
            db.BucketContributors.Add(new BucketContributor { BucketId = bucketId, SubjectId = "LawyerB" });
            db.BucketAdmins.Add(new BucketAdmin { BucketId = bucketId, SubjectId = "LawyerA" });
            await db.SaveChangesAsync();
            return true;
        });

        var json = new JsonObject
        {
            ["event"] = "deal_executed",
            ["listing_id"] = ListingId,
            ["listing"] = "ListingPDA111",
            ["property"] = AssetPda,
            ["developer_lawyer_registry"] = "DevLawyerRegistry111",
            ["spv_lawyer_registry"] = "SpvLawyerRegistry111",
            ["slot"] = 487_700_000,
            ["tx_signature"] = "DealTx11111111111111111111111111111111111111111111",
            ["block_time"] = "2026-09-01T15:30:00+00:00",
            ["program"] = "marketplace"
        }.ToJsonString();

        var (status, body) = await PostAsync(host.Client, "/webhooks/deal_executed_devnet", json);

        var (contributors, admins) = await host.WithDbAsync(async db =>
        {
            var c = await Task.FromResult(
                db.BucketContributors.Where(x => x.BucketId == bucketId)
                    .Select(x => x.SubjectId)
                    .ToList());
            var a = await Task.FromResult(
                db.BucketAdmins.Where(x => x.BucketId == bucketId).Select(x => x.SubjectId).ToList());
            return (c, a);
        });

        Assert.Multiple(() =>
        {
            Assert.That(status, Is.EqualTo(HttpStatusCode.OK));
            Assert.That(body.GetProperty("status").GetString(), Is.EqualTo("lawyers_removed"));
            Assert.That(body.GetProperty("removedContributors").GetInt32(), Is.EqualTo(2));
            Assert.That(body.GetProperty("removedAdmins").GetInt32(), Is.EqualTo(1));
            Assert.That(contributors, Is.Empty);
            Assert.That(admins, Is.EqualTo(new[] { AssetPda }), "the operator admin stays");
        });

        // Redelivery is a harmless no-op.
        var (secondStatus, secondBody) = await PostAsync(host.Client, "/webhooks/deal_executed_devnet", json);
        Assert.Multiple(() =>
        {
            Assert.That(secondStatus, Is.EqualTo(HttpStatusCode.OK));
            Assert.That(secondBody.GetProperty("removedContributors").GetInt32(), Is.EqualTo(0));
        });
    }

    [Test]
    public async Task Deal_executed_404s_while_the_bucket_is_missing_so_the_indexer_retries()
    {
        await using var host = await WebhooksHost.StartAsync();
        await SeedNamespaceAsync(host.Client);

        var json = new JsonObject
        {
            ["event"] = "deal_executed",
            ["listing_id"] = ListingId,
            ["property"] = AssetPda
        }.ToJsonString();

        var (status, _) = await PostAsync(host.Client, "/webhooks/deal_executed_devnet", json);

        Assert.That(status, Is.EqualTo(HttpStatusCode.NotFound));
    }

    // ---- The placeholders: accepted, unresolved, retry-safe --------------------------------

    [Test]
    public async Task Lawyer_elected_is_accepted_as_a_placeholder_once_the_bucket_exists()
    {
        await using var host = await WebhooksHost.StartAsync();
        var bucketId = await SeedLegalBucketAsync(host);

        var json = new JsonObject
        {
            ["event"] = "spv_lawyer_elected",
            ["listing_id"] = ListingId,
            ["listing"] = "ListingPDA111",
            ["property"] = AssetPda,
            ["winner_registry"] = "WinnerRegistry111",
            ["slot"] = 487_650_000,
            ["tx_signature"] = "ElectTx111111111111111111111111111111111111111111",
            ["block_time"] = "2026-08-30T12:00:00+00:00",
            ["program"] = "marketplace"
        }.ToJsonString();

        var (status, body) = await PostAsync(host.Client, "/webhooks/spv_lawyer_elected_devnet", json);

        Assert.Multiple(() =>
        {
            Assert.That(status, Is.EqualTo(HttpStatusCode.OK));
            Assert.That(body.GetProperty("status").GetString(), Is.EqualTo("accepted"));
            Assert.That(body.GetProperty("acted").GetBoolean(), Is.False);
            Assert.That(body.GetProperty("bucketId").GetInt64(), Is.EqualTo(bucketId));
        });
    }

    [Test]
    public async Task Agent_appointed_is_accepted_as_a_placeholder_and_finds_the_namespace_by_pda()
    {
        await using var host = await WebhooksHost.StartAsync();
        var bucketId = await SeedLegalBucketAsync(host);

        // The property program's asset_id is NOT assumed to equal the marketplace listing id:
        // a deliberately different id must still resolve via the PropertyAsset PDA fallback.
        var json = new JsonObject
        {
            ["event"] = "letting_agent_appointed",
            ["asset_id"] = 999,
            ["letting"] = "LettingPDA111",
            ["property"] = AssetPda,
            ["winner_entry"] = "WinnerEntry111",
            ["slot"] = 487_800_000,
            ["tx_signature"] = "AgentTx1111111111111111111111111111111111111111111",
            ["block_time"] = "2026-09-05T08:00:00+00:00",
            ["program"] = "property"
        }.ToJsonString();

        var (status, body) = await PostAsync(host.Client, "/webhooks/letting_agent_appointed_devnet", json);

        Assert.Multiple(() =>
        {
            Assert.That(status, Is.EqualTo(HttpStatusCode.OK));
            Assert.That(body.GetProperty("status").GetString(), Is.EqualTo("accepted"));
            Assert.That(body.GetProperty("bucketId").GetInt64(), Is.EqualTo(bucketId));
        });
    }

    [Test]
    public async Task Shares_transferred_is_accepted_as_a_placeholder()
    {
        await using var host = await WebhooksHost.StartAsync();
        await SeedLegalBucketAsync(host);

        var json = new JsonObject
        {
            ["event"] = "property_shares_transferred",
            ["kind"] = "relisted_purchase",
            ["property"] = AssetPda,
            ["listing"] = "ListingPDA111",
            ["from"] = "SellerWallet111",
            ["to"] = "BuyerWallet111",
            ["amount"] = 3,
            ["asset_id"] = ListingId,
            ["offer"] = null,
            ["slot"] = 487_900_000,
            ["tx_signature"] = "TransferTx11111111111111111111111111111111111111111",
            ["block_time"] = "2026-09-10T11:00:00+00:00",
            ["program"] = "marketplace"
        }.ToJsonString();

        var (status, body) = await PostAsync(host.Client, "/webhooks/shares_transferred_devnet", json);

        Assert.Multiple(() =>
        {
            Assert.That(status, Is.EqualTo(HttpStatusCode.OK));
            Assert.That(body.GetProperty("status").GetString(), Is.EqualTo("accepted"));
            Assert.That(body.GetProperty("acted").GetBoolean(), Is.False);
        });
    }

    [Test]
    public async Task Shares_transferred_rejects_an_unknown_kind()
    {
        await using var host = await WebhooksHost.StartAsync();
        await SeedLegalBucketAsync(host);

        var json = new JsonObject
        {
            ["event"] = "property_shares_transferred",
            ["kind"] = "gift",
            ["property"] = AssetPda,
            ["from"] = "SellerWallet111",
            ["to"] = "BuyerWallet111"
        }.ToJsonString();

        var (status, _) = await PostAsync(host.Client, "/webhooks/shares_transferred_devnet", json);

        Assert.That(status, Is.EqualTo(HttpStatusCode.BadRequest));
    }

    [Test]
    public async Task The_placeholders_404_while_the_namespace_is_missing()
    {
        await using var host = await WebhooksHost.StartAsync();

        var elected = new JsonObject
        {
            ["event"] = "spv_lawyer_elected",
            ["listing_id"] = ListingId,
            ["property"] = AssetPda
        }.ToJsonString();
        var transferred = new JsonObject
        {
            ["event"] = "property_shares_transferred",
            ["kind"] = "direct_send",
            ["property"] = AssetPda,
            ["from"] = "SellerWallet111",
            ["to"] = "BuyerWallet111"
        }.ToJsonString();

        var (electedStatus, _) = await PostAsync(host.Client, "/webhooks/spv_lawyer_elected_devnet", elected);
        var (transferredStatus, _) = await PostAsync(host.Client, "/webhooks/shares_transferred_devnet", transferred);

        Assert.Multiple(() =>
        {
            Assert.That(electedStatus, Is.EqualTo(HttpStatusCode.NotFound));
            Assert.That(transferredStatus, Is.EqualTo(HttpStatusCode.NotFound));
        });
    }
}
