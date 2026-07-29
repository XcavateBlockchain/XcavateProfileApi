using Microsoft.EntityFrameworkCore;
using XcavateBuckets.Domain.Data;
using XcavateBuckets.Domain.Entities;

namespace XcavateBuckets.Domain.Services;

/// <summary>What a caller supplies when writing a message. Mirrors the pallet's MessageInput.</summary>
/// <param name="IpfsContent">
/// Resolved text content, stored verbatim. No pallet equivalent: the chain only carried a reference,
/// so the indexer fetched this from IPFS. Here the caller supplies it and the API never fetches.
/// </param>
public sealed record MessageWriteRequest(
    string Reference,
    string? Tag,
    string? IpfsContent,
    string? Description,
    string ContentType,
    string ContentHash,
    IEnumerable<KeyValuePair<string, string>>? Properties);

/// <summary>
/// Message writing and removal. Ports <c>do_create_message</c> and <c>do_remove_message</c>.
/// </summary>
public class MessageService(
    BucketDbContext db,
    AuthorizationService auth,
    InputValidator validator,
    TimeProvider clock)
{
    /// <summary>
    /// Writes a message into a bucket. The pallet's check order matters and is reproduced exactly:
    /// bucket exists, bucket is writable, caller is a contributor, tag exists. A non-contributor
    /// writing to a locked bucket therefore sees <see cref="BucketErrorCode.BucketIsLocked"/>, not
    /// <see cref="BucketErrorCode.NotContributor"/>.
    /// </summary>
    public async Task<Message> WriteAsync(
        string caller,
        long? namespaceId,
        long bucketId,
        MessageWriteRequest request,
        CancellationToken ct)
    {
        validator.Required(request.Reference, validator.Options.MaxReferenceLen, "reference");
        validator.Text(request.Description, validator.Options.MaxNameLen, "description");
        validator.Required(request.ContentType, validator.Options.MaxCategoryLen, "contentType");
        validator.Hex32Value(request.ContentHash, "contentHash");
        validator.Text(request.Tag, validator.Options.MaxTagLen, "tag");
        validator.Text(request.IpfsContent, validator.Options.MaxIpfsContentLen, "ipfsContent");
        var propertiesJson = validator.PropertiesJson(request.Properties);

        var bucket = await auth.GetBucketAsync(namespaceId, bucketId, ct);

        if (!bucket.IsWritable)
        {
            throw BucketException.BucketIsLocked();
        }

        await auth.EnsureIsContributorAsync(bucketId, caller, ct);

        var now = clock.GetUtcNow().UtcDateTime;

        if (request.Tag is not null)
        {
            var tagExists = await db.Tags
                .AnyAsync(t => t.BucketId == bucketId && t.TagName == request.Tag, ct);
            if (!tagExists)
            {
                throw BucketException.UnknownTag();
            }

            var counter = await db.TagMessageCounts
                .FirstOrDefaultAsync(c => c.BucketId == bucketId && c.TagName == request.Tag, ct);

            if (counter is null)
            {
                counter = new TagMessageCount
                {
                    BucketId = bucketId,
                    TagName = request.Tag,
                    Count = 0,
                    UpdatedAt = now
                };
                db.TagMessageCounts.Add(counter);
            }

            if (counter.Count == int.MaxValue)
            {
                throw BucketException.ArithmeticOverflow();
            }

            counter.Count++;
            counter.UpdatedAt = now;
        }

        var message = new Message
        {
            BucketId = bucketId,
            MessageId = bucket.NextMessageId,
            Contributor = caller,
            Reference = request.Reference,
            Tag = request.Tag,
            Description = request.Description,
            ContentType = request.ContentType,
            ContentHash = request.ContentHash,
            Properties = propertiesJson,
            IpfsContent = request.IpfsContent,
            CreatedAt = now
        };

        db.Messages.Add(message);

        if (bucket.NextMessageId == long.MaxValue)
        {
            throw BucketException.ArithmeticOverflow();
        }

        bucket.NextMessageId++;
        bucket.UpdatedAt = now;

        await db.SaveChangesAsync(ct);

        return message;
    }

    /// <summary>
    /// Deletes a message and decrements its tag counter. The bucket's <c>NextMessageId</c> is
    /// deliberately not rewound — the pallet never rewinds it, so message ids are never reused.
    /// </summary>
    public async Task ForceRemoveAsync(long bucketId, long messageId, CancellationToken ct)
    {
        var message = await db.Messages
            .FirstOrDefaultAsync(m => m.BucketId == bucketId && m.MessageId == messageId, ct)
            ?? throw BucketException.UnknownMessage();

        if (message.Tag is not null)
        {
            var counter = await db.TagMessageCounts
                .FirstOrDefaultAsync(c => c.BucketId == bucketId && c.TagName == message.Tag, ct);

            if (counter is null || counter.Count == 0)
            {
                throw BucketException.ArithmeticUnderflow();
            }

            counter.Count--;
            counter.UpdatedAt = clock.GetUtcNow().UtcDateTime;
        }

        db.Messages.Remove(message);
        await db.SaveChangesAsync(ct);
    }
}
