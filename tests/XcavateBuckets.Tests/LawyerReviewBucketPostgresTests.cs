using System.Data.Common;
using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Npgsql;

namespace XcavateBuckets.Tests;

[TestFixture]
public class LawyerReviewBucketPostgresTests
{
    [Test]
    public async Task Concurrent_triggers_converge_on_one_bucket_key_and_four_messages()
    {
        var adminConnection = Environment.GetEnvironmentVariable("PROFILE_POSTGRES_TEST_CONNECTION");
        if (string.IsNullOrWhiteSpace(adminConnection))
        {
            Assert.Ignore("PostgreSQL integration requires PROFILE_POSTGRES_TEST_CONNECTION.");
        }

        var databaseName = $"lawyer_review_{Guid.NewGuid():N}";
        await using var master = new NpgsqlConnection(adminConnection);
        await master.OpenAsync();
        await using (var create = new NpgsqlCommand($"CREATE DATABASE \"{databaseName}\"", master))
        {
            await create.ExecuteNonQueryAsync();
        }

        WebhooksHost? host = null;
        var barrier = new BucketCreationLockBarrier();
        try
        {
            var connectionString = new NpgsqlConnectionStringBuilder(adminConnection)
            {
                Database = databaseName
            }.ConnectionString;
            host = await WebhooksHost.StartAsync(
                webhookSecret: "lawyer-review-test-secret",
                postgresConnectionString: connectionString,
                bucketInterceptor: barrier);
            await LawyerReviewBucketWebhookTests.SeedNamespaceAsync(host);
            // Both lawyers have a real terms PDF, so every creation attempt would snapshot one.
            await LawyerReviewBucketWebhookTests.SeedPrerequisitesAsync(host);

            var responses = await Task.WhenAll(
                LawyerReviewBucketWebhookTests.PostDeveloperAsync(host),
                LawyerReviewBucketWebhookTests.PostDeveloperAsync(host));
            var buckets = (await host.BucketsAsync())
                .Where(bucket => bucket.Category == "lawyer-document-review")
                .ToList();
            var messages = await host.WithDbAsync(async db => await db.Messages
                .OrderBy(message => message.MessageId)
                .ToListAsync());
            var snapshotKeys = host.S3.Uploads
                .Select(upload => upload.Key)
                .Where(key => key.StartsWith("terms-documents/by-listing/", StringComparison.Ordinal))
                .ToList();

            // The winner's own bucket key must open the snapshots its messages reference: a loser
            // that had snapshotted under its own discarded key would fail this.
            var bucketId = buckets.Single().BucketId;
            var reviewMessages = messages
                .Where(message => message.BucketId == bucketId)
                .ToList();
            var decrypted = await DecryptSnapshotsAsync(host, reviewMessages, bucketId);

            Assert.Multiple(() =>
            {
                Assert.That(responses.Select(response => response.Status),
                    Is.All.EqualTo(HttpStatusCode.OK));
                Assert.That(barrier.Arrivals, Is.EqualTo(2),
                    "both requests must race at the review bucket's creation lock");
                Assert.That(buckets, Has.Count.EqualTo(1));
                Assert.That(buckets.Select(bucket => bucket.EncryptionKey).Distinct().Count(), Is.EqualTo(1));
                Assert.That(reviewMessages, Has.Count.EqualTo(4));
                Assert.That(snapshotKeys, Has.Count.EqualTo(2),
                    "only the winning creation may snapshot the two terms PDFs");
                Assert.That(
                    decrypted.Select(Encoding.UTF8.GetString),
                    Is.EquivalentTo(new[]
                    {
                        Encoding.UTF8.GetString(LawyerReviewBucketWebhookTests.SpvPdf),
                        Encoding.UTF8.GetString(LawyerReviewBucketWebhookTests.DeveloperPdf)
                    }));
            });
        }
        finally
        {
            if (host is not null)
            {
                await host.DisposeAsync();
            }
            NpgsqlConnection.ClearAllPools();
            await using var drop = new NpgsqlCommand(
                $"DROP DATABASE IF EXISTS \"{databaseName}\" WITH (FORCE)", master);
            await drop.ExecuteNonQueryAsync();
        }
    }

