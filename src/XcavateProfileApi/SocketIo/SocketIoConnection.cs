using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;

namespace XcavateProfileApi.SocketIo;

/// <summary>
/// One Socket.IO session over an accepted websocket. Owns three loops: receive (parses client
/// frames and handles subscribe/unsubscribe), send (the only writer to the socket, draining a
/// bounded outgoing channel), and heartbeat (Engine.IO ping, dropping clients that stop answering).
/// All outgoing traffic — handshake, acks, broadcasts, pings — goes through the channel, so socket
/// writes never interleave.
/// </summary>
public sealed class SocketIoConnection : ISocketIoSubscriber, IDisposable
{
    private readonly WebSocket _webSocket;
    private readonly SocketIoOptions _options;
    private readonly BucketSubscriptionRegistry _registry;
    private readonly Func<long, CancellationToken, Task<bool>> _bucketExists;
    private readonly ILogger _logger;
    private readonly Channel<string> _outgoing;
    private readonly CancellationTokenSource _lifetime = new();

    private long _lastActivityTicks;
    private bool _namespaceConnected;

    public SocketIoConnection(
        WebSocket webSocket,
        SocketIoOptions options,
        BucketSubscriptionRegistry registry,
        Func<long, CancellationToken, Task<bool>> bucketExists,
        ILogger logger)
    {
        _webSocket = webSocket;
        _options = options;
        _registry = registry;
        _bucketExists = bucketExists;
        _logger = logger;
        _outgoing = Channel.CreateBounded<string>(new BoundedChannelOptions(options.OutgoingQueueCapacity)
        {
            SingleReader = true
        });
    }

    public Guid Id { get; } = Guid.NewGuid();

    /// <summary>Engine.IO session id, sent in the open packet.</summary>
    public string EngineSid { get; } = SocketIoProtocol.NewSid();

    /// <summary>Socket.IO socket id, sent in the namespace connect ack. Distinct by protocol.</summary>
    public string SocketSid { get; } = SocketIoProtocol.NewSid();

    public bool TryEnqueue(string frame)
    {
        if (_outgoing.Writer.TryWrite(frame))
        {
            return true;
        }

        // A full queue means the client is not draining; dropping it beats unbounded buffering.
        _logger.LogWarning(
            "Socket.IO connection {Sid}: outgoing queue full, dropping the connection", EngineSid);
        try
        {
            _lifetime.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // The dispatcher may race a connection that already finished; nothing left to drop.
        }

        return false;
    }

    /// <summary>Runs the session until the client leaves, a heartbeat lapses, or the host stops.</summary>
    public async Task RunAsync(CancellationToken hostStopping)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(
            hostStopping, _lifetime.Token);
        var ct = linked.Token;

        Touch();
        TryEnqueue(SocketIoProtocol.EncodeOpen(EngineSid, _options));

        var send = SendLoopAsync(ct);
        var heartbeat = HeartbeatLoopAsync(ct);

