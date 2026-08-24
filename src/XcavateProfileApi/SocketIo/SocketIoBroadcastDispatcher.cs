namespace XcavateProfileApi.SocketIo;

/// <summary>
/// Drains the broadcast queue and fans each frame out to the bucket's subscribers.
/// <see cref="ISocketIoSubscriber.TryEnqueue"/> never blocks and never throws, so one slow or
/// dead connection cannot stall delivery to the others.
/// </summary>
public sealed class SocketIoBroadcastDispatcher(
    SocketIoBroadcastQueue queue,
    BucketSubscriptionRegistry registry) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await foreach (var broadcast in queue.Reader.ReadAllAsync(stoppingToken))
        {
            foreach (var subscriber in registry.SubscribersOf(broadcast.BucketId))
            {
                subscriber.TryEnqueue(broadcast.Frame);
            }
        }
    }
}
