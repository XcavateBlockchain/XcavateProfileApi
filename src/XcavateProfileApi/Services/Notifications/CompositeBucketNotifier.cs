using XcavateBuckets.Domain.Entities;
using XcavateBuckets.Domain.Services;

namespace XcavateProfileApi.Services.Notifications;

/// <summary>
/// Fans one domain event out to several notifier backends (realtime socket broadcast, push, ...).
/// Sequential and unguarded on purpose: every backend honors the <see cref="IBucketNotifier"/>
/// enqueue-only/never-throw contract itself.
/// </summary>
public sealed class CompositeBucketNotifier(IReadOnlyList<IBucketNotifier> notifiers) : IBucketNotifier
{
    public async Task MessageWrittenAsync(Bucket bucket, Message message, CancellationToken ct)
    {
        foreach (var notifier in notifiers)
        {
            await notifier.MessageWrittenAsync(bucket, message, ct);
        }
    }

    public async Task MemberAddedAsync(
        Bucket bucket, string subjectId, BucketMemberRole role, CancellationToken ct)
    {
        foreach (var notifier in notifiers)
        {
            await notifier.MemberAddedAsync(bucket, subjectId, role, ct);
        }
    }
}
