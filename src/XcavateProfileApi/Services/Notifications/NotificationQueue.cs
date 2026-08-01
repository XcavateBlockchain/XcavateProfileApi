using System.Threading.Channels;

namespace XcavateProfileApi.Services.Notifications;

/// <summary>
/// Hand-off between the request-scoped notifier and the background dispatcher. Bounded so a slow
/// or unreachable notifications API can never grow memory without limit; when full, new pushes
/// are dropped and logged rather than blocking a mutation — notifications are best-effort.
/// </summary>
public sealed class NotificationQueue(ILogger<NotificationQueue> logger)
{
    private readonly Channel<PushNotification> _channel =
        Channel.CreateBounded<PushNotification>(new BoundedChannelOptions(10_000)
        {
            SingleReader = true
        });

    public ChannelReader<PushNotification> Reader => _channel.Reader;

    public void Enqueue(PushNotification notification)
    {
        if (!_channel.Writer.TryWrite(notification))
        {
            logger.LogWarning(
                "Notification queue full; dropping push to {Chain}:{Address}",
                notification.Chain, notification.Address);
        }
    }
}