    [Test]
    public async Task Concurrent_legacy_redeliveries_backfill_metadata_without_key_or_message_changes()
    {
        var adminConnection = Environment.GetEnvironmentVariable("PROFILE_POSTGRES_TEST_CONNECTION");
        if (string.IsNullOrWhiteSpace(adminConnection))
        {
            Assert.Ignore("PostgreSQL integration requires PROFILE_POSTGRES_TEST_CONNECTION.");
        }

        var databaseName = $"lawyer_review_{Guid.NewGuid():N}";
        await using var master = new NpgsqlConnection(adminConnection);
        await master.OpenAsync();
        await using (var create = new NpgsqlCommand($"CREATE DATABASE \"{databaseName}\"", master))
        {
            await create.ExecuteNonQueryAsync();
        }

        WebhooksHost? host = null;
        var barrier = new BucketUpdateBarrier();
        try
        {
            var connectionString = new NpgsqlConnectionStringBuilder(adminConnection)
            {
                Database = databaseName
            }.ConnectionString;
            host = await WebhooksHost.StartAsync(
                webhookSecret: "lawyer-review-test-secret",
                postgresConnectionString: connectionString,
                bucketInterceptor: barrier);
            await LawyerReviewBucketWebhookTests.SeedNamespaceAsync(host);
            await LawyerReviewBucketWebhookTests.SeedPrerequisitesAsync(
                host, spvDocument: false, developerDocument: false);
            await LawyerReviewBucketWebhookTests.PostDeveloperAsync(
                host, cluster: null, propertyId: null);
            var bucketId = await LawyerReviewBucketWebhookTests.ReviewBucketIdAsync(host);
            var originalKey = await host.WithDbAsync(async db =>
            {
                var bucket = await db.Buckets.SingleAsync(row => row.BucketId == bucketId);
                bucket.Properties = """{"kind":"lawyer_document_review","listing_id":"12","custom":"preserved"}""";
                await db.SaveChangesAsync();
                return bucket.EncryptionKey;
            });
            barrier.Enabled = true;

            var responses = await Task.WhenAll(
                LawyerReviewBucketWebhookTests.PostDeveloperAsync(host),
                LawyerReviewBucketWebhookTests.PostDeveloperAsync(host));
            var bucket = (await host.BucketsAsync()).Single(row => row.BucketId == bucketId);
            var messageCount = await host.WithDbAsync(async db => await db.Messages
                .CountAsync(message => message.BucketId == bucketId));

            Assert.Multiple(() =>
            {
                Assert.That(responses.Select(response => response.Status),
                    Is.All.EqualTo(HttpStatusCode.OK));
                Assert.That(barrier.Arrivals, Is.EqualTo(2), "both requests must race at metadata update");
                Assert.That(bucket.EncryptionKey, Is.EqualTo(originalKey));
                Assert.That(messageCount, Is.EqualTo(4));
                using var properties = System.Text.Json.JsonDocument.Parse(bucket.Properties!);
                Assert.That(properties.RootElement.GetProperty("property_id").GetString(), Is.EqualTo("12"));
                Assert.That(properties.RootElement.GetProperty("cluster").GetString(), Is.EqualTo("devnet"));
                Assert.That(properties.RootElement.GetProperty("custom").GetString(), Is.EqualTo("preserved"));
            });
        }
        finally
        {
            if (host is not null)
            {
                await host.DisposeAsync();
            }
            NpgsqlConnection.ClearAllPools();
            await using var drop = new NpgsqlCommand(
                $"DROP DATABASE IF EXISTS \"{databaseName}\" WITH (FORCE)", master);
            await drop.ExecuteNonQueryAsync();
        }
    }

