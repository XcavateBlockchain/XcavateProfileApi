using Microsoft.EntityFrameworkCore;
using XcavateBuckets.Domain.Data;
using XcavateBuckets.Domain.Entities;

namespace XcavateBuckets.Domain.Services;

/// <summary>
/// Bucket lifecycle and write state. Ports <c>do_create_bucket</c>, <c>do_lock_bucket</c>,
/// <c>do_set_key</c> and <c>do_delete_bucket</c>.
/// </summary>
public class BucketService(
    BucketDbContext db,
    AuthorizationService auth,
    InputValidator validator,
    TimeProvider clock)
{
    /// <summary>
    /// Creates a bucket in a namespace. The caller must be a manager of the namespace, not a bucket
    /// admin. The new bucket is locked, because the pallet's <c>Status::default()</c> is
    /// <c>Locked</c> — it accepts no messages until <see cref="ResumeWritingAsync"/> supplies a key.
    /// </summary>
    public async Task<Bucket> CreateAsync(
        string caller,
        long namespaceId,
        string name,
        string category,
        IEnumerable<KeyValuePair<string, string>>? properties,
        CancellationToken ct)
    {
        validator.Required(name, validator.Options.MaxNameLen, "name");
        validator.Required(category, validator.Options.MaxCategoryLen, "category");
        var propertiesJson = validator.PropertiesJson(properties);

        await auth.EnsureNamespaceExistsAsync(namespaceId, ct);
        await auth.EnsureIsManagerAsync(namespaceId, caller, ct);

        var now = clock.GetUtcNow().UtcDateTime;

        var entity = new Bucket
        {
            NamespaceId = namespaceId,
            Name = name,
            Category = category,
            Properties = propertiesJson,
            Creator = caller,
            IsWritable = false,
            EncryptionKey = null,
            NextMessageId = 0,
            CreatedAt = now,
            UpdatedAt = now
        };

        db.Buckets.Add(entity);
        await db.SaveChangesAsync(ct);

        return entity;
    }

    /// <summary>Locks a bucket for writing. Ports <c>pause_writing</c>.</summary>
    public async Task<Bucket> PauseWritingAsync(
        string caller,
        long namespaceId,
        long bucketId,
        CancellationToken ct)
    {
        await auth.EnsureIsAdminAsync(bucketId, caller, ct);
        var bucket = await auth.GetBucketAsync(namespaceId, bucketId, ct);

        bucket.IsWritable = false;
        bucket.UpdatedAt = clock.GetUtcNow().UtcDateTime;
        await db.SaveChangesAsync(ct);

        return bucket;
    }

    /// <summary>
    /// Unlocks a bucket with a new key. Ports <c>resume_writing</c>, which is
    /// <c>do_set_key(allow_locked: true)</c> — so unlike <see cref="RotateKeyAsync"/> it works on a
    /// locked bucket.
    /// </summary>
    public Task<Bucket> ResumeWritingAsync(
        string caller,
        long namespaceId,
        long bucketId,
        string newEncryptionKey,
        CancellationToken ct) =>
        SetKeyAsync(caller, namespaceId, bucketId, newEncryptionKey, allowLocked: true, ct);

    /// <summary>
    /// Replaces the key of an already-writable bucket. Ports <c>rotate_key</c>, which is
    /// <c>do_set_key(allow_locked: false)</c> — a locked bucket raises
    /// <see cref="BucketErrorCode.BucketIsLocked"/>.
    /// </summary>
    public Task<Bucket> RotateKeyAsync(
        string caller,
        long namespaceId,
        long bucketId,
        string newEncryptionKey,
        CancellationToken ct) =>
        SetKeyAsync(caller, namespaceId, bucketId, newEncryptionKey, allowLocked: false, ct);

    private async Task<Bucket> SetKeyAsync(
        string caller,
        long namespaceId,
        long bucketId,
        string newEncryptionKey,
        bool allowLocked,
        CancellationToken ct)
    {
        validator.Hex32Value(newEncryptionKey, "newEncryptionKey");

        await auth.EnsureIsAdminAsync(bucketId, caller, ct);
        var bucket = await auth.GetBucketAsync(namespaceId, bucketId, ct);

        if (!allowLocked && !bucket.IsWritable)
        {
            throw BucketException.BucketIsLocked();
        }

        bucket.IsWritable = true;
        bucket.EncryptionKey = newEncryptionKey;
        bucket.UpdatedAt = clock.GetUtcNow().UtcDateTime;
        await db.SaveChangesAsync(ct);

        return bucket;
    }

    /// <summary>
    /// Deletes a bucket. Ports <c>do_delete_bucket</c>, which refuses while any child rows remain,
    /// in this order: messages, admins, contributors, viewers, tags.
    /// </summary>
    public async Task ForceRemoveAsync(long namespaceId, long bucketId, CancellationToken ct)
    {
        if (await db.Messages.AnyAsync(m => m.BucketId == bucketId, ct))
        {
            throw BucketException.DanglingMessages();
        }

        if (await db.BucketAdmins.AnyAsync(a => a.BucketId == bucketId, ct))
        {
            throw BucketException.DanglingAdmins();
        }

        if (await db.BucketContributors.AnyAsync(c => c.BucketId == bucketId, ct))
        {
            throw BucketException.DanglingContributors();
        }

        if (await db.BucketViewers.AnyAsync(v => v.BucketId == bucketId, ct))
        {
            throw BucketException.DanglingViewers();
        }

        if (await db.Tags.AnyAsync(t => t.BucketId == bucketId, ct))
        {
            throw BucketException.DanglingTags();
        }

        var bucket = await db.Buckets
            .FirstOrDefaultAsync(b => b.NamespaceId == namespaceId && b.BucketId == bucketId, ct)
            ?? throw BucketException.UnknownBucket();

        // The tag counter rows are keyed by bucket and hold no messages at this point, so they go
        // with the bucket rather than blocking its deletion.
        var counters = await db.TagMessageCounts
            .Where(c => c.BucketId == bucketId)
            .ToListAsync(ct);
        db.TagMessageCounts.RemoveRange(counters);

        db.Buckets.Remove(bucket);
        await db.SaveChangesAsync(ct);
    }
}
