using System.Net;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using XcavateBuckets.Domain.Entities;
using XcavateBuckets.Domain.Services;
using XcavateProfile.Client;
using XcavateProfileApi.Data;
using XcavateProfileApi.Services.Notifications;

namespace XcavateBuckets.Tests;

/// <summary>
/// The API-side half of push notifications: fan-out and chain detection in
/// <see cref="PushBucketNotifier"/>, and the wire format of <see cref="NotificationsApiClient"/>.
/// </summary>
[TestFixture]
public class PushNotificationTests
{
    private static CancellationToken Ct => TestContext.CurrentContext.CancellationToken;

    /// <summary>A genuine 32-byte base58 Solana public key (also used in .env.example).</summary>
    private const string SolanaAddress = "DQJZmAVJZmN919gkbxREzb5iqoLZWLYx65Ts5JDnSb1b";

    private TestDb _fixture = null!;
    private ProfileTestDb _profiles = null!;
    private NotificationQueue _queue = null!;
    private PushBucketNotifier _notifier = null!;

    [SetUp]
    public void SetUp()
    {
        _fixture = new TestDb();
        _profiles = new ProfileTestDb();
        _queue = new NotificationQueue(NullLogger<NotificationQueue>.Instance);
        _notifier = new PushBucketNotifier(
            _fixture.Db, _profiles.Db, _queue, NullLogger<PushBucketNotifier>.Instance);
    }

    [TearDown]
    public void TearDown()
    {
        _fixture.Dispose();
        _profiles.Dispose();
    }

    private List<PushNotification> Drain()
    {
        var items = new List<PushNotification>();
        while (_queue.Reader.TryRead(out var item))
        {
            items.Add(item);
        }

        return items;
    }

    [Test]
    public async Task Message_pushes_go_to_admins_and_contributors_except_the_sender()
    {
        var bucket = await _fixture.SeedBucketAsync(
            null, isWritable: true,
            admins: [TestDb.Carol, SolanaAddress],
            contributors: [TestDb.Bob, TestDb.Carol]);
        var message = new Message { BucketId = bucket.BucketId, Contributor = TestDb.Bob };

        await _notifier.MessageWrittenAsync(bucket, message, Ct);

        var pushes = Drain();
        Assert.Multiple(() =>
        {
            // Carol appears once despite holding both roles; the sender gets nothing; the
            // Solana admin is addressed on its own chain.
            Assert.That(pushes.Select(p => (p.Chain, p.Address)), Is.EquivalentTo(new[]
            {
                (PushNotification.PolkadotChain, TestDb.Carol),
                (PushNotification.SolanaChain, SolanaAddress)
            }));
            Assert.That(pushes.Select(p => p.Title), Is.All.EqualTo("seeded"));
            Assert.That(pushes.Select(p => p.Body), Is.All.EqualTo("New message from 5FHneW…94ty"));
        });
    }

    [Test]
    public async Task Message_push_body_uses_the_senders_nickname_when_a_profile_exists()
    {
        var bucket = await _fixture.SeedBucketAsync(
            null, isWritable: true, admins: [TestDb.Carol], contributors: [TestDb.Bob]);
        _profiles.Db.Profiles.Add(new Profile
        {
            Ss58Address = TestDb.Bob,
            Nickname = "bob",
            X25519Key = TestDb.Key32
        });
        await _profiles.Db.SaveChangesAsync(Ct);
        var message = new Message { BucketId = bucket.BucketId, Contributor = TestDb.Bob };

        await _notifier.MessageWrittenAsync(bucket, message, Ct);

        Assert.That(Drain().Single().Body, Is.EqualTo("New message from bob"));
    }

    [Test]
    public async Task Member_added_pushes_name_the_granted_role()
    {
        var bucket = await _fixture.SeedBucketAsync(null);

        await _notifier.MemberAddedAsync(bucket, TestDb.Carol, BucketMemberRole.Admin, Ct);
        await _notifier.MemberAddedAsync(bucket, TestDb.Dave, BucketMemberRole.Contributor, Ct);

        var pushes = Drain();
        Assert.That(pushes, Is.EqualTo(new[]
        {
            new PushNotification(
                PushNotification.PolkadotChain, TestDb.Carol, "seeded",
                "You are now an admin of this bucket."),
            new PushNotification(
                PushNotification.PolkadotChain, TestDb.Dave, "seeded",
                "You are now a contributor of this bucket.")
        }));
    }

