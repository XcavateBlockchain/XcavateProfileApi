using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using XcavateBuckets.Domain.Entities;
using XcavateBuckets.Domain.Services;
using XcavateProfileApi.SocketIo;

namespace XcavateBuckets.Tests;

/// <summary>
/// The domain-event side of the realtime feature: a written message becomes one pre-encoded
/// <c>42["message",...]</c> frame on the broadcast queue, and nothing else does.
/// </summary>
[TestFixture]
public class SocketIoNotifierTests
{
    private static readonly CancellationToken Ct = CancellationToken.None;

    private SocketIoBroadcastQueue _queue = null!;
    private SocketIoBucketNotifier _notifier = null!;

    [SetUp]
    public void SetUp()
    {
        _queue = new SocketIoBroadcastQueue(NullLogger<SocketIoBroadcastQueue>.Instance);
        _notifier = new SocketIoBucketNotifier(_queue, NullLogger<SocketIoBucketNotifier>.Instance);
    }

    [Test]
    public async Task Written_message_is_enqueued_as_a_message_event_in_graphql_wire_shape()
    {
        var bucket = new Bucket { BucketId = 7 };
        var message = new Message
        {
            BucketId = 7,
            MessageId = 3,
            Contributor = "5FakeAddress",
            Reference = "bafyreference",
            Tag = "deed-scan",
            Description = "a deed",
            ContentType = "text/plain",
            ContentHash = TestDb.Hash32,
            Properties = """{"k":"v"}""",
            IpfsContent = "the text",
            CreatedAt = new DateTime(2026, 8, 24, 10, 30, 0, DateTimeKind.Utc)
        };

        await _notifier.MessageWrittenAsync(bucket, message, Ct);

        Assert.That(_queue.Reader.TryRead(out var broadcast), Is.True);
        Assert.That(broadcast!.BucketId, Is.EqualTo(7));
        Assert.That(broadcast.Frame, Does.StartWith("42["));

        using var doc = JsonDocument.Parse(broadcast.Frame[2..]);
        var payload = doc.RootElement[1];
        Assert.Multiple(() =>
        {
            Assert.That(doc.RootElement[0].GetString(), Is.EqualTo("message"));
            Assert.That(payload.GetProperty("id").GetString(), Is.EqualTo("7-3"),
                "the composite id matches the GraphQL Message.id");
            Assert.That(payload.GetProperty("bucketId").GetString(), Is.EqualTo("7"));
            Assert.That(payload.GetProperty("messageId").GetString(), Is.EqualTo("3"),
                "ids are strings on the wire, like the BigInt scalar");
            Assert.That(payload.GetProperty("messageIdNumber").GetString(), Is.EqualTo("3"));
            Assert.That(payload.GetProperty("contributor").GetString(), Is.EqualTo("5FakeAddress"));
            Assert.That(payload.GetProperty("reference").GetString(), Is.EqualTo("bafyreference"));
            Assert.That(payload.GetProperty("tag").GetString(), Is.EqualTo("deed-scan"));
            Assert.That(payload.GetProperty("description").GetString(), Is.EqualTo("a deed"));
            Assert.That(payload.GetProperty("contentType").GetString(), Is.EqualTo("text/plain"));
            Assert.That(payload.GetProperty("contentHash").GetString(), Is.EqualTo(TestDb.Hash32));
            Assert.That(payload.GetProperty("properties").GetString(), Is.EqualTo("""{"k":"v"}"""));
            Assert.That(payload.GetProperty("ipfsContent").GetString(), Is.EqualTo("the text"));
            Assert.That(payload.GetProperty("createdAt").GetString(),
                Is.EqualTo("2026-08-24T10:30:00.0000000Z"));
        });
    }

    [Test]
    public async Task Optional_fields_are_present_as_null_rather_than_omitted()
    {
        var message = new Message { BucketId = 1, MessageId = 0, Contributor = "5FakeAddress" };

        await _notifier.MessageWrittenAsync(new Bucket { BucketId = 1 }, message, Ct);

        Assert.That(_queue.Reader.TryRead(out var broadcast), Is.True);
        using var doc = JsonDocument.Parse(broadcast!.Frame[2..]);
        var payload = doc.RootElement[1];
        Assert.Multiple(() =>
        {
            Assert.That(payload.GetProperty("tag").ValueKind, Is.EqualTo(JsonValueKind.Null));
            Assert.That(payload.GetProperty("properties").ValueKind, Is.EqualTo(JsonValueKind.Null));
            Assert.That(payload.GetProperty("ipfsContent").ValueKind, Is.EqualTo(JsonValueKind.Null));
        });
    }

    [Test]
    public async Task Membership_events_are_not_broadcast()
    {
        await _notifier.MemberAddedAsync(
            new Bucket { BucketId = 1 }, "5FakeAddress", BucketMemberRole.Admin, Ct);

        Assert.That(_queue.Reader.TryRead(out _), Is.False,
            "the socket surface is bucket messages only");
    }
}
