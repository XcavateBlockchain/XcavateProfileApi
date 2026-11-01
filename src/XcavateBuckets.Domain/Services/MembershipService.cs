using Microsoft.EntityFrameworkCore;
using XcavateBuckets.Domain.Data;
using XcavateBuckets.Domain.Entities;

namespace XcavateBuckets.Domain.Services;

/// <summary>
/// Bucket membership. Ports <c>do_add_admin</c>, <c>do_remove_admin</c>,
/// <c>do_add_contributor</c>, <c>do_remove_contributor</c>, <c>do_add_viewer</c> and
/// <c>do_remove_viewer</c>.
/// </summary>
/// <remarks>
/// The pallet is deliberately asymmetric here: admins are appointed by a <em>namespace manager</em>,
/// while contributors and viewers are appointed by a <em>bucket admin</em>. Collapsing those two
/// roles would let any admin promote themselves indefinitely. For a standalone bucket (no
/// namespace) the bucket's creator takes the manager role, keeping that asymmetry intact.
/// </remarks>
public class MembershipService(
    BucketDbContext db,
    AuthorizationService auth,
    InputValidator validator,
    TimeProvider clock,
    IBucketNotifier notifier)
{
    public async Task<BucketAdmin> AddAdminAsync(
        string caller, long? namespaceId, long bucketId, string admin, CancellationToken ct)
    {
        validator.Required(admin, validator.Options.MaxNameLen, "admin");
        var bucket = await auth.GetBucketAsync(namespaceId, bucketId, ct);
        await auth.EnsureCanManageBucketAsync(bucket, caller, ct);

        var existing = await db.BucketAdmins
            .FirstOrDefaultAsync(a => a.BucketId == bucketId && a.SubjectId == admin, ct);
        if (existing is not null)
        {
            return existing;
        }

        var entity = new BucketAdmin
        {
            BucketId = bucketId,
            SubjectId = admin,
            AddedAt = clock.GetUtcNow().UtcDateTime
        };
        db.BucketAdmins.Add(entity);
        await db.SaveChangesAsync(ct);

        await notifier.MemberAddedAsync(bucket, admin, BucketMemberRole.Admin, ct);

        return entity;
    }

    public async Task RemoveAdminAsync(
        string caller, long? namespaceId, long bucketId, string admin, CancellationToken ct)
    {
        var bucket = await auth.GetBucketAsync(namespaceId, bucketId, ct);
        await auth.EnsureCanManageBucketAsync(bucket, caller, ct);

        var entity = await db.BucketAdmins
            .FirstOrDefaultAsync(a => a.BucketId == bucketId && a.SubjectId == admin, ct);
        if (entity is null)
        {
            return;
        }

        db.BucketAdmins.Remove(entity);
        await db.SaveChangesAsync(ct);
    }

    public async Task<BucketContributor> AddContributorAsync(
        string caller, long? namespaceId, long bucketId, string contributor, CancellationToken ct)
    {
        validator.Required(contributor, validator.Options.MaxNameLen, "contributor");
        var bucket = await auth.GetBucketAsync(namespaceId, bucketId, ct);
        await auth.EnsureIsAdminAsync(bucketId, caller, ct);

        var existing = await db.BucketContributors
            .FirstOrDefaultAsync(c => c.BucketId == bucketId && c.SubjectId == contributor, ct);
        if (existing is not null)
        {
            return existing;
        }

        var entity = new BucketContributor
        {
            BucketId = bucketId,
            SubjectId = contributor,
            AddedAt = clock.GetUtcNow().UtcDateTime
        };
        db.BucketContributors.Add(entity);
        await db.SaveChangesAsync(ct);

        await notifier.MemberAddedAsync(bucket, contributor, BucketMemberRole.Contributor, ct);

        return entity;
    }

    public async Task RemoveContributorAsync(
        string caller, long? namespaceId, long bucketId, string contributor, CancellationToken ct)
    {
        await auth.GetBucketAsync(namespaceId, bucketId, ct);
        await auth.EnsureIsAdminAsync(bucketId, caller, ct);

        var entity = await db.BucketContributors
            .FirstOrDefaultAsync(c => c.BucketId == bucketId && c.SubjectId == contributor, ct);
        if (entity is null)
        {
            return;
        }

        db.BucketContributors.Remove(entity);
        await db.SaveChangesAsync(ct);
    }

    public async Task<BucketViewer> AddViewerAsync(
        string caller, long? namespaceId, long bucketId, string viewer, CancellationToken ct)
    {
        viewer = validator.X25519Value(viewer, "viewer");
        await auth.GetBucketAsync(namespaceId, bucketId, ct);
        await auth.EnsureIsAdminAsync(bucketId, caller, ct);

        var matching = (await db.BucketViewers
                .Where(v => v.BucketId == bucketId)
                .ToListAsync(ct))
            .Where(v => X25519KeyCodec.KeysEqual(v.ViewerId, viewer))
            .ToList();
        if (matching.Count > 0)
        {
            var addedAt = matching.Min(v => v.AddedAt);
            var canonical = matching.FirstOrDefault(v => v.ViewerId == viewer);
            if (canonical is null)
            {
                canonical = new BucketViewer
                {
                    BucketId = bucketId,
                    ViewerId = viewer,
                    AddedAt = addedAt
                };
                db.BucketViewers.Add(canonical);
            }
            canonical.AddedAt = addedAt;
            db.BucketViewers.RemoveRange(matching.Where(v => v != canonical));
            await db.SaveChangesAsync(ct);
            return canonical;
        }

        var entity = new BucketViewer
        {
            BucketId = bucketId,
            ViewerId = viewer,
            AddedAt = clock.GetUtcNow().UtcDateTime
        };
        db.BucketViewers.Add(entity);
        await db.SaveChangesAsync(ct);

        return entity;
    }

    public async Task RemoveViewerAsync(
        string caller, long? namespaceId, long bucketId, string viewer, CancellationToken ct)
    {
        await auth.GetBucketAsync(namespaceId, bucketId, ct);
        await auth.EnsureIsAdminAsync(bucketId, caller, ct);

        var matching = (await db.BucketViewers
                .Where(v => v.BucketId == bucketId)
                .ToListAsync(ct))
            .Where(v => X25519KeyCodec.KeysEqual(v.ViewerId, viewer))
            .ToList();
        if (matching.Count == 0)
        {
            return;
        }

        db.BucketViewers.RemoveRange(matching);
        await db.SaveChangesAsync(ct);
    }
}
