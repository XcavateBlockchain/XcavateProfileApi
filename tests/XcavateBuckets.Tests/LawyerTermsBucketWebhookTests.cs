using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Amazon.S3;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using XcavateBuckets.Domain.Entities;
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
            "/webhooks/init_property_asset_devnet",
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
            "/webhooks/property_claim_started_devnet",
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

        Assert.Multiple(() =>
        {
            Assert.That(status, Is.EqualTo(HttpStatusCode.OK));
            Assert.That(body.GetProperty("lawyerTermsBucketCreated").GetBoolean(), Is.True);
            Assert.That(body.GetProperty("lawyerTermsHolderCount").GetInt32(), Is.EqualTo(2));
            Assert.That(state.Bucket.NamespaceId, Is.Not.Null);
            Assert.That(state.Bucket.Name, Is.EqualTo($"42 Main Street - SPV lawyer terms {Candidacy}"));
            Assert.That(state.Bucket.Category, Is.EqualTo("spv-lawyer-terms"));
            Assert.That(state.Bucket.Creator, Is.EqualTo(AssetPda));
            Assert.That(state.Bucket.IsWritable, Is.True);
            Assert.That(state.Bucket.EncryptionKey, Does.StartWith("0x"));
            Assert.That(Convert.FromHexString(state.Bucket.EncryptionKey!.AsSpan(2)), Has.Length.EqualTo(32));
            Assert.That(Convert.FromHexString(state.Bucket.EncryptionKey.AsSpan(2)).Any(value => value != 0),
                Is.True);
            using (var properties = JsonDocument.Parse(state.Bucket.Properties!))
            {
                Assert.That(properties.RootElement.GetProperty("kind").GetString(),
                    Is.EqualTo("spv_lawyer_terms"));
                Assert.That(properties.RootElement.GetProperty("listing_id").GetString(), Is.EqualTo("12"));
                Assert.That(properties.RootElement.GetProperty("round").GetString(), Is.EqualTo("1"));
                Assert.That(properties.RootElement.GetProperty("lawyer").GetString(), Is.EqualTo(Lawyer));
                Assert.That(properties.RootElement.GetProperty("candidacy").GetString(), Is.EqualTo(Candidacy));
            }

            Assert.That(state.Admins, Is.EqualTo(new[] { WebhooksHost.DefaultBucketAdminAddress }));
            Assert.That(state.Contributors, Is.EquivalentTo(new[]
            {
                WebhooksHost.DefaultBucketAdminAddress, HolderOne, HolderTwo, Lawyer
            }));
            Assert.That(state.Viewers, Is.EquivalentTo(new[]
            {
                TestDb.OtherKey32, TestDb.ViewerKey32, TestDb.Key32
            }));

            Assert.That(state.Messages, Has.Count.EqualTo(3));
            Assert.That(state.Messages.Select(message => message.Contributor), Is.All.EqualTo(
                WebhooksHost.DefaultBucketAdminAddress));
            Assert.That(state.Messages[0].Tag, Is.EqualTo("didcomm/key-sharing-v1"));
            Assert.That(state.Messages[0].ContentType,
                Is.EqualTo("application/didcomm-encrypted+json"));
            using (var keySharing = JsonDocument.Parse(state.Messages[0].IpfsContent!))
            {
                Assert.That(keySharing.RootElement.GetProperty("recipients").GetArrayLength(),
                    Is.EqualTo(3));
                Assert.That(keySharing.RootElement.GetProperty("ciphertext").GetString(),
                    Is.Not.Null.And.Not.Empty);
                Assert.That(state.Messages[0].IpfsContent, Does.Not.Contain("\"keys\""));
                Assert.That(state.Messages[0].IpfsContent, Does.Not.Contain("\"d\""));
            }
            Assert.That(state.Messages[1].Tag, Is.EqualTo("lawyer/terms-vote"));
            Assert.That(state.Messages[1].Reference,
                Is.EqualTo($"lawyer-terms/{Candidacy}/vote-prompt"));
            Assert.That(state.Messages[1].ContentType, Is.EqualTo("text/plain"));
            Assert.That(state.Messages[1].IpfsContent, Is.EqualTo(
                "All property token holders should now vote on whether to accept the Lawyer's terms."));
            Assert.That(state.Messages[1].ContentHash, Is.EqualTo("0x" + Convert.ToHexString(
                SHA256.HashData(Encoding.UTF8.GetBytes(state.Messages[1].IpfsContent!)))));
            Assert.That(state.Messages[2].Tag, Is.EqualTo("lawyer/terms-document"));
            Assert.That(state.Messages[2].Reference,
                Is.EqualTo($"https://objects.test/xcavate-profile/terms-documents/by-candidacy/{Candidacy}.pdf"));
            Assert.That(state.Messages[2].Description, Is.EqualTo("terms.pdf"));
            Assert.That(state.Messages[2].ContentType, Is.EqualTo("application/pdf"));
            Assert.That(state.Messages[2].IpfsContent, Is.Null);
            Assert.That(state.Messages[2].ContentHash,
                Is.EqualTo("0x" + Convert.ToHexString(SHA256.HashData(PdfBytes))));
            Assert.That(state.LegalMessages.Single().Tag, Is.EqualTo("voting/terms"));
            Assert.That(state.LegalMessages.Single().Contributor, Is.EqualTo("system:voting"));
        });
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

        Assert.Multiple(() =>
        {
            Assert.That(snapshot, Is.EqualTo(PdfBytes));
            Assert.That(current, Is.EqualTo(replacement));
            Assert.That(reference,
                Is.EqualTo($"https://objects.test/xcavate-profile/terms-documents/by-candidacy/{Candidacy}.pdf"));
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
            Assert.That(messageCount, Is.EqualTo(3));
            Assert.That(bucket.EncryptionKey, Is.EqualTo(originalKey));
        });
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
            Assert.That(termsMessageCount, Is.EqualTo(3));
            Assert.That(legalMessageCount, Is.EqualTo(1));
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
            Assert.That(state.Viewers, Is.EquivalentTo(new[] { TestDb.Key32 }));
        });
    }
}
