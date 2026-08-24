using XcavateBuckets.Domain.Entities;
using XcavateBuckets.Domain.Services;

namespace XcavateProfileApi.SocketIo;

/// <summary>
/// Turns a written message into a Socket.IO broadcast. Honors the <see cref="IBucketNotifier"/>
/// contract: called inside the mutation's transaction, so it only encodes and enqueues — delivery
/// happens on <see cref="SocketIoBroadcastDispatcher"/> — and never throws. Membership events are
/// deliberately not broadcast: the socket surface is bucket messages only.
/// </summary>
public sealed class SocketIoBucketNotifier(
    SocketIoBroadcastQueue queue,
    ILogger<SocketIoBucketNotifier> logger) : IBucketNotifier
{
    /// <summary>The server-to-client event name new messages arrive under.</summary>
    public const string MessageEventName = "message";

    public Task MessageWrittenAsync(Bucket bucket, Message message, CancellationToken ct)
    {
        try
        {
            var frame = SocketIoProtocol.EncodeEvent(
                MessageEventName, BucketMessageEvent.From(message));
            queue.Enqueue(new BucketBroadcast(message.BucketId, frame));
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex,
                "Failed to enqueue a Socket.IO broadcast for bucket {BucketId}", bucket.BucketId);
        }

        return Task.CompletedTask;
    }

    public Task MemberAddedAsync(
        Bucket bucket, string subjectId, BucketMemberRole role, CancellationToken ct) =>
        Task.CompletedTask;
}
