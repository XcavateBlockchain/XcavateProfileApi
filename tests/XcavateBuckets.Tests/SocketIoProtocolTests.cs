using System.Text.Json;
using XcavateProfileApi.SocketIo;

namespace XcavateBuckets.Tests;

/// <summary>
/// The wire format itself, against the socket.io v4 spec: Engine.IO v4 framing with Socket.IO v5
/// packets. Pure string round-trips, no sockets.
/// </summary>
[TestFixture]
public class SocketIoProtocolTests
{
    [Test]
    public void Open_packet_carries_the_handshake_fields()
    {
        var frame = SocketIoProtocol.EncodeOpen("abc123", new SocketIoOptions());

        Assert.That(frame, Does.StartWith("0{"));
        using var json = JsonDocument.Parse(frame[1..]);
        Assert.Multiple(() =>
        {
            Assert.That(json.RootElement.GetProperty("sid").GetString(), Is.EqualTo("abc123"));
            Assert.That(json.RootElement.GetProperty("upgrades").GetArrayLength(), Is.Zero,
                "a direct websocket session has nothing to upgrade to");
            Assert.That(json.RootElement.GetProperty("pingInterval").GetInt32(), Is.EqualTo(25_000));
            Assert.That(json.RootElement.GetProperty("pingTimeout").GetInt32(), Is.EqualTo(20_000));
            Assert.That(json.RootElement.GetProperty("maxPayload").GetInt32(), Is.EqualTo(1_000_000));
        });
    }

    [Test]
    public void Connect_ack_and_connect_error_have_the_protocol_shapes()
    {
        Assert.Multiple(() =>
        {
            Assert.That(SocketIoProtocol.EncodeConnectAck("s1"), Is.EqualTo("""40{"sid":"s1"}"""));
            Assert.That(SocketIoProtocol.EncodeConnectError("/", "nope"),
                Is.EqualTo("""44{"message":"nope"}"""));
            Assert.That(SocketIoProtocol.EncodeConnectError("/admin", "Invalid namespace"),
                Is.EqualTo("""44/admin,{"message":"Invalid namespace"}"""));
        });
    }

    [Test]
    public void Events_and_acks_serialize_camel_case()
    {
        Assert.Multiple(() =>
        {
            Assert.That(SocketIoProtocol.EncodeEvent("message", new { BucketId = "5" }),
                Is.EqualTo("""42["message",{"bucketId":"5"}]"""));
            Assert.That(SocketIoProtocol.EncodeAck(7, new { Ok = true }),
                Is.EqualTo("""437[{"ok":true}]"""));
        });
    }

    [TestCase("0", SocketIoPacketType.Connect, "/", null, null)]
    [TestCase("0/admin,", SocketIoPacketType.Connect, "/admin", null, null)]
    [TestCase("0/admin", SocketIoPacketType.Connect, "/admin", null, null,
        Description = "a namespace with no payload may omit the trailing comma")]
    [TestCase("""0{"token":"t"}""", SocketIoPacketType.Connect, "/", null, """{"token":"t"}""")]
    [TestCase("1", SocketIoPacketType.Disconnect, "/", null, null)]
    [TestCase("""2["subscribe","5"]""", SocketIoPacketType.Event, "/", null, """["subscribe","5"]""")]
    [TestCase("""213["subscribe","5"]""", SocketIoPacketType.Event, "/", 13L, """["subscribe","5"]""")]
    [TestCase("""2/chat,4["x"]""", SocketIoPacketType.Event, "/chat", 4L, """["x"]""")]
    [TestCase("""51-["x"]""", SocketIoPacketType.BinaryEvent, "/", null, """["x"]""",
        Description = "the binary attachment count is skipped so the packet can be ignored whole")]
    public void Packets_parse(
        string body, SocketIoPacketType type, string ns, long? ackId, string? json)
    {
        Assert.That(SocketIoProtocol.TryParsePacket(body, out var packet), Is.True);
        Assert.Multiple(() =>
        {
            Assert.That(packet.Type, Is.EqualTo(type));
            Assert.That(packet.Namespace, Is.EqualTo(ns));
            Assert.That(packet.AckId, Is.EqualTo(ackId));
            Assert.That(packet.Json, Is.EqualTo(json));
        });
    }

    [TestCase("", Description = "empty")]
    [TestCase("9", Description = "no such packet type")]
    [TestCase("x[]", Description = "type is not a digit")]
    [TestCase("2999999999999999999999[\"x\"]", Description = "ack id overflows a long")]
    public void Malformed_packets_are_rejected(string body) =>
        Assert.That(SocketIoProtocol.TryParsePacket(body, out _), Is.False);

    [Test]
    public void Sids_are_url_safe_and_distinct()
    {
        var first = SocketIoProtocol.NewSid();
        var second = SocketIoProtocol.NewSid();

        Assert.Multiple(() =>
        {
            Assert.That(first, Has.Length.EqualTo(20));
            Assert.That(first, Does.Not.Contain("+").And.Not.Contain("/"));
            Assert.That(first, Is.Not.EqualTo(second));
        });
    }
}
