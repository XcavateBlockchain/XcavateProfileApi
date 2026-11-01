using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Amazon.S3;
using Microsoft.EntityFrameworkCore;
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
    private static readonly byte[] SpvPdf = "%PDF-1.4 SPV terms"u8.ToArray();
    private static readonly byte[] DeveloperPdf = "%PDF-1.4 developer terms"u8.ToArray();

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

        Assert.Multiple(() =>
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
                TestDb.Key32Base64Url, TestDb.OtherKey32Base64Url,
                TestDb.ViewerKey32Base64Url, TestDb.Hash32Base64Url
            }));
            Assert.That(state.Messages, Has.Count.EqualTo(4));
            Assert.That(state.Messages.Select(message => message.Contributor), Is.All.EqualTo(
                WebhooksHost.DefaultBucketAdminAddress));
            Assert.That(state.Messages[0].Tag, Is.EqualTo("didcomm/key-sharing-v1"));
            using (var keySharing = JsonDocument.Parse(state.Messages[0].IpfsContent!))
            {
                Assert.That(keySharing.RootElement.GetProperty("recipients").GetArrayLength(), Is.EqualTo(4));
                Assert.That(state.Messages[0].IpfsContent, Does.Not.Contain("\"d\""));
            }
            Assert.That(state.Messages[1].Tag, Is.EqualTo("lawyer/document-review"));
            Assert.That(state.Messages[1].Reference, Is.EqualTo("lawyer-review/12/prompt"));
            Assert.That(state.Messages[1].IpfsContent, Is.EqualTo(ReviewPrompt));
            Assert.That(state.Messages[1].ContentHash, Is.EqualTo("0x" + Convert.ToHexString(
                SHA256.HashData(Encoding.UTF8.GetBytes(ReviewPrompt)))));
            AssertDocument(state.Messages[2], "spv-lawyer", SpvPdf);
            AssertDocument(state.Messages[3], "developer-lawyer", DeveloperPdf);
        });
    }

    private static void AssertDocument(
        XcavateBuckets.Domain.Entities.Message message, string role, byte[] pdf)
    {
        Assert.Multiple(() =>
        {
            Assert.That(message.Tag, Is.EqualTo("lawyer/terms-document"));
            Assert.That(message.Reference, Is.EqualTo(
                $"https://objects.test/xcavate-profile/terms-documents/by-listing/12/{role}.pdf"));
            Assert.That(message.Description, Is.EqualTo("terms.pdf"));
            Assert.That(message.ContentType, Is.EqualTo("application/pdf"));
            Assert.That(message.IpfsContent, Is.Null);
            Assert.That(message.ContentHash,
                Is.EqualTo("0x" + Convert.ToHexString(SHA256.HashData(pdf))));
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

        Assert.Multiple(() =>
        {
            Assert.That(status, Is.EqualTo(HttpStatusCode.OK));
            Assert.That(messages, Has.Count.EqualTo(4));
            Assert.That(messages[messageIndex].Tag, Is.EqualTo("lawyer/terms-document-missing"));
            Assert.That(messages[messageIndex].Reference,
                Is.EqualTo($"lawyer-review/12/{role}-document-missing"));
            Assert.That(messages[messageIndex].ContentType, Is.EqualTo("text/plain"));
            Assert.That(messages[messageIndex].IpfsContent, Is.EqualTo(fallback));
            Assert.That(messages[messageIndex].ContentHash, Is.EqualTo("0x" + Convert.ToHexString(
                SHA256.HashData(Encoding.UTF8.GetBytes(fallback)))));
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
            await db.SaveChangesAsync();
            return bucket.EncryptionKey;
        });

        var redeliveryStatus = spvRedelivery
            ? (await PostSpvAsync(host, "mainnet")).Status
            : (await PostDeveloperAsync(host, cluster: "mainnet")).Status;
        var bucket = (await host.BucketsAsync()).Single(bucket => bucket.BucketId == bucketId);
        var messageCount = await host.WithDbAsync(async db => await db.Messages
            .CountAsync(message => message.BucketId == bucketId));

        Assert.Multiple(() =>
        {
            Assert.That(initialStatus, Is.EqualTo(HttpStatusCode.OK));
            Assert.That(redeliveryStatus, Is.EqualTo(HttpStatusCode.OK));
            Assert.That(bucket.EncryptionKey, Is.EqualTo(originalKey));
            Assert.That(messageCount, Is.EqualTo(4));
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
            Assert.That(state.Viewers, Is.EquivalentTo(new[] { TestDb.Key32Base64Url, TestDb.OtherKey32Base64Url }));
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
            Assert.That(state.Viewers, Is.EquivalentTo(new[] { TestDb.Key32Base64Url, TestDb.OtherKey32Base64Url }));
            using var keySharing = JsonDocument.Parse(state.KeyMessage.IpfsContent!);
            Assert.That(keySharing.RootElement.GetProperty("recipients").GetArrayLength(), Is.EqualTo(2));
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
