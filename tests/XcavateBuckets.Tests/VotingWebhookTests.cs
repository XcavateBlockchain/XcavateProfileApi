using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using XcavateBuckets.Domain.Entities;
using XcavateProfileApi.Services.Indexer;

namespace XcavateBuckets.Tests;

/// <summary>
/// The seven voting webhooks (terms vote open/finalize, agent election open, proposal
/// create/finalize, challenge create/finalize) driven against the real controller and bucket
/// domain services over SQLite, like <see cref="PropertyLifecycleWebhookTests"/> does for the
/// lifecycle events. The indexer's payloads carry only instruction evidence, so the display
/// facts come from <see cref="WebhooksHost.Indexer"/>: these tests pin the enriched announcement
/// bodies (ISO expiries, resolved tallies, derived outcomes), the auto-approval skip, the 502
/// retry contract when the indexer cannot answer, the redelivery dedup guard, the system
/// identity + bucket unlock, and the deal-executed eviction recovery.
/// </summary>
[TestFixture]
public class VotingWebhookTests
{
    private const long ListingId = 12;
    private const string AssetPda = "AssetPDA111";
    private const long AssetSlot = 487_500_123;
    private const long Expiry = 1_790_000_000;
    private const string DocumentsHash = "9v4Wz5kGQrjqbBhDLzLzT9ckLd5dvNozhKCzBSs2YNez";
    private const string ProposalPda = "ProposalPDA111";
    private const string ChallengePda = "ChallengePDA111";

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

    /// <summary>Seeds the namespace AND the legal bucket (the claim webhook), returning the bucket id.</summary>
    private static async Task<long> SeedLegalBucketAsync(WebhooksHost host)
    {
        await SeedNamespaceAsync(host.Client);
        var claim = new JsonObject
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

        var (status, body) = await PostAsync(host.Client, "/webhooks/property_claim_started_devnet", claim);
        Assert.That(status, Is.EqualTo(HttpStatusCode.OK), "the claim webhook seed must succeed");

        return body.GetProperty("bucketId").GetInt64();
    }

    // ---- The payloads, verbatim as the indexer POSTs them (instruction evidence only) ----------

    private static string SpvCaseClaimedJson(int round = 1)
    {
        return new JsonObject
        {
            ["event"] = "spv_case_claimed",
            ["property_id"] = ListingId,
            ["round"] = round,
            ["costs"] = 500_000,
            ["lawyer"] = "LawyerWallet111",
            ["listing"] = "ListingPDA111",
            ["property"] = AssetPda,
            ["candidacy"] = "CandidacyPDA111",
            ["slot"] = 487_700_100,
            ["tx_signature"] = "ClaimCaseTx11111111111111111111111111111111111111",
            ["block_time"] = "2026-10-01T10:00:00+00:00",
            ["program"] = "marketplace"
        }.ToJsonString();
    }

    private static string SpvLawyerElectedJson()
    {
        return new JsonObject
        {
            ["event"] = "spv_lawyer_elected",
            ["listing_id"] = ListingId,
            ["listing"] = "ListingPDA111",
            ["property"] = AssetPda,
            ["winner_registry"] = "WinnerRegistryPDA111",
            ["slot"] = 487_700_200,
            ["tx_signature"] = "ElectedTx1111111111111111111111111111111111111111",
            ["block_time"] = "2026-10-02T10:00:00+00:00",
            ["program"] = "marketplace"
        }.ToJsonString();
    }

    private static string ElectionOpenedJson(int round = 1)
    {
        return new JsonObject
        {
            ["event"] = "agent_election_opened",
            ["property_id"] = ListingId,
            ["round"] = round,
            ["agent"] = "AgentWallet111",
            ["property"] = AssetPda,
            ["letting"] = "LettingPDA111",
            ["candidacy"] = "CandidacyPDA111",
            ["slot"] = 487_700_300,
            ["tx_signature"] = "ElectionTx1111111111111111111111111111111111111111",
            ["block_time"] = "2026-10-03T10:00:00+00:00",
            ["program"] = "property"
        }.ToJsonString();
    }

