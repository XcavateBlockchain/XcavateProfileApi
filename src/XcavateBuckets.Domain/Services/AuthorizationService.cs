using Microsoft.EntityFrameworkCore;
using XcavateBuckets.Domain.Data;
using XcavateBuckets.Domain.Entities;

namespace XcavateBuckets.Domain.Services;

/// <summary>
/// Role and existence checks, porting the <c>is_*</c> and <c>ensure_is_*</c> helpers from the
/// pallet's <c>functions.rs</c>.
/// </summary>
public class AuthorizationService(BucketDbContext db)
{
    public Task<bool> IsManagerAsync(long namespaceId, string subject, CancellationToken ct) =>
        db.NamespaceManagers
            .AnyAsync(m => m.NamespaceId == namespaceId && m.Manager == subject, ct);

    public Task<bool> IsAdminAsync(long bucketId, string subject, CancellationToken ct) =>
        db.BucketAdmins
            .AnyAsync(a => a.BucketId == bucketId && a.SubjectId == subject, ct);

    public Task<bool> IsContributorAsync(long bucketId, string subject, CancellationToken ct) =>
        db.BucketContributors
            .AnyAsync(c => c.BucketId == bucketId && c.SubjectId == subject, ct);

    public Task<bool> IsViewerAsync(long bucketId, string viewerId, CancellationToken ct) =>
        db.BucketViewers
            .AnyAsync(v => v.BucketId == bucketId && v.ViewerId == viewerId, ct);

    public async Task EnsureIsManagerAsync(long namespaceId, string subject, CancellationToken ct)
    {
        if (!await IsManagerAsync(namespaceId, subject, ct))
        {
            throw BucketException.NotManager();
        }
    }

    public async Task EnsureIsAdminAsync(long bucketId, string subject, CancellationToken ct)
    {
        if (!await IsAdminAsync(bucketId, subject, ct))
        {
            throw BucketException.NotAdmin();
        }
    }

    public async Task EnsureIsContributorAsync(long bucketId, string subject, CancellationToken ct)
    {
        if (!await IsContributorAsync(bucketId, subject, ct))
        {
            throw BucketException.NotContributor();
        }
    }

    public async Task EnsureNamespaceExistsAsync(long namespaceId, CancellationToken ct)
    {
        if (!await db.Namespaces.AnyAsync(n => n.NamespaceId == namespaceId, ct))
        {
            throw BucketException.UnknownNamespace();
        }
    }

    /// <summary>
    /// Loads a bucket by its namespace and id. The pallet keys <c>Buckets</c> by
    /// <c>(namespace_id, bucket_id)</c>, so a bucket that exists under a different namespace must
    /// read as missing rather than as someone else's bucket.
    /// </summary>
    public async Task<Bucket> GetBucketAsync(long namespaceId, long bucketId, CancellationToken ct)
    {
        var bucket = await db.Buckets
            .FirstOrDefaultAsync(b => b.NamespaceId == namespaceId && b.BucketId == bucketId, ct);

        return bucket ?? throw BucketException.UnknownBucket();
    }
}
