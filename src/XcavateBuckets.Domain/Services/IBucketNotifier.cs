using XcavateBuckets.Domain.Entities;

namespace XcavateBuckets.Domain.Services;

/// <summary>The bucket role a subject was granted, for notification purposes.</summary>
public enum BucketMemberRole
{
    Admin,
    Contributor
}

/// <summary>
/// Receives domain events the host may fan out as push notifications. Called by the write
/// services immediately after their <c>SaveChangesAsync</c>, which is inside the mutation's
/// transaction — before commit. Implementations must therefore only enqueue work and must never
/// throw: a notification is best-effort and may never fail or slow the mutation it reports on.
/// </summary>
public interface IBucketNotifier
{
    /// <summary>A message was persisted into a bucket.</summary>
    Task MessageWrittenAsync(Bucket bucket, Message message, CancellationToken ct);

    /// <summary>
    /// A subject was newly granted a role in a bucket. Not raised on idempotent re-adds. A role
    /// change is remove + add across the role tables, so promotions and demotions land here too.
    /// </summary>
    Task MemberAddedAsync(Bucket bucket, string subjectId, BucketMemberRole role, CancellationToken ct);
}

/// <summary>Default when the host configures no notification backend.</summary>
public sealed class NullBucketNotifier : IBucketNotifier
{
    public Task MessageWrittenAsync(Bucket bucket, Message message, CancellationToken ct) =>
        Task.CompletedTask;

    public Task MemberAddedAsync(
        Bucket bucket, string subjectId, BucketMemberRole role, CancellationToken ct) =>
        Task.CompletedTask;
}