    private static string ProposalCreatedJson(long proposalId = 7)
    {
        return new JsonObject
        {
            ["event"] = "proposal_created",
            ["property_id"] = ListingId,
            ["proposal_id"] = proposalId,
            ["amount"] = 250_000,
            ["details_hash"] = DocumentsHash,
            ["letting"] = "LettingPDA111",
            ["proposal"] = ProposalPda,
            ["slot"] = 487_700_400,
            ["tx_signature"] = "ProposalTx111111111111111111111111111111111111111",
            ["block_time"] = "2026-10-04T10:00:00+00:00",
            ["program"] = "property"
        }.ToJsonString();
    }

    private static string ProposalFinalizedJson()
    {
        return new JsonObject
        {
            ["event"] = "proposal_finalized",
            ["property_id"] = ListingId,
            ["letting"] = "LettingPDA111",
            ["property"] = AssetPda,
            ["proposal"] = ProposalPda,
            ["slot"] = 487_700_500,
            ["tx_signature"] = "ProposalFinalTx1111111111111111111111111111111111",
            ["block_time"] = "2026-10-05T10:00:00+00:00",
            ["program"] = "property"
        }.ToJsonString();
    }

    private static string ChallengeCreatedJson(long challengeId = 3)
    {
        return new JsonObject
        {
            ["event"] = "challenge_created",
            ["property_id"] = ListingId,
            ["challenge_id"] = challengeId,
            ["challenger"] = "ChallengerWallet111",
            ["letting"] = "LettingPDA111",
            ["challenge"] = ChallengePda,
            ["slot"] = 487_700_600,
            ["tx_signature"] = "ChallengeTx11111111111111111111111111111111111111",
            ["block_time"] = "2026-10-06T10:00:00+00:00",
            ["program"] = "property"
        }.ToJsonString();
    }

    private static string ChallengeFinalizedJson()
    {
        return new JsonObject
        {
            ["event"] = "challenge_finalized",
            ["property_id"] = ListingId,
            ["letting"] = "LettingPDA111",
            ["property"] = AssetPda,
            ["challenge"] = ChallengePda,
            ["agent_entry"] = "AgentEntryPDA111",
            ["slot"] = 487_700_700,
            ["tx_signature"] = "ChallengeFinalTx111111111111111111111111111111111",
            ["block_time"] = "2026-10-07T10:00:00+00:00",
            ["program"] = "property"
        }.ToJsonString();
    }

    // ---- Reading back what was posted ------------------------------------------------------------

    private static async Task<List<Message>> MessagesAsync(WebhooksHost host, long bucketId)
    {
        return await host.WithDbAsync(async db =>
            await Task.FromResult(
                db.Messages.Where(m => m.BucketId == bucketId)
                    .OrderBy(m => m.MessageId)
                    .ToList()));
    }

    private static async Task<List<string>> ContributorIdsAsync(WebhooksHost host, long bucketId)
    {
        return await host.WithDbAsync(async db =>
            await Task.FromResult(
                db.BucketContributors.Where(c => c.BucketId == bucketId)
                    .Select(c => c.SubjectId)
                    .ToList()));
    }

    /// <summary>The stored plaintext body parsed back, detached from its JsonDocument.</summary>
    private static JsonElement BodyOf(Message message)
    {
        using var doc = JsonDocument.Parse(message.IpfsContent!);
        return doc.RootElement.Clone();
    }

    /// <summary>The property names of the body JSON, so tests can pin the exact contract shape.</summary>
    private static string[] BodyKeys(Message message)
    {
        return BodyOf(message).EnumerateObject().Select(p => p.Name).ToArray();
    }

