using System.Linq;
using System.Text.Json;
using Substrate.NET.Wallet.Keyring;
using Substrate.NetApi;
using Substrate.NetApi.Model.Types;
using XcavateProfileApi.SocketIo;
using static Substrate.NetApi.Mnemonic;
using Account = Substrate.NetApi.Model.Types.Account;

namespace XcavateBuckets.Tests;

/// <summary>
/// The realtime endpoint end to end over the shipped pipeline: a socket.io handshake against the
/// test server, subscriptions, and a signed GraphQL <c>write</c> arriving as a <c>message</c>
/// event on the socket.
/// </summary>
[TestFixture]
public class SocketIoIntegrationTests
{
    private const string Key32 = TestDb.Key32;
    private const string Hash32 = TestDb.Hash32;

    private static readonly TimeSpan EventTimeout = TimeSpan.FromSeconds(10);

    private static Account Alice()
    {
        var mnemonic = string.Join(
            " ", MnemonicFromEntropy(Enumerable.Repeat((byte)0x31, 16).ToArray(), BIP39Wordlist.English));

        return new Keyring()
            .AddFromMnemonic(mnemonic, new Meta { Name = "SocketIoTests" }, KeyType.Sr25519)
            .Account;
    }

    /// <summary>
    /// A standalone writable bucket with <paramref name="account"/> as creator, admin and
    /// contributor. Bucket ids are global, so the first call yields bucket 1, the next bucket 2.
    /// </summary>
    private static async Task SeedWritableBucketAsync(GraphQLHost host, Account account, long bucketId)
    {
        async Task Run(string mutation)
        {
            var response = await host.SignedAsync(mutation, account);
            Assert.That(response.FirstErrorCode(), Is.Null, response.RootElement.ToString());
        }

        await Run("""mutation { createBucket(metadata: { name: "realtime", category: "chat" }) { id } }""");
        await Run($$"""mutation { addAdmin(bucketId: "{{bucketId}}", admin: "{{account.Value}}") { id } }""");
        await Run($$"""mutation { addContributor(bucketId: "{{bucketId}}", contributor: "{{account.Value}}") { id } }""");
        await Run($$"""mutation { resumeWriting(bucketId: "{{bucketId}}", newEncryptionKey: "{{Key32}}") { isWritable } }""");
    }

    private static async Task WriteMessageAsync(
        GraphQLHost host, Account account, long bucketId, string content)
    {
        var response = await host.SignedAsync(
            $$"""
              mutation {
                write(bucketId: "{{bucketId}}", message: {
                  reference: "ref-{{content}}"
                  ipfsContent: "{{content}}"
                  metadata: {
                    description: "a message"
                    contentType: "text/plain"
                    contentHash: "{{Hash32}}"
                  }
                }) { id }
              }
              """,
            account);
        Assert.That(response.FirstErrorCode(), Is.Null, response.RootElement.ToString());
    }

    [Test]
    public async Task Handshake_speaks_engine_io_v4_and_connects_the_default_namespace()
    {
        await using var host = await GraphQLHost.StartAsync();
        await using var client = await SocketIoTestClient.ConnectAsync(host);

        var open = client.OpenPacket.RootElement;
        Assert.Multiple(() =>
        {
            Assert.That(open.GetProperty("sid").GetString(), Is.Not.Null.And.Not.Empty);
            Assert.That(open.GetProperty("upgrades").GetArrayLength(), Is.Zero);
            Assert.That(open.GetProperty("pingInterval").GetInt32(), Is.EqualTo(25_000));
            Assert.That(open.GetProperty("pingTimeout").GetInt32(), Is.EqualTo(20_000));
            Assert.That(client.SocketSid, Is.Not.Null.And.Not.Empty);
            Assert.That(client.SocketSid, Is.Not.EqualTo(open.GetProperty("sid").GetString()),
                "the Socket.IO sid is distinct from the Engine.IO sid");
        });
    }

    [Test]
    public async Task Connecting_to_another_namespace_is_rejected()
    {
        await using var host = await GraphQLHost.StartAsync();
        await using var client = await SocketIoTestClient.ConnectAsync(host, connectNamespace: false);

        await client.SendRawAsync("40/admin,");
        var frame = await client.ReceiveFrameAsync();

        Assert.That(frame, Is.EqualTo("""44/admin,{"message":"Invalid namespace"}"""));
    }

    [Test]
    public async Task Subscribing_to_an_unknown_bucket_acks_UNKNOWN_BUCKET()
    {
        await using var host = await GraphQLHost.StartAsync();
        await using var client = await SocketIoTestClient.ConnectAsync(host);

        var ack = await client.EmitWithAckAsync("subscribe", "42");

        Assert.Multiple(() =>
        {
            Assert.That(ack.GetProperty("ok").GetBoolean(), Is.False);
            Assert.That(ack.GetProperty("error").GetProperty("code").GetString(),
                Is.EqualTo("UNKNOWN_BUCKET"));
        });
    }

