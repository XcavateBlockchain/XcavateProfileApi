using System.Data.Common;
using System.Net;
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
        var barrier = new BucketInsertBarrier();
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

            var responses = await Task.WhenAll(
                LawyerReviewBucketWebhookTests.PostDeveloperAsync(host),
                LawyerReviewBucketWebhookTests.PostDeveloperAsync(host));
            var buckets = (await host.BucketsAsync())
                .Where(bucket => bucket.Category == "lawyer-document-review")
                .ToList();
            var messageCount = await host.WithDbAsync(async db => await db.Messages.CountAsync());

            Assert.Multiple(() =>
            {
                Assert.That(responses.Select(response => response.Status),
                    Is.All.EqualTo(HttpStatusCode.OK));
                Assert.That(barrier.Arrivals, Is.EqualTo(2), "both requests must race at bucket insertion");
                Assert.That(buckets, Has.Count.EqualTo(1));
                Assert.That(buckets.Select(bucket => bucket.EncryptionKey).Distinct().Count(), Is.EqualTo(1));
                Assert.That(messageCount, Is.EqualTo(4));
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

    private sealed class BucketInsertBarrier : DbCommandInterceptor
    {
        private readonly TaskCompletionSource _bothInserts =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _arrivals;

        public int Arrivals => _arrivals;

        public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            await WaitForBothInsertsAsync(command.CommandText);
            return await base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
        }

        public override async ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            await WaitForBothInsertsAsync(command.CommandText);
            return await base.NonQueryExecutingAsync(command, eventData, result, cancellationToken);
        }

        private async Task WaitForBothInsertsAsync(string commandText)
        {
            var bucketInsert = commandText.Contains("INSERT INTO buckets", StringComparison.Ordinal)
                || commandText.Contains("INSERT INTO \"buckets\"", StringComparison.Ordinal);
            if (!bucketInsert)
            {
                return;
            }
            if (Interlocked.Increment(ref _arrivals) == 2)
            {
                _bothInserts.TrySetResult();
            }
            await _bothInserts.Task.WaitAsync(TimeSpan.FromSeconds(15));
        }
    }
}
