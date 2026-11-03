using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Amazon.S3;
using Microsoft.EntityFrameworkCore;
using XcavateBuckets.Domain;
using XcavateProfileApi.Services;
using XcavateProfileApi.Services.Indexer;

namespace XcavateBuckets.Tests;

[TestFixture]
public class LawyerReviewBucketWebhookTests
{
    private const long ListingId = 12;
    private const string AssetPda = "AssetPDA111";
    private const string SpvLawyer = "SpvLawyerWallet111";
    private const string DeveloperLawyer = "DeveloperLawyerWallet111";
    private const string HolderOne = "HolderWallet111";
    private const string HolderTwo = "HolderWallet222";
    private const string WebhookSecret = "lawyer-review-test-secret";
    private const string SystemDefaultPubkey = "11111111111111111111111111111111";
    private const string ReviewPrompt =
        "The SPV Lawyer and Developer Lawyer must now review each other's documents and finalize the terms. Please feel free to discuss any questions here.";
    private const string SpvFallback =
        "SPV Lawyer's terms document is not available. Please send it here.";
    private const string DeveloperFallback =
        "Developer Lawyer's terms document is not available. Please send it here.";
    internal static readonly byte[] SpvPdf = "%PDF-1.4 SPV terms"u8.ToArray();
    internal static readonly byte[] DeveloperPdf = "%PDF-1.4 developer terms"u8.ToArray();

    internal static async Task<(HttpStatusCode Status, string Body)> PostDeveloperAsync(
        WebhooksHost host,
        string? eventName = "developer_lawyer_assigned",
        string? developerLawyer = DeveloperLawyer,
        long? listingId = ListingId,
        string? presentedSecret = WebhookSecret,
        string? cluster = "devnet",
        long? propertyId = ListingId)
    {
        var json = new JsonObject
        {
            ["event"] = eventName,
            ["listing_id"] = listingId,
            ["property_id"] = propertyId,
            ["cluster"] = cluster,
            ["developer_lawyer"] = developerLawyer,
            ["developer_lawyer_registry"] = "DeveloperLawyerRegistry111",
            ["listing"] = "ListingPDA111",
            ["property"] = AssetPda,
            ["slot"] = 487_700_200,
            ["tx_signature"] = "DeveloperAssignmentTx111",
            ["block_time"] = "2026-10-01T10:05:00+00:00",
            ["program"] = "marketplace"
        }.ToJsonString();
        var path = "/webhooks/developer_lawyer_assigned_devnet";
        if (presentedSecret is not null)
        {
            path += $"?secret={Uri.EscapeDataString(presentedSecret)}";
        }
        using var response = await host.Client.PostAsync(
            path, new StringContent(json, Encoding.UTF8, "application/json"));
        return (response.StatusCode, await response.Content.ReadAsStringAsync());
    }

    internal static async Task<(HttpStatusCode Status, JsonElement Body)> PostDeveloperJsonAsync(
        WebhooksHost host, string? cluster = "devnet")
    {
        var (status, text) = await PostDeveloperAsync(host, cluster: cluster);
        return (status, JsonDocument.Parse(text).RootElement.Clone());
    }

    internal static async Task<(HttpStatusCode Status, JsonElement Body)> PostSpvAsync(
        WebhooksHost host, string? cluster = "devnet")
    {
        var json = new JsonObject
        {
            ["event"] = "spv_lawyer_elected",
            ["listing_id"] = ListingId,
            ["property_id"] = ListingId,
            ["cluster"] = cluster,
            ["listing"] = "ListingPDA111",
            ["property"] = AssetPda,
            ["winner_registry"] = "WinnerRegistry111",
            ["slot"] = 487_650_000,
            ["tx_signature"] = "SpvElectionTx111",
            ["block_time"] = "2026-10-01T10:00:00+00:00",
            ["program"] = "marketplace"
        }.ToJsonString();
        using var response = await host.Client.PostAsync(
            $"/webhooks/spv_lawyer_elected_devnet?secret={Uri.EscapeDataString(WebhookSecret)}",
            new StringContent(json, Encoding.UTF8, "application/json"));
        var text = await response.Content.ReadAsStringAsync();
        var body = response.StatusCode == HttpStatusCode.OK
            ? JsonDocument.Parse(text).RootElement.Clone()
            : default;
        return (response.StatusCode, body);
    }

    internal static async Task SeedNamespaceAsync(WebhooksHost host, string name = "42 Main Street")
    {
        var json = new JsonObject
        {
            ["event"] = "property_asset_registered",
            ["pubkey"] = AssetPda,
            ["listing_id"] = ListingId,
            ["name"] = name,
            ["metadata_uri"] = "https://metadata.test/42-main-street.json",
            ["share_mint"] = "ShareMint11111111111111111111111111111111111",
            ["slot"] = 487_500_123,
            ["tx_signature"] = "TxSignature11111111111111111111111111111111111111111",
            ["program"] = "marketplace"
        }.ToJsonString();
        using var response = await host.Client.PostAsync(
            $"/webhooks/init_property_asset_devnet?secret={Uri.EscapeDataString(WebhookSecret)}",
            new StringContent(json, Encoding.UTF8, "application/json"));
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
    }

    internal static async Task<long> SeedLegalBucketAsync(WebhooksHost host)
    {
        var json = new JsonObject
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
        using var response = await host.Client.PostAsync(
            $"/webhooks/property_claim_started_devnet?secret={Uri.EscapeDataString(WebhookSecret)}",
            new StringContent(json, Encoding.UTF8, "application/json"));
        var body = JsonSerializer.Deserialize<JsonElement>(await response.Content.ReadAsStringAsync());
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        return body.GetProperty("bucketId").GetInt64();
    }

    internal static async Task SeedPrerequisitesAsync(
        WebhooksHost host,
        IReadOnlyList<string>? holders = null,
        string? missingKey = null,
        bool spvDocument = true,
        bool developerDocument = true)
    {
        holders ??= [HolderOne, HolderTwo];
        host.Indexer.SpvElection = new SpvElectionInfo(1, 1_790_000_000, 1, SpvLawyer);
        host.Indexer.ListingLawyers = new ListingLawyersInfo(SpvLawyer, DeveloperLawyer);
        host.Indexer.HolderWallets = holders;
        host.Indexer.ShareHolderWallets = holders;
        foreach (var wallet in holders.Append(SpvLawyer).Append(DeveloperLawyer).Distinct())
        {
            if (wallet != missingKey)
            {
                await host.SeedViewerKeyAsync(wallet, KeyFor(wallet));
            }
        }
        if (spvDocument)
        {
            await host.SeedTermsDocumentAsync(SpvLawyer, SpvPdf);
        }
        if (developerDocument)
        {
            await host.SeedTermsDocumentAsync(DeveloperLawyer, DeveloperPdf);
        }
    }

    private static string KeyFor(string wallet) => wallet switch
    {
        SpvLawyer => TestDb.Key32,
        DeveloperLawyer => TestDb.OtherKey32,
        HolderOne => TestDb.ViewerKey32,
        _ => TestDb.Hash32
    };

    internal static async Task<int> ReviewBucketCountAsync(WebhooksHost host) =>
        await host.WithDbAsync(async db =>
            await db.Buckets.CountAsync(bucket => bucket.Category == "lawyer-document-review"));

    internal static async Task<long> ReviewBucketIdAsync(WebhooksHost host) =>
        await host.WithDbAsync(async db => await db.Buckets
            .Where(bucket => bucket.Category == "lawyer-document-review")
            .Select(bucket => bucket.BucketId)
            .SingleAsync());

    private const long LegalDeadline = 1_790_000_000;
    private const string DocumentsHashBase58 = "4wBqpZM9xaSheZzJSMawUKKwhdpChKbZ5eu5ky4Vigw";
    private const string OtherDocumentsHashBase58 = "JEKNVnkbo3jma5nREBBJCDoXFVeKkD56V3xKrvRmWxFG";
    private const string DocumentsHash = "0102030405060708090a0b0c0d0e0f101112131415161718191a1b1c1d1e1f20";
    private const string ConfirmationTx = "ConfirmDocumentsTx111";
    private static readonly string ZeroHash = new('0', 64);

    private static ListingReviewInfo ReviewState(
        string spvStatus = "PENDING",
        string? spvHash = null,
        string developerStatus = "PENDING",
        string? developerHash = null,
        bool secondAttempt = false,
        string status = "SOLD_OUT") =>
        new(
            SpvLawyer,
            spvStatus,
            spvHash ?? ZeroHash,
            DeveloperLawyer,
            developerStatus,
            developerHash ?? ZeroHash,
            secondAttempt,
            status,
            LegalDeadline);

    private static async Task<long> SeedReviewBucketAsync(WebhooksHost host)
    {
        await SeedNamespaceAsync(host);
        await SeedPrerequisitesAsync(host);
        var (status, body) = await PostDeveloperJsonAsync(host);
        Assert.That(status, Is.EqualTo(HttpStatusCode.OK));
        host.Indexer.ListingReview = ReviewState();
        return body.GetProperty("lawyerReviewBucketId").GetInt64();
    }

    /// <summary>
    /// The wire (PascalCase) form of the mirror status the indexer would have emitted for, matching
    /// the pinned v2 <c>documents_confirmed</c> contract, where <c>status</c> is the listing status
    /// <em>after</em> the verdict instruction ran.
    /// </summary>
    private static string? WireListingStatus(string? listingStatus) => listingStatus switch
    {
        "SOLD_OUT" => "SoldOut",
        "LEGAL" => "Legal",
        "FINALIZED" => "Finalized",
        "CANCELLED" => "Cancelled",
        "EXPIRED" => "Expired",
        "REFUNDING" => "Refunding",
        _ => null
    };

    private static string ReviewOutcome(string? listingStatus) => listingStatus switch
    {
        "LEGAL" or "FINALIZED" => "approved",
        "CANCELLED" or "EXPIRED" or "REFUNDING" => "cancelled",
        _ => "unresolved"
    };

    private static async Task<(HttpStatusCode Status, string Body)> PostConfirmationAsync(
        WebhooksHost host,
        string? lawyer = SpvLawyer,
        bool approve = true,
        string instructionPath = "0",
        string txSignature = ConfirmationTx,
        string? presentedSecret = WebhookSecret,
        string eventName = "documents_confirmed",
        string? documentsHash = DocumentsHashBase58,
        string? cluster = "devnet",
        long? propertyId = ListingId,
        string route = "/webhooks/documents_confirmed_devnet",
        string logEvidence = "decoded",
        string? status = null,
        string? reviewOutcome = null,
        long slot = 487_700_300,
        string? futureField = null)
    {
        // The event-time status the program emitted for this instruction. Left unset it follows the
        // scripted mirror, which is what the indexer's own row would say; tests pass it explicitly
        // to model the mirror lagging behind the instruction.
        var listingStatus = status ?? WireListingStatus(host.Indexer.ListingReview?.Status);
        var json = new JsonObject
        {
            ["event"] = eventName,
            ["payload_version"] = 2,
            ["log_evidence"] = logEvidence,
            ["review_outcome"] = reviewOutcome
                ?? (logEvidence == "decoded" ? ReviewOutcome(listingStatus) : "unresolved"),
            ["listing_id"] = ListingId,
            ["property_id"] = propertyId,
            ["cluster"] = cluster,
            ["lawyer"] = lawyer,
            ["approve"] = approve,
            ["documents_hash"] = documentsHash,
            ["status"] = logEvidence == "decoded" ? listingStatus : null,
            ["listing"] = "ListingPDA111",
            ["property"] = AssetPda,
            ["slot"] = slot,
            ["tx_signature"] = txSignature,
            ["instruction_path"] = instructionPath,
            ["block_time"] = "2026-10-02T10:00:00+00:00",
            ["program"] = "marketplace"
        };
        if (futureField is not null)
        {
            json["settlement_escrow"] = futureField;
        }

        var path = route;
        if (presentedSecret is not null)
        {
            path += $"?secret={Uri.EscapeDataString(presentedSecret)}";
        }
        using var response = await host.Client.PostAsync(
            path, new StringContent(json.ToJsonString(), Encoding.UTF8, "application/json"));
        return (response.StatusCode, await response.Content.ReadAsStringAsync());
    }

    private static async Task<(HttpStatusCode Status, string Body)> PostStatusAsync(
        WebhooksHost host,
        string txSignature = "ReviewStatusTx111",
        string? presentedSecret = WebhookSecret,
        string route = "/webhooks/silent_verdict_resolved_devnet",
        string? status = null,
        string? silentLawyer = null,
        string logEvidence = "decoded",
        string? reviewOutcome = null)
    {
        var json = new JsonObject
        {
            ["event"] = "silent_verdict_resolved",
            ["payload_version"] = 2,
            ["log_evidence"] = logEvidence,
            ["review_outcome"] = reviewOutcome
                ?? (logEvidence == "decoded" ? ReviewOutcome(status) : "unresolved"),
            ["listing_id"] = ListingId,
            ["property_id"] = ListingId,
            ["cluster"] = "devnet",
            ["status"] = logEvidence == "decoded" ? status : null,
            ["silent_lawyer"] = silentLawyer,
            ["listing"] = "ListingPDA111",
            ["property"] = AssetPda,
            ["slot"] = 487_700_310,
            ["tx_signature"] = txSignature,
            ["instruction_path"] = "0",
            ["block_time"] = "2026-10-02T11:00:00+00:00",
            ["program"] = "marketplace"
        }.ToJsonString();
        var path = route;
        if (presentedSecret is not null)
        {
            path += $"?secret={Uri.EscapeDataString(presentedSecret)}";
        }
        using var response = await host.Client.PostAsync(
            path, new StringContent(json, Encoding.UTF8, "application/json"));
        return (response.StatusCode, await response.Content.ReadAsStringAsync());
    }

    private static async Task<(HttpStatusCode Status, string Body)> PostDealAsync(
        WebhooksHost host,
        long? listingId = ListingId,
        bool legacyShape = false,
        string? status = "Finalized")
    {
        var json = new JsonObject
        {
            ["event"] = "deal_executed",
            ["listing_id"] = listingId,
            ["listing"] = "ListingPDA111",
            ["property"] = AssetPda,
            ["developer_lawyer_registry"] = "DeveloperLawyerRegistry111",
            ["spv_lawyer_registry"] = "SpvLawyerRegistry111",
            ["slot"] = 487_700_320,
            ["tx_signature"] = "DealExecutedTx111",
            ["block_time"] = "2026-10-03T10:00:00+00:00",
            ["program"] = "marketplace"
        };
        if (!legacyShape)
        {
            json["payload_version"] = 2;
            json["property_id"] = ListingId;
            json["cluster"] = "devnet";
            json["status"] = status;
        }

        using var response = await host.Client.PostAsync(
            $"/webhooks/deal_executed_devnet?secret={Uri.EscapeDataString(WebhookSecret)}",
            new StringContent(json.ToJsonString(), Encoding.UTF8, "application/json"));
        return (response.StatusCode, await response.Content.ReadAsStringAsync());
    }

