using Microsoft.EntityFrameworkCore;
using XcavateBuckets.Domain.Data;

namespace XcavateProfileApi.SocketIo;

/// <summary>
/// HTTP entry point for <c>/socket.io/</c>: validates the Engine.IO handshake query, accepts the
/// websocket, and runs a <see cref="SocketIoConnection"/> for its lifetime. Only the websocket
/// transport is served — HTTP long-polling requests get a 400 telling the client how to connect.
/// </summary>
public sealed class SocketIoConnectionHandler(
    BucketSubscriptionRegistry registry,
    SocketIoOptions options,
    IServiceScopeFactory scopes,
    IHostApplicationLifetime lifetime,
    ILogger<SocketIoConnection> connectionLogger)
{
    public async Task HandleAsync(HttpContext context)
    {
        if (context.Request.Query["EIO"] != "4")
        {
            await RejectAsync(context, code: 5,
                "Unsupported protocol version; this server speaks Engine.IO v4 (connect with EIO=4).");
            return;
        }

        if (context.Request.Query["transport"] != "websocket")
        {
            await RejectAsync(context, code: 3,
                "Bad request; this server supports only the websocket transport. "
                + "Configure the client with transports: [\"websocket\"].");
            return;
        }

        if (!context.WebSockets.IsWebSocketRequest)
        {
            await RejectAsync(context, code: 3, "Bad request; expected a websocket upgrade.");
            return;
        }

        using var webSocket = await context.WebSockets.AcceptWebSocketAsync();
        using var connection = new SocketIoConnection(
            webSocket, options, registry, BucketExistsAsync, connectionLogger);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(
            context.RequestAborted, lifetime.ApplicationStopping);

        await connection.RunAsync(linked.Token);
    }

    /// <summary>The error body shape socket.io servers use, with an explanatory message.</summary>
    private static Task RejectAsync(HttpContext context, int code, string message)
    {
        context.Response.StatusCode = StatusCodes.Status400BadRequest;
        return context.Response.WriteAsJsonAsync(new { code, message });
    }

    /// <summary>Connections outlive any request scope, so each check gets a scope of its own.</summary>
    private async Task<bool> BucketExistsAsync(long bucketId, CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<BucketDbContext>();
        return await db.Buckets.AnyAsync(b => b.BucketId == bucketId, ct);
    }
}
