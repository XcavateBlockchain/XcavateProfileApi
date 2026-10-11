using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using XcavateBuckets.Domain.Entities;
using XcavateProfile.Client;
using XcavateProfileApi.Services.Indexer;

namespace XcavateBuckets.Tests;

/// <summary>
/// The six property-lifecycle webhooks (claim started, lawyer elected, deal executed, agent
/// appointed, shares transferred, holder changed) driven against the real controller and bucket
/// domain services over SQLite, like <see cref="PropertyAssetWebhookTests"/> does for the asset
/// webhook. Covers the bucket creation, the lawyer's write-grant plus the holder read re-sync on
/// a ratified election, the lawyer removal at settlement, the agent's write-grant, the resale
/// viewer swap, the 404 retry-me ordering contract, the 502 indexer-resolution contract, and
/// idempotent redelivery.
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

    // ---- spv_lawyer_elected: the vote announcement plus the membership grants -----------------

    private static string LawyerElectedJson()
    {
        return new JsonObject
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
    }

    private static async Task<List<string>> ContributorIdsAsync(WebhooksHost host, long bucketId)
    {
        return await host.WithDbAsync(async db =>
            await Task.FromResult(
                db.BucketContributors.Where(c => c.BucketId == bucketId)
                    .Select(c => c.SubjectId)
                    .ToList()));
    }

    [Test]
    public async Task Lawyer_elected_ratified_grants_the_lawyer_write_and_the_holders_read()
    {
        await using var host = await WebhooksHost.StartAsync();
        var bucketId = await SeedLegalBucketAsync(host);
        // The indexer's listing row shows the seat filled: its spvLawyer IS the winner's wallet.
        host.Indexer.SpvElection = new SpvElectionInfo(1, 1_790_000_000, 1, "LawyerWallet111");
        // Two current holders: one with a registered key, one without.
        var holderKey = "0x" + new string('d', 64);
        await SeedProfileAsync(host, "HolderWallet111", holderKey);
        host.Indexer.HolderWallets = new[] { "HolderWallet111", "KeylessWallet222" };

        var (status, body) = await PostAsync(
            host.Client, "/webhooks/spv_lawyer_elected_devnet", LawyerElectedJson());

        var contributors = await ContributorIdsAsync(host, bucketId);
        var viewers = await ViewerIdsAsync(host, bucketId);

        Assert.Multiple(() =>
        {
            Assert.That(status, Is.EqualTo(HttpStatusCode.OK));
            Assert.That(body.GetProperty("status").GetString(), Is.EqualTo("posted"));
            Assert.That(body.GetProperty("bucketId").GetInt64(), Is.EqualTo(bucketId));
            Assert.That(body.GetProperty("lawyerContributorAdded").GetBoolean(), Is.True);
            Assert.That(body.GetProperty("holdersAdded").GetInt32(), Is.EqualTo(1));
            Assert.That(body.GetProperty("holdersSkippedNoKey").GetInt32(), Is.EqualTo(1));
            Assert.That(contributors, Does.Contain("LawyerWallet111"), "the lawyer gets WRITE");
            Assert.That(contributors, Does.Contain("system:voting"), "the announcement's poster");
            Assert.That(viewers, Is.EqualTo(new[] { holderKey }), "the keyed holder gets READ");
        });
    }

    [Test]
    public async Task Lawyer_elected_rejected_adds_nobody()
    {
        await using var host = await WebhooksHost.StartAsync();
        var bucketId = await SeedLegalBucketAsync(host);
        // The all-zero pubkey: the round failed open, nobody was seated.
        host.Indexer.SpvElection =
            new SpvElectionInfo(1, 1_790_000_000, 0, "11111111111111111111111111111111");
        var holderKey = "0x" + new string('d', 64);
        await SeedProfileAsync(host, "HolderWallet111", holderKey);
        host.Indexer.HolderWallets = new[] { "HolderWallet111" };

        var (status, body) = await PostAsync(
            host.Client, "/webhooks/spv_lawyer_elected_devnet", LawyerElectedJson());

        var contributors = await ContributorIdsAsync(host, bucketId);
        var viewers = await ViewerIdsAsync(host, bucketId);

        Assert.Multiple(() =>
        {
            Assert.That(status, Is.EqualTo(HttpStatusCode.OK));
            Assert.That(body.GetProperty("status").GetString(), Is.EqualTo("posted"),
                "the announcement still posts…");
            Assert.That(body.GetProperty("lawyerContributorAdded").GetBoolean(), Is.False);
            Assert.That(body.GetProperty("holdersAdded").GetInt32(), Is.EqualTo(0));
            Assert.That(body.GetProperty("holdersSkippedNoKey").GetInt32(), Is.EqualTo(0));
            Assert.That(contributors, Is.EqualTo(new[] { "system:voting" }),
                "…but beyond its system poster nobody is granted anything");
            Assert.That(viewers, Is.Empty);
        });
    }

    [Test]
    public async Task Lawyer_elected_redelivery_repeats_no_grant()
    {
        await using var host = await WebhooksHost.StartAsync();
        var bucketId = await SeedLegalBucketAsync(host);
        host.Indexer.SpvElection = new SpvElectionInfo(1, 1_790_000_000, 1, "LawyerWallet111");
        var holderKey = "0x" + new string('d', 64);
        await SeedProfileAsync(host, "HolderWallet111", holderKey);
        host.Indexer.HolderWallets = new[] { "HolderWallet111" };

        await PostAsync(host.Client, "/webhooks/spv_lawyer_elected_devnet", LawyerElectedJson());
        var (secondStatus, second) = await PostAsync(
            host.Client, "/webhooks/spv_lawyer_elected_devnet", LawyerElectedJson());

        var contributors = await ContributorIdsAsync(host, bucketId);
        var viewers = await ViewerIdsAsync(host, bucketId);
        var messageCount = await host.WithDbAsync(async db =>
            await Task.FromResult(db.Messages.Count(m => m.BucketId == bucketId)));

        Assert.Multiple(() =>
        {
            Assert.That(secondStatus, Is.EqualTo(HttpStatusCode.OK));
            Assert.That(second.GetProperty("status").GetString(), Is.EqualTo("ignored"),
                "the duplicate-guard catches the announcement…");
            Assert.That(second.GetProperty("lawyerContributorAdded").GetBoolean(), Is.False);
            Assert.That(second.GetProperty("holdersAdded").GetInt32(), Is.EqualTo(0));
            Assert.That(contributors.Count(c => c == "LawyerWallet111"), Is.EqualTo(1),
                "…and the grants stay singular");
            Assert.That(viewers, Is.EqualTo(new[] { holderKey }));
            Assert.That(messageCount, Is.EqualTo(1));
        });
    }

    [Test]
    public async Task Lawyer_elected_502s_mid_grant_when_the_holder_sync_fails_and_the_redelivery_completes_it()
    {
        await using var host = await WebhooksHost.StartAsync();
        var bucketId = await SeedLegalBucketAsync(host);
        host.Indexer.SpvElection = new SpvElectionInfo(1, 1_790_000_000, 1, "LawyerWallet111");
        var holderKey = "0x" + new string('d', 64);
        await SeedProfileAsync(host, "HolderWallet111", holderKey);
        host.Indexer.HolderWallets = new[] { "HolderWallet111" };
        // The announcement's own resolution answers, but the holder enumeration does not.
        host.Indexer.ThrowOnHolders = true;

        var (downStatus, _) = await PostAsync(
            host.Client, "/webhooks/spv_lawyer_elected_devnet", LawyerElectedJson());

        var contributorsAfterFailure = await ContributorIdsAsync(host, bucketId);
        var viewersAfterFailure = await ViewerIdsAsync(host, bucketId);

        Assert.Multiple(() =>
        {
            Assert.That(downStatus, Is.EqualTo(HttpStatusCode.BadGateway),
                "retryable: the indexer delivers again with backoff");
            Assert.That(contributorsAfterFailure, Is.EqualTo(new[] { "system:voting" }),
                "the grant transaction rolled back; only the committed message's poster stays");
            Assert.That(viewersAfterFailure, Is.Empty);
        });

        host.Indexer.ThrowOnHolders = false;
        var (retryStatus, retry) = await PostAsync(
            host.Client, "/webhooks/spv_lawyer_elected_devnet", LawyerElectedJson());

        var contributors = await ContributorIdsAsync(host, bucketId);
        var viewers = await ViewerIdsAsync(host, bucketId);
        var messageCount = await host.WithDbAsync(async db =>
            await Task.FromResult(db.Messages.Count(m => m.BucketId == bucketId)));

        Assert.Multiple(() =>
        {
            Assert.That(retryStatus, Is.EqualTo(HttpStatusCode.OK));
            Assert.That(retry.GetProperty("status").GetString(), Is.EqualTo("ignored"),
                "the committed announcement is duplicate-guarded…");
            Assert.That(retry.GetProperty("lawyerContributorAdded").GetBoolean(), Is.True);
            Assert.That(contributors, Does.Contain("LawyerWallet111"),
                "…while the grants complete");
            Assert.That(viewers, Is.EqualTo(new[] { holderKey }));
            Assert.That(messageCount, Is.EqualTo(1), "the announcement is not re-posted");
        });
    }

    // ---- letting_agent_appointed: the agent's write access -----------------------------------

    private static string AgentAppointedJson(string? winnerEntry = "WinnerEntry111")
    {
        return new JsonObject
        {
            ["event"] = "letting_agent_appointed",
            ["asset_id"] = 999,
            ["letting"] = "LettingPDA111",
            ["property"] = AssetPda,
            ["winner_entry"] = winnerEntry,
            ["slot"] = 487_800_000,
            ["tx_signature"] = "AgentTx1111111111111111111111111111111111111111111",
            ["block_time"] = "2026-09-05T08:00:00+00:00",
            ["program"] = "property"
        }.ToJsonString();
    }

    [Test]
    public async Task Agent_appointed_adds_the_agent_as_a_contributor_and_finds_the_namespace_by_pda()
    {
        await using var host = await WebhooksHost.StartAsync();
        var bucketId = await SeedLegalBucketAsync(host);
        // The entry PDA resolves to the agent's wallet through the indexer's lettingAgents mirror.
        host.Indexer.LettingAgentWallet = "AgentWallet111";

        // The property program's asset_id is NOT assumed to equal the marketplace listing id:
        // a deliberately different id (999) must still resolve via the PropertyAsset PDA fallback.
        var (status, body) = await PostAsync(
            host.Client, "/webhooks/letting_agent_appointed_devnet", AgentAppointedJson());

        var contributors = await ContributorIdsAsync(host, bucketId);

        Assert.Multiple(() =>
        {
            Assert.That(status, Is.EqualTo(HttpStatusCode.OK));
            Assert.That(body.GetProperty("status").GetString(), Is.EqualTo("agent_added"));
            Assert.That(body.GetProperty("agent").GetString(), Is.EqualTo("AgentWallet111"));
            Assert.That(body.GetProperty("bucketId").GetInt64(), Is.EqualTo(bucketId));
            Assert.That(contributors, Is.EqualTo(new[] { "AgentWallet111" }),
                "the agent gets WRITE on the legal bucket");
        });

        // Redelivery: the contributor-add returns the existing row — 200, no duplicate.
        var (secondStatus, _) = await PostAsync(
            host.Client, "/webhooks/letting_agent_appointed_devnet", AgentAppointedJson());
        var afterRedelivery = await ContributorIdsAsync(host, bucketId);
        Assert.Multiple(() =>
        {
            Assert.That(secondStatus, Is.EqualTo(HttpStatusCode.OK));
            Assert.That(afterRedelivery.Count(c => c == "AgentWallet111"), Is.EqualTo(1));
        });
    }

    [Test]
    public async Task Agent_appointed_502s_when_the_agent_wallet_cannot_be_resolved()
    {
        await using var host = await WebhooksHost.StartAsync();
        var bucketId = await SeedLegalBucketAsync(host);

        // The indexer answered but has no row for the entry PDA.
        host.Indexer.LettingAgentWallet = null;
        var (missingStatus, _) = await PostAsync(
            host.Client, "/webhooks/letting_agent_appointed_devnet", AgentAppointedJson());

        // The indexer cannot answer at all.
        host.Indexer.ThrowOnQuery = true;
        var (downStatus, _) = await PostAsync(
            host.Client, "/webhooks/letting_agent_appointed_devnet", AgentAppointedJson());

        var contributors = await ContributorIdsAsync(host, bucketId);

        Assert.Multiple(() =>
        {
            Assert.That(missingStatus, Is.EqualTo(HttpStatusCode.BadGateway),
                "retryable: the write-grant is never silently skipped");
            Assert.That(downStatus, Is.EqualTo(HttpStatusCode.BadGateway));
            Assert.That(contributors, Is.Empty);
        });
    }

    [Test]
    public async Task Agent_appointed_without_a_winner_entry_is_ignored()
    {
        await using var host = await WebhooksHost.StartAsync();
        var bucketId = await SeedLegalBucketAsync(host);
        host.Indexer.LettingAgentWallet = "AgentWallet111";

        var (status, body) = await PostAsync(
            host.Client, "/webhooks/letting_agent_appointed_devnet", AgentAppointedJson(winnerEntry: null));

        var contributors = await ContributorIdsAsync(host, bucketId);

        Assert.Multiple(() =>
        {
            Assert.That(status, Is.EqualTo(HttpStatusCode.OK));
            Assert.That(body.GetProperty("status").GetString(), Is.EqualTo("ignored"));
            Assert.That(body.GetProperty("acted").GetBoolean(), Is.False);
            Assert.That(contributors, Is.Empty, "a failed-open round seats no agent to add");
        });
    }

    [Test]
    public async Task Shares_transferred_adds_the_buyer_viewer_and_removes_the_seller()
    {
        await using var host = await WebhooksHost.StartAsync();
        var bucketId = await SeedLegalBucketAsync(host);
        var buyerKey = "0x" + new string('a', 64);
        var sellerKey = "0x" + new string('b', 64);
        await SeedProfileAsync(host, "BuyerWallet111", buyerKey);
        await SeedProfileAsync(host, "SellerWallet111", sellerKey);
        await SeedViewerAsync(host, bucketId, sellerKey);

        var (status, body) = await PostAsync(
            host.Client, "/webhooks/shares_transferred_devnet", TransferJson());

        var viewers = await ViewerIdsAsync(host, bucketId);

        Assert.Multiple(() =>
        {
            Assert.That(status, Is.EqualTo(HttpStatusCode.OK));
            Assert.That(body.GetProperty("status").GetString(), Is.EqualTo("processed"));
            Assert.That(body.GetProperty("buyerViewerAdded").GetBoolean(), Is.True);
            Assert.That(body.GetProperty("sellerViewerRemoved").GetBoolean(), Is.True);
            Assert.That(viewers, Is.EqualTo(new[] { buyerKey }),
                "the seller is out and the buyer is in");
        });
    }

    [Test]
    public async Task Shares_transferred_skips_a_side_whose_wallet_has_no_profile_key()
    {
        await using var host = await WebhooksHost.StartAsync();
        var bucketId = await SeedLegalBucketAsync(host);
        var sellerKey = "0x" + new string('b', 64);
        await SeedProfileAsync(host, "SellerWallet111", sellerKey);
        await SeedViewerAsync(host, bucketId, sellerKey);
        // No profile for the buyer wallet at all.

        var (status, body) = await PostAsync(
            host.Client, "/webhooks/shares_transferred_devnet", TransferJson());

        var viewers = await ViewerIdsAsync(host, bucketId);

        Assert.Multiple(() =>
        {
            Assert.That(status, Is.EqualTo(HttpStatusCode.OK), "a missing key is a skip, never an error");
            Assert.That(body.GetProperty("buyerViewerAdded").GetBoolean(), Is.False);
            Assert.That(body.GetProperty("buyerNote").GetString(), Does.Contain("no profile X25519 key"));
            Assert.That(body.GetProperty("sellerViewerRemoved").GetBoolean(), Is.True);
            Assert.That(viewers, Is.Empty);
        });
    }

    [Test]
    public async Task Shares_transferred_redelivery_is_idempotent()
    {
        await using var host = await WebhooksHost.StartAsync();
        var bucketId = await SeedLegalBucketAsync(host);
        var buyerKey = "0x" + new string('a', 64);
        await SeedProfileAsync(host, "BuyerWallet111", buyerKey);

        await PostAsync(host.Client, "/webhooks/shares_transferred_devnet", TransferJson());
        var (secondStatus, _) = await PostAsync(
            host.Client, "/webhooks/shares_transferred_devnet", TransferJson());

        var viewers = await ViewerIdsAsync(host, bucketId);

        Assert.Multiple(() =>
        {
            Assert.That(secondStatus, Is.EqualTo(HttpStatusCode.OK));
            Assert.That(viewers, Is.EqualTo(new[] { buyerKey }), "no duplicate viewer row");
        });
    }

    [Test]
    public async Task Shares_transferred_resolves_a_migrated_wallet_through_wallet_migrations()
    {
        await using var host = await WebhooksHost.StartAsync();
        var bucketId = await SeedLegalBucketAsync(host);
        // The seller's profile still sits under its legacy SS58 address; the on-chain wallet is
        // the Solana one, linked by a WalletMigrations row.
        var sellerKey = new string('b', 64);
        await host.WithProfileDbAsync(async db =>
        {
            db.Profiles.Add(new Profile { Ss58Address = "LegacySs58Seller", X25519Key = sellerKey });
            db.WalletMigrations.Add(new WalletMigration
            {
                Ss58Address = "LegacySs58Seller",
                SolanaAddress = "SellerWallet111"
            });
            await db.SaveChangesAsync();
            return true;
        });
        await SeedViewerAsync(host, bucketId, sellerKey);

        var (status, body) = await PostAsync(
            host.Client, "/webhooks/shares_transferred_devnet", TransferJson());

        var viewers = await ViewerIdsAsync(host, bucketId);

        Assert.Multiple(() =>
        {
            Assert.That(status, Is.EqualTo(HttpStatusCode.OK));
            Assert.That(body.GetProperty("sellerViewerRemoved").GetBoolean(), Is.True);
            Assert.That(viewers, Is.Empty);
        });
    }

    [Test]
    public async Task Shares_transferred_self_transfer_changes_nothing()
    {
        await using var host = await WebhooksHost.StartAsync();
        var bucketId = await SeedLegalBucketAsync(host);
        var key = "0x" + new string('a', 64);
        await SeedProfileAsync(host, "SameWallet111", key);

        var json = new JsonObject
        {
            ["event"] = "property_shares_transferred",
            ["kind"] = "direct_send",
            ["property"] = AssetPda,
            ["from"] = "SameWallet111",
            ["to"] = "SameWallet111"
        }.ToJsonString();

        var (status, body) = await PostAsync(host.Client, "/webhooks/shares_transferred_devnet", json);
        var viewers = await ViewerIdsAsync(host, bucketId);

        Assert.Multiple(() =>
        {
            Assert.That(status, Is.EqualTo(HttpStatusCode.OK));
            Assert.That(body.GetProperty("status").GetString(), Is.EqualTo("ignored"));
            Assert.That(viewers, Is.Empty, "an add followed by a remove would wipe the access");
        });
    }

    // ---- property_holder_changed: the primary-market holder lifecycle -------------------------

    private static string HolderChangedJson(string action, string investor, string reason = "reserve_shares")
    {
        return new JsonObject
        {
            ["event"] = "property_holder_changed",
            ["action"] = action,
            ["reason"] = reason,
            ["investor"] = investor,
            ["listing"] = "ListingPDA111",
            ["property"] = AssetPda,
            ["listing_id"] = ListingId,
            ["amount"] = action == "added" ? 5 : null,
            ["slot"] = 487_550_000,
            ["tx_signature"] = $"HolderTx{Guid.NewGuid():N}",
            ["block_time"] = "2026-08-27T10:00:00+00:00",
            ["program"] = "marketplace"
        }.ToJsonString();
    }

    [Test]
    public async Task Holder_changed_adds_the_investor_as_a_viewer()
    {
        await using var host = await WebhooksHost.StartAsync();
        var bucketId = await SeedLegalBucketAsync(host);
        var key = "0x" + new string('c', 64);
        await SeedProfileAsync(host, "InvestorWallet111", key);

        var (status, body) = await PostAsync(
            host.Client, "/webhooks/property_holder_changed_devnet",
            HolderChangedJson("added", "InvestorWallet111"));

        var viewers = await ViewerIdsAsync(host, bucketId);

        Assert.Multiple(() =>
        {
            Assert.That(status, Is.EqualTo(HttpStatusCode.OK));
            Assert.That(body.GetProperty("status").GetString(), Is.EqualTo("processed"));
            Assert.That(body.GetProperty("action").GetString(), Is.EqualTo("added"));
            Assert.That(viewers, Is.EqualTo(new[] { key }));
        });
    }

    [Test]
    public async Task Holder_changed_removes_the_investor_viewer()
    {
        await using var host = await WebhooksHost.StartAsync();
        var bucketId = await SeedLegalBucketAsync(host);
        var key = "0x" + new string('c', 64);
        await SeedProfileAsync(host, "InvestorWallet111", key);
        await SeedViewerAsync(host, bucketId, key);

        var (status, body) = await PostAsync(
            host.Client, "/webhooks/property_holder_changed_devnet",
            HolderChangedJson("removed", "InvestorWallet111", reason: "unreserve_shares"));

        var viewers = await ViewerIdsAsync(host, bucketId);

        Assert.Multiple(() =>
        {
            Assert.That(status, Is.EqualTo(HttpStatusCode.OK));
            Assert.That(body.GetProperty("action").GetString(), Is.EqualTo("removed"));
            Assert.That(viewers, Is.Empty);
        });
    }

    [Test]
    public async Task Holder_changed_defers_while_the_bucket_is_missing_not_a_retry_storm()
    {
        await using var host = await WebhooksHost.StartAsync();
        await SeedNamespaceAsync(host.Client);

        // The whole sale phase happens before the bucket exists: the event is ACKNOWLEDGED as
        // deferred (the claim-started reconciliation covers it), never a 404 retry loop.
        var (status, body) = await PostAsync(
            host.Client, "/webhooks/property_holder_changed_devnet",
            HolderChangedJson("added", "InvestorWallet111"));

        var buckets = await host.BucketsAsync();

        Assert.Multiple(() =>
        {
            Assert.That(status, Is.EqualTo(HttpStatusCode.OK));
            Assert.That(body.GetProperty("status").GetString(), Is.EqualTo("deferred"));
            Assert.That(buckets, Is.Empty);
        });
    }

    [Test]
    public async Task Holder_changed_skips_a_wallet_without_a_profile_key()
    {
        await using var host = await WebhooksHost.StartAsync();
        var bucketId = await SeedLegalBucketAsync(host);

        var (status, body) = await PostAsync(
            host.Client, "/webhooks/property_holder_changed_devnet",
            HolderChangedJson("added", "NoProfileWallet999"));

        var viewers = await ViewerIdsAsync(host, bucketId);

        Assert.Multiple(() =>
        {
            Assert.That(status, Is.EqualTo(HttpStatusCode.OK), "a missing key is a skip, never an error");
            Assert.That(body.GetProperty("status").GetString(), Is.EqualTo("skipped"));
            Assert.That(viewers, Is.Empty);
        });
    }

    [Test]
    public async Task Claim_started_reconciles_the_current_holders_into_the_bucket()
    {
        await using var host = await WebhooksHost.StartAsync();
        await SeedNamespaceAsync(host.Client);
        // Two current holders at claim time: one with a registered key, one without.
        var keyedWallet = "HolderWallet111";
        var keyedKey = "0x" + new string('d', 64);
        await SeedProfileAsync(host, keyedWallet, keyedKey);
        host.Indexer.HolderWallets = new[] { keyedWallet, "KeylessWallet222" };

        var (status, body) = await PostAsync(
            host.Client, "/webhooks/property_claim_started_devnet", ClaimStartedJson());

        var buckets = await host.BucketsAsync();
        var viewers = await ViewerIdsAsync(host, buckets[0].BucketId);

        Assert.Multiple(() =>
        {
            Assert.That(status, Is.EqualTo(HttpStatusCode.OK));
            Assert.That(body.GetProperty("status").GetString(), Is.EqualTo("created"));
            Assert.That(body.GetProperty("holdersAdded").GetInt32(), Is.EqualTo(1));
            Assert.That(body.GetProperty("holdersSkippedNoKey").GetInt32(), Is.EqualTo(1));
            Assert.That(viewers, Is.EqualTo(new[] { keyedKey }));
        });
    }

    [Test]
    public async Task Claim_started_502s_and_rolls_the_bucket_back_when_the_indexer_is_down()
    {
        await using var host = await WebhooksHost.StartAsync();
        await SeedNamespaceAsync(host.Client);
        host.Indexer.ThrowOnQuery = true;

        var (status, _) = await PostAsync(
            host.Client, "/webhooks/property_claim_started_devnet", ClaimStartedJson());

        var buckets = await host.BucketsAsync();

        Assert.Multiple(() =>
        {
            Assert.That(status, Is.EqualTo(HttpStatusCode.BadGateway),
                "retryable: the indexer retries with backoff");
            Assert.That(buckets, Is.Empty,
                "the bucket creation must roll back with the failed reconciliation");
        });
    }

    private const string BuyerWallet = "BuyerWallet111";
    private const string SellerWallet = "SellerWallet111";

    private static string TransferJson()
    {
        return new JsonObject
        {
            ["event"] = "property_shares_transferred",
            ["kind"] = "relisted_purchase",
            ["property"] = AssetPda,
            ["listing"] = "ListingPDA111",
            ["from"] = SellerWallet,
            ["to"] = BuyerWallet,
            ["amount"] = 3,
            ["asset_id"] = ListingId,
            ["offer"] = null,
            ["slot"] = 487_900_000,
            ["tx_signature"] = "TransferTx11111111111111111111111111111111111111111",
            ["block_time"] = "2026-09-10T11:00:00+00:00",
            ["program"] = "marketplace"
        }.ToJsonString();
    }

    private static async Task SeedProfileAsync(WebhooksHost host, string wallet, string x25519Key)
    {
        await host.WithProfileDbAsync(async db =>
        {
            db.Profiles.Add(new Profile { Ss58Address = wallet, X25519Key = x25519Key });
            await db.SaveChangesAsync();
            return true;
        });
    }

    private static async Task SeedViewerAsync(WebhooksHost host, long bucketId, string viewerKey)
    {
        await host.WithDbAsync(async db =>
        {
            db.BucketViewers.Add(new BucketViewer { BucketId = bucketId, ViewerId = viewerKey });
            await db.SaveChangesAsync();
            return true;
        });
    }

    private static async Task<List<string>> ViewerIdsAsync(WebhooksHost host, long bucketId)
    {
        return await host.WithDbAsync(async db =>
            await Task.FromResult(
                db.BucketViewers.Where(v => v.BucketId == bucketId).Select(v => v.ViewerId).ToList()));
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
    public async Task Lifecycle_webhooks_404_while_the_namespace_is_missing()
    {
        await using var host = await WebhooksHost.StartAsync();

        var elected = new JsonObject
        {
            ["event"] = "spv_lawyer_elected",
            ["listing_id"] = ListingId,
            ["property"] = AssetPda
        }.ToJsonString();
        var appointed = AgentAppointedJson();
        var transferred = new JsonObject
        {
            ["event"] = "property_shares_transferred",
            ["kind"] = "direct_send",
            ["property"] = AssetPda,
            ["from"] = "SellerWallet111",
            ["to"] = "BuyerWallet111"
        }.ToJsonString();

        // With a resolvable election row the namespace lookup is what fails.
        host.Indexer.SpvElection = new SpvElectionInfo(1, 1_790_000_000, 1, "LawyerWallet111");
        host.Indexer.LettingAgentWallet = "AgentWallet111";

        var (electedStatus, _) = await PostAsync(host.Client, "/webhooks/spv_lawyer_elected_devnet", elected);
        var (appointedStatus, _) = await PostAsync(
            host.Client, "/webhooks/letting_agent_appointed_devnet", appointed);
        var (transferredStatus, _) = await PostAsync(host.Client, "/webhooks/shares_transferred_devnet", transferred);

        Assert.Multiple(() =>
        {
            Assert.That(electedStatus, Is.EqualTo(HttpStatusCode.NotFound));
            Assert.That(appointedStatus, Is.EqualTo(HttpStatusCode.NotFound));
            Assert.That(transferredStatus, Is.EqualTo(HttpStatusCode.NotFound));
        });
    }
}