    private static async Task<List<XcavateBuckets.Domain.Entities.Message>> ReviewMessagesAsync(
        WebhooksHost host, long bucketId) =>
        await host.WithDbAsync(async db => await db.Messages
            .Where(message => message.BucketId == bucketId)
            .OrderBy(message => message.MessageId)
            .ToListAsync());

    private static async Task<(byte[] PrivateKey, string? Kid)> BucketKeyAsync(
        WebhooksHost host, long bucketId)
    {
        var body = await host.WithDbAsync(async db => await db.Messages
            .Where(message => message.BucketId == bucketId
                && message.Tag == "didcomm/key-sharing-v1")
            .OrderByDescending(message => message.MessageId)
            .Select(message => message.IpfsContent)
            .FirstAsync());
        var plaintext = LawyerTermsBucketProtocolTests.DecryptForRecipient(
            body!, WebhooksHost.DefaultBucketAdminPrivateKey);
        using var document = JsonDocument.Parse(plaintext);
        var key = document.RootElement.GetProperty("keys")[0];
        return (
            Microsoft.AspNetCore.WebUtilities.WebEncoders.Base64UrlDecode(
                key.GetProperty("d").GetString()!),
            key.GetProperty("kid").GetString());
    }

    private static async Task<string> DecryptMessageAsync(
        WebhooksHost host, XcavateBuckets.Domain.Entities.Message message)
    {
        var key = await BucketKeyAsync(host, message.BucketId);
        return Encoding.UTF8.GetString(LawyerTermsBucketProtocolTests.DecryptCompactMessage(
            message.IpfsContent!, key.PrivateKey, key.Kid));
    }

    [Test]
    public async Task Confirmation_posts_encrypted_approval_and_open_status_for_the_assigned_lawyer()
    {
        await using var host = await WebhooksHost.StartAsync(webhookSecret: WebhookSecret);
        var bucketId = await SeedReviewBucketAsync(host);
        host.Indexer.ListingReview = ReviewState(spvStatus: "APPROVED", spvHash: DocumentsHash);

        var (status, _) = await PostConfirmationAsync(host);
        var messages = await ReviewMessagesAsync(host, bucketId);
        var approval = messages.Single(message => message.Tag == "lawyer/terms-approved");
        var open = messages.Single(message => message.Tag == "voting/terms");

        await Assert.MultipleAsync(async () =>
        {
            Assert.That(status, Is.EqualTo(HttpStatusCode.OK));
            Assert.That(messages, Has.Count.EqualTo(6));
            Assert.That(approval.Contributor, Is.EqualTo(SpvLawyer));
            Assert.That(approval.Reference,
                Is.EqualTo($"lawyer-review/12/approved/{ConfirmationTx}:0"));
            Assert.That(approval.Description, Is.EqualTo("Approved terms"));
            Assert.That(approval.ContentType, Is.EqualTo("text/plain;charset=utf-8"));
            Assert.That(approval.IpfsContent, Does.Not.Contain("Approved terms"));
            Assert.That(await DecryptMessageAsync(host, approval), Is.EqualTo("Approved terms"));
            Assert.That(approval.ContentHash, Is.EqualTo("0x" + Convert.ToHexString(
                SHA256.HashData(Encoding.UTF8.GetBytes(approval.IpfsContent!)))));
            Assert.That(open.Contributor, Is.EqualTo(WebhooksHost.DefaultBucketAdminAddress));
            Assert.That(open.Reference, Is.EqualTo("voting/terms/12/attempt/1/open"));
            using var body = JsonDocument.Parse(open.IpfsContent!);
            Assert.That(body.RootElement.GetProperty("kind").GetString(), Is.EqualTo("terms"));
            Assert.That(body.RootElement.GetProperty("listingId").GetInt64(), Is.EqualTo(12));
            Assert.That(body.RootElement.GetProperty("round").GetInt32(), Is.EqualTo(1));
            Assert.That(body.RootElement.GetProperty("documentsHash").GetString(),
                Is.EqualTo(DocumentsHash));
            Assert.That(body.RootElement.GetProperty("status").GetString(), Is.EqualTo("open"));
            Assert.That(body.RootElement.GetProperty("listingStatus").GetString(), Is.EqualTo("SOLD_OUT"));
            Assert.That(DateTimeOffset.Parse(
                body.RootElement.GetProperty("expiresAt").GetString()!).ToUnixTimeSeconds(),
                Is.EqualTo(LegalDeadline));
            Assert.That(body.RootElement.TryGetProperty("yes", out _), Is.False);
            Assert.That(body.RootElement.TryGetProperty("no", out _), Is.False);
        });
    }

    [Test]
    public async Task Second_approval_posts_ratified_result_without_investor_tallies()
    {
        await using var host = await WebhooksHost.StartAsync(webhookSecret: WebhookSecret);
        var bucketId = await SeedReviewBucketAsync(host);
        host.Indexer.ListingReview = ReviewState(
            spvStatus: "APPROVED",
            spvHash: DocumentsHash,
            developerStatus: "APPROVED",
            developerHash: DocumentsHash,
            status: "LEGAL");

        var (status, _) = await PostConfirmationAsync(host, lawyer: DeveloperLawyer);
        var messages = await ReviewMessagesAsync(host, bucketId);
        var approval = messages.Single(message => message.Tag == "lawyer/terms-approved");
        var result = messages.Single(message => message.Tag == "voting/result");

        await Assert.MultipleAsync(async () =>
        {
            Assert.That(status, Is.EqualTo(HttpStatusCode.OK));
            Assert.That(messages, Has.Count.EqualTo(6));
            Assert.That(approval.Contributor, Is.EqualTo(DeveloperLawyer));
            Assert.That(await DecryptMessageAsync(host, approval), Is.EqualTo("Approved terms"));
            Assert.That(result.Contributor, Is.EqualTo(WebhooksHost.DefaultBucketAdminAddress));
            Assert.That(result.Reference, Is.EqualTo("voting/result/terms/12/attempt/1"));
            using var body = JsonDocument.Parse(result.IpfsContent!);
            Assert.That(body.RootElement.GetProperty("status").GetString(), Is.EqualTo("ratified"));
            Assert.That(body.RootElement.GetProperty("listingStatus").GetString(), Is.EqualTo("LEGAL"));
            Assert.That(body.RootElement.GetProperty("round").GetInt32(), Is.EqualTo(1));
            Assert.That(body.RootElement.GetProperty("documentsHash").GetString(),
                Is.EqualTo(DocumentsHash));
            Assert.That(body.RootElement.TryGetProperty("yes", out _), Is.False);
            Assert.That(body.RootElement.TryGetProperty("no", out _), Is.False);
            Assert.That(body.RootElement.TryGetProperty("abstain", out _), Is.False);
        });
    }

    [Test]
    public async Task First_rejection_closes_attempt_one_and_opens_attempt_two()
    {
        await using var host = await WebhooksHost.StartAsync(webhookSecret: WebhookSecret);
        var bucketId = await SeedReviewBucketAsync(host);
        host.Indexer.ListingReview = ReviewState(secondAttempt: true);

        var (status, _) = await PostConfirmationAsync(host, approve: false);
        var messages = await ReviewMessagesAsync(host, bucketId);
        var rejection = messages.Single(message => message.Tag == "lawyer/terms-rejected");
        var result = messages.Single(message => message.Tag == "voting/result");
        var open = messages.Single(message => message.Tag == "voting/terms");

        await Assert.MultipleAsync(async () =>
        {
            Assert.That(status, Is.EqualTo(HttpStatusCode.OK));
            Assert.That(messages, Has.Count.EqualTo(7));
            Assert.That(rejection.Contributor, Is.EqualTo(SpvLawyer));
            Assert.That(rejection.Reference,
                Is.EqualTo($"lawyer-review/12/rejected/{ConfirmationTx}:0"));
            Assert.That(rejection.Description, Is.EqualTo("Rejected terms"));
            Assert.That(await DecryptMessageAsync(host, rejection), Is.EqualTo("Rejected terms"));
            Assert.That(result.Reference, Is.EqualTo("voting/result/terms/12/attempt/1"));
            Assert.That(open.Reference, Is.EqualTo("voting/terms/12/attempt/2/open"));
            using var resultBody = JsonDocument.Parse(result.IpfsContent!);
            Assert.That(resultBody.RootElement.GetProperty("status").GetString(), Is.EqualTo("rejected"));
            Assert.That(resultBody.RootElement.GetProperty("round").GetInt32(), Is.EqualTo(1));
            Assert.That(resultBody.RootElement.GetProperty("documentsHash").GetString(),
                Is.EqualTo(DocumentsHash));
            using var openBody = JsonDocument.Parse(open.IpfsContent!);
            Assert.That(openBody.RootElement.GetProperty("status").GetString(), Is.EqualTo("open"));
            Assert.That(openBody.RootElement.GetProperty("round").GetInt32(), Is.EqualTo(2));
            Assert.That(openBody.RootElement.TryGetProperty("documentsHash", out _), Is.False);
        });
    }

    [Test]
    public async Task First_split_approval_after_rejection_closes_attempt_one_and_opens_attempt_two()
    {
        await using var host = await WebhooksHost.StartAsync(webhookSecret: WebhookSecret);
        var bucketId = await SeedReviewBucketAsync(host);
        host.Indexer.ListingReview = ReviewState(secondAttempt: true);

        var (status, _) = await PostConfirmationAsync(host, approve: true);
        var messages = await ReviewMessagesAsync(host, bucketId);
        var approval = messages.Single(message => message.Tag == "lawyer/terms-approved");
        var result = messages.Single(message => message.Tag == "voting/result");
        var open = messages.Single(message => message.Tag == "voting/terms");

        await Assert.MultipleAsync(async () =>
        {
            Assert.That(status, Is.EqualTo(HttpStatusCode.OK));
            Assert.That(messages, Has.Count.EqualTo(7));
            Assert.That(approval.Contributor, Is.EqualTo(SpvLawyer));
            Assert.That(await DecryptMessageAsync(host, approval), Is.EqualTo("Approved terms"));
            Assert.That(result.Reference, Is.EqualTo("voting/result/terms/12/attempt/1"));
            Assert.That(open.Reference, Is.EqualTo("voting/terms/12/attempt/2/open"));
            using var resultBody = JsonDocument.Parse(result.IpfsContent!);
            Assert.That(resultBody.RootElement.GetProperty("status").GetString(), Is.EqualTo("rejected"));
            Assert.That(resultBody.RootElement.GetProperty("round").GetInt32(), Is.EqualTo(1));
            Assert.That(resultBody.RootElement.GetProperty("documentsHash").GetString(),
                Is.EqualTo(DocumentsHash));
            using var openBody = JsonDocument.Parse(open.IpfsContent!);
            Assert.That(openBody.RootElement.GetProperty("status").GetString(), Is.EqualTo("open"));
            Assert.That(openBody.RootElement.GetProperty("round").GetInt32(), Is.EqualTo(2));
        });
    }

    [TestCase(DocumentsHash)]
    [TestCase("4wBqpZM9xaSheZzJSMawUKKwhdpChKbZ5eu5ky4Vig")]
    [TestCase("11111111111111111111111111111111")]
    public async Task Confirmation_requires_canonical_base58_32_byte_documents_hash(string candidate)
    {
        await using var host = await WebhooksHost.StartAsync(webhookSecret: WebhookSecret);
        var bucketId = await SeedReviewBucketAsync(host);
        host.Indexer.ListingReview = ReviewState(spvStatus: "APPROVED", spvHash: DocumentsHash);

        var (status, _) = await PostConfirmationAsync(host, documentsHash: candidate);
        var messages = await ReviewMessagesAsync(host, bucketId);

        Assert.Multiple(() =>
        {
            Assert.That(status, Is.EqualTo(HttpStatusCode.BadRequest));
            Assert.That(messages, Has.Count.EqualTo(4));
        });
    }

    [Test]
    public async Task Delayed_confirmation_uses_immutable_bucket_seats_after_close_case()
    {
        await using var host = await WebhooksHost.StartAsync(webhookSecret: WebhookSecret);
        var bucketId = await SeedReviewBucketAsync(host);
        host.Indexer.ListingReview = new ListingReviewInfo(
            SystemDefaultPubkey,
            "PENDING",
            ZeroHash,
            SystemDefaultPubkey,
            "PENDING",
            ZeroHash,
            true,
            "CANCELLED",
            LegalDeadline);

        var (status, _) = await PostConfirmationAsync(host);
        var messages = await ReviewMessagesAsync(host, bucketId);
        var approval = messages.Single(message => message.Tag == "lawyer/terms-approved");

        await Assert.MultipleAsync(async () =>
        {
            Assert.That(status, Is.EqualTo(HttpStatusCode.OK));
            Assert.That(approval.Contributor, Is.EqualTo(SpvLawyer));
            Assert.That(await DecryptMessageAsync(host, approval), Is.EqualTo("Approved terms"));
            Assert.That(messages.Count(message => message.Tag == "voting/result"), Is.EqualTo(1));
        });
    }

    [Test]
    public async Task Second_rejection_posts_cancelled_result_without_reopening_review()
    {
        await using var host = await WebhooksHost.StartAsync(webhookSecret: WebhookSecret);
        var bucketId = await SeedReviewBucketAsync(host);
        host.Indexer.ListingReview = ReviewState(
            spvStatus: "REJECTED",
            spvHash: DocumentsHash,
            developerStatus: "APPROVED",
            developerHash: DocumentsHash,
            secondAttempt: true,
            status: "CANCELLED");

        var (status, _) = await PostConfirmationAsync(host, approve: false);
        var messages = await ReviewMessagesAsync(host, bucketId);
        var result = messages.Single(message => message.Tag == "voting/result");

        Assert.Multiple(() =>
        {
            Assert.That(status, Is.EqualTo(HttpStatusCode.OK));
            Assert.That(messages, Has.Count.EqualTo(6));
            Assert.That(messages.Any(message => message.Tag == "voting/terms"), Is.False);
            Assert.That(result.Reference, Is.EqualTo("voting/result/terms/12/attempt/2"));
            Assert.That(result.Description,
                Is.EqualTo("Lawyer document review attempt 2 cancelled"));
            using var body = JsonDocument.Parse(result.IpfsContent!);
            Assert.That(body.RootElement.GetProperty("status").GetString(), Is.EqualTo("cancelled"));
            Assert.That(body.RootElement.GetProperty("listingStatus").GetString(), Is.EqualTo("CANCELLED"));
            Assert.That(body.RootElement.GetProperty("round").GetInt32(), Is.EqualTo(2));
        });
    }

