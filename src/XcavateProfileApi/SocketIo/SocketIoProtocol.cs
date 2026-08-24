using System.Security.Cryptography;
using System.Text.Json;

namespace XcavateProfileApi.SocketIo;

/// <summary>Socket.IO packet types (protocol v5), the digit after the Engine.IO message marker.</summary>
public enum SocketIoPacketType
{
    Connect = 0,
    Disconnect = 1,
    Event = 2,
    Ack = 3,
    ConnectError = 4,
    BinaryEvent = 5,
    BinaryAck = 6
}

/// <summary>
/// One parsed Socket.IO packet: <c>&lt;type&gt;[&lt;namespace&gt;,][&lt;ack id&gt;][&lt;json&gt;]</c>.
/// <see cref="Json"/> is the raw payload text (an array for events, an object for connect), or
/// null when the packet carried none.
/// </summary>
public sealed record SocketIoPacket(
    SocketIoPacketType Type, string Namespace, long? AckId, string? Json);

/// <summary>
/// Encoder/parser for the wire format spoken by standard socket.io v4 clients: Engine.IO
/// protocol v4 framing (one packet per websocket text frame, first character is the packet type)
/// carrying Socket.IO protocol v5 packets. Pure string functions, no I/O.
/// </summary>
public static class SocketIoProtocol
{
    public const string DefaultNamespace = "/";

    /// <summary>Engine.IO ping, sent by the server every <see cref="SocketIoOptions.PingInterval"/>.</summary>
    public const string Ping = "2";

    /// <summary>Engine.IO pong, the client's reply to <see cref="Ping"/>.</summary>
    public const string Pong = "3";

    /// <summary>Marks an Engine.IO frame that carries a Socket.IO packet.</summary>
    public const char MessageMarker = '4';

    private static readonly JsonSerializerOptions Wire = new(JsonSerializerDefaults.Web);

    /// <summary>A fresh session id: 15 random bytes, URL-safe base64 — the socket.io shape.</summary>
    public static string NewSid() =>
        Convert.ToBase64String(RandomNumberGenerator.GetBytes(15))
            .Replace('+', '-')
            .Replace('/', '_');

    /// <summary>The Engine.IO open packet, the first frame the server sends after the upgrade.</summary>
    public static string EncodeOpen(string sid, SocketIoOptions options) =>
        "0" + JsonSerializer.Serialize(new
        {
            sid,
            upgrades = Array.Empty<string>(),
            pingInterval = (int)options.PingInterval.TotalMilliseconds,
            pingTimeout = (int)options.PingTimeout.TotalMilliseconds,
            maxPayload = options.MaxPayload
        }, Wire);

    /// <summary>Confirms a namespace connect: <c>40{"sid":"..."}</c>.</summary>
    public static string EncodeConnectAck(string sid) =>
        "40" + JsonSerializer.Serialize(new { sid }, Wire);

    /// <summary>Rejects a namespace connect; surfaces as <c>connect_error</c> on the client.</summary>
    public static string EncodeConnectError(string ns, string message)
    {
        var prefix = ns == DefaultNamespace ? "44" : $"44{ns},";
        return prefix + JsonSerializer.Serialize(new { message }, Wire);
    }

    /// <summary>An event on the default namespace: <c>42["name",payload]</c>.</summary>
    public static string EncodeEvent(string eventName, object payload) =>
        "42" + JsonSerializer.Serialize(new[] { eventName, payload }, Wire);

    /// <summary>The reply to a client event that requested an acknowledgement.</summary>
    public static string EncodeAck(long ackId, object payload) =>
        $"43{ackId}" + JsonSerializer.Serialize(new[] { payload }, Wire);

    /// <summary>
    /// Parses one Socket.IO packet from the text after the Engine.IO message marker. Returns false
    /// on anything malformed; the caller ignores such frames rather than failing the connection.
    /// </summary>
    public static bool TryParsePacket(string body, out SocketIoPacket packet)
    {
        packet = null!;
        if (body.Length == 0 || body[0] < '0' || body[0] > '6')
        {
            return false;
        }

        var type = (SocketIoPacketType)(body[0] - '0');
        var i = 1;

        // Binary packets prefix an attachment count: "<n>-". Not supported here, but skipping it
        // keeps the rest of such a packet parseable so it can be ignored deliberately.
        if (type is SocketIoPacketType.BinaryEvent or SocketIoPacketType.BinaryAck)
        {
            while (i < body.Length && char.IsAsciiDigit(body[i]))
            {
                i++;
            }

            if (i < body.Length && body[i] == '-')
            {
                i++;
            }
        }

        var ns = DefaultNamespace;
        if (i < body.Length && body[i] == '/')
        {
            var comma = body.IndexOf(',', i);
            if (comma < 0)
            {
                // A namespace with no payload may omit the trailing comma, e.g. "40/admin".
                ns = body[i..];
                i = body.Length;
            }
            else
            {
                ns = body[i..comma];
                i = comma + 1;
            }
        }

        long? ackId = null;
        var digitsStart = i;
        while (i < body.Length && char.IsAsciiDigit(body[i]))
        {
            i++;
        }

        if (i > digitsStart)
        {
            if (!long.TryParse(body.AsSpan(digitsStart..i), out var parsed))
            {
                return false;
            }

            ackId = parsed;
        }

        packet = new SocketIoPacket(type, ns, ackId, i < body.Length ? body[i..] : null);
        return true;
    }
}
