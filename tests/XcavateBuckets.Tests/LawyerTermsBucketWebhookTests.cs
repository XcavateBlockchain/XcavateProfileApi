using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Amazon.S3;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using XcavateBuckets.Domain;
using XcavateBuckets.Domain.Entities;
using XcavateProfileApi.Services;
using XcavateProfileApi.Services.Indexer;

namespace XcavateBuckets.Tests;

[TestFixture]
public class LawyerTermsBucketWebhookTests
{
    private const long ListingId = 12;
    private const string AssetPda = "AssetPDA111";
    private const string Lawyer = "LawyerWallet111";
    private const string HolderOne = "HolderWallet111";
    private const string HolderTwo = "HolderWallet222";
    private const string Candidacy = "CandidacyPDA111";
    private const string WebhookSecret = "lawyer-terms-test-secret";
    private const long Expiry = 1_790_000_000;
    private static readonly byte[] PdfBytes = "%PDF-1.4 lawyer terms"u8.ToArray();

    private static async Task<(HttpStatusCode Status, JsonElement Body)> PostClaimAsync(
        WebhooksHost host,
        string? lawyer = Lawyer,
        string? candidacy = Candidacy,
        int round = 1,
        string? presentedSecret = WebhookSecret)
    {
        var json = new JsonObject
        {
            ["event"] = "spv_case_claimed",
            ["property_id"] = ListingId,
            ["round"] = round,
            ["costs"] = 500_000,
            ["lawyer"] = lawyer,
            ["listing"] = "ListingPDA111",
            ["property"] = AssetPda,
            ["candidacy"] = candidacy,
            ["cluster"] = "devnet",
            ["slot"] = 487_700_100,
            ["tx_signature"] = $"Tx-{lawyer}-{candidacy}",
            ["block_time"] = "2026-10-01T10:00:00+00:00",
            ["program"] = "marketplace"
        }.ToJsonString();
        var path = "/webhooks/spv_case_claimed_devnet";
        if (presentedSecret is not null)
        {
            path += $"?secret={Uri.EscapeDataString(presentedSecret)}";
        }
        using var response = await host.Client.PostAsync(
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
        return (response.StatusCode, body);
    }

    private static async Task SeedNamespaceAsync(WebhooksHost host)
    {
        var json = new JsonObject
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
        using var response = await host.Client.PostAsync(
            $"/webhooks/init_property_asset_devnet?secret={Uri.EscapeDataString(WebhookSecret)}",
            new StringContent(json, Encoding.UTF8, "application/json"));
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
    }

    private static async Task<long> SeedLegalBucketAsync(WebhooksHost host)
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

    private static async Task SeedPrerequisitesAsync(
        WebhooksHost host,
        IReadOnlyList<string>? holders = null,
        string? lawyer = Lawyer,
        string? missingKey = null,
        bool document = true)
    {
        holders ??= [HolderOne, HolderTwo];
        host.Indexer.SpvElection = new SpvElectionInfo(1, Expiry, 1, "11111111111111111111111111111111");
        host.Indexer.ShareHolderWallets = holders;
        var keys = new Dictionary<string, string>
        {
            [Lawyer] = TestDb.Key32,
            [HolderOne] = TestDb.OtherKey32,
            [HolderTwo] = TestDb.ViewerKey32,
            [WebhooksHost.DefaultBucketAdminAddress] = TestDb.Key32,
            ["LawyerWallet222"] = TestDb.Hash32
        };
        foreach (var wallet in holders.Append(lawyer!).Distinct(StringComparer.Ordinal))
        {
            if (wallet != missingKey)
            {
                await host.SeedViewerKeyAsync(wallet, keys[wallet]);
            }
        }
        if (document)
        {
            await host.SeedTermsDocumentAsync(lawyer!, PdfBytes);
        }
    }

    private static async Task<int> TermsBucketCountAsync(WebhooksHost host) =>
        await host.WithDbAsync(async db =>
            await db.Buckets.CountAsync(bucket => bucket.Category == "spv-lawyer-terms"));

    private const string Voter = "VoterWallet333";
    private const string VoteTx = "VoteTx1111111111111111111111111111111111111111111";

    private static async Task<(HttpStatusCode Status, JsonElement Body)> PostVoteAsync(
        WebhooksHost host,
        string instructionPath = "0",
        string? voter = Voter,
        string? candidacy = Candidacy,
        string? eventName = "spv_lawyer_vote_cast")
    {
        var json = new JsonObject
        {
            ["event"] = eventName,
            ["property_id"] = ListingId,
            ["voter"] = voter,
            ["candidacy"] = candidacy,
            ["amount"] = 25,
            ["listing"] = "ListingPDA111",
            ["cluster"] = "devnet",
            ["slot"] = 487_700_150,
            ["tx_signature"] = VoteTx,
            ["instruction_path"] = instructionPath,
            ["block_time"] = "2026-10-01T11:00:00+00:00",
            ["program"] = "marketplace"
        }.ToJsonString();
        using var response = await host.Client.PostAsync(
            $"/webhooks/spv_lawyer_vote_cast_devnet?secret={Uri.EscapeDataString(WebhookSecret)}",
            new StringContent(json, Encoding.UTF8, "application/json"));
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
        return (response.StatusCode, body);
    }

    private static async Task<long> SeedTermsBucketAsync(WebhooksHost host)
    {
        await SeedNamespaceAsync(host);
        await SeedLegalBucketAsync(host);
        await SeedPrerequisitesAsync(host);
        var (status, body) = await PostClaimAsync(host);
        Assert.That(status, Is.EqualTo(HttpStatusCode.OK));
        return body.GetProperty("lawyerTermsBucketId").GetInt64();
    }

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
            WebEncoders.Base64UrlDecode(key.GetProperty("d").GetString()!),
            key.GetProperty("kid").GetString());
    }

    private static async Task<Message> ApprovalMessageAsync(WebhooksHost host, long bucketId) =>
        await host.WithDbAsync(async db => await db.Messages.SingleAsync(message =>
            message.BucketId == bucketId && message.Tag == "lawyer/terms-approved"));

    [Test]
    public async Task Spv_lawyer_vote_posts_encrypted_approved_terms_as_the_voter()
    {
        await using var host = await WebhooksHost.StartAsync(webhookSecret: WebhookSecret);
        var bucketId = await SeedTermsBucketAsync(host);

        var (status, body) = await PostVoteAsync(host);
        var message = await ApprovalMessageAsync(host, bucketId);
        var key = await BucketKeyAsync(host, bucketId);
        var plaintext = LawyerTermsBucketProtocolTests.DecryptCompactMessage(
            message.IpfsContent!, key.PrivateKey, key.Kid);
        var contributors = await host.WithDbAsync(async db => await db.BucketContributors
            .Where(row => row.BucketId == bucketId)
            .Select(row => row.SubjectId)
            .ToListAsync());

        Assert.Multiple(() =>
        {
            Assert.That(status, Is.EqualTo(HttpStatusCode.OK));
            Assert.That(body.GetProperty("status").GetString(), Is.EqualTo("posted"));
            Assert.That(Encoding.UTF8.GetString(plaintext), Is.EqualTo("Approved terms"));
            Assert.That(message.IpfsContent, Does.Not.Contain("Approved terms"));
            Assert.That(message.Contributor, Is.EqualTo(Voter));
            Assert.That(message.Description, Is.EqualTo("Approved terms"));
            Assert.That(message.ContentType, Is.EqualTo("text/plain;charset=utf-8"));
            Assert.That(message.Reference,
                Is.EqualTo($"lawyer-terms/{Candidacy}/approved/{VoteTx}:0"));
            Assert.That(message.ContentHash, Is.EqualTo("0x" + Convert.ToHexString(
                SHA256.HashData(Encoding.UTF8.GetBytes(message.IpfsContent!)))));
            Assert.That(contributors, Does.Contain(Voter));
        });
    }

    [Test]
    public async Task Spv_lawyer_vote_redelivery_does_not_duplicate_the_message_or_tag_count()
    {
        await using var host = await WebhooksHost.StartAsync(webhookSecret: WebhookSecret);
        var bucketId = await SeedTermsBucketAsync(host);
        await PostVoteAsync(host);

        var (status, body) = await PostVoteAsync(host);
        var state = await host.WithDbAsync(async db => new
        {
            Messages = await db.Messages.CountAsync(message =>
                message.BucketId == bucketId && message.Tag == "lawyer/terms-approved"),
            TagCount = await db.TagMessageCounts.Where(row =>
                row.BucketId == bucketId && row.TagName == "lawyer/terms-approved")
                .Select(row => row.Count)
                .SingleAsync()
        });

        Assert.Multiple(() =>
        {
            Assert.That(status, Is.EqualTo(HttpStatusCode.OK));
            Assert.That(body.GetProperty("status").GetString(), Is.EqualTo("ignored"));
            Assert.That(state.Messages, Is.EqualTo(1));
            Assert.That(state.TagCount, Is.EqualTo(1));
        });
    }

    [Test]
    public async Task Spv_lawyer_vote_instruction_paths_remain_distinct_in_one_transaction()
    {
        await using var host = await WebhooksHost.StartAsync(webhookSecret: WebhookSecret);
        var bucketId = await SeedTermsBucketAsync(host);
        await PostVoteAsync(host, instructionPath: "0");

        var (status, body) = await PostVoteAsync(host, instructionPath: "1");
        var references = await host.WithDbAsync(async db => await db.Messages
            .Where(message => message.BucketId == bucketId
                && message.Tag == "lawyer/terms-approved")
            .OrderBy(message => message.MessageId)
            .Select(message => message.Reference)
            .ToListAsync());

        Assert.Multiple(() =>
        {
            Assert.That(status, Is.EqualTo(HttpStatusCode.OK));
            Assert.That(body.GetProperty("status").GetString(), Is.EqualTo("posted"));
            Assert.That(references, Is.EqualTo(new[]
            {
                $"lawyer-terms/{Candidacy}/approved/{VoteTx}:0",
                $"lawyer-terms/{Candidacy}/approved/{VoteTx}:1"
            }));
        });
    }

    [Test]
    public async Task Spv_lawyer_vote_for_an_unknown_candidacy_is_retryable_without_side_effects()
    {
        await using var host = await WebhooksHost.StartAsync(webhookSecret: WebhookSecret);
        var bucketId = await SeedTermsBucketAsync(host);

        var (status, body) = await PostVoteAsync(host, candidacy: "OtherCandidacyPDA222");
        var messageCount = await host.WithDbAsync(async db => await db.Messages
            .CountAsync(message => message.BucketId == bucketId));

        Assert.Multiple(() =>
        {
            Assert.That(status, Is.EqualTo(HttpStatusCode.NotFound));
            Assert.That(body.GetProperty("status").GetString(), Is.EqualTo("not_found"));
            Assert.That(messageCount, Is.EqualTo(4));
        });
    }

    [TestCase("wrong_event", Voter)]
    [TestCase("spv_lawyer_vote_cast", null)]
    public async Task Spv_lawyer_vote_rejects_invalid_payloads_without_side_effects(
        string eventName, string? voter)
    {
        await using var host = await WebhooksHost.StartAsync(webhookSecret: WebhookSecret);
        var bucketId = await SeedTermsBucketAsync(host);

        var (status, _) = await PostVoteAsync(host, eventName: eventName, voter: voter);
        var messageCount = await host.WithDbAsync(async db => await db.Messages
            .CountAsync(message => message.BucketId == bucketId));

        Assert.Multiple(() =>
        {
            Assert.That(status, Is.EqualTo(HttpStatusCode.BadRequest));
            Assert.That(messageCount, Is.EqualTo(4));
        });
    }

    [Test]
    public async Task Spv_lawyer_vote_without_admin_configuration_returns_503_without_side_effects()
    {
        await using var host = await WebhooksHost.StartAsync(
            webhookSecret: WebhookSecret, bucketAdminAddress: "", bucketAdminX25519Key: "");
        await SeedNamespaceAsync(host);

        var (status, _) = await PostVoteAsync(host);
        var state = await host.WithDbAsync(async db => new
        {
            Messages = await db.Messages.CountAsync(),
            Contributors = await db.BucketContributors.CountAsync()
        });

        Assert.Multiple(() =>
        {
            Assert.That(status, Is.EqualTo(HttpStatusCode.ServiceUnavailable));
            Assert.That(state.Messages, Is.Zero);
            Assert.That(state.Contributors, Is.Zero);
        });
    }

    [Test]
    public async Task Claim_creates_the_complete_encrypted_terms_bucket_and_keeps_the_vote_announcement()
    {
        await using var host = await WebhooksHost.StartAsync(webhookSecret: WebhookSecret);
        await SeedNamespaceAsync(host);
        var legalBucketId = await SeedLegalBucketAsync(host);
        await SeedPrerequisitesAsync(host);

        var (status, body) = await PostClaimAsync(host);
        var bucketId = body.GetProperty("lawyerTermsBucketId").GetInt64();
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
                .OrderBy(row => row.MessageId).ToListAsync(),
            LegalMessages = await db.Messages.Where(row => row.BucketId == legalBucketId).ToListAsync()
        });

        await Assert.MultipleAsync(async () =>
        {
            Assert.That(status, Is.EqualTo(HttpStatusCode.OK));
            Assert.That(body.GetProperty("lawyerTermsBucketCreated").GetBoolean(), Is.True);
            Assert.That(body.GetProperty("lawyerTermsHolderCount").GetInt32(), Is.EqualTo(2));
            Assert.That(state.Bucket.NamespaceId, Is.Not.Null);
            Assert.That(state.Bucket.Name, Is.EqualTo($"42 Main Street - SPV lawyer terms {Candidacy}"));
            Assert.That(state.Bucket.Category, Is.EqualTo("spv-lawyer-terms"));
            Assert.That(state.Bucket.Creator, Is.EqualTo(AssetPda));
            Assert.That(state.Bucket.IsWritable, Is.True);
            Assert.That(state.Bucket.EncryptionKey, Has.Length.EqualTo(43));
            Assert.That(WebEncoders.Base64UrlDecode(state.Bucket.EncryptionKey!), Has.Length.EqualTo(32));
            Assert.That(WebEncoders.Base64UrlDecode(state.Bucket.EncryptionKey!).Any(value => value != 0),
                Is.True);
            using (var properties = JsonDocument.Parse(state.Bucket.Properties!))
            {
                Assert.That(properties.RootElement.GetProperty("kind").GetString(),
                    Is.EqualTo("spv_lawyer_terms"));
                Assert.That(properties.RootElement.GetProperty("listing_id").GetString(), Is.EqualTo("12"));
                Assert.That(properties.RootElement.GetProperty("round").GetString(), Is.EqualTo("1"));
                Assert.That(properties.RootElement.GetProperty("lawyer").GetString(), Is.EqualTo(Lawyer));
                Assert.That(properties.RootElement.GetProperty("candidacy").GetString(), Is.EqualTo(Candidacy));
                Assert.That(properties.RootElement.GetProperty("cluster").GetString(), Is.EqualTo("devnet"));
            }

            Assert.That(state.Admins, Is.EqualTo(new[] { WebhooksHost.DefaultBucketAdminAddress }));
            Assert.That(state.Contributors, Is.EquivalentTo(new[]
            {
                WebhooksHost.DefaultBucketAdminAddress, HolderOne, HolderTwo, Lawyer
            }));
            Assert.That(state.Viewers, Is.EquivalentTo(new[]
            {
                WebhooksHost.DefaultBucketAdminViewerKey,
                TestDb.OtherKey32Base64Url, TestDb.ViewerKey32Base64Url, TestDb.Key32Base64Url
            }));

            Assert.That(state.Messages, Has.Count.EqualTo(4));
            Assert.That(state.Messages.Select(message => message.Contributor), Is.All.EqualTo(
                WebhooksHost.DefaultBucketAdminAddress));
            Assert.That(state.Messages[0].Tag, Is.EqualTo("didcomm/key-sharing-v1"));
            Assert.That(state.Messages[0].ContentType,
                Is.EqualTo("application/didcomm-encrypted+json"));
            using (var keySharing = JsonDocument.Parse(state.Messages[0].IpfsContent!))
            {
                Assert.That(keySharing.RootElement.GetProperty("recipients").GetArrayLength(),
                    Is.EqualTo(4));
                Assert.That(keySharing.RootElement.GetProperty("ciphertext").GetString(),
                    Is.Not.Null.And.Not.Empty);
                Assert.That(state.Messages[0].IpfsContent, Does.Not.Contain("\"keys\""));
                Assert.That(state.Messages[0].IpfsContent, Does.Not.Contain("\"d\""));
            }
            var adminPlaintext = LawyerTermsBucketProtocolTests.DecryptForRecipient(
                state.Messages[0].IpfsContent!, WebhooksHost.DefaultBucketAdminPrivateKey);
            byte[] bucketPrivateKey;
            string bucketKid;
            using (var adminPayload = JsonDocument.Parse(adminPlaintext))
            {
                var recipients = adminPayload.RootElement.GetProperty("to").EnumerateArray()
                    .Select(value => value.GetString()).ToArray();
                var secretJwk = adminPayload.RootElement.GetProperty("keys")[0];
                bucketPrivateKey = WebEncoders.Base64UrlDecode(secretJwk.GetProperty("d").GetString()!);
                bucketKid = secretJwk.GetProperty("kid").GetString()!;
                Assert.That(adminPayload.RootElement.GetProperty("from").GetString(),
                    Is.EqualTo(WebhooksHost.DefaultBucketAdminAddress));
                Assert.That(recipients, Does.Contain(WebhooksHost.DefaultBucketAdminViewerKey));
                Assert.That(WebEncoders.Base64UrlDecode(secretJwk.GetProperty("x").GetString()!),
                    Is.EqualTo(WebEncoders.Base64UrlDecode(state.Bucket.EncryptionKey!)));
            }
            var promptPlaintext = LawyerTermsBucketProtocolTests.DecryptCompactMessage(
                state.Messages[1].IpfsContent!, bucketPrivateKey, bucketKid);
            var snapshotPayload = await host.S3.DownloadObjectAsync(
                "xcavate-profile", $"terms-documents/by-candidacy/{Candidacy}.pdf", CancellationToken.None);
            var decryptedPdf = LawyerTermsBucketProtocolTests.DecryptCompactMessage(
                Encoding.UTF8.GetString(snapshotPayload), bucketPrivateKey, bucketKid);
            Assert.That(state.Messages[1].Tag, Is.EqualTo("lawyer/terms-vote"));
            Assert.That(state.Messages[1].Reference,
                Is.EqualTo($"lawyer-terms/{Candidacy}/vote-prompt"));
            Assert.That(state.Messages[1].ContentType, Is.EqualTo("text/plain;charset=utf-8"));
            Assert.That(Encoding.UTF8.GetString(promptPlaintext), Is.EqualTo(
                "All property token holders should now vote on whether to accept the Lawyer's terms."));
            Assert.That(state.Messages[1].ContentHash, Is.EqualTo("0x" + Convert.ToHexString(
                SHA256.HashData(Encoding.UTF8.GetBytes(state.Messages[1].IpfsContent!)))));
            Assert.That(state.Messages[2].Tag, Is.EqualTo("lawyer/terms-document"));
            Assert.That(state.Messages[2].Reference,
                Is.EqualTo($"http://localhost/api/terms-documents/by-candidacy/{Candidacy}"));
            Assert.That(state.Messages[2].Description, Is.EqualTo("terms.pdf"));
            Assert.That(state.Messages[2].ContentType, Is.EqualTo("application/pdf"));
            Assert.That(state.Messages[2].IpfsContent, Is.Null);
            Assert.That(decryptedPdf, Is.EqualTo(PdfBytes));
            Assert.That(state.Messages[2].ContentHash,
                Is.EqualTo("0x" + Convert.ToHexString(SHA256.HashData(snapshotPayload))));
            Assert.That(state.Messages[3].Tag, Is.EqualTo("voting/terms"));
            Assert.That(state.Messages[3].Reference, Is.EqualTo("voting/terms/12/round/1/open"));
            using (var vote = JsonDocument.Parse(state.Messages[3].IpfsContent!))
            {
                Assert.That(vote.RootElement.GetProperty("kind").GetString(), Is.EqualTo("terms"));
                Assert.That(vote.RootElement.GetProperty("listingId").GetInt64(), Is.EqualTo(ListingId));
                Assert.That(vote.RootElement.GetProperty("round").GetInt32(), Is.EqualTo(1));
                Assert.That(vote.RootElement.GetProperty("status").GetString(), Is.EqualTo("open"));
            }
            Assert.That(state.LegalMessages.Single().Tag, Is.EqualTo("voting/terms"));
            Assert.That(state.LegalMessages.Single().Contributor, Is.EqualTo("system:voting"));
        });
    }

    [Test]
    public async Task Claim_stores_a_profile_api_reference_that_downloads_the_encrypted_snapshot()
    {
        await using var host = await WebhooksHost.StartAsync(webhookSecret: WebhookSecret);
        await SeedNamespaceAsync(host);
        await SeedLegalBucketAsync(host);
        await SeedPrerequisitesAsync(host);
        var (_, body) = await PostClaimAsync(host);
        var bucketId = body.GetProperty("lawyerTermsBucketId").GetInt64();
        var reference = await host.WithDbAsync(async db => await db.Messages
            .Where(message => message.BucketId == bucketId && message.Tag == "lawyer/terms-document")
            .Select(message => message.Reference)
            .SingleAsync());

        using var response = await host.Client.GetAsync(reference);
        var downloaded = await response.Content.ReadAsByteArrayAsync();
        var snapshot = await host.S3.DownloadObjectAsync(
            "xcavate-profile", $"terms-documents/by-candidacy/{Candidacy}.pdf", CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(reference,
                Is.EqualTo($"http://localhost/api/terms-documents/by-candidacy/{Candidacy}"));
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
            Assert.That(response.Content.Headers.ContentType?.MediaType,
                Is.EqualTo("application/didcomm-encrypted+json"));
            Assert.That(downloaded, Is.EqualTo(snapshot));
        });
    }

    [Test]
    public async Task Configured_public_origin_wins_over_the_request_origin_for_snapshot_references()
    {
        await using var host = await WebhooksHost.StartAsync(
            webhookSecret: WebhookSecret,
            profileApiPublicUrl: "https://profile-api.xcavate.io/");
        await SeedNamespaceAsync(host);
        await SeedLegalBucketAsync(host);
        await SeedPrerequisitesAsync(host);
        var (_, body) = await PostClaimAsync(host);
        var bucketId = body.GetProperty("lawyerTermsBucketId").GetInt64();

        var reference = await host.WithDbAsync(async db => await db.Messages
            .Where(message => message.BucketId == bucketId && message.Tag == "lawyer/terms-document")
            .Select(message => message.Reference)
            .SingleAsync());

        Assert.That(reference,
            Is.EqualTo($"https://profile-api.xcavate.io/api/terms-documents/by-candidacy/{Candidacy}"));
    }

    [Test]
    public async Task Later_terms_upload_does_not_change_the_snapshot_sent_to_voters()
    {
        await using var host = await WebhooksHost.StartAsync(webhookSecret: WebhookSecret);
        await SeedNamespaceAsync(host);
        await SeedLegalBucketAsync(host);
        await SeedPrerequisitesAsync(host);
        var (_, body) = await PostClaimAsync(host);
        var bucketId = body.GetProperty("lawyerTermsBucketId").GetInt64();
        var replacement = "%PDF-1.4 changed terms"u8.ToArray();
        using (var content = new MemoryStream(replacement, writable: false))
        {
            await host.S3.UploadImageAsync(
                "xcavate-profile", $"terms-documents/{Lawyer}.pdf", content, "application/pdf");
        }

        var snapshot = await host.S3.DownloadObjectAsync(
            "xcavate-profile", $"terms-documents/by-candidacy/{Candidacy}.pdf", CancellationToken.None);
        var current = await host.S3.DownloadObjectAsync(
            "xcavate-profile", $"terms-documents/{Lawyer}.pdf", CancellationToken.None);
        var reference = await host.WithDbAsync(async db => await db.Messages
            .Where(message => message.BucketId == bucketId && message.Tag == "lawyer/terms-document")
            .Select(message => message.Reference)
            .SingleAsync());
        var keySharingBody = await host.WithDbAsync(async db => await db.Messages
            .Where(message => message.BucketId == bucketId && message.Tag == "didcomm/key-sharing-v1")
            .Select(message => message.IpfsContent)
            .SingleAsync());
        var keySharingPlaintext = LawyerTermsBucketProtocolTests.DecryptForRecipient(
            keySharingBody!, WebhooksHost.DefaultBucketAdminPrivateKey);
        using var keySharingDocument = JsonDocument.Parse(keySharingPlaintext);
        var secretJwk = keySharingDocument.RootElement.GetProperty("keys")[0];
        var bucketPrivateKey = WebEncoders.Base64UrlDecode(secretJwk.GetProperty("d").GetString()!);
        var bucketKid = secretJwk.GetProperty("kid").GetString();
        var decryptedSnapshot = LawyerTermsBucketProtocolTests.DecryptCompactMessage(
            Encoding.UTF8.GetString(snapshot), bucketPrivateKey, bucketKid);

        Assert.Multiple(() =>
        {
            Assert.That(decryptedSnapshot, Is.EqualTo(PdfBytes));
            Assert.That(current, Is.EqualTo(replacement));
            Assert.That(reference,
                Is.EqualTo($"http://localhost/api/terms-documents/by-candidacy/{Candidacy}"));
        });
    }

    [Test]
    public async Task Redelivery_reuses_the_bucket_and_duplicates_no_messages_or_keys()
    {
        await using var host = await WebhooksHost.StartAsync(webhookSecret: WebhookSecret);
        await SeedNamespaceAsync(host);
        await SeedLegalBucketAsync(host);
        await SeedPrerequisitesAsync(host);

        var (firstStatus, first) = await PostClaimAsync(host);
        var bucketId = first.GetProperty("lawyerTermsBucketId").GetInt64();
        var originalKey = (await host.BucketsAsync()).Single(bucket => bucket.BucketId == bucketId).EncryptionKey;
        var (secondStatus, second) = await PostClaimAsync(host);
        var bucket = (await host.BucketsAsync()).Single(bucket => bucket.BucketId == bucketId);
        var messageCount = await host.WithDbAsync(async db =>
            await db.Messages.CountAsync(message => message.BucketId == bucketId));

        Assert.Multiple(() =>
        {
            Assert.That(firstStatus, Is.EqualTo(HttpStatusCode.OK));
            Assert.That(secondStatus, Is.EqualTo(HttpStatusCode.OK));
            Assert.That(second.GetProperty("lawyerTermsBucketCreated").GetBoolean(), Is.False);
            Assert.That(second.GetProperty("lawyerTermsBucketId").GetInt64(), Is.EqualTo(bucketId));
            Assert.That(messageCount, Is.EqualTo(4));
            Assert.That(bucket.EncryptionKey, Is.EqualTo(originalKey));
        });
    }

    [Test]
    public async Task Redelivery_migrates_an_old_snapshot_reference_without_rewriting_encrypted_state()
    {
        await using var host = await WebhooksHost.StartAsync(
            webhookSecret: WebhookSecret,
            profileApiPublicUrl: "https://profile-api.xcavate.io");
        await SeedNamespaceAsync(host);
        await SeedLegalBucketAsync(host);
        await SeedPrerequisitesAsync(host);
        var (_, first) = await PostClaimAsync(host);
        var bucketId = first.GetProperty("lawyerTermsBucketId").GetInt64();
        var original = await host.WithDbAsync(async db => new
        {
            Prompt = await db.Messages.SingleAsync(message =>
                message.BucketId == bucketId && message.Tag == "lawyer/terms-vote"),
            KeySharing = await db.Messages.SingleAsync(message =>
                message.BucketId == bucketId && message.Tag == "didcomm/key-sharing-v1"),
            EncryptionKey = await db.Buckets.Where(bucket => bucket.BucketId == bucketId)
                .Select(bucket => bucket.EncryptionKey).SingleAsync()
        });
        await host.WithDbAsync(async db =>
        {
            var document = await db.Messages.SingleAsync(message =>
                message.BucketId == bucketId && message.Tag == "lawyer/terms-document");
            document.Reference =
                $"https://xcavate-profile.fsn1.your-objectstorage.com/terms-documents/by-candidacy/{Candidacy}.pdf";
            await db.SaveChangesAsync();
            return true;
        });

        var (status, second) = await PostClaimAsync(host);
        var state = await host.WithDbAsync(async db => new
        {
            Messages = await db.Messages.Where(message => message.BucketId == bucketId)
                .OrderBy(message => message.MessageId).ToListAsync(),
            EncryptionKey = await db.Buckets.Where(bucket => bucket.BucketId == bucketId)
                .Select(bucket => bucket.EncryptionKey).SingleAsync()
        });
        var prompt = state.Messages.Single(message => message.Tag == "lawyer/terms-vote");
        var keySharing = state.Messages.Single(message => message.Tag == "didcomm/key-sharing-v1");
        var document = state.Messages.Single(message => message.Tag == "lawyer/terms-document");

        Assert.Multiple(() =>
        {
            Assert.That(status, Is.EqualTo(HttpStatusCode.OK));
            Assert.That(second.GetProperty("lawyerTermsBucketCreated").GetBoolean(), Is.False);
            Assert.That(document.Reference,
                Is.EqualTo($"https://profile-api.xcavate.io/api/terms-documents/by-candidacy/{Candidacy}"));
            Assert.That(prompt.IpfsContent, Is.EqualTo(original.Prompt.IpfsContent));
            Assert.That(prompt.ContentHash, Is.EqualTo(original.Prompt.ContentHash));
            Assert.That(prompt.ContentType, Is.EqualTo(original.Prompt.ContentType));
            Assert.That(keySharing.IpfsContent, Is.EqualTo(original.KeySharing.IpfsContent));
            Assert.That(state.EncryptionKey, Is.EqualTo(original.EncryptionKey));
            Assert.That(state.Messages, Has.Count.EqualTo(4));
        });
    }

    [Test]
    public async Task Existing_plaintext_messages_are_repaired_on_redelivery()
    {
        await using var host = await WebhooksHost.StartAsync(webhookSecret: WebhookSecret);
        await SeedNamespaceAsync(host);
        await SeedLegalBucketAsync(host);
        await SeedPrerequisitesAsync(host);
        var (_, first) = await PostClaimAsync(host);
        var bucketId = first.GetProperty("lawyerTermsBucketId").GetInt64();
        await host.WithDbAsync(async db =>
        {
            var prompt = await db.Messages.SingleAsync(message =>
                message.BucketId == bucketId && message.Tag == "lawyer/terms-vote");
            prompt.IpfsContent = LawyerTermsBucketProtocol.VotePrompt;
            prompt.ContentType = "text/plain";
            await db.SaveChangesAsync();
            return true;
        });
        using (var rawPdf = new MemoryStream(PdfBytes, writable: false))
        {
            await host.S3.UploadImageAsync(
                "xcavate-profile", $"terms-documents/by-candidacy/{Candidacy}.pdf",
                rawPdf, "application/pdf");
        }

        await host.WithDbAsync(async db =>
        {
            var keySharing = await db.Messages.SingleAsync(message =>
                message.BucketId == bucketId && message.Tag == "didcomm/key-sharing-v1");
            var plaintext = LawyerTermsBucketProtocolTests.DecryptForRecipient(
                keySharing.IpfsContent!, WebhooksHost.DefaultBucketAdminPrivateKey);
            var payload = JsonNode.Parse(plaintext)!.AsObject();
            var secret = payload["keys"]![0]!.AsObject();
            secret.Remove("kid");
            secret.Remove("use");
            secret.Remove("y");
            var recipients = payload["to"]!.AsArray()
                .Select(node => X25519KeyCodec.Decode32(node!.GetValue<string>(), "recipient"))
                .ToList();
            keySharing.IpfsContent = LawyerTermsBucketProtocol.EncryptForRecipients(
                payload.ToJsonString(), recipients);
            await db.SaveChangesAsync();
            return true;
        });

        var (status, second) = await PostClaimAsync(host);
        var messages = await host.WithDbAsync(async db => await db.Messages
            .Where(message => message.BucketId == bucketId)
            .OrderBy(message => message.MessageId)
            .ToListAsync());
        var keySharingPlaintext = LawyerTermsBucketProtocolTests.DecryptForRecipient(
            messages[0].IpfsContent!, WebhooksHost.DefaultBucketAdminPrivateKey);
        using var keySharingDocument = JsonDocument.Parse(keySharingPlaintext);
        var secretJwk = keySharingDocument.RootElement.GetProperty("keys")[0];
        var bucketPrivateKey = WebEncoders.Base64UrlDecode(secretJwk.GetProperty("d").GetString()!);
        var bucketKid = secretJwk.TryGetProperty("kid", out var kidElement)
            ? kidElement.GetString()
            : null;
        var promptPlaintext = LawyerTermsBucketProtocolTests.DecryptCompactMessage(
            messages[1].IpfsContent!, bucketPrivateKey, bucketKid);
        var snapshot = await host.S3.DownloadObjectAsync(
            "xcavate-profile", $"terms-documents/by-candidacy/{Candidacy}.pdf", CancellationToken.None);
        var decryptedPdf = LawyerTermsBucketProtocolTests.DecryptCompactMessage(
            Encoding.UTF8.GetString(snapshot), bucketPrivateKey, bucketKid);
        using var promptHeader = JsonDocument.Parse(WebEncoders.Base64UrlDecode(
            messages[1].IpfsContent!.Split('.')[0]));
        using var snapshotHeader = JsonDocument.Parse(WebEncoders.Base64UrlDecode(
            Encoding.UTF8.GetString(snapshot).Split('.')[0]));

        Assert.Multiple(() =>
        {
            Assert.That(promptHeader.RootElement.TryGetProperty("kid", out _), Is.False);
            Assert.That(snapshotHeader.RootElement.TryGetProperty("kid", out _), Is.False);
            Assert.That(status, Is.EqualTo(HttpStatusCode.OK));
            Assert.That(second.GetProperty("lawyerTermsBucketCreated").GetBoolean(), Is.False);
            Assert.That(messages, Has.Count.EqualTo(4));
            Assert.That(messages[1].ContentType, Is.EqualTo("text/plain;charset=utf-8"));
            Assert.That(Encoding.UTF8.GetString(promptPlaintext),
                Is.EqualTo(LawyerTermsBucketProtocol.VotePrompt));
            Assert.That(decryptedPdf, Is.EqualTo(PdfBytes));
        });
    }

    [Test]
    public async Task Repair_reuses_an_already_encrypted_snapshot_without_double_encryption()
    {
        await using var host = await WebhooksHost.StartAsync(webhookSecret: WebhookSecret);
        await SeedNamespaceAsync(host);
        await SeedLegalBucketAsync(host);
        await SeedPrerequisitesAsync(host);
        var (_, first) = await PostClaimAsync(host);
        var bucketId = first.GetProperty("lawyerTermsBucketId").GetInt64();
        await host.WithDbAsync(async db =>
        {
            var prompt = await db.Messages.SingleAsync(message =>
                message.BucketId == bucketId && message.Tag == "lawyer/terms-vote");
            prompt.IpfsContent = LawyerTermsBucketProtocol.VotePrompt;
            prompt.ContentType = "text/plain";
            await db.SaveChangesAsync();
            return true;
        });

        var (_, second) = await PostClaimAsync(host);
        var messages = await host.WithDbAsync(async db => await db.Messages
            .Where(message => message.BucketId == bucketId)
            .OrderBy(message => message.MessageId)
            .ToListAsync());
        var keySharingPlaintext = LawyerTermsBucketProtocolTests.DecryptForRecipient(
            messages[0].IpfsContent!, WebhooksHost.DefaultBucketAdminPrivateKey);
        using var keySharingDocument = JsonDocument.Parse(keySharingPlaintext);
        var secretJwk = keySharingDocument.RootElement.GetProperty("keys")[0];
        var bucketPrivateKey = WebEncoders.Base64UrlDecode(secretJwk.GetProperty("d").GetString()!);
        var bucketKid = secretJwk.GetProperty("kid").GetString();
        var snapshot = await host.S3.DownloadObjectAsync(
            "xcavate-profile", $"terms-documents/by-candidacy/{Candidacy}.pdf", CancellationToken.None);
        var decryptedPdf = LawyerTermsBucketProtocolTests.DecryptCompactMessage(
            Encoding.UTF8.GetString(snapshot), bucketPrivateKey, bucketKid);

        Assert.Multiple(() =>
        {
            Assert.That(second.GetProperty("lawyerTermsBucketCreated").GetBoolean(), Is.False);
            Assert.That(decryptedPdf, Is.EqualTo(PdfBytes));
        });
    }

    [Test]
    public async Task Repair_rejects_a_compact_snapshot_with_the_wrong_key_instead_of_nesting_jwe()
    {
        await using var host = await WebhooksHost.StartAsync(webhookSecret: WebhookSecret);
        await SeedNamespaceAsync(host);
        await SeedLegalBucketAsync(host);
        await SeedPrerequisitesAsync(host);
        await PostClaimAsync(host);
        await host.WithDbAsync(async db =>
        {
            var bucket = await db.Buckets.SingleAsync(bucket => bucket.Category == "spv-lawyer-terms");
            var prompt = await db.Messages.SingleAsync(message =>
                message.BucketId == bucket.BucketId && message.Tag == "lawyer/terms-vote");
            prompt.IpfsContent = LawyerTermsBucketProtocol.VotePrompt;
            prompt.ContentType = "text/plain";
            await db.SaveChangesAsync();
            return true;
        });
        var otherKey = LawyerTermsBucketProtocol.CreateKeySharing(
            "OtherAdmin", [new KeyValuePair<string, string>("OtherViewer", TestDb.ViewerKey32Base64Url)]);
        var wrongPayload = LawyerTermsBucketProtocol.EncryptMessage(PdfBytes, otherKey.SecretJwk);
        var wrongBytes = Encoding.UTF8.GetBytes(wrongPayload);
        using (var content = new MemoryStream(wrongBytes, writable: false))
        {
            await host.S3.UploadImageAsync(
                "xcavate-profile", $"terms-documents/by-candidacy/{Candidacy}.pdf",
                content, LawyerTermsBucketProtocol.KeySharingContentType);
        }

        Assert.ThrowsAsync<Org.BouncyCastle.Crypto.InvalidCipherTextException>(
            async () => await PostClaimAsync(host));
        var snapshot = await host.S3.DownloadObjectAsync(
            "xcavate-profile", $"terms-documents/by-candidacy/{Candidacy}.pdf", CancellationToken.None);
        Assert.That(snapshot, Is.EqualTo(wrongBytes));
    }

    [Test]
    public async Task A_second_lawyer_gets_a_distinct_bucket_without_a_second_round_announcement()
    {
        await using var host = await WebhooksHost.StartAsync(webhookSecret: WebhookSecret);
        await SeedNamespaceAsync(host);
        var legalBucketId = await SeedLegalBucketAsync(host);
        await SeedPrerequisitesAsync(host);
        await PostClaimAsync(host);
        const string secondLawyer = "LawyerWallet222";
        await host.SeedViewerKeyAsync(secondLawyer, TestDb.Hash32);
        await host.SeedTermsDocumentAsync(secondLawyer, PdfBytes);

        var (status, _) = await PostClaimAsync(
            host, lawyer: secondLawyer, candidacy: "CandidacyPDA222");
        var legalMessageCount = await host.WithDbAsync(async db =>
            await db.Messages.CountAsync(message => message.BucketId == legalBucketId));

        await Assert.MultipleAsync(async () =>
        {
            Assert.That(status, Is.EqualTo(HttpStatusCode.OK));
            Assert.That(await TermsBucketCountAsync(host), Is.EqualTo(2));
            Assert.That(legalMessageCount, Is.EqualTo(1));
        });
    }

    [Test]
    public async Task Terms_setup_survives_a_missing_legal_bucket_and_redelivery_finishes_the_vote()
    {
        await using var host = await WebhooksHost.StartAsync(webhookSecret: WebhookSecret);
        await SeedNamespaceAsync(host);
        await SeedPrerequisitesAsync(host);

        var (firstStatus, _) = await PostClaimAsync(host);
        var bucketId = (await host.BucketsAsync())
            .Single(bucket => bucket.Category == "spv-lawyer-terms").BucketId;
        var legalBucketId = await SeedLegalBucketAsync(host);
        var (secondStatus, second) = await PostClaimAsync(host);
        var termsMessageCount = await host.WithDbAsync(async db =>
            await db.Messages.CountAsync(message => message.BucketId == bucketId));
        var legalMessageCount = await host.WithDbAsync(async db =>
            await db.Messages.CountAsync(message => message.BucketId == legalBucketId));

        Assert.Multiple(() =>
        {
            Assert.That(firstStatus, Is.EqualTo(HttpStatusCode.NotFound));
            Assert.That(secondStatus, Is.EqualTo(HttpStatusCode.OK));
            Assert.That(second.GetProperty("lawyerTermsBucketCreated").GetBoolean(), Is.False);
            Assert.That(second.GetProperty("lawyerTermsBucketId").GetInt64(), Is.EqualTo(bucketId));
            Assert.That(termsMessageCount, Is.EqualTo(4));
            Assert.That(legalMessageCount, Is.EqualTo(1));
        });
    }

    [Test]
    public async Task Election_result_is_posted_to_legal_and_all_lawyer_terms_buckets_in_the_round()
    {
        await using var host = await WebhooksHost.StartAsync(webhookSecret: WebhookSecret);
        await SeedNamespaceAsync(host);
        var legalBucketId = await SeedLegalBucketAsync(host);
        await SeedPrerequisitesAsync(host);
        var (_, claim) = await PostClaimAsync(host);
        var termsBucketId = claim.GetProperty("lawyerTermsBucketId").GetInt64();
        const string losingLawyer = "LawyerWallet222";
        await host.SeedViewerKeyAsync(losingLawyer, TestDb.Hash32);
        await host.SeedTermsDocumentAsync(losingLawyer, PdfBytes);
        var (_, losingClaim) = await PostClaimAsync(
            host, lawyer: losingLawyer, candidacy: "CandidacyPDA222");
        var losingBucketId = losingClaim.GetProperty("lawyerTermsBucketId").GetInt64();
        host.Indexer.SpvElection = new SpvElectionInfo(1, Expiry, 1, Lawyer);
        host.Indexer.SpvCandidacy = new SpvCandidacyInfo(Candidacy, 1, Lawyer);
        var json = new JsonObject
        {
            ["event"] = "spv_lawyer_elected",
            ["listing_id"] = ListingId,
            ["property_id"] = ListingId,
            ["listing"] = "ListingPDA111",
            ["property"] = AssetPda,
            ["cluster"] = "devnet",
            ["winner_registry"] = "WinnerRegistryPDA111",
            ["slot"] = 487_700_200,
            ["tx_signature"] = "ElectedTx1111111111111111111111111111111111111111",
            ["block_time"] = "2026-10-02T10:00:00+00:00",
            ["program"] = "marketplace"
        }.ToJsonString();

        using var response = await host.Client.PostAsync(
            $"/webhooks/spv_lawyer_elected_devnet?secret={Uri.EscapeDataString(WebhookSecret)}",
            new StringContent(json, Encoding.UTF8, "application/json"));
        var state = await host.WithDbAsync(async db => new
        {
            LegalResult = await db.Messages.SingleAsync(message =>
                message.BucketId == legalBucketId && message.Tag == "voting/result"),
            TermsResult = await db.Messages.SingleAsync(message =>
                message.BucketId == termsBucketId && message.Tag == "voting/result"),
            LosingResult = await db.Messages.SingleAsync(message =>
                message.BucketId == losingBucketId && message.Tag == "voting/result")
        });

        Assert.Multiple(() =>
        {
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
            Assert.That(state.LegalResult.Contributor, Is.EqualTo("system:voting"));
            Assert.That(state.TermsResult.Contributor, Is.EqualTo(WebhooksHost.DefaultBucketAdminAddress));
            Assert.That(state.LosingResult.Contributor, Is.EqualTo(WebhooksHost.DefaultBucketAdminAddress));
            Assert.That(state.TermsResult.Reference, Is.EqualTo("voting/result/terms/12/round/1"));
            Assert.That(state.TermsResult.IpfsContent, Is.EqualTo(state.LegalResult.IpfsContent));
            Assert.That(state.LosingResult.IpfsContent, Is.EqualTo(state.LegalResult.IpfsContent));
            using var result = JsonDocument.Parse(state.TermsResult.IpfsContent!);
            Assert.That(result.RootElement.GetProperty("status").GetString(), Is.EqualTo("ratified"));
            Assert.That(result.RootElement.GetProperty("round").GetInt32(), Is.EqualTo(1));
        });
    }

    [Test]
    public async Task Rejected_election_result_closes_only_buckets_in_that_round()
    {
        await using var host = await WebhooksHost.StartAsync(webhookSecret: WebhookSecret);
        await SeedNamespaceAsync(host);
        await SeedLegalBucketAsync(host);
        await SeedPrerequisitesAsync(host);
        var (_, firstClaim) = await PostClaimAsync(host);
        var roundOneBucketId = firstClaim.GetProperty("lawyerTermsBucketId").GetInt64();
        const string secondLawyer = "LawyerWallet222";
        await host.SeedViewerKeyAsync(secondLawyer, TestDb.Hash32);
        await host.SeedTermsDocumentAsync(secondLawyer, PdfBytes);
        var (_, secondClaim) = await PostClaimAsync(
            host, lawyer: secondLawyer, candidacy: "CandidacyPDA222", round: 2);
        var roundTwoBucketId = secondClaim.GetProperty("lawyerTermsBucketId").GetInt64();
        host.Indexer.SpvElection = new SpvElectionInfo(2, Expiry, 2, "11111111111111111111111111111111");
        var json = new JsonObject
        {
            ["event"] = "spv_lawyer_elected",
            ["listing_id"] = ListingId,
            ["property_id"] = ListingId,
            ["listing"] = "ListingPDA111",
            ["property"] = AssetPda,
            ["cluster"] = "devnet",
            ["winner_registry"] = "WinnerRegistryPDA111",
            ["slot"] = 487_700_200,
            ["tx_signature"] = "ElectedTx1111111111111111111111111111111111111111",
            ["block_time"] = "2026-10-02T10:00:00+00:00",
            ["program"] = "marketplace"
        }.ToJsonString();

        using var response = await host.Client.PostAsync(
            $"/webhooks/spv_lawyer_elected_devnet?secret={Uri.EscapeDataString(WebhookSecret)}",
            new StringContent(json, Encoding.UTF8, "application/json"));
        var state = await host.WithDbAsync(async db => new
        {
            RoundOneHasResult = await db.Messages.AnyAsync(message =>
                message.BucketId == roundOneBucketId && message.Tag == "voting/result"),
            RoundTwoResult = await db.Messages.SingleAsync(message =>
                message.BucketId == roundTwoBucketId && message.Tag == "voting/result")
        });

        Assert.Multiple(() =>
        {
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
            Assert.That(state.RoundOneHasResult, Is.False);
            Assert.That(state.RoundTwoResult.Contributor, Is.EqualTo(WebhooksHost.DefaultBucketAdminAddress));
            using var result = JsonDocument.Parse(state.RoundTwoResult.IpfsContent!);
            Assert.That(result.RootElement.GetProperty("status").GetString(), Is.EqualTo("rejected"));
            Assert.That(result.RootElement.GetProperty("round").GetInt32(), Is.EqualTo(2));
        });
    }

    [TestCase(true)]
    [TestCase(false)]
    public async Task Late_claim_after_finalization_reconciles_open_then_result(bool ratified)
    {
        await using var host = await WebhooksHost.StartAsync(webhookSecret: WebhookSecret);
        await SeedNamespaceAsync(host);
        var legalBucketId = await SeedLegalBucketAsync(host);
        await SeedPrerequisitesAsync(host);
        const string losingLawyer = "LawyerWallet222";
        await host.SeedViewerKeyAsync(losingLawyer, TestDb.Hash32);
        await host.SeedTermsDocumentAsync(losingLawyer, PdfBytes);
        host.Indexer.SpvElection = new SpvElectionInfo(
            1, Expiry, 1, ratified ? Lawyer : "11111111111111111111111111111111");
        host.Indexer.SpvCandidacy = new SpvCandidacyInfo(Candidacy, 1, Lawyer);
        var json = new JsonObject
        {
            ["event"] = "spv_lawyer_elected",
            ["listing_id"] = ListingId,
            ["property_id"] = ListingId,
            ["listing"] = "ListingPDA111",
            ["property"] = AssetPda,
            ["cluster"] = "devnet",
            ["winner_registry"] = "WinnerRegistryPDA111",
            ["slot"] = 487_700_200,
            ["tx_signature"] = "ElectedTx1111111111111111111111111111111111111111",
            ["block_time"] = "2026-10-02T10:00:00+00:00",
            ["program"] = "marketplace"
        }.ToJsonString();
        using (var finalResponse = await host.Client.PostAsync(
            $"/webhooks/spv_lawyer_elected_devnet?secret={Uri.EscapeDataString(WebhookSecret)}",
            new StringContent(json, Encoding.UTF8, "application/json")))
        {
            Assert.That(finalResponse.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        }
        host.Indexer.SpvElection = new SpvElectionInfo(
            1, Expiry, 1, "11111111111111111111111111111111");

        var (claimStatus, claim) = await PostClaimAsync(
            host, lawyer: losingLawyer, candidacy: "CandidacyPDA222");
        var bucketId = claim.GetProperty("lawyerTermsBucketId").GetInt64();
        var (redeliveryStatus, _) = await PostClaimAsync(
            host, lawyer: losingLawyer, candidacy: "CandidacyPDA222");
        var state = await host.WithDbAsync(async db => new
        {
            LegalResult = await db.Messages.SingleAsync(message =>
                message.BucketId == legalBucketId && message.Tag == "voting/result"),
            TermsMessages = await db.Messages.Where(message => message.BucketId == bucketId)
                .OrderBy(message => message.MessageId).ToListAsync()
        });

        Assert.Multiple(() =>
        {
            Assert.That(claimStatus, Is.EqualTo(HttpStatusCode.OK));
            Assert.That(redeliveryStatus, Is.EqualTo(HttpStatusCode.OK));
            Assert.That(state.TermsMessages, Has.Count.EqualTo(5));
            Assert.That(state.TermsMessages[^2].Tag, Is.EqualTo("voting/terms"));
            Assert.That(state.TermsMessages[^1].Tag, Is.EqualTo("voting/result"));
            Assert.That(state.TermsMessages[^1].Contributor,
                Is.EqualTo(WebhooksHost.DefaultBucketAdminAddress));
            Assert.That(state.TermsMessages[^1].IpfsContent,
                Is.EqualTo(state.LegalResult.IpfsContent));
            using var result = JsonDocument.Parse(state.TermsMessages[^1].IpfsContent!);
            Assert.That(result.RootElement.GetProperty("status").GetString(),
                Is.EqualTo(ratified ? "ratified" : "rejected"));
        });
    }

    [Test]
    public async Task Missing_or_blank_admin_returns_503_without_a_terms_bucket()
    {
        await using var host = await WebhooksHost.StartAsync(
            webhookSecret: WebhookSecret, bucketAdminAddress: "");
        await SeedNamespaceAsync(host);
        await SeedLegalBucketAsync(host);
        await SeedPrerequisitesAsync(host);

        var (status, _) = await PostClaimAsync(host);

        await Assert.MultipleAsync(async () =>
        {
            Assert.That(status, Is.EqualTo(HttpStatusCode.ServiceUnavailable));
            Assert.That(await TermsBucketCountAsync(host), Is.Zero);
        });
    }

    [TestCase("")]
    [TestCase("{not-json}")]
    public async Task Missing_or_invalid_admin_jwk_returns_503_without_a_terms_bucket(string jwk)
    {
        await using var host = await WebhooksHost.StartAsync(
            webhookSecret: WebhookSecret, bucketAdminX25519Key: jwk);
        await SeedNamespaceAsync(host);
        await SeedLegalBucketAsync(host);
        await SeedPrerequisitesAsync(host);

        var (status, _) = await PostClaimAsync(host);

        await Assert.MultipleAsync(async () =>
        {
            Assert.That(status, Is.EqualTo(HttpStatusCode.ServiceUnavailable));
            Assert.That(await TermsBucketCountAsync(host), Is.Zero);
        });
    }

    [Test]
    public async Task Missing_namespace_returns_404_without_a_terms_bucket()
    {
        await using var host = await WebhooksHost.StartAsync(webhookSecret: WebhookSecret);
        await SeedPrerequisitesAsync(host);

        var (status, _) = await PostClaimAsync(host);

        await Assert.MultipleAsync(async () =>
        {
            Assert.That(status, Is.EqualTo(HttpStatusCode.NotFound));
            Assert.That(await TermsBucketCountAsync(host), Is.Zero);
        });
    }

    [Test]
    public async Task Missing_pdf_returns_404_without_a_terms_bucket()
    {
        await using var host = await WebhooksHost.StartAsync(webhookSecret: WebhookSecret);
        await SeedNamespaceAsync(host);
        await SeedLegalBucketAsync(host);
        await SeedPrerequisitesAsync(host, document: false);

        var (status, _) = await PostClaimAsync(host);

        await Assert.MultipleAsync(async () =>
        {
            Assert.That(status, Is.EqualTo(HttpStatusCode.NotFound));
            Assert.That(await TermsBucketCountAsync(host), Is.Zero);
        });
    }

    [TestCase(Lawyer)]
    [TestCase(HolderOne)]
    public async Task Missing_required_viewer_key_returns_404_without_a_terms_bucket(string missingKey)
    {
        await using var host = await WebhooksHost.StartAsync(webhookSecret: WebhookSecret);
        await SeedNamespaceAsync(host);
        await SeedLegalBucketAsync(host);
        await SeedPrerequisitesAsync(host, holders: [HolderOne], missingKey: missingKey);

        var (status, _) = await PostClaimAsync(host);

        await Assert.MultipleAsync(async () =>
        {
            Assert.That(status, Is.EqualTo(HttpStatusCode.NotFound));
            Assert.That(await TermsBucketCountAsync(host), Is.Zero);
        });
    }

    [Test]
    public async Task Holder_resolution_failure_returns_502_without_a_terms_bucket()
    {
        await using var host = await WebhooksHost.StartAsync(webhookSecret: WebhookSecret);
        await SeedNamespaceAsync(host);
        await SeedLegalBucketAsync(host);
        await SeedPrerequisitesAsync(host);
        host.Indexer.ThrowOnHolders = true;

        var (status, _) = await PostClaimAsync(host);

        await Assert.MultipleAsync(async () =>
        {
            Assert.That(status, Is.EqualTo(HttpStatusCode.BadGateway));
            Assert.That(await TermsBucketCountAsync(host), Is.Zero);
        });
    }

    [Test]
    public async Task Object_storage_failure_returns_502_without_a_terms_bucket()
    {
        await using var host = await WebhooksHost.StartAsync(webhookSecret: WebhookSecret);
        await SeedNamespaceAsync(host);
        await SeedLegalBucketAsync(host);
        await SeedPrerequisitesAsync(host);
        host.S3.DownloadError = new AmazonS3Exception("simulated object storage failure");

        var (status, _) = await PostClaimAsync(host);

        await Assert.MultipleAsync(async () =>
        {
            Assert.That(status, Is.EqualTo(HttpStatusCode.BadGateway));
            Assert.That(await TermsBucketCountAsync(host), Is.Zero);
        });
    }

    [Test]
    public async Task Missing_lawyer_or_candidacy_is_rejected_before_any_bucket_creation()
    {
        await using var host = await WebhooksHost.StartAsync(webhookSecret: WebhookSecret);
        await SeedNamespaceAsync(host);
        await SeedLegalBucketAsync(host);
        await SeedPrerequisitesAsync(host);

        var (noLawyer, _) = await PostClaimAsync(host, lawyer: null);
        var (noCandidacy, _) = await PostClaimAsync(host, candidacy: null);

        await Assert.MultipleAsync(async () =>
        {
            Assert.That(noLawyer, Is.EqualTo(HttpStatusCode.BadRequest));
            Assert.That(noCandidacy, Is.EqualTo(HttpStatusCode.BadRequest));
            Assert.That(await TermsBucketCountAsync(host), Is.Zero);
        });
    }

    [TestCase(null)]
    [TestCase("wrong-secret")]
    public async Task Missing_or_wrong_webhook_secret_is_unauthorized_without_side_effects(
        string? presentedSecret)
    {
        await using var host = await WebhooksHost.StartAsync(webhookSecret: WebhookSecret);
        await SeedNamespaceAsync(host);
        await SeedLegalBucketAsync(host);
        await SeedPrerequisitesAsync(host);

        var (status, _) = await PostClaimAsync(host, presentedSecret: presentedSecret);

        await Assert.MultipleAsync(async () =>
        {
            Assert.That(status, Is.EqualTo(HttpStatusCode.Unauthorized));
            Assert.That(await TermsBucketCountAsync(host), Is.Zero);
        });
    }

    [Test]
    public async Task Missing_webhook_secret_configuration_returns_503_without_side_effects()
    {
        await using var host = await WebhooksHost.StartAsync();

        var (status, _) = await PostClaimAsync(host);

        await Assert.MultipleAsync(async () =>
        {
            Assert.That(status, Is.EqualTo(HttpStatusCode.ServiceUnavailable));
            Assert.That(await TermsBucketCountAsync(host), Is.Zero);
        });
    }

    [Test]
    public async Task Base64url_profile_keys_are_preserved_as_viewer_memberships()
    {
        const string lawyerKey = "u4FbLrw9hDL8bTbGBoXwgIgAgJ_71i4K3ZX6KW8oMDo";
        const string holderKey = "wGf64t6XctVDZW1Nbb3n_A6NFHgjsUCCKZplxaZ921A";
        await using var host = await WebhooksHost.StartAsync(webhookSecret: WebhookSecret);
        await SeedNamespaceAsync(host);
        await SeedLegalBucketAsync(host);
        await SeedPrerequisitesAsync(host);
        await host.SeedViewerKeyAsync(Lawyer, lawyerKey);
        await host.SeedViewerKeyAsync(HolderOne, holderKey);

        var (status, body) = await PostClaimAsync(host);
        Assert.That(status, Is.EqualTo(HttpStatusCode.OK));
        var bucketId = body.GetProperty("lawyerTermsBucketId").GetInt64();
        var viewers = await host.WithDbAsync(async db => await db.BucketViewers
            .Where(row => row.BucketId == bucketId)
            .Select(row => row.ViewerId)
            .ToListAsync());

        Assert.That(viewers, Is.EquivalentTo(new[]
        {
            WebhooksHost.DefaultBucketAdminViewerKey,
            lawyerKey,
            holderKey,
            TestDb.ViewerKey32Base64Url
        }));
    }

    [Test]
    public async Task Duplicate_wallets_and_viewer_keys_create_only_one_membership_per_subject()
    {
        await using var host = await WebhooksHost.StartAsync(webhookSecret: WebhookSecret);
        await SeedNamespaceAsync(host);
        await SeedLegalBucketAsync(host);
        await SeedPrerequisitesAsync(host,
            holders: [Lawyer, WebhooksHost.DefaultBucketAdminAddress, HolderOne]);
        await host.SeedViewerKeyAsync(HolderOne, TestDb.Key32);

        var (_, body) = await PostClaimAsync(host);
        var bucketId = body.GetProperty("lawyerTermsBucketId").GetInt64();
        var state = await host.WithDbAsync(async db => new
        {
            Contributors = await db.BucketContributors.Where(row => row.BucketId == bucketId)
                .Select(row => row.SubjectId).ToListAsync(),
            Viewers = await db.BucketViewers.Where(row => row.BucketId == bucketId)
                .Select(row => row.ViewerId).ToListAsync()
        });

        Assert.Multiple(() =>
        {
            Assert.That(state.Contributors, Is.EquivalentTo(new[]
            {
                WebhooksHost.DefaultBucketAdminAddress, HolderOne, Lawyer
            }));
            Assert.That(state.Viewers, Is.EquivalentTo(new[]
            {
                WebhooksHost.DefaultBucketAdminViewerKey, TestDb.Key32Base64Url
            }));
        });
    }
}