    [Test]
    public async Task Independent_status_webhook_posts_result_without_an_approval_message()
    {
        await using var host = await WebhooksHost.StartAsync(webhookSecret: WebhookSecret);
        var bucketId = await SeedReviewBucketAsync(host);
        host.Indexer.ListingReview = ReviewState(
            spvStatus: "APPROVED",
            spvHash: DocumentsHash,
            developerStatus: "REJECTED",
            developerHash: DocumentsHash,
            secondAttempt: true,
            status: "CANCELLED");

        var (status, _) = await PostStatusAsync(host);
        var messages = await ReviewMessagesAsync(host, bucketId);
        var result = messages.Single(message => message.Tag == "voting/result");

        Assert.Multiple(() =>
        {
            Assert.That(status, Is.EqualTo(HttpStatusCode.OK));
            Assert.That(messages, Has.Count.EqualTo(5));
            Assert.That(messages.Any(message => message.Tag == "lawyer/terms-approved"), Is.False);
            Assert.That(messages.Any(message => message.Tag == "lawyer/terms-rejected"), Is.False);
            Assert.That(result.Reference, Is.EqualTo("voting/result/terms/12/attempt/2"));
            Assert.That(result.Description,
                Is.EqualTo("Lawyer document review attempt 2 cancelled"));
            using var body = JsonDocument.Parse(result.IpfsContent!);
            Assert.That(body.RootElement.GetProperty("status").GetString(), Is.EqualTo("cancelled"));
            Assert.That(body.RootElement.GetProperty("listingStatus").GetString(), Is.EqualTo("CANCELLED"));
        });
    }

    [Test]
    public async Task Deal_execution_posts_finalized_result_and_still_removes_legal_lawyers()
    {
        await using var host = await WebhooksHost.StartAsync(webhookSecret: WebhookSecret);
        await SeedNamespaceAsync(host);
        var legalBucketId = await SeedLegalBucketAsync(host);
        await SeedPrerequisitesAsync(host);
        var (setupStatus, setup) = await PostSpvAsync(host);
        Assert.That(setupStatus, Is.EqualTo(HttpStatusCode.OK));
        var bucketId = setup.GetProperty("lawyerReviewBucketId").GetInt64();
        host.Indexer.ListingReview = ReviewState(
            spvStatus: "APPROVED",
            spvHash: DocumentsHash,
            developerStatus: "APPROVED",
            developerHash: DocumentsHash,
            status: "FINALIZED");

        var (status, _) = await PostDealAsync(host);
        var messages = await ReviewMessagesAsync(host, bucketId);
        var result = messages.Single(message =>
            message.Tag == "voting/result" && message.Reference!.EndsWith("/finalized"));
        var legalContributors = await host.WithDbAsync(async db => await db.BucketContributors
            .Where(row => row.BucketId == legalBucketId)
            .Select(row => row.SubjectId)
            .ToListAsync());

        Assert.Multiple(() =>
        {
            Assert.That(status, Is.EqualTo(HttpStatusCode.OK));
            Assert.That(legalContributors, Does.Not.Contain(SpvLawyer));
            Assert.That(result.Contributor, Is.EqualTo(WebhooksHost.DefaultBucketAdminAddress));
            Assert.That(result.Reference, Is.EqualTo("voting/result/terms/12/attempt/1/finalized"));
            Assert.That(result.Description,
                Is.EqualTo("Lawyer document review attempt 1 finalized"));
            using var body = JsonDocument.Parse(result.IpfsContent!);
            Assert.That(body.RootElement.GetProperty("status").GetString(), Is.EqualTo("finalized"));
            Assert.That(body.RootElement.GetProperty("listingStatus").GetString(), Is.EqualTo("FINALIZED"));
            Assert.That(body.RootElement.GetProperty("round").GetInt32(), Is.EqualTo(1));
        });
    }

    [Test]
    public async Task Property_only_deal_uses_namespace_listing_id_for_legal_and_review_buckets()
    {
        await using var host = await WebhooksHost.StartAsync(webhookSecret: WebhookSecret);
        await SeedNamespaceAsync(host);
        var legalBucketId = await SeedLegalBucketAsync(host);
        await SeedPrerequisitesAsync(host);
        var (setupStatus, setup) = await PostSpvAsync(host);
        Assert.That(setupStatus, Is.EqualTo(HttpStatusCode.OK));
        var bucketId = setup.GetProperty("lawyerReviewBucketId").GetInt64();
        host.Indexer.ListingReview = ReviewState(
            spvStatus: "APPROVED",
            spvHash: DocumentsHash,
            developerStatus: "APPROVED",
            developerHash: DocumentsHash,
            status: "FINALIZED");

        var (status, _) = await PostDealAsync(host, listingId: null);
        var messages = await ReviewMessagesAsync(host, bucketId);
        var legalContributors = await host.WithDbAsync(async db => await db.BucketContributors
            .Where(row => row.BucketId == legalBucketId)
            .Select(row => row.SubjectId)
            .ToListAsync());

        Assert.Multiple(() =>
        {
            Assert.That(status, Is.EqualTo(HttpStatusCode.OK));
            Assert.That(legalContributors, Does.Not.Contain(SpvLawyer));
            Assert.That(messages.Any(message => message.Reference
                == "voting/result/terms/12/attempt/1/finalized"), Is.True);
        });
    }

    [Test]
    public async Task Deal_without_payload_or_namespace_listing_id_is_controlled_and_atomic()
    {
        await using var host = await WebhooksHost.StartAsync(webhookSecret: WebhookSecret);
        await SeedNamespaceAsync(host);
        var legalBucketId = await SeedLegalBucketAsync(host);
        await SeedPrerequisitesAsync(host);
        var (setupStatus, setup) = await PostSpvAsync(host);
        Assert.That(setupStatus, Is.EqualTo(HttpStatusCode.OK));
        var bucketId = setup.GetProperty("lawyerReviewBucketId").GetInt64();
        host.Indexer.ListingReview = ReviewState(
            spvStatus: "APPROVED",
            spvHash: DocumentsHash,
            developerStatus: "APPROVED",
            developerHash: DocumentsHash,
            status: "FINALIZED");
        await host.WithDbAsync(async db =>
        {
            var ns = await db.Namespaces.SingleAsync();
            ns.PropertyId = null;
            await db.SaveChangesAsync();
            return true;
        });

        var (status, _) = await PostDealAsync(host, listingId: null);
        var messages = await ReviewMessagesAsync(host, bucketId);
        var legalContributors = await host.WithDbAsync(async db => await db.BucketContributors
            .Where(row => row.BucketId == legalBucketId)
            .Select(row => row.SubjectId)
            .ToListAsync());

        Assert.Multiple(() =>
        {
            Assert.That(status, Is.EqualTo(HttpStatusCode.BadRequest));
            Assert.That(legalContributors, Does.Contain(SpvLawyer));
            Assert.That(messages, Has.Count.EqualTo(4));
        });
    }

    [Test]
    public async Task Confirmation_redelivery_reconciles_without_duplicate_messages_or_key_rotation()
    {
        await using var host = await WebhooksHost.StartAsync(webhookSecret: WebhookSecret);
        var bucketId = await SeedReviewBucketAsync(host);
        host.Indexer.ListingReview = ReviewState(spvStatus: "APPROVED", spvHash: DocumentsHash);
        var originalKey = (await host.BucketsAsync()).Single(bucket => bucket.BucketId == bucketId).EncryptionKey;

        var (firstStatus, _) = await PostConfirmationAsync(host);
        var (secondStatus, secondBody) = await PostConfirmationAsync(host);
        var messages = await ReviewMessagesAsync(host, bucketId);
        var bucket = (await host.BucketsAsync()).Single(row => row.BucketId == bucketId);

        Assert.Multiple(() =>
        {
            Assert.That(firstStatus, Is.EqualTo(HttpStatusCode.OK));
            Assert.That(secondStatus, Is.EqualTo(HttpStatusCode.OK));
            Assert.That(secondBody, Does.Contain("\"status\":\"ignored\""));
            Assert.That(messages, Has.Count.EqualTo(6));
            Assert.That(messages.Count(message => message.Tag == "didcomm/key-sharing-v1"), Is.EqualTo(1));
            Assert.That(messages.Count(message => message.Tag == "lawyer/terms-approved"), Is.EqualTo(1));
            Assert.That(messages.Count(message => message.Tag == "voting/terms"), Is.EqualTo(1));
            Assert.That(bucket.EncryptionKey, Is.EqualTo(originalKey));
        });
    }

    [TestCase(null)]
    [TestCase("wrong-secret")]
    public async Task Confirmation_requires_the_indexer_webhook_secret(string? presentedSecret)
    {
        await using var host = await WebhooksHost.StartAsync(webhookSecret: WebhookSecret);
        var bucketId = await SeedReviewBucketAsync(host);
        host.Indexer.ListingReview = ReviewState(spvStatus: "APPROVED", spvHash: DocumentsHash);

        var (status, _) = await PostConfirmationAsync(host, presentedSecret: presentedSecret);
        var messages = await ReviewMessagesAsync(host, bucketId);

        Assert.Multiple(() =>
        {
            Assert.That(status, Is.EqualTo(HttpStatusCode.Unauthorized));
            Assert.That(messages, Has.Count.EqualTo(4));
        });
    }

    [TestCase(false, DocumentsHashBase58)]
    [TestCase(true, OtherDocumentsHashBase58)]
    public async Task Confirmation_rejects_a_verdict_or_hash_that_contradicts_indexer(
        bool approve, string documentsHash)
    {
        await using var host = await WebhooksHost.StartAsync(webhookSecret: WebhookSecret);
        var bucketId = await SeedReviewBucketAsync(host);
        host.Indexer.ListingReview = ReviewState(spvStatus: "APPROVED", spvHash: DocumentsHash);

        var (status, _) = await PostConfirmationAsync(
            host, approve: approve, documentsHash: documentsHash);
        var messages = await ReviewMessagesAsync(host, bucketId);

        Assert.Multiple(() =>
        {
            Assert.That(status, Is.EqualTo(HttpStatusCode.BadRequest));
            Assert.That(messages, Has.Count.EqualTo(4));
        });
    }

    [Test]
    public async Task Confirmation_rejects_a_lawyer_who_is_not_assigned_to_the_review()
    {
        await using var host = await WebhooksHost.StartAsync(webhookSecret: WebhookSecret);
        var bucketId = await SeedReviewBucketAsync(host);
        host.Indexer.ListingReview = ReviewState(spvStatus: "APPROVED", spvHash: DocumentsHash);

        var (status, _) = await PostConfirmationAsync(host, lawyer: "DifferentLawyerWallet999");
        var messages = await ReviewMessagesAsync(host, bucketId);

        Assert.Multiple(() =>
        {
            Assert.That(status, Is.EqualTo(HttpStatusCode.BadRequest));
            Assert.That(messages, Has.Count.EqualTo(4));
        });
    }

    [Test]
    public async Task Confirmation_missing_review_bucket_is_retryable_without_side_effects()
    {
        await using var host = await WebhooksHost.StartAsync(webhookSecret: WebhookSecret);
        await SeedNamespaceAsync(host);
        await SeedPrerequisitesAsync(host);
        host.Indexer.ListingReview = ReviewState(spvStatus: "APPROVED", spvHash: DocumentsHash);

        var (status, _) = await PostConfirmationAsync(host);
        var messageCount = await host.WithDbAsync(async db => await db.Messages.CountAsync());

        Assert.Multiple(() =>
        {
            Assert.That(status, Is.EqualTo(HttpStatusCode.NotFound));
            Assert.That(messageCount, Is.Zero);
        });
    }

    [Test]
    public async Task Confirmation_indexer_failure_is_retryable_without_side_effects()
    {
        await using var host = await WebhooksHost.StartAsync(webhookSecret: WebhookSecret);
        var bucketId = await SeedReviewBucketAsync(host);
        host.Indexer.ThrowOnQuery = true;

        var (status, _) = await PostConfirmationAsync(host);
        var messages = await ReviewMessagesAsync(host, bucketId);

        Assert.Multiple(() =>
        {
            Assert.That(status, Is.EqualTo(HttpStatusCode.BadGateway));
            Assert.That(messages, Has.Count.EqualTo(4));
        });
    }

    [Test]
    public async Task Confirmation_rejects_invalid_payloads_without_side_effects()
    {
        await using var host = await WebhooksHost.StartAsync(webhookSecret: WebhookSecret);
        var bucketId = await SeedReviewBucketAsync(host);

        var wrongEvent = await PostConfirmationAsync(host, eventName: "lawyer_documents_confirmed");
        var missingLawyer = await PostConfirmationAsync(host, lawyer: null);
        var missingHash = await PostConfirmationAsync(host, documentsHash: null);
        var wrongCluster = await PostConfirmationAsync(host, cluster: "testnet");
        var wrongProperty = await PostConfirmationAsync(host, propertyId: 13);
        var messages = await ReviewMessagesAsync(host, bucketId);

        Assert.Multiple(() =>
        {
            Assert.That(wrongEvent.Status, Is.EqualTo(HttpStatusCode.BadRequest));
            Assert.That(missingLawyer.Status, Is.EqualTo(HttpStatusCode.BadRequest));
            Assert.That(missingHash.Status, Is.EqualTo(HttpStatusCode.BadRequest));
            Assert.That(wrongCluster.Status, Is.EqualTo(HttpStatusCode.BadRequest));
            Assert.That(wrongProperty.Status, Is.EqualTo(HttpStatusCode.BadRequest));
            Assert.That(messages, Has.Count.EqualTo(4));
        });
    }

    [TestCase("/webhooks/lawyer_documents_confirmed_devnet", true)]
    [TestCase("/webhooks/lawyer_review_status_changed_devnet", false)]
    public async Task Invented_webhook_routes_are_not_required(string route, bool confirmation)
    {
        await using var host = await WebhooksHost.StartAsync(webhookSecret: WebhookSecret);
        var bucketId = await SeedReviewBucketAsync(host);
        host.Indexer.ListingReview = ReviewState(spvStatus: "APPROVED", spvHash: DocumentsHash);

        var response = confirmation
            ? await PostConfirmationAsync(host, route: route)
            : await PostStatusAsync(host, route: route);
        var messages = await ReviewMessagesAsync(host, bucketId);

        Assert.Multiple(() =>
        {
            Assert.That(response.Status, Is.EqualTo(HttpStatusCode.NotFound));
            Assert.That(messages, Has.Count.EqualTo(4));
        });
    }

