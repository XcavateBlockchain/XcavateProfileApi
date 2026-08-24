namespace XcavateProfileApi.SocketIo;

/// <summary>
/// Tunables for the Socket.IO endpoint. The defaults mirror the reference socket.io server
/// (pingInterval 25 s, pingTimeout 20 s, maxPayload 1 MB), so standard clients work unconfigured.
/// </summary>
public sealed record SocketIoOptions
{
    /// <summary>Request path the endpoint is mounted on — the socket.io default.</summary>
    public const string Path = "/socket.io";

    /// <summary>How often the server sends an Engine.IO ping.</summary>
    public TimeSpan PingInterval { get; init; } = TimeSpan.FromSeconds(25);

    /// <summary>Grace period after a ping before an unresponsive client is dropped.</summary>
    public TimeSpan PingTimeout { get; init; } = TimeSpan.FromSeconds(20);

    /// <summary>Advertised in the handshake; clients cap their outgoing frames to it.</summary>
    public int MaxPayload { get; init; } = 1_000_000;

    /// <summary>
    /// Server-side cap on one incoming frame. The only client frames this protocol surface needs
    /// are connect/subscribe/pong, all tiny; anything larger drops the connection.
    /// </summary>
    public int MaxIncomingFrameBytes { get; init; } = 65_536;

    /// <summary>
    /// Outgoing frames buffered per connection. A client that cannot keep up is dropped rather
    /// than buffered without bound — it can reconnect and re-fetch history over GraphQL.
    /// </summary>
    public int OutgoingQueueCapacity { get; init; } = 1_024;
}