    [Test]
    public async Task Subscribing_with_a_malformed_bucket_id_acks_INVALID_INPUT()
    {
        await using var host = await GraphQLHost.StartAsync();
        await using var client = await SocketIoTestClient.ConnectAsync(host);

        var ack = await client.EmitWithAckAsync("subscribe", true);

        Assert.Multiple(() =>
        {
            Assert.That(ack.GetProperty("ok").GetBoolean(), Is.False);
            Assert.That(ack.GetProperty("error").GetProperty("code").GetString(),
                Is.EqualTo("INVALID_INPUT"));
        });
    }

    [Test]
    public async Task Unknown_events_ack_UNKNOWN_EVENT()
    {
        await using var host = await GraphQLHost.StartAsync();
        await using var client = await SocketIoTestClient.ConnectAsync(host);

        var ack = await client.EmitWithAckAsync("query", "5");

        Assert.Multiple(() =>
        {
            Assert.That(ack.GetProperty("ok").GetBoolean(), Is.False);
            Assert.That(ack.GetProperty("error").GetProperty("code").GetString(),
                Is.EqualTo("UNKNOWN_EVENT"));
        });
    }

    [Test]
    public async Task A_written_message_reaches_the_subscriber_in_graphql_wire_shape()
    {
        var alice = Alice();
        await using var host = await GraphQLHost.StartAsync();
        await SeedWritableBucketAsync(host, alice, bucketId: 1);

        await using var client = await SocketIoTestClient.ConnectAsync(host);
        var ack = await client.EmitWithAckAsync("subscribe", "1");
        Assert.That(ack.GetProperty("ok").GetBoolean(), Is.True, ack.ToString());
        Assert.That(ack.GetProperty("bucketId").GetString(), Is.EqualTo("1"));

        await WriteMessageAsync(host, alice, bucketId: 1, content: "hello");

        var (eventName, data) = await client.WaitForEventAsync(EventTimeout);
        Assert.Multiple(() =>
        {
            Assert.That(eventName, Is.EqualTo("message"));
            Assert.That(data.GetProperty("id").GetString(), Is.EqualTo("1-0"));
            Assert.That(data.GetProperty("bucketId").GetString(), Is.EqualTo("1"));
            Assert.That(data.GetProperty("messageId").GetString(), Is.EqualTo("0"),
                "ids are strings on the wire, like the GraphQL BigInt scalar");
            Assert.That(data.GetProperty("contributor").GetString(), Is.EqualTo(alice.Value));
            Assert.That(data.GetProperty("reference").GetString(), Is.EqualTo("ref-hello"));
            Assert.That(data.GetProperty("ipfsContent").GetString(), Is.EqualTo("hello"));
            Assert.That(data.GetProperty("contentHash").GetString(), Is.EqualTo(Hash32));
            Assert.That(data.GetProperty("tag").ValueKind, Is.EqualTo(JsonValueKind.Null));
            Assert.That(DateTime.TryParse(data.GetProperty("createdAt").GetString(), out _), Is.True);
        });
    }

    [Test]
    public async Task Delivery_is_filtered_to_the_subscribed_bucket()
    {
        var alice = Alice();
        await using var host = await GraphQLHost.StartAsync();
        await SeedWritableBucketAsync(host, alice, bucketId: 1);
        await SeedWritableBucketAsync(host, alice, bucketId: 2);

        await using var client = await SocketIoTestClient.ConnectAsync(host);
        var ack = await client.EmitWithAckAsync("subscribe", "2");
        Assert.That(ack.GetProperty("ok").GetBoolean(), Is.True, ack.ToString());

        // The queue is FIFO and dispatch is sequential, so if bucket 1's event were delivered it
        // would arrive first; seeing bucket 2's event first proves bucket 1's was filtered out.
        await WriteMessageAsync(host, alice, bucketId: 1, content: "not-for-us");
        await WriteMessageAsync(host, alice, bucketId: 2, content: "for-us");

        var (_, data) = await client.WaitForEventAsync(EventTimeout);
        Assert.Multiple(() =>
        {
            Assert.That(data.GetProperty("bucketId").GetString(), Is.EqualTo("2"));
            Assert.That(data.GetProperty("ipfsContent").GetString(), Is.EqualTo("for-us"));
        });
    }