    [Test]
    public async Task Developer_assignment_creates_complete_bucket_and_ordered_messages()
    {
        await using var host = await WebhooksHost.StartAsync(webhookSecret: WebhookSecret);
        await SeedNamespaceAsync(host);
        await SeedPrerequisitesAsync(host);

        var (status, body) = await PostDeveloperJsonAsync(host);
        var bucketId = body.GetProperty("lawyerReviewBucketId").GetInt64();
        var state = await host.WithDbAsync(async db => new
        {
            Bucket = await db.Buckets.SingleAsync(bucket => bucket.BucketId == bucketId),
            Admins = await db.BucketAdmins.Where(row => row.BucketId == bucketId)
                .Select(row => row.SubjectId).ToListAsync(),
            Contributors = await db.BucketContributors.Where(row => row.BucketId == bucketId)
                .Select(row => row.SubjectId).ToListAsync(),
            Viewers = await db.BucketViewers.Where(row => row.BucketId == bucketId)
                .Select(row => row.ViewerId).ToListAsync(),
            Messages = await db.Messages.Where(row => row.BucketId == bucketId)
                .OrderBy(row => row.MessageId).ToListAsync()
        });

        await Assert.MultipleAsync(async () =>
        {
            Assert.That(status, Is.EqualTo(HttpStatusCode.OK));
            Assert.That(body.GetProperty("lawyerReviewBucketCreated").GetBoolean(), Is.True);
            Assert.That(body.GetProperty("lawyerReviewHolderCount").GetInt32(), Is.EqualTo(2));
            Assert.That(state.Bucket.Name, Is.EqualTo("42 Main Street - Lawyer document review"));
            Assert.That(state.Bucket.Category, Is.EqualTo("lawyer-document-review"));
            Assert.That(state.Bucket.Creator, Is.EqualTo(AssetPda));
            Assert.That(state.Bucket.IsWritable, Is.True);
            Assert.That(Microsoft.AspNetCore.WebUtilities.WebEncoders.Base64UrlDecode(
                state.Bucket.EncryptionKey!), Has.Length.EqualTo(32));
            using (var properties = JsonDocument.Parse(state.Bucket.Properties!))
            {
                Assert.That(properties.RootElement.GetProperty("kind").GetString(),
                    Is.EqualTo("lawyer_document_review"));
                Assert.That(properties.RootElement.GetProperty("listing_id").GetString(), Is.EqualTo("12"));
                Assert.That(properties.RootElement.GetProperty("property_id").GetString(), Is.EqualTo("12"));
                Assert.That(properties.RootElement.GetProperty("cluster").GetString(), Is.EqualTo("devnet"));
                Assert.That(properties.RootElement.GetProperty("spv_lawyer").GetString(), Is.EqualTo(SpvLawyer));
                Assert.That(properties.RootElement.GetProperty("developer_lawyer").GetString(),
                    Is.EqualTo(DeveloperLawyer));
                Assert.That(properties.RootElement.GetProperty("tx_signature").GetString(),
                    Is.EqualTo("DeveloperAssignmentTx111"));
            }
            Assert.That(state.Admins, Is.EqualTo(new[] { WebhooksHost.DefaultBucketAdminAddress }));
            Assert.That(state.Contributors, Is.EquivalentTo(new[]
            {
                WebhooksHost.DefaultBucketAdminAddress, SpvLawyer, DeveloperLawyer
            }));
            Assert.That(state.Viewers, Is.EquivalentTo(new[]
            {
                WebhooksHost.DefaultBucketAdminViewerKey,
                TestDb.Key32Base64Url, TestDb.OtherKey32Base64Url,
                TestDb.ViewerKey32Base64Url, TestDb.Hash32Base64Url
            }));
            Assert.That(state.Messages, Has.Count.EqualTo(4));
            Assert.That(state.Messages.Select(message => message.Contributor), Is.All.EqualTo(
                WebhooksHost.DefaultBucketAdminAddress));
            Assert.That(state.Messages[0].Tag, Is.EqualTo("didcomm/key-sharing-v1"));
            using (var keySharing = JsonDocument.Parse(state.Messages[0].IpfsContent!))
            {
                Assert.That(keySharing.RootElement.GetProperty("recipients").GetArrayLength(), Is.EqualTo(5));
                Assert.That(state.Messages[0].IpfsContent, Does.Not.Contain("\"d\""));
            }
            Assert.That(state.Messages[1].Tag, Is.EqualTo("lawyer/document-review"));
            Assert.That(state.Messages[1].Reference, Is.EqualTo("lawyer-review/12/prompt"));
            Assert.That(state.Messages[1].ContentType, Is.EqualTo("text/plain;charset=utf-8"));
            Assert.That(state.Messages[1].IpfsContent, Does.Not.Contain(ReviewPrompt));
            Assert.That(await DecryptMessageAsync(host, state.Messages[1]), Is.EqualTo(ReviewPrompt));
            Assert.That(state.Messages[1].ContentHash, Is.EqualTo("0x" + Convert.ToHexString(
                SHA256.HashData(Encoding.UTF8.GetBytes(state.Messages[1].IpfsContent!)))));
            await AssertDocumentAsync(host, state.Messages[2], "spv-lawyer", SpvPdf);
            await AssertDocumentAsync(host, state.Messages[3], "developer-lawyer", DeveloperPdf);
        });
    }

    private static async Task AssertDocumentAsync(
        WebhooksHost host,
        XcavateBuckets.Domain.Entities.Message message,
        string role,
        byte[] pdf)
    {
        // The snapshot's object key is content-addressed with the digest of the payload stored at
        // it, so it is read back from the message's own reference instead of being guessed at —
        // which is exactly what stops a losing creation attempt's bytes from posing as the
        // winning bucket's snapshot.
        Assert.That(message.Reference, Does.StartWith(
            $"https://objects.test/xcavate-profile/terms-documents/by-listing/12/{role}-")
            .And.EndsWith(".pdf"));
        var path = new Uri(message.Reference!).AbsolutePath.TrimStart('/');
        var slash = path.IndexOf('/');
        var snapshot = await host.S3.DownloadObjectAsync(
            path[..slash], path[(slash + 1)..], CancellationToken.None);
        var key = await BucketKeyAsync(host, message.BucketId);
        var decrypted = LawyerTermsBucketProtocolTests.DecryptCompactMessage(
            Encoding.UTF8.GetString(snapshot), key.PrivateKey, key.Kid);

        Assert.Multiple(() =>
        {
            Assert.That(message.Tag, Is.EqualTo("lawyer/terms-document"));
            Assert.That(message.Reference, Is.EqualTo(
                "https://objects.test/xcavate-profile/terms-documents/by-listing/12/"
                + $"{role}-{message.ContentHash![2..].ToLowerInvariant()}.pdf"));
            Assert.That(message.Description, Is.EqualTo("terms.pdf"));
            Assert.That(message.ContentType, Is.EqualTo("application/pdf"));
            Assert.That(message.IpfsContent, Is.Null);
            Assert.That(decrypted, Is.EqualTo(pdf));
            Assert.That(message.ContentHash,
                Is.EqualTo("0x" + Convert.ToHexString(SHA256.HashData(snapshot))));
        });
    }

    [TestCase(false, true, 2, SpvFallback)]
    [TestCase(true, false, 3, DeveloperFallback)]
    public async Task Missing_pdfs_send_exact_fallbacks(
        bool spvDocument, bool developerDocument, int messageIndex, string fallback)
    {
        await using var host = await WebhooksHost.StartAsync(webhookSecret: WebhookSecret);
        await SeedNamespaceAsync(host);
        await SeedPrerequisitesAsync(host, spvDocument: spvDocument, developerDocument: developerDocument);

        var (status, _) = await PostDeveloperJsonAsync(host);
        var bucketId = await ReviewBucketIdAsync(host);
        var messages = await host.WithDbAsync(async db => await db.Messages
            .Where(message => message.BucketId == bucketId)
            .OrderBy(message => message.MessageId)
            .ToListAsync());
        var role = messageIndex == 2 ? "spv" : "developer";

        await Assert.MultipleAsync(async () =>
        {
            Assert.That(status, Is.EqualTo(HttpStatusCode.OK));
            Assert.That(messages, Has.Count.EqualTo(4));
            Assert.That(messages[messageIndex].Tag, Is.EqualTo("lawyer/terms-document-missing"));
            Assert.That(messages[messageIndex].Reference,
                Is.EqualTo($"lawyer-review/12/{role}-document-missing"));
            Assert.That(messages[messageIndex].ContentType, Is.EqualTo("text/plain;charset=utf-8"));
            Assert.That(messages[messageIndex].IpfsContent, Does.Not.Contain(fallback));
            Assert.That(await DecryptMessageAsync(host, messages[messageIndex]), Is.EqualTo(fallback));
            Assert.That(messages[messageIndex].ContentHash, Is.EqualTo("0x" + Convert.ToHexString(
                SHA256.HashData(Encoding.UTF8.GetBytes(messages[messageIndex].IpfsContent!)))));
        });
    }

    [TestCase(true)]
    [TestCase(false)]
    public async Task Either_trigger_order_creates_exactly_one_complete_bucket(bool spvFirst)
    {
        await using var host = await WebhooksHost.StartAsync(webhookSecret: WebhookSecret);
        await SeedNamespaceAsync(host);
        var legalBucketId = await SeedLegalBucketAsync(host);
        await SeedPrerequisitesAsync(host);

        var first = spvFirst ? await PostSpvAsync(host) : await PostDeveloperJsonAsync(host);
        var second = spvFirst ? await PostDeveloperJsonAsync(host) : await PostSpvAsync(host);
        var bucketId = await ReviewBucketIdAsync(host);
        var messageCount = await host.WithDbAsync(async db => await db.Messages
            .CountAsync(message => message.BucketId == bucketId));
        var legalMessageCount = await host.WithDbAsync(async db => await db.Messages
            .CountAsync(message => message.BucketId == legalBucketId));

        Assert.Multiple(() =>
        {
            Assert.That(first.Status, Is.EqualTo(HttpStatusCode.OK));
            Assert.That(second.Status, Is.EqualTo(HttpStatusCode.OK));
            Assert.That(first.Body.GetProperty("lawyerReviewBucketCreated").GetBoolean(), Is.True);
            Assert.That(second.Body.GetProperty("lawyerReviewBucketCreated").GetBoolean(), Is.False);
            Assert.That(messageCount, Is.EqualTo(4));
            Assert.That(legalMessageCount, Is.EqualTo(1));
        });
    }

