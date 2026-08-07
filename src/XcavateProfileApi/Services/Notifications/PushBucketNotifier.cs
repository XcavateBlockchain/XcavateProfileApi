using Microsoft.EntityFrameworkCore;
using XcavateBuckets.Domain.Data;
using XcavateBuckets.Domain.Entities;
using XcavateBuckets.Domain.Services;
using XcavateProfileApi.Data;
using XcavateProfileApiClient.Signing;

namespace XcavateProfileApi.Services.Notifications;

/// <summary>
/// Fans bucket events out into per-wallet pushes. Runs inside the mutation's transaction, so it
/// only reads and enqueues — delivery happens on <see cref="NotificationDispatcher"/>. Never
/// throws: losing a push must never fail the mutation that raised it.
/// </summary>
public class PushBucketNotifier(
    BucketDbContext bucketDb,
    ProfileDbContext profileDb,
    NotificationQueue queue,
    ILogger<PushBucketNotifier> logger) : IBucketNotifier
{
    /// <summary>
    /// Format validators only, as in <see cref="Controllers.MigrationsController"/>: a checksummed
    /// SS58 address is Polkadot, a 32-byte base58 key is Solana. The two formats never overlap.
    /// </summary>
    private static readonly Sr25519SignatureScheme Sr25519Format = new();
    private static readonly SolanaSignatureScheme SolanaFormat = new();

    // Payload limits enforced by the notifications API.
    private const int MaxTitleLength = 150;
    private const int MaxBodyLength = 500;

    public async Task MessageWrittenAsync(Bucket bucket, Message message, CancellationToken ct)
    {
        try
        {
            var admins = await bucketDb.BucketAdmins
                .Where(a => a.BucketId == bucket.BucketId)
                .Select(a => a.SubjectId)
                .ToListAsync(ct);
            var contributors = await bucketDb.BucketContributors
                .Where(c => c.BucketId == bucket.BucketId)
                .Select(c => c.SubjectId)
                .ToListAsync(ct);

            // Viewers are X25519 encryption keys, not wallet addresses — nothing to push to.
            var recipients = admins.Concat(contributors)
                .Where(address => address != message.Contributor)
                .Distinct();

            var sender = await SenderLabelAsync(message.Contributor, ct);
            var title = Truncate(BucketLabel(bucket), MaxTitleLength);
            var body = Truncate($"New message from {sender}", MaxBodyLength);

            foreach (var recipient in recipients)
            {
                EnqueueTo(recipient, title, body, PushNotification.MessageType, bucket.BucketId);
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex,
                "Failed to enqueue message notifications for bucket {BucketId}", bucket.BucketId);
        }
    }

    public Task MemberAddedAsync(
        Bucket bucket, string subjectId, BucketMemberRole role, CancellationToken ct)
    {
        try
        {
            var body = role == BucketMemberRole.Admin
                ? "You are now an admin of this bucket."
                : "You are now a contributor of this bucket.";
            EnqueueTo(subjectId, Truncate(BucketLabel(bucket), MaxTitleLength), body,
                PushNotification.MemberAddedType, bucket.BucketId);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex,
                "Failed to enqueue member-added notification for bucket {BucketId}",
                bucket.BucketId);
        }

        return Task.CompletedTask;
    }

    private void EnqueueTo(string address, string title, string body, string type, long bucketId)
    {
        var chain = DetectChain(address);
        if (chain is null)
        {
            return;
        }

        queue.Enqueue(new PushNotification(
            chain, address, title, body, type, bucketId.ToString()));
    }

    private static string? DetectChain(string address) =>
        SolanaFormat.CanVerify(address) ? PushNotification.SolanaChain
        : Sr25519Format.CanVerify(address) ? PushNotification.PolkadotChain
        : null;

    private static string BucketLabel(Bucket bucket) => bucket.Name ?? $"Bucket #{bucket.BucketId}";

    /// <summary>Sender label for the push body: profile nickname, or a shortened address.</summary>
    private async Task<string> SenderLabelAsync(string? address, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(address))
        {
            return "unknown";
        }

        var nickname = await profileDb.Profiles
            .Where(p => p.Ss58Address == address)
            .Select(p => p.Nickname)
            .FirstOrDefaultAsync(ct);
        if (!string.IsNullOrWhiteSpace(nickname))
        {
            return nickname;
        }

        return address.Length <= 12 ? address : $"{address[..6]}…{address[^4..]}";
    }

    private static string Truncate(string value, int max) =>
        value.Length <= max ? value : value[..max];
}