    [Test]
    public async Task Unsubscribing_stops_delivery()
    {
        var alice = Alice();
        await using var host = await GraphQLHost.StartAsync();
        await SeedWritableBucketAsync(host, alice, bucketId: 1);
        await SeedWritableBucketAsync(host, alice, bucketId: 2);

        await using var client = await SocketIoTestClient.ConnectAsync(host);
        Assert.That((await client.EmitWithAckAsync("subscribe", "1")).GetProperty("ok").GetBoolean(), Is.True);
        Assert.That((await client.EmitWithAckAsync("subscribe", "2")).GetProperty("ok").GetBoolean(), Is.True);
        Assert.That((await client.EmitWithAckAsync("unsubscribe", "1")).GetProperty("ok").GetBoolean(), Is.True);

        await WriteMessageAsync(host, alice, bucketId: 1, content: "muted");
        await WriteMessageAsync(host, alice, bucketId: 2, content: "audible");

        var (_, data) = await client.WaitForEventAsync(EventTimeout);
        Assert.That(data.GetProperty("bucketId").GetString(), Is.EqualTo("2"),
            "bucket 1 was unsubscribed, so its earlier write must not arrive");
    }

    [Test]
    public async Task Every_subscriber_of_the_bucket_receives_the_message()
    {
        var alice = Alice();
        await using var host = await GraphQLHost.StartAsync();
        await SeedWritableBucketAsync(host, alice, bucketId: 1);

        await using var first = await SocketIoTestClient.ConnectAsync(host);
        await using var second = await SocketIoTestClient.ConnectAsync(host);
        Assert.That((await first.EmitWithAckAsync("subscribe", "1")).GetProperty("ok").GetBoolean(), Is.True);
        Assert.That((await second.EmitWithAckAsync("subscribe", "1")).GetProperty("ok").GetBoolean(), Is.True);

        await WriteMessageAsync(host, alice, bucketId: 1, content: "fan-out");

        var (_, firstData) = await first.WaitForEventAsync(EventTimeout);
        var (_, secondData) = await second.WaitForEventAsync(EventTimeout);
        Assert.Multiple(() =>
        {
            Assert.That(firstData.GetProperty("id").GetString(), Is.EqualTo("1-0"));
            Assert.That(secondData.GetProperty("id").GetString(), Is.EqualTo("1-0"));
        });
    }

    [Test]
    public async Task Subscribe_without_an_ack_reports_failures_as_subscriptionError_events()
    {
        await using var host = await GraphQLHost.StartAsync();
        await using var client = await SocketIoTestClient.ConnectAsync(host);

        await client.SendRawAsync("""42["subscribe","42"]""");

        var (eventName, data) = await client.WaitForEventAsync(EventTimeout);
        Assert.Multiple(() =>
        {
            Assert.That(eventName, Is.EqualTo("subscriptionError"));
            Assert.That(data.GetProperty("action").GetString(), Is.EqualTo("subscribe"));
            Assert.That(data.GetProperty("bucketId").GetString(), Is.EqualTo("42"));
            Assert.That(data.GetProperty("error").GetProperty("code").GetString(),
                Is.EqualTo("UNKNOWN_BUCKET"));
        });
    }

    [Test]
    public async Task Polling_transport_is_rejected_with_a_hint()
    {
        await using var host = await GraphQLHost.StartAsync();

        var response = await host.Client.GetAsync("/socket.io/?EIO=4&transport=polling");
        var body = await response.Content.ReadAsStringAsync();

        Assert.Multiple(() =>
        {
            Assert.That((int)response.StatusCode, Is.EqualTo(400));
            Assert.That(body, Does.Contain("websocket"),
                "the rejection must tell the client how to connect");
        });
    }

    [Test]
    public async Task Other_engine_io_protocol_versions_are_rejected()
    {
        await using var host = await GraphQLHost.StartAsync();

        var response = await host.Client.GetAsync("/socket.io/?EIO=3&transport=websocket");

        Assert.That((int)response.StatusCode, Is.EqualTo(400));
    }

    [Test]
    public async Task Server_pings_and_a_ponging_client_stays_connected()
    {
        var options = new SocketIoOptions
        {
            PingInterval = TimeSpan.FromMilliseconds(200),
            PingTimeout = TimeSpan.FromMilliseconds(300)
        };
        await using var host = await GraphQLHost.StartAsync(options);
        await using var client = await SocketIoTestClient.ConnectAsync(host);

        var ping = await client.ReceiveFrameAsync(TimeSpan.FromSeconds(5), autoPong: false);
        Assert.That(ping, Is.EqualTo("2"));
        await client.SendRawAsync("3");

        var nextPing = await client.ReceiveFrameAsync(TimeSpan.FromSeconds(5), autoPong: false);
        Assert.That(nextPing, Is.EqualTo("2"), "answering the ping keeps the session alive");
    }

    [Test]
    public async Task A_client_that_stops_ponging_is_dropped()
    {
        var options = new SocketIoOptions
        {
            PingInterval = TimeSpan.FromMilliseconds(200),
            PingTimeout = TimeSpan.FromMilliseconds(300)
        };
        await using var host = await GraphQLHost.StartAsync(options);
        await using var client = await SocketIoTestClient.ConnectAsync(host);

        Assert.That(await client.WaitForCloseAsync(TimeSpan.FromSeconds(5)), Is.True,
            "a silent client must be dropped after pingInterval + pingTimeout");
    }
}
