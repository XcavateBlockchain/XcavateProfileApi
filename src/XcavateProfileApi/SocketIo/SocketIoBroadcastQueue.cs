using System.Threading.Channels;

namespace XcavateProfileApi.SocketIo;

/// <summary>One frame to deliver to every subscriber of a bucket, encoded once at enqueue time.</summary>
public sealed record BucketBroadcast(long BucketId, string Frame);

/// <summary>
/// Hand-off between the request-scoped notifier and the background dispatcher, mirroring
/// <see cref="Services.Notifications.NotificationQueue"/>: bounded so slow fan-out can never grow
/// memory without limit, dropping and logging rather than blocking a mutation — realtime delivery
/// is best-effort and clients can always re-fetch history over GraphQL.
/// </summary>
public sealed class SocketIoBroadcastQueue(ILogger<SocketIoBroadcastQueue> logger)
{
    private readonly Channel<BucketBroadcast> _channel =
        Channel.CreateBounded<BucketBroadcast>(new BoundedChannelOptions(10_000)
        {
            SingleReader = true
        });

    public ChannelReader<BucketBroadcast> Reader => _channel.Reader;

    public void Enqueue(BucketBroadcast broadcast)
    {
        if (!_channel.Writer.TryWrite(broadcast))
        {
            logger.LogWarning(
                "Socket.IO broadcast queue full; dropping a message event for bucket {BucketId}",
                broadcast.BucketId);
        }
    }
}