    [Test]
    public async Task Redelivery_and_the_second_trigger_reuse_bucket_key_and_messages()
    {
        await using var host = await WebhooksHost.StartAsync(webhookSecret: WebhookSecret);
        await SeedNamespaceAsync(host);
        await SeedLegalBucketAsync(host);
        await SeedPrerequisitesAsync(host);
        await PostDeveloperJsonAsync(host);
        var bucketId = await ReviewBucketIdAsync(host);
        var originalKey = (await host.BucketsAsync()).Single(bucket => bucket.BucketId == bucketId).EncryptionKey;

        var (_, redelivery) = await PostDeveloperJsonAsync(host);
        var (spvStatus, _) = await PostSpvAsync(host);
        var bucket = (await host.BucketsAsync()).Single(bucket => bucket.BucketId == bucketId);
        var messageCount = await host.WithDbAsync(async db => await db.Messages
            .CountAsync(message => message.BucketId == bucketId));

        Assert.Multiple(() =>
        {
            Assert.That(redelivery.GetProperty("lawyerReviewBucketCreated").GetBoolean(), Is.False);
            Assert.That(spvStatus, Is.EqualTo(HttpStatusCode.OK));
            Assert.That(bucket.EncryptionKey, Is.EqualTo(originalKey));
            Assert.That(messageCount, Is.EqualTo(4));
        });
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task Legacy_redelivery_adds_exact_metadata_without_key_or_message_changes(bool spvRedelivery)
    {
        await using var host = await WebhooksHost.StartAsync(webhookSecret: WebhookSecret);
        await SeedNamespaceAsync(host);
        await SeedLegalBucketAsync(host);
        await SeedPrerequisitesAsync(host);
        var (initialStatus, _) = await PostDeveloperAsync(host, cluster: null, propertyId: null);
        var bucketId = await ReviewBucketIdAsync(host);
        var originalKey = await host.WithDbAsync(async db =>
        {
            var bucket = await db.Buckets.SingleAsync(row => row.BucketId == bucketId);
            var properties = JsonNode.Parse(bucket.Properties!)!.AsObject();
            properties.Remove("property_id");
            properties.Remove("cluster");
            bucket.Properties = properties.ToJsonString();
            // The other half of the legacy shape: the prompt went out in the clear.
            var prompt = await db.Messages.SingleAsync(message =>
                message.BucketId == bucket.BucketId && message.Tag == "lawyer/document-review");
            prompt.IpfsContent = ReviewPrompt;
            prompt.ContentType = "text/plain";
            await db.SaveChangesAsync();
            return bucket.EncryptionKey;
        });

        var redeliveryStatus = spvRedelivery
            ? (await PostSpvAsync(host, "mainnet")).Status
            : (await PostDeveloperAsync(host, cluster: "mainnet")).Status;
        var bucket = (await host.BucketsAsync()).Single(bucket => bucket.BucketId == bucketId);
        var messages = await host.WithDbAsync(async db => await db.Messages
            .Where(message => message.BucketId == bucketId)
            .OrderBy(message => message.MessageId)
            .ToListAsync());

        await Assert.MultipleAsync(async () =>
        {
            Assert.That(initialStatus, Is.EqualTo(HttpStatusCode.OK));
            Assert.That(redeliveryStatus, Is.EqualTo(HttpStatusCode.OK));
            Assert.That(bucket.EncryptionKey, Is.EqualTo(originalKey));
            Assert.That(messages, Has.Count.EqualTo(4));
            Assert.That(messages.Count(message =>
                message.Tag == "didcomm/key-sharing-v1"), Is.EqualTo(1));
            Assert.That(messages[1].IpfsContent, Does.Not.Contain(ReviewPrompt));
            Assert.That(await DecryptMessageAsync(host, messages[1]), Is.EqualTo(ReviewPrompt));
            using var properties = JsonDocument.Parse(bucket.Properties!);
            Assert.That(properties.RootElement.GetProperty("kind").GetString(),
                Is.EqualTo("lawyer_document_review"));
            Assert.That(properties.RootElement.GetProperty("listing_id").GetString(), Is.EqualTo("12"));
            Assert.That(properties.RootElement.GetProperty("property_id").GetString(), Is.EqualTo("12"));
            Assert.That(properties.RootElement.GetProperty("cluster").GetString(), Is.EqualTo("mainnet"));
        });
    }

    [Test]
    public async Task Legacy_redelivery_finishing_last_cannot_remove_new_cluster_metadata()
    {
        var barrier = new LegacyMetadataUpdateBarrier();
        await using var host = await WebhooksHost.StartAsync(
            webhookSecret: WebhookSecret, bucketInterceptor: barrier);
        await SeedNamespaceAsync(host);
        await SeedPrerequisitesAsync(host);
        await PostDeveloperAsync(host, cluster: null, propertyId: null);
        var bucketId = await ReviewBucketIdAsync(host);
        var originalKey = await host.WithDbAsync(async db =>
        {
            var bucket = await db.Buckets.SingleAsync(row => row.BucketId == bucketId);
            var properties = JsonNode.Parse(bucket.Properties!)!.AsObject();
            properties.Remove("property_id");
            properties.Remove("cluster");
            bucket.Properties = properties.ToJsonString();
            await db.SaveChangesAsync();
            return bucket.EncryptionKey;
        });
        barrier.Enabled = true;

        var legacy = PostDeveloperAsync(host, cluster: null, propertyId: null);
        var current = PostDeveloperAsync(host);
        var responses = await Task.WhenAll(legacy, current);
        var bucket = (await host.BucketsAsync()).Single(row => row.BucketId == bucketId);
        var messageCount = await host.WithDbAsync(async db => await db.Messages
            .CountAsync(message => message.BucketId == bucketId));

        Assert.Multiple(() =>
        {
            Assert.That(responses.Select(response => response.Status), Is.All.EqualTo(HttpStatusCode.OK));
            Assert.That(barrier.LegacyFinishedLast, Is.True,
                $"the legacy write must finish last; updates: {barrier.Trace}");
            Assert.That(bucket.EncryptionKey, Is.EqualTo(originalKey));
            Assert.That(messageCount, Is.EqualTo(4));
            using var properties = JsonDocument.Parse(bucket.Properties!);
            Assert.That(properties.RootElement.GetProperty("property_id").GetString(), Is.EqualTo("12"));
            Assert.That(properties.RootElement.GetProperty("cluster").GetString(), Is.EqualTo("devnet"));
        });
    }

    /// <summary>The review bucket's persisted verdict ledger, as the bucket metadata holds it.</summary>
    private static async Task<JsonElement> ReviewLedgerAsync(WebhooksHost host, long bucketId)
    {
        var bucket = (await host.BucketsAsync()).Single(row => row.BucketId == bucketId);
        using var properties = JsonDocument.Parse(bucket.Properties!);
        return properties.RootElement.TryGetProperty("verdict_ledger", out var ledger)
            ? ledger.Clone()
            : default;
    }

    private static async Task<string?> BucketEncryptionKeyAsync(WebhooksHost host, long bucketId) =>
        (await host.BucketsAsync()).Single(row => row.BucketId == bucketId).EncryptionKey;

    // ---------------------------------------------------------------------------------------
    // The pinned v2 documents_confirmed / silent_verdict_resolved / deal_executed contract.
    // ---------------------------------------------------------------------------------------

    [Test]
    public async Task Missing_log_evidence_is_accepted_and_reconciled_without_a_status()
    {
        await using var host = await WebhooksHost.StartAsync(webhookSecret: WebhookSecret);
        var bucketId = await SeedReviewBucketAsync(host);
        host.Indexer.ListingReview = ReviewState(spvStatus: "APPROVED", spvHash: DocumentsHash);

        // The indexer could not decode the program's log, so it delivers the instruction evidence
        // with no post-instruction status. It must still be accepted, and still recorded.
        var (status, body) = await PostConfirmationAsync(host, logEvidence: "missing");
        var messages = await ReviewMessagesAsync(host, bucketId);
        var approval = messages.Single(message => message.Tag == "lawyer/terms-approved");
        var open = messages.Single(message => message.Tag == "voting/terms");
        var ledger = await ReviewLedgerAsync(host, bucketId);

        await Assert.MultipleAsync(async () =>
        {
            Assert.That(status, Is.EqualTo(HttpStatusCode.OK));
            Assert.That(body, Does.Contain("\"status\":\"posted\""));
            Assert.That(messages, Has.Count.EqualTo(6));
            Assert.That(await DecryptMessageAsync(host, approval), Is.EqualTo("Approved terms"));
            Assert.That(ledger.GetArrayLength(), Is.EqualTo(1));
            Assert.That(ledger[0]!.GetProperty("s").ValueKind, Is.EqualTo(JsonValueKind.Null),
                "an undecoded log carries no event-time status");
            using var openBody = JsonDocument.Parse(open.IpfsContent!);
            Assert.That(openBody.RootElement.GetProperty("status").GetString(), Is.EqualTo("open"));
            Assert.That(openBody.RootElement.GetProperty("listingStatus").GetString(),
                Is.EqualTo("SOLD_OUT"));
        });
    }

    [Test]
    public async Task Malformed_log_evidence_is_accepted_and_leaves_the_ledger_to_decide()
    {
        await using var host = await WebhooksHost.StartAsync(webhookSecret: WebhookSecret);
        var bucketId = await SeedReviewBucketAsync(host);
        host.Indexer.ListingReview = ReviewState(spvStatus: "APPROVED", spvHash: DocumentsHash);
        await PostConfirmationAsync(host);
        host.Indexer.ListingReview = ReviewState(
            spvStatus: "APPROVED",
            spvHash: DocumentsHash,
            developerStatus: "APPROVED",
            developerHash: DocumentsHash,
            status: "LEGAL");

        var (status, _) = await PostConfirmationAsync(
            host, lawyer: DeveloperLawyer, instructionPath: "1", logEvidence: "malformed");
        var messages = await ReviewMessagesAsync(host, bucketId);

        await Assert.MultipleAsync(async () =>
        {
            Assert.That(status, Is.EqualTo(HttpStatusCode.OK));
            Assert.That(messages, Has.Count.EqualTo(8));
            Assert.That(messages.Count(message => message.Tag == "lawyer/terms-approved"),
                Is.EqualTo(2));
            var result = messages.Single(message => message.Tag == "voting/result");
            using var body = JsonDocument.Parse(result.IpfsContent!);
            Assert.That(body.RootElement.GetProperty("status").GetString(), Is.EqualTo("ratified"));
            Assert.That(body.RootElement.GetProperty("listingStatus").GetString(), Is.EqualTo("LEGAL"));
        });
    }

    [Test]
    public async Task Two_verdict_instructions_in_one_transaction_post_two_distinct_messages()
    {
        await using var host = await WebhooksHost.StartAsync(webhookSecret: WebhookSecret);
        var bucketId = await SeedReviewBucketAsync(host);
        host.Indexer.ListingReview = ReviewState(spvStatus: "APPROVED", spvHash: DocumentsHash);

        // One transaction can carry both seats' instructions; the dedup identity is the
        // (event, tx_signature, instruction_path) triple, so both must land.
        var first = await PostConfirmationAsync(host, instructionPath: "0");
        host.Indexer.ListingReview = ReviewState(
            spvStatus: "APPROVED",
            spvHash: DocumentsHash,
            developerStatus: "APPROVED",
            developerHash: DocumentsHash,
            status: "LEGAL");
        var second = await PostConfirmationAsync(host, lawyer: DeveloperLawyer, instructionPath: "1");
        var messages = await ReviewMessagesAsync(host, bucketId);
        var ledger = await ReviewLedgerAsync(host, bucketId);

        await Assert.MultipleAsync(async () =>
        {
            Assert.That(first.Status, Is.EqualTo(HttpStatusCode.OK));
            Assert.That(second.Status, Is.EqualTo(HttpStatusCode.OK));
            Assert.That(second.Body, Does.Contain("\"status\":\"posted\""));
            Assert.That(messages, Has.Count.EqualTo(8));
            Assert.That(messages
                .Where(message => message.Tag == "lawyer/terms-approved")
                .Select(message => message.Reference),
                Is.EquivalentTo(new[]
                {
                    $"lawyer-review/12/approved/{ConfirmationTx}:0",
                    $"lawyer-review/12/approved/{ConfirmationTx}:1"
                }));
            Assert.That(ledger.GetArrayLength(), Is.EqualTo(2));
            Assert.That(messages.Single(message => message.Tag == "voting/result").Reference,
                Is.EqualTo("voting/result/terms/12/attempt/1"));
        });
    }

    [Test]
    public async Task Duplicate_redelivery_of_one_instruction_path_posts_exactly_one_verdict()
    {
        await using var host = await WebhooksHost.StartAsync(webhookSecret: WebhookSecret);
        var bucketId = await SeedReviewBucketAsync(host);
        host.Indexer.ListingReview = ReviewState(spvStatus: "APPROVED", spvHash: DocumentsHash);
        var key = await BucketEncryptionKeyAsync(host, bucketId);

        var deliveries = new List<(HttpStatusCode Status, string Body)>();
        for (var attempt = 0; attempt < 3; attempt++)
        {
            deliveries.Add(await PostConfirmationAsync(host));
        }

        var messages = await ReviewMessagesAsync(host, bucketId);
        var ledger = await ReviewLedgerAsync(host, bucketId);

        await Assert.MultipleAsync(async () =>
        {
            Assert.That(deliveries.Select(delivery => delivery.Status),
                Is.All.EqualTo(HttpStatusCode.OK));
            Assert.That(deliveries[1].Body, Does.Contain("\"status\":\"ignored\""));
            Assert.That(deliveries[2].Body, Does.Contain("\"status\":\"ignored\""));
            Assert.That(messages, Has.Count.EqualTo(6));
            Assert.That(messages.Count(message => message.Tag == "lawyer/terms-approved"),
                Is.EqualTo(1));
            Assert.That(messages.Count(message => message.Tag == "voting/terms"), Is.EqualTo(1));
            Assert.That(ledger.GetArrayLength(), Is.EqualTo(1));
            Assert.That(await BucketEncryptionKeyAsync(host, bucketId), Is.EqualTo(key));
        });
    }

    [Test]
    public async Task Unknown_future_fields_are_ignored()
    {
        await using var host = await WebhooksHost.StartAsync(webhookSecret: WebhookSecret);
        var bucketId = await SeedReviewBucketAsync(host);
        host.Indexer.ListingReview = ReviewState(spvStatus: "APPROVED", spvHash: DocumentsHash);

        var (status, _) = await PostConfirmationAsync(host, futureField: "SettlementEscrowPDA1");
        var messages = await ReviewMessagesAsync(host, bucketId);

        Assert.Multiple(() =>
        {
            Assert.That(status, Is.EqualTo(HttpStatusCode.OK));
            Assert.That(messages, Has.Count.EqualTo(6));
        });
    }

    [Test]
    public async Task Legacy_property_only_deal_shape_still_finalizes_the_review()
    {
        await using var host = await WebhooksHost.StartAsync(webhookSecret: WebhookSecret);
        await SeedNamespaceAsync(host);
        await SeedLegalBucketAsync(host);
        await SeedPrerequisitesAsync(host);
        var (_, setup) = await PostSpvAsync(host);
        var bucketId = setup.GetProperty("lawyerReviewBucketId").GetInt64();
        host.Indexer.ListingReview = ReviewState(
            spvStatus: "APPROVED",
            spvHash: DocumentsHash,
            developerStatus: "APPROVED",
            developerHash: DocumentsHash,
            status: "FINALIZED");

        // Pre-v2 rows carry no listing_id, property_id, cluster or status and keep arriving.
        var (status, _) = await PostDealAsync(host, listingId: null, legacyShape: true);
        var messages = await ReviewMessagesAsync(host, bucketId);

        Assert.Multiple(() =>
        {
            Assert.That(status, Is.EqualTo(HttpStatusCode.OK));
            Assert.That(messages.Single(message =>
                    message.Tag == "voting/result"
                    && message.Reference!.EndsWith("/finalized")).Reference,
                Is.EqualTo("voting/result/terms/12/attempt/1/finalized"));
        });
    }

    // ---------------------------------------------------------------------------------------
    // Split resets, derived from the persisted verdict ledger rather than from the mirror.
    // ---------------------------------------------------------------------------------------

    [TestCase(true)]
    [TestCase(false)]
    public async Task Split_verdicts_close_attempt_one_whichever_seat_voted_first(bool approveFirst)
    {
        await using var host = await WebhooksHost.StartAsync(webhookSecret: WebhookSecret);
        var bucketId = await SeedReviewBucketAsync(host);
        var key = await BucketEncryptionKeyAsync(host, bucketId);
        // Both seats confirm the same documents; one approves and one rejects. Which of the two
        // instructions reaches the API first must not change what the bucket records, so the
        // mirror is scripted to match whichever verdict arrives first.
        host.Indexer.ListingReview = approveFirst
            ? ReviewState(spvStatus: "APPROVED", spvHash: DocumentsHash)
            : ReviewState(developerStatus: "REJECTED", developerHash: DocumentsHash);
        var firstDelivery = approveFirst
            ? await PostConfirmationAsync(host, approve: true, slot: 487_700_300)
            : await PostConfirmationAsync(
                host, lawyer: DeveloperLawyer, approve: false,
                instructionPath: "1", slot: 487_700_300);
        host.Indexer.ListingReview = ReviewState(secondAttempt: true);
        var secondDelivery = approveFirst
            ? await PostConfirmationAsync(
                host, lawyer: DeveloperLawyer, approve: false,
                instructionPath: "1", txSignature: "SecondConfirmTx111", slot: 487_700_310)
            : await PostConfirmationAsync(
                host, approve: true, txSignature: "SecondConfirmTx111", slot: 487_700_310);

        var messages = await ReviewMessagesAsync(host, bucketId);
        var ledger = await ReviewLedgerAsync(host, bucketId);

        // The late redelivery of the first instruction must not reopen attempt two.
        var redelivery = approveFirst
            ? await PostConfirmationAsync(host, approve: true, slot: 487_700_300)
            : await PostConfirmationAsync(
                host, lawyer: DeveloperLawyer, approve: false,
                instructionPath: "1", slot: 487_700_300);

        await Assert.MultipleAsync(async () =>
        {
            Assert.That(firstDelivery.Status, Is.EqualTo(HttpStatusCode.OK));
            Assert.That(secondDelivery.Status, Is.EqualTo(HttpStatusCode.OK));
            Assert.That(redelivery.Body, Does.Contain("\"status\":\"ignored\""));
            Assert.That(ledger.GetArrayLength(), Is.EqualTo(2));
            Assert.That(messages, Has.Count.EqualTo(9));
            Assert.That(messages.Count(message => message.Tag == "lawyer/terms-approved"),
                Is.EqualTo(1));
            Assert.That(messages.Count(message => message.Tag == "lawyer/terms-rejected"),
                Is.EqualTo(1));
            Assert.That(messages.Count(message =>
                message.Tag == "voting/terms"
                && message.Reference == "voting/terms/12/attempt/1/open"), Is.EqualTo(1));
            Assert.That(messages.Count(message =>
                message.Tag == "voting/terms"
                && message.Reference == "voting/terms/12/attempt/2/open"), Is.EqualTo(1));
            var closed = messages.Single(message =>
                message.Reference == "voting/result/terms/12/attempt/1");
            using (var closedBody = JsonDocument.Parse(closed.IpfsContent!))
            {
                Assert.That(closedBody.RootElement.GetProperty("status").GetString(),
                    Is.EqualTo("rejected"));
                Assert.That(closedBody.RootElement.GetProperty("round").GetInt32(), Is.EqualTo(1));
                Assert.That(closedBody.RootElement.GetProperty("documentsHash").GetString(),
                    Is.EqualTo(DocumentsHash));
            }

            var reopened = messages.Single(message =>
                message.Reference == "voting/terms/12/attempt/2/open");
            using var reopenedBody = JsonDocument.Parse(reopened.IpfsContent!);
            Assert.That(reopenedBody.RootElement.GetProperty("round").GetInt32(), Is.EqualTo(2));
            Assert.That(reopenedBody.RootElement.TryGetProperty("documentsHash", out _), Is.False,
                "the reset cleared both seats' document hashes");
            Assert.That(DateTimeOffset.Parse(
                reopenedBody.RootElement.GetProperty("expiresAt").GetString()!).ToUnixTimeSeconds(),
                Is.EqualTo(LegalDeadline));
            Assert.That(await BucketEncryptionKeyAsync(host, bucketId), Is.EqualTo(key));
        });
    }

    [Test]
    public async Task Split_is_announced_from_the_ledger_while_the_mirror_lags_behind()
    {
        await using var host = await WebhooksHost.StartAsync(webhookSecret: WebhookSecret);
        var bucketId = await SeedReviewBucketAsync(host);
        host.Indexer.ListingReview = ReviewState(spvStatus: "APPROVED", spvHash: DocumentsHash);
        await PostConfirmationAsync(host, slot: 487_700_300);

        // The mirror still shows attempt one open with the second seat unruled — the verdict it
        // contradicts already has its own row in the ledger, which is what settles the round.
        var (status, _) = await PostConfirmationAsync(
            host,
            lawyer: DeveloperLawyer,
            approve: false,
            instructionPath: "1",
            txSignature: "SecondConfirmTx111",
            slot: 487_700_310,
            status: "SoldOut");
        var messages = await ReviewMessagesAsync(host, bucketId);

        await Assert.MultipleAsync(async () =>
        {
            Assert.That(status, Is.EqualTo(HttpStatusCode.OK));
            Assert.That(messages, Has.Count.EqualTo(9));
            Assert.That(messages.Any(message =>
                message.Reference == "voting/result/terms/12/attempt/1"), Is.True);
            Assert.That(messages.Any(message =>
                message.Reference == "voting/terms/12/attempt/2/open"), Is.True);
        });
    }

    [Test]
    public async Task Second_split_cancels_from_the_ledger_without_reopening_the_review()
    {
        await using var host = await WebhooksHost.StartAsync(webhookSecret: WebhookSecret);
        var bucketId = await SeedReviewBucketAsync(host);
        host.Indexer.ListingReview = ReviewState(spvStatus: "APPROVED", spvHash: DocumentsHash);
        await PostConfirmationAsync(host, approve: true, slot: 487_700_300);
        host.Indexer.ListingReview = ReviewState(secondAttempt: true);
        await PostConfirmationAsync(
            host, lawyer: DeveloperLawyer, approve: false, instructionPath: "1",
            txSignature: "SecondConfirmTx111", slot: 487_700_310);

        // Attempt two splits the other way round; the mirror is still reporting the round open.
        host.Indexer.ListingReview = ReviewState(secondAttempt: true, spvStatus: "REJECTED",
            spvHash: DocumentsHash);
        await PostConfirmationAsync(
            host, approve: false, txSignature: "ThirdConfirmTx111", slot: 487_700_320);
        // The second split's log could not be decoded, so nothing but the ledger says how the
        // round ended: attempt two closed on a split and the sale is cancelled.
        var (status, _) = await PostConfirmationAsync(
            host,
            lawyer: DeveloperLawyer,
            approve: true,
            instructionPath: "1",
            txSignature: "FourthConfirmTx111",
            slot: 487_700_330,
            logEvidence: "missing");

        var messages = await ReviewMessagesAsync(host, bucketId);
        var ledger = await ReviewLedgerAsync(host, bucketId);

        await Assert.MultipleAsync(async () =>
        {
            Assert.That(status, Is.EqualTo(HttpStatusCode.OK));
            Assert.That(ledger.GetArrayLength(), Is.EqualTo(4));
            var attemptTwo = messages
                .Where(message => message.Reference == "voting/result/terms/12/attempt/2")
                .ToList();
            // The second split both closes attempt two and settles the sale, so attempt two carries
            // two distinct announcements: the split that closed it, and the cancellation it led to.
            Assert.That(attemptTwo, Has.Count.EqualTo(2));
            Assert.That(attemptTwo.Select(message => message.Description), Is.EqualTo(new[]
            {
                "Lawyer document review attempt 2 rejected",
                "Lawyer document review attempt 2 cancelled"
            }).AsCollection);
            using var closedBody = JsonDocument.Parse(attemptTwo[0].IpfsContent!);
            Assert.That(closedBody.RootElement.GetProperty("status").GetString(), Is.EqualTo("rejected"));
            Assert.That(closedBody.RootElement.GetProperty("round").GetInt32(), Is.EqualTo(2));
            using var cancelledBody = JsonDocument.Parse(attemptTwo[1].IpfsContent!);
            Assert.That(cancelledBody.RootElement.GetProperty("status").GetString(),
                Is.EqualTo("cancelled"));
            Assert.That(cancelledBody.RootElement.GetProperty("listingStatus").GetString(),
                Is.EqualTo("CANCELLED"));
            Assert.That(cancelledBody.RootElement.GetProperty("round").GetInt32(), Is.EqualTo(2));
            Assert.That(messages.Count(message => message.Tag == "voting/result"), Is.EqualTo(3));
            Assert.That(messages.Any(message =>
                message.Tag == "voting/terms"
                && message.Reference!.Contains("attempt/3", StringComparison.Ordinal)), Is.False);
            // Attempt one opens carrying its hash; attempt two is announced when it opens (no seat
            // has ruled yet, so with no hash) and again when its first seat rules on a hash — the
            // byte-identical duplicate guard collapses redeliveries but not new information.
            Assert.That(messages.Count(message => message.Tag == "voting/terms"), Is.EqualTo(3));
            Assert.That(messages.Count(message =>
                message.Reference == "voting/terms/12/attempt/2/open"), Is.EqualTo(2));
        });
    }

    // ---------------------------------------------------------------------------------------
    // Delayed delivery, after the listing has left the indexer's active review mirror.
    // ---------------------------------------------------------------------------------------

    [Test]
    public async Task Verdict_is_accepted_after_the_review_row_left_the_indexer()
    {
        await using var host = await WebhooksHost.StartAsync(webhookSecret: WebhookSecret);
        var bucketId = await SeedReviewBucketAsync(host);
        host.Indexer.ListingReview = null;

        var first = await PostConfirmationAsync(host, status: "SoldOut");
        host.Indexer.ListingReview = null;
        var second = await PostConfirmationAsync(
            host, lawyer: DeveloperLawyer, approve: true, instructionPath: "1", status: "Legal");

        var messages = await ReviewMessagesAsync(host, bucketId);
        var approval = messages.Single(message =>
            message.Reference!.EndsWith(":0") && message.Tag == "lawyer/terms-approved");

        await Assert.MultipleAsync(async () =>
        {
            Assert.That(first.Status, Is.EqualTo(HttpStatusCode.OK));
            Assert.That(second.Status, Is.EqualTo(HttpStatusCode.OK));
            Assert.That(messages, Has.Count.EqualTo(8));
            Assert.That(approval.Contributor, Is.EqualTo(SpvLawyer));
            Assert.That(await DecryptMessageAsync(host, approval), Is.EqualTo("Approved terms"));
            var open = messages.Single(message => message.Tag == "voting/terms");
            using var openBody = JsonDocument.Parse(open.IpfsContent!);
            Assert.That(openBody.RootElement.GetProperty("documentsHash").GetString(),
                Is.EqualTo(DocumentsHash));
            Assert.That(openBody.RootElement.TryGetProperty("expiresAt", out _), Is.False,
                "the deadline lived only on the mirror row that is gone");
            var result = messages.Single(message => message.Tag == "voting/result");
            using var resultBody = JsonDocument.Parse(result.IpfsContent!);
            Assert.That(resultBody.RootElement.GetProperty("status").GetString(),
                Is.EqualTo("ratified"));
            Assert.That(resultBody.RootElement.GetProperty("listingStatus").GetString(),
                Is.EqualTo("LEGAL"));
        });
    }

    [Test]
    public async Task Status_and_deal_webhooks_reconcile_after_the_listing_disappeared()
    {
        await using var host = await WebhooksHost.StartAsync(webhookSecret: WebhookSecret);
        await SeedNamespaceAsync(host);
        var legalBucketId = await SeedLegalBucketAsync(host);
        await SeedPrerequisitesAsync(host);
        var (_, setup) = await PostSpvAsync(host);
        var bucketId = setup.GetProperty("lawyerReviewBucketId").GetInt64();
        host.Indexer.ListingReview = ReviewState(spvStatus: "APPROVED", spvHash: DocumentsHash);
        await PostConfirmationAsync(host);

        // The settlement arrives after the listing stopped being mirrored at all: the bucket's
        // seats and verdict ledger are all that is left to post against.
        host.Indexer.ListingReview = null;
        var (dealStatus, _) = await PostDealAsync(host);

        var messages = await ReviewMessagesAsync(host, bucketId);
        var legalContributors = await host.WithDbAsync(async db => await db.BucketContributors
            .Where(row => row.BucketId == legalBucketId)
            .Select(row => row.SubjectId)
            .ToListAsync());

        await Assert.MultipleAsync(async () =>
        {
            Assert.That(dealStatus, Is.EqualTo(HttpStatusCode.OK));
            Assert.That(legalContributors, Does.Not.Contain(SpvLawyer));
            Assert.That(legalContributors, Does.Not.Contain(DeveloperLawyer));
            Assert.That(messages.Single(message =>
                    message.Tag == "voting/result"
                    && message.Reference!.EndsWith("/finalized")).Reference,
                Is.EqualTo("voting/result/terms/12/attempt/1/finalized"));
            using var body = JsonDocument.Parse(messages[^1].IpfsContent!);
            Assert.That(body.RootElement.GetProperty("listingStatus").GetString(),
                Is.EqualTo("FINALIZED"));
        });
    }

    [Test]
    public async Task Silent_verdict_reconciles_after_the_listing_disappeared()
    {
        await using var host = await WebhooksHost.StartAsync(webhookSecret: WebhookSecret);
        var bucketId = await SeedReviewBucketAsync(host);
        host.Indexer.ListingReview = ReviewState(spvStatus: "APPROVED", spvHash: DocumentsHash);
        await PostConfirmationAsync(host);
        host.Indexer.ListingReview = null;

        var (statusStatus, _) = await PostStatusAsync(host, status: "Cancelled");
        var messages = await ReviewMessagesAsync(host, bucketId);

        await Assert.MultipleAsync(async () =>
        {
            Assert.That(statusStatus, Is.EqualTo(HttpStatusCode.OK));
            var result = messages.Single(message =>
                message.Reference == "voting/result/terms/12/attempt/1");
            Assert.That(result.Description,
                Is.EqualTo("Lawyer document review attempt 1 cancelled"));
            using var body = JsonDocument.Parse(result.IpfsContent!);
            Assert.That(body.RootElement.GetProperty("status").GetString(), Is.EqualTo("cancelled"));
            Assert.That(body.RootElement.GetProperty("listingStatus").GetString(),
                Is.EqualTo("CANCELLED"));
        });
    }

    // ---------------------------------------------------------------------------------------
    // Terminal outcomes: every listing status gets its own announcement, because the card the
    // dashboard builds from a message shows nothing but that status.
    // ---------------------------------------------------------------------------------------

    [TestCase("Cancelled", "cancelled", "CANCELLED")]
    [TestCase("Expired", "expired", "EXPIRED")]
    [TestCase("Refunding", "refunded", "REFUNDING")]
    public async Task Terminal_listing_status_announces_its_own_outcome_once(
        string wireStatus, string outcome, string listingStatus)
    {
        await using var host = await WebhooksHost.StartAsync(webhookSecret: WebhookSecret);
        var bucketId = await SeedReviewBucketAsync(host);
        host.Indexer.ListingReview = ReviewState(spvStatus: "APPROVED", spvHash: DocumentsHash);

        var (status, body) = await PostStatusAsync(host, status: wireStatus);
        var messages = await ReviewMessagesAsync(host, bucketId);
        var card = messages.Single(message => message.Tag == "voting/result");

        // The same instruction arriving twice must not put a second card in the chat.
        var (redelivery, redeliveryBody) = await PostStatusAsync(host, status: wireStatus);
        var redeliveredMessages = await ReviewMessagesAsync(host, bucketId);

        await Assert.MultipleAsync(async () =>
        {
            Assert.That(status, Is.EqualTo(HttpStatusCode.OK));
            Assert.That(body, Does.Contain("\"status\":\"posted\""));
            Assert.That(card.Reference, Is.EqualTo("voting/result/terms/12/attempt/1"));
            Assert.That(card.Description,
                Is.EqualTo($"Lawyer document review attempt 1 {outcome}"));
            using var announcement = JsonDocument.Parse(card.IpfsContent!);
            Assert.That(announcement.RootElement.GetProperty("kind").GetString(), Is.EqualTo("terms"));
            Assert.That(announcement.RootElement.GetProperty("status").GetString(), Is.EqualTo(outcome));
            Assert.That(announcement.RootElement.GetProperty("listingStatus").GetString(),
                Is.EqualTo(listingStatus));
            Assert.That(announcement.RootElement.GetProperty("round").GetInt32(), Is.EqualTo(1));
            Assert.That(announcement.RootElement.GetProperty("documentsHash").GetString(),
                Is.EqualTo(DocumentsHash));
            Assert.That(redelivery, Is.EqualTo(HttpStatusCode.OK));
            Assert.That(redeliveryBody, Does.Contain("\"status\":\"ignored\""));
            Assert.That(redeliveredMessages.Count(message => message.Tag == "voting/result"),
                Is.EqualTo(1));
            Assert.That(redeliveredMessages.Select(message => message.MessageId),
                Is.EqualTo(messages.Select(message => message.MessageId)));
        });
    }

    [Test]
    public async Task Settlement_announces_finalized_as_a_distinct_card_from_the_legal_it_follows()
    {
        await using var host = await WebhooksHost.StartAsync(webhookSecret: WebhookSecret);
        await SeedNamespaceAsync(host);
        await SeedLegalBucketAsync(host);
        await SeedPrerequisitesAsync(host);
        var (_, setup) = await PostSpvAsync(host);
        var bucketId = setup.GetProperty("lawyerReviewBucketId").GetInt64();
        host.Indexer.ListingReview = ReviewState(spvStatus: "APPROVED", spvHash: DocumentsHash);
        await PostConfirmationAsync(host, slot: 487_700_300);
        host.Indexer.ListingReview = ReviewState(
            spvStatus: "APPROVED",
            spvHash: DocumentsHash,
            developerStatus: "APPROVED",
            developerHash: DocumentsHash,
            status: "LEGAL");
        await PostConfirmationAsync(
            host, lawyer: DeveloperLawyer, instructionPath: "1", slot: 487_700_310);

        // The settlement is a later event on the same attempt. Its reference was always distinct,
        // so the byte-identity duplicate guard posts it too — which makes its status the only thing
        // telling the two cards apart.
        host.Indexer.ListingReview = ReviewState(
            spvStatus: "APPROVED",
            spvHash: DocumentsHash,
            developerStatus: "APPROVED",
            developerHash: DocumentsHash,
            status: "FINALIZED");
        var (dealStatus, _) = await PostDealAsync(host);
        var messages = await ReviewMessagesAsync(host, bucketId);
        var announcements = messages
            .Where(message => message.Tag == "voting/result")
            .ToList();

        var (redelivery, _) = await PostDealAsync(host);
        var redeliveredMessages = await ReviewMessagesAsync(host, bucketId);

        await Assert.MultipleAsync(async () =>
        {
            Assert.That(dealStatus, Is.EqualTo(HttpStatusCode.OK));
            Assert.That(announcements, Has.Count.EqualTo(2));
            Assert.That(announcements.Select(message => message.Reference), Is.EqualTo(new[]
            {
                "voting/result/terms/12/attempt/1",
                "voting/result/terms/12/attempt/1/finalized"
            }).AsCollection);
            Assert.That(announcements.Select(message => message.Description), Is.EqualTo(new[]
            {
                "Lawyer document review attempt 1 ratified",
                "Lawyer document review attempt 1 finalized"
            }).AsCollection);
            Assert.That(announcements.Select(message =>
                JsonDocument.Parse(message.IpfsContent!).RootElement
                    .GetProperty("status").GetString()),
                Is.EquivalentTo(new[] { "ratified", "finalized" }));
            Assert.That(announcements[0].IpfsContent, Is.Not.EqualTo(announcements[1].IpfsContent),
                "the settlement must not rebuild the Legal card byte for byte");
            Assert.That(announcements.Single(message => message.Reference!.EndsWith("/finalized"))
                .Description, Is.EqualTo("Lawyer document review attempt 1 finalized"));
            Assert.That(redelivery, Is.EqualTo(HttpStatusCode.OK));
            Assert.That(redeliveredMessages.Select(message => message.MessageId),
                Is.EqualTo(messages.Select(message => message.MessageId)));
        });
    }

    [Test]
    public async Task Round_open_announcement_posts_without_a_deadline_when_the_mirror_lags()
    {
        await using var host = await WebhooksHost.StartAsync(webhookSecret: WebhookSecret);
        var bucketId = await SeedReviewBucketAsync(host);
        // The commonest moment for a round-open announcement, and the mirror row is not there yet:
        // the OPEN round comes from replaying the verdict ledger, so the card posts with no
        // countdown rather than not at all.
        host.Indexer.ListingReview = null;

        var (status, body) = await PostConfirmationAsync(host, status: "SoldOut");
        var messages = await ReviewMessagesAsync(host, bucketId);
        var open = messages.Single(message => message.Tag == "voting/terms");

        var (redelivery, redeliveryBody) = await PostConfirmationAsync(host, status: "SoldOut");
        var redeliveredMessages = await ReviewMessagesAsync(host, bucketId);

        await Assert.MultipleAsync(async () =>
        {
            Assert.That(status, Is.EqualTo(HttpStatusCode.OK));
            Assert.That(body, Does.Contain("\"status\":\"posted\""));
            Assert.That(open.Contributor, Is.EqualTo(WebhooksHost.DefaultBucketAdminAddress));
            Assert.That(open.Reference, Is.EqualTo("voting/terms/12/attempt/1/open"));
            using var announcement = JsonDocument.Parse(open.IpfsContent!);
            Assert.That(announcement.RootElement.GetProperty("kind").GetString(), Is.EqualTo("terms"));
            Assert.That(announcement.RootElement.GetProperty("listingId").GetInt64(),
                Is.EqualTo(ListingId));
            Assert.That(announcement.RootElement.GetProperty("round").GetInt32(), Is.EqualTo(1));
            Assert.That(announcement.RootElement.GetProperty("status").GetString(), Is.EqualTo("open"));
            Assert.That(announcement.RootElement.GetProperty("listingStatus").GetString(),
                Is.EqualTo("SOLD_OUT"));
            Assert.That(announcement.RootElement.GetProperty("documentsHash").GetString(),
                Is.EqualTo(DocumentsHash));
            Assert.That(announcement.RootElement.TryGetProperty("expiresAt", out _), Is.False,
                "the deadline lives only on the mirror row that has not caught up");
            Assert.That(redelivery, Is.EqualTo(HttpStatusCode.OK));
            Assert.That(redeliveryBody, Does.Contain("\"status\":\"ignored\""));
            Assert.That(redeliveredMessages.Count(message => message.Tag == "voting/terms"),
                Is.EqualTo(1));
            Assert.That(redeliveredMessages.Select(message => message.MessageId),
                Is.EqualTo(messages.Select(message => message.MessageId)));
        });
    }

    // ---------------------------------------------------------------------------------------
    // Buckets an earlier build created: a redelivery repairs them instead of sticking at 503.
    // ---------------------------------------------------------------------------------------

    [Test]
    public async Task Legacy_plaintext_prompt_is_encrypted_on_redelivery_without_rotating_the_key()
    {
        await using var host = await WebhooksHost.StartAsync(webhookSecret: WebhookSecret);
        var bucketId = await SeedReviewBucketAsync(host);
        var key = await BucketEncryptionKeyAsync(host, bucketId);
        await host.WithDbAsync(async db =>
        {
            var prompt = await db.Messages.SingleAsync(message =>
                message.BucketId == bucketId && message.Tag == "lawyer/document-review");
            prompt.IpfsContent = ReviewPrompt;
            prompt.ContentType = "text/plain";
            await db.SaveChangesAsync();
            return true;
        });

        var (status, _) = await PostDeveloperAsync(host);
        var messages = await ReviewMessagesAsync(host, bucketId);
        var prompt = messages.Single(message => message.Tag == "lawyer/document-review");

        await Assert.MultipleAsync(async () =>
        {
            Assert.That(status, Is.EqualTo(HttpStatusCode.OK));
            Assert.That(messages, Has.Count.EqualTo(4), "the repair adds no messages");
            Assert.That(messages.Count(message =>
                message.Tag == "didcomm/key-sharing-v1"), Is.EqualTo(1));
            Assert.That(prompt.ContentType, Is.EqualTo("text/plain;charset=utf-8"));
            Assert.That(prompt.IpfsContent, Does.Not.Contain(ReviewPrompt));
            Assert.That(prompt.ContentHash, Is.EqualTo("0x" + Convert.ToHexString(
                SHA256.HashData(Encoding.UTF8.GetBytes(prompt.IpfsContent!)))));
            Assert.That(await DecryptMessageAsync(host, prompt), Is.EqualTo(ReviewPrompt));
            Assert.That(await BucketEncryptionKeyAsync(host, bucketId), Is.EqualTo(key));
        });
    }

    [Test]
    public async Task Legacy_bucket_the_admin_cannot_decrypt_repairs_on_the_next_verdict()
    {
        await using var host = await WebhooksHost.StartAsync(webhookSecret: WebhookSecret);
        var bucketId = await SeedReviewBucketAsync(host);
        var originalKey = await BucketEncryptionKeyAsync(host, bucketId);

        // The genuine legacy shape: the key-sharing message lists the lawyers and the holders but
        // never the operator admin, so the API could not encrypt a verdict for this bucket at all.
        await host.WithDbAsync(async db =>
        {
            var keySharing = await db.Messages.SingleAsync(message =>
                message.BucketId == bucketId && message.Tag == "didcomm/key-sharing-v1");
            var plaintext = LawyerTermsBucketProtocolTests.DecryptForRecipient(
                keySharing.IpfsContent!, WebhooksHost.DefaultBucketAdminPrivateKey);
            var payload = JsonNode.Parse(plaintext)!.AsObject();
            var recipients = new JsonArray
            {
                TestDb.Key32Base64Url,
                TestDb.OtherKey32Base64Url,
                TestDb.ViewerKey32Base64Url,
                TestDb.Hash32Base64Url
            };
            payload["to"] = recipients;
            keySharing.IpfsContent = LawyerTermsBucketProtocol.EncryptForRecipients(
                payload.ToJsonString(),
                recipients.Select(node =>
                    X25519KeyCodec.Decode32(node!.GetValue<string>(), "recipient")).ToList());
            await db.SaveChangesAsync();
            return true;
        });
        var before = await ReviewMessagesAsync(host, bucketId);
        Assert.That(before, Has.Count.EqualTo(4));
        Assert.ThrowsAsync< CryptographicException>(async () => await BucketKeyAsync(
            host, bucketId));

        host.Indexer.ListingReview = ReviewState(spvStatus: "APPROVED", spvHash: DocumentsHash);
        var (status, body) = await PostConfirmationAsync(host);
        Assert.That(status, Is.EqualTo(HttpStatusCode.OK), body);
        var after = await ReviewMessagesAsync(host, bucketId);
        var approval = after.Single(message => message.Tag == "lawyer/terms-approved");
        var newKey = await BucketKeyAsync(host, bucketId);

        await Assert.MultipleAsync(async () =>
        {
            Assert.That(status, Is.EqualTo(HttpStatusCode.OK));
            Assert.That(after, Has.Count.EqualTo(7));
            Assert.That(after.Take(4).Select(message => message.IpfsContent),
                Is.EqualTo(before.Select(message => message.IpfsContent)),
                "every message the bucket already held stays byte-for-byte as it was");
            Assert.That(after.Count(message =>
                message.Tag == "didcomm/key-sharing-v1"), Is.EqualTo(2));
            Assert.That(await DecryptMessageAsync(host, approval), Is.EqualTo("Approved terms"));
            Assert.That(newKey.PrivateKey, Has.Length.EqualTo(32));
            Assert.That(after[4].IpfsContent, Does.Not.Contain("\"d\""));
            Assert.That(await BucketEncryptionKeyAsync(host, bucketId), Is.Not.EqualTo(originalKey));
        });

        // And the repair converges: redelivering adds neither another key nor another message.
        var convergedKey = await BucketEncryptionKeyAsync(host, bucketId);
        var redelivered = await PostConfirmationAsync(host);
        var messages = await ReviewMessagesAsync(host, bucketId);

        await Assert.MultipleAsync(async () =>
        {
            Assert.That(redelivered.Status, Is.EqualTo(HttpStatusCode.OK));
            Assert.That(messages, Has.Count.EqualTo(7));
            Assert.That(messages.Count(message =>
                message.Tag == "didcomm/key-sharing-v1"), Is.EqualTo(2));
            Assert.That(await DecryptMessageAsync(host, messages[5]), Is.EqualTo("Approved terms"));
            Assert.That(await BucketEncryptionKeyAsync(host, bucketId), Is.EqualTo(convergedKey));
        });
    }

    private sealed class LegacyMetadataUpdateBarrier
        : Microsoft.EntityFrameworkCore.Diagnostics.DbCommandInterceptor
    {
        private readonly TaskCompletionSource _currentUpdate =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public bool Enabled { get; set; }
        public bool LegacyFinishedLast { get; private set; }
        public string Trace { get; private set; } = "";

        public override async ValueTask<Microsoft.EntityFrameworkCore.Diagnostics.InterceptionResult<System.Data.Common.DbDataReader>>
            ReaderExecutingAsync(
                System.Data.Common.DbCommand command,
                Microsoft.EntityFrameworkCore.Diagnostics.CommandEventData eventData,
                Microsoft.EntityFrameworkCore.Diagnostics.InterceptionResult<System.Data.Common.DbDataReader> result,
                CancellationToken cancellationToken = default)
        {
            var metadataUpdate = Enabled
                && command.CommandText.Contains("UPDATE", StringComparison.Ordinal)
                && command.CommandText.Contains("buckets", StringComparison.Ordinal);
            var current = command.Parameters.Cast<System.Data.Common.DbParameter>()
                .Any(parameter => parameter.Value is string value
                    && (value.Contains("\"cluster\"", StringComparison.Ordinal) || value == "devnet"));
            if (metadataUpdate)
            {
                Trace += $"\ncurrent={current}: " + string.Join(" | ",
                    command.Parameters.Cast<System.Data.Common.DbParameter>().Select(parameter => parameter.Value));
            }
            if (metadataUpdate && !current)
            {
                await _currentUpdate.Task.WaitAsync(TimeSpan.FromSeconds(15));
                var legacyResult = await base.ReaderExecutingAsync(
                    command, eventData, result, cancellationToken);
                LegacyFinishedLast = true;
                return legacyResult;
            }
            var executed = await base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
            if (metadataUpdate && current)
            {
                LegacyFinishedLast = false;
                _currentUpdate.TrySetResult();
            }
            return executed;
        }

        public override async ValueTask<Microsoft.EntityFrameworkCore.Diagnostics.InterceptionResult<int>>
            NonQueryExecutingAsync(
                System.Data.Common.DbCommand command,
                Microsoft.EntityFrameworkCore.Diagnostics.CommandEventData eventData,
                Microsoft.EntityFrameworkCore.Diagnostics.InterceptionResult<int> result,
                CancellationToken cancellationToken = default)
        {
            var metadataUpdate = Enabled
                && command.CommandText.Contains("UPDATE", StringComparison.Ordinal)
                && command.CommandText.Contains("buckets", StringComparison.Ordinal);
            var current = command.Parameters.Cast<System.Data.Common.DbParameter>()
                .Any(parameter => parameter.Value is string value
                    && (value.Contains("\"cluster\"", StringComparison.Ordinal) || value == "devnet"));
            if (metadataUpdate)
            {
                Trace += $"\ncurrent={current}: " + string.Join(" | ",
                    command.Parameters.Cast<System.Data.Common.DbParameter>().Select(parameter => parameter.Value));
            }
            if (metadataUpdate && !current)
            {
                await _currentUpdate.Task.WaitAsync(TimeSpan.FromSeconds(15));
                var legacyResult = await base.NonQueryExecutingAsync(
                    command, eventData, result, cancellationToken);
                LegacyFinishedLast = true;
                return legacyResult;
            }
            var executed = await base.NonQueryExecutingAsync(command, eventData, result, cancellationToken);
            if (metadataUpdate && current)
            {
                LegacyFinishedLast = false;
                _currentUpdate.TrySetResult();
            }
            return executed;
        }
    }

    [TestCase("testnet", ListingId)]
    [TestCase("devnet", 13L)]
    public async Task Invalid_new_metadata_returns_400_without_a_bucket(string cluster, long propertyId)
    {
        await using var host = await WebhooksHost.StartAsync(webhookSecret: WebhookSecret);
        await SeedNamespaceAsync(host);
        await SeedPrerequisitesAsync(host);

        var (status, _) = await PostDeveloperAsync(host, cluster: cluster, propertyId: propertyId);

        await Assert.MultipleAsync(async () =>
        {
            Assert.That(status, Is.EqualTo(HttpStatusCode.BadRequest));
            Assert.That(await ReviewBucketCountAsync(host), Is.Zero);
        });
    }

    [TestCase(null, null)]
    [TestCase("", DeveloperLawyer)]
    [TestCase(SystemDefaultPubkey, DeveloperLawyer)]
    [TestCase(SpvLawyer, "")]
    [TestCase(SpvLawyer, SystemDefaultPubkey)]
    public async Task Incomplete_lawyer_pair_returns_404_without_a_bucket(
        string? spvLawyer, string? developerLawyer)
    {
        await using var host = await WebhooksHost.StartAsync(webhookSecret: WebhookSecret);
        await SeedNamespaceAsync(host);
        host.Indexer.ListingLawyers = spvLawyer is null && developerLawyer is null
            ? null
            : new ListingLawyersInfo(spvLawyer!, developerLawyer!);

        var (status, body) = await PostDeveloperAsync(host);

        await Assert.MultipleAsync(async () =>
        {
            Assert.That(status, Is.EqualTo(HttpStatusCode.NotFound));
            Assert.That(body, Does.Contain("Both lawyer seats must be assigned"));
            Assert.That(await ReviewBucketCountAsync(host), Is.Zero);
        });
    }

    [Test]
    public async Task Missing_admin_returns_503_without_a_bucket()
    {
        await using var host = await WebhooksHost.StartAsync(
            webhookSecret: WebhookSecret, bucketAdminAddress: "");
        await SeedNamespaceAsync(host);
        await SeedPrerequisitesAsync(host);

        var (status, _) = await PostDeveloperAsync(host);

        await Assert.MultipleAsync(async () =>
        {
            Assert.That(status, Is.EqualTo(HttpStatusCode.ServiceUnavailable));
            Assert.That(await ReviewBucketCountAsync(host), Is.Zero);
        });
    }

    [Test]
    public async Task Missing_namespace_returns_404_without_a_bucket()
    {
        await using var host = await WebhooksHost.StartAsync(webhookSecret: WebhookSecret);
        await SeedPrerequisitesAsync(host);

        var (status, _) = await PostDeveloperAsync(host);

        await Assert.MultipleAsync(async () =>
        {
            Assert.That(status, Is.EqualTo(HttpStatusCode.NotFound));
            Assert.That(await ReviewBucketCountAsync(host), Is.Zero);
        });
    }

    [TestCase(HolderOne)]
    [TestCase(SpvLawyer)]
    [TestCase(DeveloperLawyer)]
    public async Task Missing_required_viewer_key_returns_404_without_a_bucket(string missingKey)
    {
        await using var host = await WebhooksHost.StartAsync(webhookSecret: WebhookSecret);
        await SeedNamespaceAsync(host);
        await SeedPrerequisitesAsync(host, missingKey: missingKey);

        var (status, _) = await PostDeveloperAsync(host);

        await Assert.MultipleAsync(async () =>
        {
            Assert.That(status, Is.EqualTo(HttpStatusCode.NotFound));
            Assert.That(await ReviewBucketCountAsync(host), Is.Zero);
        });
    }

    [Test]
    public async Task Indexer_resolution_failure_returns_502_without_a_bucket()
    {
        await using var host = await WebhooksHost.StartAsync(webhookSecret: WebhookSecret);
        await SeedNamespaceAsync(host);
        await SeedPrerequisitesAsync(host);
        host.Indexer.ThrowOnQuery = true;

        var (status, _) = await PostDeveloperAsync(host);

        await Assert.MultipleAsync(async () =>
        {
            Assert.That(status, Is.EqualTo(HttpStatusCode.BadGateway));
            Assert.That(await ReviewBucketCountAsync(host), Is.Zero);
        });
    }

    [Test]
    public async Task Holder_resolution_failure_returns_502_without_a_bucket()
    {
        await using var host = await WebhooksHost.StartAsync(webhookSecret: WebhookSecret);
        await SeedNamespaceAsync(host);
        await SeedPrerequisitesAsync(host);
        host.Indexer.ThrowOnHolders = true;

        var (status, _) = await PostDeveloperAsync(host);

        await Assert.MultipleAsync(async () =>
        {
            Assert.That(status, Is.EqualTo(HttpStatusCode.BadGateway));
            Assert.That(await ReviewBucketCountAsync(host), Is.Zero);
        });
    }

    [Test]
    public async Task Object_storage_failure_returns_502_without_a_bucket()
    {
        await using var host = await WebhooksHost.StartAsync(webhookSecret: WebhookSecret);
        await SeedNamespaceAsync(host);
        await SeedPrerequisitesAsync(host);
        host.S3.DownloadError = new AmazonS3Exception("simulated object storage failure");

        var (status, _) = await PostDeveloperAsync(host);

        await Assert.MultipleAsync(async () =>
        {
            Assert.That(status, Is.EqualTo(HttpStatusCode.BadGateway));
            Assert.That(await ReviewBucketCountAsync(host), Is.Zero);
        });
    }

    [Test]
    public async Task Long_namespace_name_is_truncated_to_256_characters()
    {
        await using var host = await WebhooksHost.StartAsync(webhookSecret: WebhookSecret);
        await SeedNamespaceAsync(host, new string('x', 256));
        await SeedPrerequisitesAsync(host);

        var (status, _) = await PostDeveloperJsonAsync(host);
        var bucketId = await ReviewBucketIdAsync(host);
        var name = await host.WithDbAsync(async db => await db.Buckets
            .Where(bucket => bucket.BucketId == bucketId)
            .Select(bucket => bucket.Name)
            .SingleAsync());
        const string suffix = " - Lawyer document review";

        Assert.Multiple(() =>
        {
            Assert.That(status, Is.EqualTo(HttpStatusCode.OK));
            Assert.That(name, Is.EqualTo(new string('x', 256 - suffix.Length) + suffix));
            Assert.That(name, Has.Length.EqualTo(256));
        });
    }

    [Test]
    public async Task Duplicate_wallets_and_keys_preserve_roles_and_single_viewers()
    {
        await using var host = await WebhooksHost.StartAsync(webhookSecret: WebhookSecret);
        await SeedNamespaceAsync(host);
        await SeedPrerequisitesAsync(host, holders: [SpvLawyer, DeveloperLawyer, HolderOne, HolderOne]);
        await host.SeedViewerKeyAsync(HolderOne, TestDb.Key32);

        var (status, _) = await PostDeveloperJsonAsync(host);
        var bucketId = await ReviewBucketIdAsync(host);
        var state = await host.WithDbAsync(async db => new
        {
            Contributors = await db.BucketContributors.Where(row => row.BucketId == bucketId)
                .Select(row => row.SubjectId).ToListAsync(),
            Viewers = await db.BucketViewers.Where(row => row.BucketId == bucketId)
                .Select(row => row.ViewerId).ToListAsync()
        });

        Assert.Multiple(() =>
        {
            Assert.That(status, Is.EqualTo(HttpStatusCode.OK));
            Assert.That(state.Contributors, Is.EquivalentTo(new[]
            {
                WebhooksHost.DefaultBucketAdminAddress, SpvLawyer, DeveloperLawyer
            }));
            Assert.That(state.Viewers, Is.EquivalentTo(new[]
            {
                WebhooksHost.DefaultBucketAdminViewerKey, TestDb.Key32Base64Url, TestDb.OtherKey32Base64Url
            }));
        });
    }

    [Test]
    public async Task No_claimed_holders_creates_lawyers_only_bucket()
    {
        await using var host = await WebhooksHost.StartAsync(webhookSecret: WebhookSecret);
        await SeedNamespaceAsync(host);
        await SeedPrerequisitesAsync(host, holders: []);

        var (status, body) = await PostDeveloperJsonAsync(host);
        var bucketId = await ReviewBucketIdAsync(host);
        var state = await host.WithDbAsync(async db => new
        {
            Contributors = await db.BucketContributors.Where(row => row.BucketId == bucketId)
                .Select(row => row.SubjectId).ToListAsync(),
            Viewers = await db.BucketViewers.Where(row => row.BucketId == bucketId)
                .Select(row => row.ViewerId).ToListAsync(),
            KeyMessage = await db.Messages.SingleAsync(message =>
                message.BucketId == bucketId && message.Tag == "didcomm/key-sharing-v1")
        });

        Assert.Multiple(() =>
        {
            Assert.That(status, Is.EqualTo(HttpStatusCode.OK));
            Assert.That(body.GetProperty("lawyerReviewHolderCount").GetInt32(), Is.Zero);
            Assert.That(state.Contributors, Is.EquivalentTo(new[]
            {
                WebhooksHost.DefaultBucketAdminAddress, SpvLawyer, DeveloperLawyer
            }));
            Assert.That(state.Viewers, Is.EquivalentTo(new[]
            {
                WebhooksHost.DefaultBucketAdminViewerKey, TestDb.Key32Base64Url, TestDb.OtherKey32Base64Url
            }));
            using var keySharing = JsonDocument.Parse(state.KeyMessage.IpfsContent!);
            Assert.That(keySharing.RootElement.GetProperty("recipients").GetArrayLength(), Is.EqualTo(3));
        });
    }

    [Test]
    public async Task Spv_trigger_defers_until_developer_lawyer_is_assigned()
    {
        await using var host = await WebhooksHost.StartAsync(webhookSecret: WebhookSecret);
        await SeedNamespaceAsync(host);
        await SeedLegalBucketAsync(host);
        host.Indexer.SpvElection = new SpvElectionInfo(1, 1_790_000_000, 1, SpvLawyer);
        host.Indexer.ListingLawyers = new ListingLawyersInfo(SpvLawyer, SystemDefaultPubkey);

        var (status, body) = await PostSpvAsync(host);

        await Assert.MultipleAsync(async () =>
        {
            Assert.That(status, Is.EqualTo(HttpStatusCode.OK));
            Assert.That(body.TryGetProperty("lawyerReviewBucketId", out _), Is.False);
            Assert.That(await ReviewBucketCountAsync(host), Is.Zero);
        });
    }

    [Test]
    public async Task Spv_trigger_propagates_review_setup_failure()
    {
        await using var host = await WebhooksHost.StartAsync(webhookSecret: WebhookSecret);
        await SeedNamespaceAsync(host);
        await SeedLegalBucketAsync(host);
        await SeedPrerequisitesAsync(host, missingKey: DeveloperLawyer);

        var (status, _) = await PostSpvAsync(host);

        await Assert.MultipleAsync(async () =>
        {
            Assert.That(status, Is.EqualTo(HttpStatusCode.NotFound));
            Assert.That(await ReviewBucketCountAsync(host), Is.Zero);
        });
    }

    [Test]
    public async Task Object_storage_upload_failure_returns_502_without_a_bucket()
    {
        await using var host = await WebhooksHost.StartAsync(webhookSecret: WebhookSecret);
        await SeedNamespaceAsync(host);
        await SeedPrerequisitesAsync(host);
        host.S3.UploadError = new AmazonS3Exception("simulated snapshot upload failure");

        var (status, _) = await PostDeveloperAsync(host);

        await Assert.MultipleAsync(async () =>
        {
            Assert.That(status, Is.EqualTo(HttpStatusCode.BadGateway));
            Assert.That(await ReviewBucketCountAsync(host), Is.Zero);
        });
    }

    [Test]
    public async Task Invalid_developer_assignment_payloads_return_400_without_a_bucket()
    {
        await using var host = await WebhooksHost.StartAsync(webhookSecret: WebhookSecret);
        await SeedNamespaceAsync(host);
        await SeedPrerequisitesAsync(host);

        var (wrongEvent, _) = await PostDeveloperAsync(host, eventName: "spv_lawyer_elected");
        var (missingLawyer, _) = await PostDeveloperAsync(host, developerLawyer: null);
        var (blankLawyer, _) = await PostDeveloperAsync(host, developerLawyer: " ");
        var (missingListing, _) = await PostDeveloperAsync(host, listingId: null);

        await Assert.MultipleAsync(async () =>
        {
            Assert.That(wrongEvent, Is.EqualTo(HttpStatusCode.BadRequest));
            Assert.That(missingLawyer, Is.EqualTo(HttpStatusCode.BadRequest));
            Assert.That(blankLawyer, Is.EqualTo(HttpStatusCode.BadRequest));
            Assert.That(missingListing, Is.EqualTo(HttpStatusCode.BadRequest));
            Assert.That(await ReviewBucketCountAsync(host), Is.Zero);
        });
    }
}
