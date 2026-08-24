using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

namespace XcavateBuckets.Tests;

/// <summary>
/// A minimal socket.io v4 client over the test server's websocket — its own handful of protocol,
/// written from the socket.io spec rather than the server code, so the two sides verify each
/// other: Engine.IO handshake, namespace connect, emit-with-ack, and event observation.
/// </summary>
internal sealed class SocketIoTestClient : IAsyncDisposable
{
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(5);

    private readonly WebSocket _socket;
    private long _nextAckId;

    private SocketIoTestClient(WebSocket socket, JsonDocument openPacket)
    {
        _socket = socket;
        OpenPacket = openPacket;
    }

    /// <summary>The parsed Engine.IO open packet (sid, upgrades, pingInterval, ...).</summary>
    public JsonDocument OpenPacket { get; }

    /// <summary>The Socket.IO sid from the namespace connect ack; null when not connected.</summary>
    public string? SocketSid { get; private set; }

    public static async Task<SocketIoTestClient> ConnectAsync(
        GraphQLHost host, bool connectNamespace = true)
    {
        var socket = await host.CreateWebSocketClient().ConnectAsync(
            new Uri("ws://localhost/socket.io/?EIO=4&transport=websocket"), CancellationToken.None);

        var open = await ReceiveRawAsync(socket, DefaultTimeout);
        if (open is null || !open.StartsWith('0'))
        {
            throw new InvalidOperationException($"Expected an Engine.IO open packet, got: {open}");
        }

        var client = new SocketIoTestClient(socket, JsonDocument.Parse(open[1..]));
        if (connectNamespace)
        {
            await client.SendRawAsync("40");
            var ack = await client.ReceiveFrameAsync();
            if (!ack.StartsWith("40"))
            {
                throw new InvalidOperationException($"Expected a connect ack, got: {ack}");
            }

            client.SocketSid = JsonDocument.Parse(ack[2..]).RootElement
                .GetProperty("sid").GetString();
        }

        return client;
    }

    public Task SendRawAsync(string frame) =>
        _socket.SendAsync(
            Encoding.UTF8.GetBytes(frame), WebSocketMessageType.Text,
            endOfMessage: true, CancellationToken.None);

    /// <summary>
    /// The next frame from the server. With <paramref name="autoPong"/> (the default) Engine.IO
    /// pings are answered and skipped, so callers see only meaningful frames; heartbeat tests
    /// pass false to observe the pings themselves.
    /// </summary>
    public async Task<string> ReceiveFrameAsync(TimeSpan? timeout = null, bool autoPong = true)
    {
        var deadline = DateTime.UtcNow + (timeout ?? DefaultTimeout);
        while (true)
        {
            var remaining = deadline - DateTime.UtcNow;
            if (remaining <= TimeSpan.Zero)
            {
                throw new TimeoutException("No frame arrived within the timeout.");
            }

            var frame = await ReceiveRawAsync(_socket, remaining)
                ?? throw new InvalidOperationException("The server closed the connection.");

            if (autoPong && frame == "2")
            {
                await SendRawAsync("3");
                continue;
            }

            return frame;
        }
    }

    /// <summary>Emits an event with an ack id and returns the ack's payload element.</summary>
    public async Task<JsonElement> EmitWithAckAsync(
        string eventName, object? argument, TimeSpan? timeout = null)
    {
        var ackId = _nextAckId++;
        var args = argument is null ? new[] { (object)eventName } : [eventName, argument];
        await SendRawAsync($"42{ackId}{JsonSerializer.Serialize(args)}");

        var expected = $"43{ackId}";
        var deadline = DateTime.UtcNow + (timeout ?? DefaultTimeout);
        while (true)
        {
            var frame = await ReceiveFrameAsync(deadline - DateTime.UtcNow);
            if (frame.StartsWith(expected) && frame.Length > expected.Length
                && frame[expected.Length] == '[')
            {
                using var doc = JsonDocument.Parse(frame[expected.Length..]);
                return doc.RootElement[0].Clone();
            }
        }
    }

    /// <summary>Waits for the next Socket.IO event and returns its name and first argument.</summary>
    public async Task<(string Event, JsonElement Data)> WaitForEventAsync(TimeSpan? timeout = null)
    {
        var deadline = DateTime.UtcNow + (timeout ?? DefaultTimeout);
        while (true)
        {
            var frame = await ReceiveFrameAsync(deadline - DateTime.UtcNow);
            if (frame.StartsWith("42") && frame.Length > 2 && frame[2] == '[')
            {
                using var doc = JsonDocument.Parse(frame[2..]);
                return (doc.RootElement[0].GetString()!, doc.RootElement[1].Clone());
            }
        }
    }

    /// <summary>True when the server closes within the timeout; false when it stays open.</summary>
    public async Task<bool> WaitForCloseAsync(TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        try
        {
            while (DateTime.UtcNow < deadline)
            {
                if (await ReceiveRawAsync(_socket, deadline - DateTime.UtcNow) is null)
                {
                    return true;
                }
            }

            return false;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
        catch (WebSocketException)
        {
            return true;
        }
    }

    /// <summary>One complete text frame, or null when the server sent a close frame.</summary>
    private static async Task<string?> ReceiveRawAsync(WebSocket socket, TimeSpan timeout)
    {
        using var cts = new CancellationTokenSource(timeout);
        var buffer = new byte[65536];
        using var frame = new MemoryStream();

        WebSocketReceiveResult result;
        do
        {
            result = await socket.ReceiveAsync(buffer, cts.Token);
            if (result.MessageType == WebSocketMessageType.Close)
            {
                return null;
            }

            frame.Write(buffer, 0, result.Count);
        } while (!result.EndOfMessage);

        return Encoding.UTF8.GetString(frame.GetBuffer(), 0, (int)frame.Length);
    }

    public async ValueTask DisposeAsync()
    {
        OpenPacket.Dispose();
        if (_socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            try
            {
                await _socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", cts.Token);
            }
            catch (Exception)
            {
                // The server may already have torn the socket down.
            }
        }

        _socket.Dispose();
    }
}