        try
        {
            await ReceiveLoopAsync(ct);
        }
        catch (OperationCanceledException)
        {
            // Heartbeat lapse, slow-consumer drop, or host shutdown.
        }
        catch (WebSocketException ex)
        {
            _logger.LogDebug(ex, "Socket.IO connection {Sid} ended abruptly", EngineSid);
        }
        finally
        {
            _registry.Disconnect(this);
            _lifetime.Cancel();
            _outgoing.Writer.TryComplete();
            try
            {
                await Task.WhenAll(send, heartbeat);
            }
            catch (Exception)
            {
                // Loop teardown after cancellation; the receive loop already surfaced real errors.
            }

            await TryCloseAsync();
        }
    }

    public void Dispose() => _lifetime.Dispose();

    private async Task SendLoopAsync(CancellationToken ct)
    {
        await foreach (var frame in _outgoing.Reader.ReadAllAsync(ct))
        {
            await _webSocket.SendAsync(
                Encoding.UTF8.GetBytes(frame), WebSocketMessageType.Text, endOfMessage: true, ct);
        }
    }

    private async Task HeartbeatLoopAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(_options.PingInterval);
        while (await timer.WaitForNextTickAsync(ct))
        {
            var idleMs = Environment.TickCount64 - Interlocked.Read(ref _lastActivityTicks);
            if (idleMs > (_options.PingInterval + _options.PingTimeout).TotalMilliseconds)
            {
                _logger.LogDebug(
                    "Socket.IO connection {Sid}: no pong within the timeout, dropping", EngineSid);
                _lifetime.Cancel();
                return;
            }

            TryEnqueue(SocketIoProtocol.Ping);
        }
    }

    private async Task ReceiveLoopAsync(CancellationToken ct)
    {
        var buffer = new byte[4096];
        using var frame = new MemoryStream();

        while (_webSocket.State == WebSocketState.Open)
        {
            frame.SetLength(0);
            WebSocketReceiveResult result;
            do
            {
                result = await _webSocket.ReceiveAsync(buffer, ct);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    return;
                }

                frame.Write(buffer, 0, result.Count);
                if (frame.Length > _options.MaxIncomingFrameBytes)
                {
                    _logger.LogWarning(
                        "Socket.IO connection {Sid}: frame over {Max} bytes, dropping the connection",
                        EngineSid, _options.MaxIncomingFrameBytes);
                    return;
                }
            } while (!result.EndOfMessage);

            Touch();
            if (result.MessageType != WebSocketMessageType.Text)
            {
                continue; // Binary Engine.IO frames have no meaning on this surface.
            }

            await HandleFrameAsync(
                Encoding.UTF8.GetString(frame.GetBuffer(), 0, (int)frame.Length), ct);
        }
    }

    private async Task HandleFrameAsync(string frame, CancellationToken ct)
    {
        if (frame.Length == 0)
        {
            return;
        }

        switch (frame[0])
        {
            case '3': // Pong — Touch() already recorded the activity.
                return;
            case '2': // v4 clients never ping, but answering costs nothing.
                TryEnqueue(SocketIoProtocol.Pong + frame[1..]);
                return;
            case '1': // Engine.IO close.
                _lifetime.Cancel();
                return;
            case SocketIoProtocol.MessageMarker:
                await HandlePacketAsync(frame[1..], ct);
                return;
            default: // Open/upgrade/noop never originate from a client; ignore.
                return;
        }
    }

    private async Task HandlePacketAsync(string body, CancellationToken ct)
    {
        if (!SocketIoProtocol.TryParsePacket(body, out var packet))
        {
            return;
        }

        switch (packet.Type)
        {
            case SocketIoPacketType.Connect:
                if (packet.Namespace != SocketIoProtocol.DefaultNamespace)
                {
                    TryEnqueue(SocketIoProtocol.EncodeConnectError(
                        packet.Namespace, "Invalid namespace"));
                    return;
                }

                _namespaceConnected = true;
                TryEnqueue(SocketIoProtocol.EncodeConnectAck(SocketSid));
                return;

            case SocketIoPacketType.Disconnect:
                _namespaceConnected = false;
                _registry.Disconnect(this);
                return;

            case SocketIoPacketType.Event:
                if (_namespaceConnected)
                {
                    await HandleEventAsync(packet, ct);
                }

                return;

            default: // Client acks and binary packets have no server-side meaning here.
                return;
        }
    }

    private async Task HandleEventAsync(SocketIoPacket packet, CancellationToken ct)
    {
        string eventName;
        JsonElement? argument = null;
        try
        {
            using var doc = JsonDocument.Parse(packet.Json ?? "[]");
            if (doc.RootElement.ValueKind != JsonValueKind.Array
                || doc.RootElement.GetArrayLength() == 0
                || doc.RootElement[0].ValueKind != JsonValueKind.String)
            {
                return;
            }

            eventName = doc.RootElement[0].GetString()!;
            if (doc.RootElement.GetArrayLength() > 1)
            {
                argument = doc.RootElement[1].Clone();
            }
        }
        catch (JsonException)
        {
            return;
        }

        switch (eventName)
        {
            case "subscribe":
                await SubscribeAsync(packet.AckId, argument, ct);
                return;
            case "unsubscribe":
                Unsubscribe(packet.AckId, argument);
                return;
            default:
                Fail(packet.AckId, eventName, bucketId: null, "UNKNOWN_EVENT",
                    $"Unknown event \"{eventName}\"; this endpoint accepts \"subscribe\" and \"unsubscribe\".");
                return;
        }
    }

    private async Task SubscribeAsync(long? ackId, JsonElement? argument, CancellationToken ct)
    {
        if (!TryReadBucketId(argument, out var bucketId))
        {
            Fail(ackId, "subscribe", bucketId: null, "INVALID_INPUT",
                "Pass the bucket id as a string, a number, or an object {\"bucketId\": ...}.");
            return;
        }

        if (!await _bucketExists(bucketId, ct))
        {
            Fail(ackId, "subscribe", bucketId, "UNKNOWN_BUCKET",
                $"Bucket {bucketId} does not exist.");
            return;
        }

        _registry.Subscribe(this, bucketId);
        Ok(ackId, bucketId);
    }

    private void Unsubscribe(long? ackId, JsonElement? argument)
    {
        if (!TryReadBucketId(argument, out var bucketId))
        {
            Fail(ackId, "unsubscribe", bucketId: null, "INVALID_INPUT",
                "Pass the bucket id as a string, a number, or an object {\"bucketId\": ...}.");
            return;
        }

        _registry.Unsubscribe(this, bucketId);
        Ok(ackId, bucketId);
    }

    private void Ok(long? ackId, long bucketId)
    {
        if (ackId is { } id)
        {
            TryEnqueue(SocketIoProtocol.EncodeAck(
                id, new { ok = true, bucketId = bucketId.ToString() }));
        }
    }

    /// <summary>
    /// Reports a failed request: through the ack when one was requested, otherwise as a
    /// <c>subscriptionError</c> event so callback-less clients are not left guessing.
    /// </summary>
    private void Fail(long? ackId, string action, long? bucketId, string code, string message)
    {
        var error = new { code, message };
        if (ackId is { } id)
        {
            TryEnqueue(SocketIoProtocol.EncodeAck(
                id, new { ok = false, bucketId = bucketId?.ToString(), error }));
        }
        else
        {
            TryEnqueue(SocketIoProtocol.EncodeEvent(
                "subscriptionError", new { action, bucketId = bucketId?.ToString(), error }));
        }
    }

    private static bool TryReadBucketId(JsonElement? argument, out long bucketId)
    {
        bucketId = 0;
        if (argument is not { } element)
        {
            return false;
        }

        switch (element.ValueKind)
        {
            case JsonValueKind.String:
                return long.TryParse(element.GetString(), out bucketId) && bucketId >= 0;
            case JsonValueKind.Number:
                return element.TryGetInt64(out bucketId) && bucketId >= 0;
            case JsonValueKind.Object:
                return element.TryGetProperty("bucketId", out var nested)
                    && nested.ValueKind is JsonValueKind.String or JsonValueKind.Number
                    && TryReadBucketId(nested, out bucketId);
            default:
                return false;
        }
    }

    private void Touch() =>
        Interlocked.Exchange(ref _lastActivityTicks, Environment.TickCount64);

    private async Task TryCloseAsync()
    {
        if (_webSocket.State is WebSocketState.Open or WebSocketState.CloseReceived)
        {
            using var closeTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            try
            {
                await _webSocket.CloseAsync(
                    WebSocketCloseStatus.NormalClosure, "closed", closeTimeout.Token);
            }
            catch (Exception)
            {
                // The peer may already be gone; the socket is torn down either way.
            }
        }
    }
}
