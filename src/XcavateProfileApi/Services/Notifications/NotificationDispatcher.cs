namespace XcavateProfileApi.Services.Notifications;

/// <summary>
/// Drains the queue and delivers pushes sequentially. <see cref="NotificationsApiClient.SendAsync"/>
/// only throws on shutdown cancellation, so one delivery failing never stops the loop.
/// </summary>
public sealed class NotificationDispatcher(
    NotificationQueue queue,
    NotificationsApiClient client) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await foreach (var notification in queue.Reader.ReadAllAsync(stoppingToken))
        {
            await client.SendAsync(notification, stoppingToken);
        }
    }
}