    private static string ExpectedHash(string body) =>
        "0x" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(body)));

    /// <summary>The default election row the indexer "says" for ListingId.</summary>
    private static SpvElectionInfo SpvElection(int round = 1, string? spvLawyer = null) =>
        new(round, Expiry, 1, spvLawyer ?? "11111111111111111111111111111111");

    // ---- spv_case_claimed / spv_lawyer_elected (the "terms" flow) --------------------------------

    [Test]
    public async Task Spv_case_claimed_posts_the_enriched_open_message_and_unlocks_the_bucket()
    {
        await using var host = await WebhooksHost.StartAsync();
        var bucketId = await SeedLegalBucketAsync(host);
        host.Indexer.SpvElection = SpvElection();

        var (status, body) = await PostAsync(
            host.Client, "/webhooks/spv_case_claimed_devnet", SpvCaseClaimedJson());

        var messages = await MessagesAsync(host, bucketId);
        var bucket = (await host.BucketsAsync()).Single();
        var contributors = await ContributorIdsAsync(host, bucketId);

        Assert.Multiple(() =>
        {
            Assert.That(status, Is.EqualTo(HttpStatusCode.OK));
            Assert.That(body.GetProperty("status").GetString(), Is.EqualTo("posted"));
            Assert.That(body.GetProperty("bucketId").GetInt64(), Is.EqualTo(bucketId));

            var message = messages.Single();
            Assert.That(message.Tag, Is.EqualTo("voting/terms"));
            Assert.That(message.Contributor, Is.EqualTo("system:voting"));
            Assert.That(message.ContentType, Is.EqualTo("application/json"));
            Assert.That(message.Reference, Is.EqualTo("voting/terms/12/round/1/open"));
            Assert.That(message.ContentHash, Is.EqualTo(ExpectedHash(message.IpfsContent!)));

            // The exact open shape, with expiry ISO-8601 resolved server-side.
            Assert.That(BodyKeys(message), Is.EqualTo(new[]
            {
                "kind", "listingId", "round", "expiresAt", "status"
            }));
            var posted = BodyOf(message);
            Assert.That(posted.GetProperty("kind").GetString(), Is.EqualTo("terms"));
            Assert.That(posted.GetProperty("listingId").GetInt64(), Is.EqualTo(ListingId));
            Assert.That(posted.GetProperty("round").GetInt32(), Is.EqualTo(1));
            Assert.That(posted.GetProperty("expiresAt").GetString(),
                Is.EqualTo(IndexerClient.Iso8601(Expiry)));
            Assert.That(posted.GetProperty("status").GetString(), Is.EqualTo("open"));

            // Posting unlocked the bucket and installed the system identity as a contributor.
            Assert.That(bucket.IsWritable, Is.True);
            Assert.That(contributors, Does.Contain("system:voting"));
        });
    }

    [Test]
    public async Task Spv_case_claimed_redelivery_is_ignored_not_duplicated()
    {
        await using var host = await WebhooksHost.StartAsync();
        var bucketId = await SeedLegalBucketAsync(host);
        host.Indexer.SpvElection = SpvElection();

        var (firstStatus, _) = await PostAsync(
            host.Client, "/webhooks/spv_case_claimed_devnet", SpvCaseClaimedJson());
        // Later candidacies of the SAME round rebuild the same body too.
        var (secondStatus, second) = await PostAsync(
            host.Client, "/webhooks/spv_case_claimed_devnet", SpvCaseClaimedJson());

        var messages = await MessagesAsync(host, bucketId);

        Assert.Multiple(() =>
        {
            Assert.That(firstStatus, Is.EqualTo(HttpStatusCode.OK));
            Assert.That(secondStatus, Is.EqualTo(HttpStatusCode.OK));
            Assert.That(second.GetProperty("status").GetString(), Is.EqualTo("ignored"));
            Assert.That(messages.Count, Is.EqualTo(1));
        });
    }

    [Test]
    public async Task A_new_election_round_posts_again()
    {
        await using var host = await WebhooksHost.StartAsync();
        var bucketId = await SeedLegalBucketAsync(host);

        host.Indexer.SpvElection = SpvElection();
        await PostAsync(host.Client, "/webhooks/spv_case_claimed_devnet", SpvCaseClaimedJson());

        // The round failed and a new one opened: a different body posts again.
        host.Indexer.SpvElection = SpvElection(round: 2);
        var (status, body) = await PostAsync(
            host.Client, "/webhooks/spv_case_claimed_devnet", SpvCaseClaimedJson(round: 2));

        var messages = await MessagesAsync(host, bucketId);

        Assert.Multiple(() =>
        {
            Assert.That(status, Is.EqualTo(HttpStatusCode.OK));
            Assert.That(body.GetProperty("status").GetString(), Is.EqualTo("posted"));
            Assert.That(messages.Count, Is.EqualTo(2));
            Assert.That(BodyOf(messages[1]).GetProperty("round").GetInt32(), Is.EqualTo(2));
            Assert.That(messages[1].Reference, Is.EqualTo("voting/terms/12/round/2/open"));
        });
    }

    [Test]
    public async Task Spv_lawyer_elected_posts_ratified_when_the_seat_is_filled()
    {
        await using var host = await WebhooksHost.StartAsync();
        var bucketId = await SeedLegalBucketAsync(host);
        host.Indexer.SpvElection = SpvElection(round: 1, spvLawyer: "LawyerWallet111");

        var (status, _) = await PostAsync(
            host.Client, "/webhooks/spv_lawyer_elected_devnet", SpvLawyerElectedJson());

        var message = (await MessagesAsync(host, bucketId)).Single();
        var posted = BodyOf(message);

        Assert.Multiple(() =>
        {
            Assert.That(status, Is.EqualTo(HttpStatusCode.OK));
            Assert.That(message.Tag, Is.EqualTo("voting/result"));
            Assert.That(message.Reference, Is.EqualTo("voting/result/terms/12/round/1"));
            Assert.That(BodyKeys(message), Is.EqualTo(new[]
            {
                "kind", "listingId", "round", "status"
            }));
            Assert.That(posted.GetProperty("kind").GetString(), Is.EqualTo("terms"));
            Assert.That(posted.GetProperty("status").GetString(), Is.EqualTo("ratified"));
        });
    }

    [Test]
    public async Task Spv_lawyer_elected_posts_rejected_when_the_round_failed_open()
    {
        await using var host = await WebhooksHost.StartAsync();
        var bucketId = await SeedLegalBucketAsync(host);
        // The all-zero pubkey: nobody was seated.
        host.Indexer.SpvElection = SpvElection();

        var (status, _) = await PostAsync(
            host.Client, "/webhooks/spv_lawyer_elected_devnet", SpvLawyerElectedJson());

        var message = (await MessagesAsync(host, bucketId)).Single();

        Assert.Multiple(() =>
        {
            Assert.That(status, Is.EqualTo(HttpStatusCode.OK));
            Assert.That(BodyOf(message).GetProperty("status").GetString(), Is.EqualTo("rejected"));
        });
    }

    // ---- agent_election_opened ----------------------------------------------------------------------

    [Test]
    public async Task Agent_election_opened_posts_the_round_with_the_letting_expiry()
    {
        await using var host = await WebhooksHost.StartAsync();
        var bucketId = await SeedLegalBucketAsync(host);
        host.Indexer.PropertyLetting =
            new PropertyLettingInfo("AgentWallet111", Expiry, 1, 6000, 0);

        var (status, _) = await PostAsync(
            host.Client, "/webhooks/agent_election_opened_devnet", ElectionOpenedJson());

        var message = (await MessagesAsync(host, bucketId)).Single();
        var posted = BodyOf(message);

        Assert.Multiple(() =>
        {
            Assert.That(status, Is.EqualTo(HttpStatusCode.OK));
            Assert.That(message.Tag, Is.EqualTo("voting/election"));
            Assert.That(message.Reference, Is.EqualTo("voting/election/12/round/1/open"));
            Assert.That(BodyKeys(message), Is.EqualTo(new[]
            {
                "kind", "listingId", "round", "expiresAt", "quorumBps", "status"
            }));
            Assert.That(posted.GetProperty("kind").GetString(), Is.EqualTo("election"));
            Assert.That(posted.GetProperty("round").GetInt32(), Is.EqualTo(1));
            Assert.That(posted.GetProperty("expiresAt").GetString(),
                Is.EqualTo(IndexerClient.Iso8601(Expiry)));
            Assert.That(posted.GetProperty("quorumBps").GetInt32(), Is.EqualTo(6000));
        });
    }

    // ---- proposal_created / finalized -----------------------------------------------------------------

    [Test]
    public async Task Proposal_created_posts_the_open_message()
    {
        await using var host = await WebhooksHost.StartAsync();
        var bucketId = await SeedLegalBucketAsync(host);
        host.Indexer.OpenProposal = new ProposalInfo(7, Expiry, 4000, 6000);

        var (status, _) = await PostAsync(
            host.Client, "/webhooks/proposal_created_devnet", ProposalCreatedJson());

        var message = (await MessagesAsync(host, bucketId)).Single();
        var posted = BodyOf(message);

        Assert.Multiple(() =>
        {
            Assert.That(status, Is.EqualTo(HttpStatusCode.OK));
            Assert.That(message.Tag, Is.EqualTo("voting/proposal"));
            Assert.That(message.Reference, Is.EqualTo("voting/proposal/12/7/open"));
            Assert.That(BodyKeys(message), Is.EqualTo(new[]
            {
                "kind", "listingId", "id", "expiresAt", "quorumBps", "thresholdBps", "status"
            }));
            Assert.That(posted.GetProperty("id").GetInt64(), Is.EqualTo(7));
            Assert.That(posted.GetProperty("expiresAt").GetString(),
                Is.EqualTo(IndexerClient.Iso8601(Expiry)));
            Assert.That(posted.GetProperty("thresholdBps").GetInt32(), Is.EqualTo(6000));
        });
    }

    [Test]
    public async Task Proposal_created_skips_an_auto_approved_spend()
    {
        await using var host = await WebhooksHost.StartAsync();
        var bucketId = await SeedLegalBucketAsync(host);
        // No proposal row: the chain auto-approved the spend inside the same transaction.
        host.Indexer.OpenProposal = null;

        var (status, body) = await PostAsync(
            host.Client, "/webhooks/proposal_created_devnet", ProposalCreatedJson());

        var messages = await MessagesAsync(host, bucketId);

        Assert.Multiple(() =>
        {
            Assert.That(status, Is.EqualTo(HttpStatusCode.OK));
            Assert.That(body.GetProperty("status").GetString(), Is.EqualTo("ignored"));
            Assert.That(messages, Is.Empty, "no phantom vote is advertised");
        });
    }

    [Test]
    public async Task Proposal_finalized_derives_passed_from_the_frozen_tallies()
    {
        await using var host = await WebhooksHost.StartAsync();
        var bucketId = await SeedLegalBucketAsync(host);
        // 700 yes / 200 no / 100 abstain on a 1000-share asset: full turnout, yes over no.
        host.Indexer.FinalizedProposal =
            new ProposalResultInfo(7, 700, 200, 100, 4000, 6000);

        var (status, _) = await PostAsync(
            host.Client, "/webhooks/proposal_finalized_devnet", ProposalFinalizedJson());

        var message = (await MessagesAsync(host, bucketId)).Single();
        var posted = BodyOf(message);

        Assert.Multiple(() =>
        {
            Assert.That(status, Is.EqualTo(HttpStatusCode.OK));
            Assert.That(message.Tag, Is.EqualTo("voting/result"));
            Assert.That(message.Reference, Is.EqualTo("voting/result/proposal/12/7"));
            Assert.That(BodyKeys(message), Is.EqualTo(new[]
            {
                "kind", "listingId", "id", "status", "yes", "no", "abstain"
            }));
            Assert.That(posted.GetProperty("status").GetString(), Is.EqualTo("passed"));
            Assert.That(posted.GetProperty("yes").GetInt64(), Is.EqualTo(700));
            Assert.That(posted.GetProperty("abstain").GetInt64(), Is.EqualTo(100));
        });
    }

    [Test]
    public async Task Proposal_finalized_derives_failed_on_a_quorum_or_majority_miss()
    {
        await using var host = await WebhooksHost.StartAsync();
        var bucketId = await SeedLegalBucketAsync(host);
        // Only 400 of 1000 shares turned out, and no beats yes: failed on both counts.
        host.Indexer.FinalizedProposal =
            new ProposalResultInfo(7, 100, 300, 0, 6000, 0);

        var (status, _) = await PostAsync(
            host.Client, "/webhooks/proposal_finalized_devnet", ProposalFinalizedJson());

        var message = (await MessagesAsync(host, bucketId)).Single();

        Assert.Multiple(() =>
        {
            Assert.That(status, Is.EqualTo(HttpStatusCode.OK));
            Assert.That(BodyOf(message).GetProperty("status").GetString(), Is.EqualTo("failed"));
        });
    }

    // ---- challenge_created / finalized -----------------------------------------------------------------

    [Test]
    public async Task Challenge_created_posts_the_open_message()
    {
        await using var host = await WebhooksHost.StartAsync();
        var bucketId = await SeedLegalBucketAsync(host);
        host.Indexer.OpenChallenge = new ChallengeInfo(3, Expiry, 3000);

        var (status, _) = await PostAsync(
            host.Client, "/webhooks/challenge_created_devnet", ChallengeCreatedJson());

        var message = (await MessagesAsync(host, bucketId)).Single();
        var posted = BodyOf(message);

        Assert.Multiple(() =>
        {
            Assert.That(status, Is.EqualTo(HttpStatusCode.OK));
            Assert.That(message.Tag, Is.EqualTo("voting/challenge"));
            Assert.That(message.Reference, Is.EqualTo("voting/challenge/12/3/open"));
            Assert.That(BodyKeys(message), Is.EqualTo(new[]
            {
                "kind", "listingId", "id", "expiresAt", "quorumBps", "status"
            }));
            Assert.That(posted.GetProperty("kind").GetString(), Is.EqualTo("challenge"));
            Assert.That(posted.GetProperty("id").GetInt64(), Is.EqualTo(3));
            Assert.That(posted.GetProperty("expiresAt").GetString(),
                Is.EqualTo(IndexerClient.Iso8601(Expiry)));
        });
    }

    [Test]
    public async Task Challenge_finalized_derives_the_outcome_without_a_threshold()
    {
        await using var host = await WebhooksHost.StartAsync();
        var bucketId = await SeedLegalBucketAsync(host);
        // Full turnout of 1000, yes over no: passed (challenges have no high-tier threshold).
        host.Indexer.FinalizedChallenge =
            new ChallengeResultInfo(3, 550, 350, 100, 3000);

        var (status, _) = await PostAsync(
            host.Client, "/webhooks/challenge_finalized_devnet", ChallengeFinalizedJson());

        var message = (await MessagesAsync(host, bucketId)).Single();
        var posted = BodyOf(message);

        Assert.Multiple(() =>
        {
            Assert.That(status, Is.EqualTo(HttpStatusCode.OK));
            Assert.That(message.Tag, Is.EqualTo("voting/result"));
            Assert.That(message.Reference, Is.EqualTo("voting/result/challenge/12/3"));
            Assert.That(posted.GetProperty("status").GetString(), Is.EqualTo("passed"));
            Assert.That(posted.GetProperty("no").GetInt64(), Is.EqualTo(350));
        });
    }

    // ---- the 502 resolution contract -------------------------------------------------------------------

    [Test]
    public async Task A_vote_state_that_cannot_be_resolved_is_a_retryable_502()
    {
        await using var host = await WebhooksHost.StartAsync();
        var bucketId = await SeedLegalBucketAsync(host);

        // 1. The indexer simply has no row yet.
        host.Indexer.SpvElection = null;
        var (missingStatus, _) = await PostAsync(
            host.Client, "/webhooks/spv_case_claimed_devnet", SpvCaseClaimedJson());

        // 2. The indexer cannot answer at all.
        host.Indexer.ThrowOnQuery = true;
        var (downStatus, _) = await PostAsync(
            host.Client, "/webhooks/challenge_created_devnet", ChallengeCreatedJson());

        var messages = await MessagesAsync(host, bucketId);

        Assert.Multiple(() =>
        {
            Assert.That(missingStatus, Is.EqualTo(HttpStatusCode.BadGateway));
            Assert.That(downStatus, Is.EqualTo(HttpStatusCode.BadGateway));
            Assert.That(messages, Is.Empty, "nothing hollow was posted");
        });
    }

    // ---- lookup and validation contract ------------------------------------------------------------------

    [Test]
    public async Task Voting_webhooks_404_while_the_namespace_or_bucket_is_missing()
    {
        await using var host = await WebhooksHost.StartAsync();
        host.Indexer.SpvElection = SpvElection();

        var (noNamespace, _) = await PostAsync(
            host.Client, "/webhooks/spv_case_claimed_devnet", SpvCaseClaimedJson());
        Assert.That(noNamespace, Is.EqualTo(HttpStatusCode.NotFound));

        await SeedNamespaceAsync(host.Client);
        var (noBucket, _) = await PostAsync(
            host.Client, "/webhooks/spv_case_claimed_devnet", SpvCaseClaimedJson());
        Assert.That(noBucket, Is.EqualTo(HttpStatusCode.NotFound));
    }

    [Test]
    public async Task Voting_webhooks_400_on_a_wrong_event_name_or_missing_subject()
    {
        await using var host = await WebhooksHost.StartAsync();
        await SeedLegalBucketAsync(host);
        host.Indexer.SpvElection = SpvElection();

        var wrongEvent = SpvCaseClaimedJson().Replace("spv_case_claimed", "case_claimed");
        var (wrongEventStatus, _) = await PostAsync(
            host.Client, "/webhooks/spv_case_claimed_devnet", wrongEvent);

        var noPropertyId = SpvCaseClaimedJson()
            .Replace($"\"property_id\":{ListingId}", "\"property_id\":null");
        var (noIdStatus, _) = await PostAsync(
            host.Client, "/webhooks/spv_case_claimed_devnet", noPropertyId);

        Assert.Multiple(() =>
        {
            Assert.That(wrongEventStatus, Is.EqualTo(HttpStatusCode.BadRequest));
            Assert.That(noIdStatus, Is.EqualTo(HttpStatusCode.BadRequest));
        });
    }

    // ---- the system identity survives the settlement sweep ------------------------------------------------

    [Test]
    public async Task The_deal_executed_sweep_does_not_evict_the_system_poster_permanently()
    {
        await using var host = await WebhooksHost.StartAsync();
        var bucketId = await SeedLegalBucketAsync(host);

        // Pre-settlement: an SPV-case claim posts, installing the system contributor.
        host.Indexer.SpvElection = SpvElection();
        await PostAsync(host.Client, "/webhooks/spv_case_claimed_devnet", SpvCaseClaimedJson());

        // Settlement: the sweep removes every contributor and admin except the operator admin.
        var deal = new JsonObject
        {
            ["event"] = "deal_executed",
            ["listing_id"] = ListingId,
            ["listing"] = "ListingPDA111",
            ["property"] = AssetPda,
            ["slot"] = 487_800_000,
            ["tx_signature"] = "DealTx11111111111111111111111111111111111111111111",
            ["block_time"] = "2026-10-08T15:30:00+00:00",
            ["program"] = "marketplace"
        }.ToJsonString();
        var (dealStatus, _) = await PostAsync(host.Client, "/webhooks/deal_executed_devnet", deal);
        Assert.That(dealStatus, Is.EqualTo(HttpStatusCode.OK));

        var afterSweep = await ContributorIdsAsync(host, bucketId);
        Assert.That(afterSweep, Does.Not.Contain("system:voting"), "the sweep evicts it…");

        // …but the next (post-settlement) voting webhook re-adds it and posts normally.
        host.Indexer.OpenProposal = new ProposalInfo(7, Expiry, 4000, 0);
        var (status, _) = await PostAsync(
            host.Client, "/webhooks/proposal_created_devnet", ProposalCreatedJson());

        var afterRepost = await ContributorIdsAsync(host, bucketId);
        var messages = await MessagesAsync(host, bucketId);

        Assert.Multiple(() =>
        {
            Assert.That(status, Is.EqualTo(HttpStatusCode.OK));
            Assert.That(afterRepost, Does.Contain("system:voting"), "…and the next webhook re-adds it");
            Assert.That(messages.Count, Is.EqualTo(2));
            Assert.That(messages[1].Tag, Is.EqualTo("voting/proposal"));
        });
    }

    // ---- the on-chain pass rule, mirrored ---------------------------------------------------------------

    [Test]
    public void VotePassed_mirrors_the_chain_rule()
    {
        Assert.Multiple(() =>
        {
            // Full turnout, simple majority → pass.
            Assert.That(IndexerClient.VotePassed(700, 200, 100, 4000, null, 1000), Is.True);
            // Yes under no → fail, whatever the turnout.
            Assert.That(IndexerClient.VotePassed(200, 700, 100, 4000, null, 1000), Is.False);
            // Turnout exactly AT quorum is not over it (the property rule is strict >).
            Assert.That(IndexerClient.VotePassed(250, 150, 0, 4000, null, 1000), Is.False);
            // High-tier proposal: the yes/(yes+no) split must additionally reach the threshold.
            Assert.That(IndexerClient.VotePassed(500, 400, 100, 4000, 6000, 1000), Is.False);
            Assert.That(IndexerClient.VotePassed(620, 280, 100, 4000, 6000, 1000), Is.True);
        });
    }
}
