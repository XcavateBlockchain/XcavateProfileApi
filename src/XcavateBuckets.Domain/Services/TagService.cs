using Microsoft.EntityFrameworkCore;
using XcavateBuckets.Domain.Data;
using XcavateBuckets.Domain.Entities;

namespace XcavateBuckets.Domain.Services;

/// <summary>
/// Tag lifecycle. Ports <c>do_create_tag</c> and <c>do_delete_tag</c>.
/// </summary>
public class TagService(
    BucketDbContext db,
    AuthorizationService auth,
    InputValidator validator,
    TimeProvider clock)
{
    /// <summary>
    /// Creates a tag in a bucket. Takes only a bucket id, matching <c>create_tag</c>'s signature —
    /// the pallet checks bucket-admin but never bucket existence, because <c>Admins</c> is keyed by
    /// bucket id alone. Off-chain the foreign key enforces existence instead.
    /// </summary>
    public async Task<Tag> CreateAsync(
        string caller, long bucketId, string newTag, CancellationToken ct)
    {
        validator.Required(newTag, validator.Options.MaxTagLen, "newTag");
        await auth.EnsureIsAdminAsync(bucketId, caller, ct);

        var now = clock.GetUtcNow().UtcDateTime;

        var existing = await db.Tags
            .FirstOrDefaultAsync(t => t.BucketId == bucketId && t.TagName == newTag, ct);
        if (existing is not null)
        {
            return existing;
        }

        var entity = new Tag
        {
            BucketId = bucketId,
            TagName = newTag,
            Creator = caller,
            CreatedAt = now
        };
        db.Tags.Add(entity);

        // Create the counter row up front so write and forceRemoveTag always have one to read,
        // standing in for the pallet's ValueQuery default of 0.
        var counterExists = await db.TagMessageCounts
            .AnyAsync(c => c.BucketId == bucketId && c.TagName == newTag, ct);
        if (!counterExists)
        {
            db.TagMessageCounts.Add(new TagMessageCount
            {
                BucketId = bucketId,
                TagName = newTag,
                Count = 0,
                UpdatedAt = now
            });
        }

        await db.SaveChangesAsync(ct);

        return entity;
    }

    /// <summary>
    /// Deletes a tag. Ports <c>do_delete_tag</c>, which refuses while any message still references
    /// the tag.
    /// </summary>
    public async Task ForceRemoveAsync(long bucketId, string tag, CancellationToken ct)
    {
        var counter = await db.TagMessageCounts
            .FirstOrDefaultAsync(c => c.BucketId == bucketId && c.TagName == tag, ct);

        if (counter is not null && counter.Count != 0)
        {
            throw BucketException.DanglingMessages();
        }

        var entity = await db.Tags
            .FirstOrDefaultAsync(t => t.BucketId == bucketId && t.TagName == tag, ct)
            ?? throw BucketException.UnknownTag();

        db.Tags.Remove(entity);
        if (counter is not null)
        {
            db.TagMessageCounts.Remove(counter);
        }

        await db.SaveChangesAsync(ct);
    }
}