    private sealed class BucketUpdateBarrier : DbCommandInterceptor
    {
        private readonly TaskCompletionSource _bothUpdates =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _arrivals;

        public bool Enabled { get; set; }
        public int Arrivals => _arrivals;

        public override async ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            if (Enabled
                && (command.CommandText.Contains("UPDATE buckets", StringComparison.Ordinal)
                    || command.CommandText.Contains("UPDATE \"buckets\"", StringComparison.Ordinal)))
            {
                if (Interlocked.Increment(ref _arrivals) == 2)
                {
                    _bothUpdates.TrySetResult();
                }
                await _bothUpdates.Task.WaitAsync(TimeSpan.FromSeconds(15));
            }
            return await base.NonQueryExecutingAsync(command, eventData, result, cancellationToken);
        }
    }

    /// <summary>
    /// Reads each document message's snapshot object back from the reference it carries and
    /// decrypts it with the bucket's own key, so a snapshot written under some other creation
    /// attempt's key cannot pass.
    /// </summary>
    private static async Task<List<byte[]>> DecryptSnapshotsAsync(
        WebhooksHost host,
        IReadOnlyList<XcavateBuckets.Domain.Entities.Message> reviewMessages,
        long bucketId)
    {
        var keySharing = await host.WithDbAsync(async db => await db.Messages
            .Where(message => message.BucketId == bucketId
                && message.Tag == "didcomm/key-sharing-v1")
            .OrderByDescending(message => message.MessageId)
            .Select(message => message.IpfsContent)
            .SingleAsync());
        var plaintext = LawyerTermsBucketProtocolTests.DecryptForRecipient(
            keySharing!, WebhooksHost.DefaultBucketAdminPrivateKey);
        using var document = JsonDocument.Parse(plaintext);
        var secret = document.RootElement.GetProperty("keys")[0];
        var privateKey = WebEncoders.Base64UrlDecode(secret.GetProperty("d").GetString()!);
        var kid = secret.TryGetProperty("kid", out var kidElement)
            ? kidElement.GetString()
            : null;

        var decrypted = new List<byte[]>();
        foreach (var message in reviewMessages.Where(message =>
                     message.Tag == "lawyer/terms-document"))
        {
            // The reference is the API's route for the snapshot, so it is read the way the review
            // page reads it — through the API, which also proves the bucket's winner served bytes.
            using var served = await host.Client.GetAsync(message.Reference!);
            Assert.That(served.StatusCode, Is.EqualTo(HttpStatusCode.OK));
            var snapshot = await served.Content.ReadAsByteArrayAsync();
            Assert.That(message.ContentHash, Is.EqualTo("0x" + Convert.ToHexString(
                System.Security.Cryptography.SHA256.HashData(snapshot))),
                "the object at the message's reference is the payload the message hashes");
            decrypted.Add(LawyerTermsBucketProtocolTests.DecryptCompactMessage(
                Encoding.UTF8.GetString(snapshot), privateKey, kid));
        }

        return decrypted;
    }

    /// <summary>
    /// Holds both concurrent creations at the review bucket's advisory lock until both have
    /// arrived, so the database's own lock — not the test's timing — is what serialises them.
    /// </summary>
    private sealed class BucketCreationLockBarrier : DbCommandInterceptor
    {
        private readonly TaskCompletionSource _bothArrived =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _arrivals;

        public int Arrivals => _arrivals;

        public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            await WaitForBothAsync(command.CommandText);
            return await base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
        }

        public override async ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            await WaitForBothAsync(command.CommandText);
            return await base.NonQueryExecutingAsync(command, eventData, result, cancellationToken);
        }

        private async Task WaitForBothAsync(string commandText)
        {
            if (!commandText.Contains("pg_advisory_xact_lock", StringComparison.Ordinal))
            {
                return;
            }
            if (Interlocked.Increment(ref _arrivals) == 2)
            {
                _bothArrived.TrySetResult();
            }
            await _bothArrived.Task.WaitAsync(TimeSpan.FromSeconds(15));
        }
    }
}