    [Test]
    public async Task Subjects_that_are_not_wallet_addresses_are_skipped()
    {
        var bucket = await _fixture.SeedBucketAsync(null);

        // An X25519 viewer key, and TestDb.Alice — which is not a checksum-valid SS58 string
        // (unlike Bob, Carol and Dave), standing in for corrupt member data.
        await _notifier.MemberAddedAsync(bucket, TestDb.Key32, BucketMemberRole.Contributor, Ct);
        await _notifier.MemberAddedAsync(bucket, TestDb.Alice, BucketMemberRole.Contributor, Ct);

        Assert.That(Drain(), Is.Empty);
    }

    [Test]
    public async Task Client_sends_the_documented_request_shape()
    {
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK));
        var client = NewClient(handler, apiKey: "secret-key");

        await client.SendAsync(
            new PushNotification("solana", SolanaAddress, "My bucket", "Hello"), Ct);

        var (request, body) = handler.Requests.Single();
        using var json = JsonDocument.Parse(body!);
        Assert.Multiple(() =>
        {
            Assert.That(request.Method, Is.EqualTo(HttpMethod.Post));
            Assert.That(request.RequestUri!.ToString(),
                Is.EqualTo("https://notifications.example/api/fcm/send-notification/"));
            Assert.That(request.Headers.Authorization!.ToString(), Is.EqualTo("Api-Key secret-key"));
            Assert.That(json.RootElement.GetProperty("chain").GetString(), Is.EqualTo("solana"));
            Assert.That(json.RootElement.GetProperty("address").GetString(),
                Is.EqualTo(SolanaAddress));
            Assert.That(json.RootElement.GetProperty("title").GetString(), Is.EqualTo("My bucket"));
            Assert.That(json.RootElement.GetProperty("body").GetString(), Is.EqualTo("Hello"));
        });
    }

    [Test]
    public void Client_swallows_transport_failures_and_error_statuses()
    {
        var failing = NewClient(new StubHandler(_ => throw new HttpRequestException("down")));
        var notFound = NewClient(
            new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.NotFound)));
        var push = new PushNotification("polkadot", TestDb.Alice, "t", "b");

        Assert.Multiple(() =>
        {
            Assert.DoesNotThrowAsync(() => failing.SendAsync(push, Ct));
            Assert.DoesNotThrowAsync(() => notFound.SendAsync(push, Ct));
        });
    }

    [Test]
    public async Task Dispatcher_delivers_queued_pushes()
    {
        var delivered = new TaskCompletionSource();
        var sent = 0;
        var handler = new StubHandler(_ =>
        {
            if (Interlocked.Increment(ref sent) == 2)
            {
                delivered.TrySetResult();
            }

            return new HttpResponseMessage(HttpStatusCode.OK);
        });
        var dispatcher = new NotificationDispatcher(_queue, NewClient(handler));

        await dispatcher.StartAsync(Ct);
        try
        {
            _queue.Enqueue(new PushNotification("polkadot", TestDb.Alice, "t", "one"));
            _queue.Enqueue(new PushNotification("solana", SolanaAddress, "t", "two"));
            await delivered.Task.WaitAsync(TimeSpan.FromSeconds(10), Ct);
        }
        finally
        {
            await dispatcher.StopAsync(CancellationToken.None);
        }

        Assert.That(handler.Requests, Has.Count.EqualTo(2));
    }

    private static NotificationsApiClient NewClient(StubHandler handler, string apiKey = "key") =>
        new(
            new StubHttpClientFactory(handler),
            new NotificationsOptions { BaseUrl = "https://notifications.example", ApiKey = apiKey },
            NullLogger<NotificationsApiClient>.Instance);

    /// <summary>
    /// The profile store on its own in-memory connection: sharing the bucket fixture's connection
    /// would make the second EnsureCreated a no-op, since SQLite already has tables.
    /// </summary>
    private sealed class ProfileTestDb : IDisposable
    {
        private readonly SqliteConnection _connection = new("DataSource=:memory:");

        public ProfileTestDb()
        {
            _connection.Open();
            Db = new ProfileDbContext(new DbContextOptionsBuilder<ProfileDbContext>()
                .UseSqlite(_connection)
                .Options);
            Db.Database.EnsureCreated();
        }

        public ProfileDbContext Db { get; }

        public void Dispose()
        {
            Db.Dispose();
            _connection.Dispose();
        }
    }

    /// <summary>Captures outbound requests; the body is read eagerly before disposal.</summary>
    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond)
        : HttpMessageHandler
    {
        public List<(HttpRequestMessage Request, string? Body)> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null
                ? null
                : await request.Content.ReadAsStringAsync(cancellationToken);
            Requests.Add((request, body));
            return respond(request);
        }
    }

    private sealed class StubHttpClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }
}
