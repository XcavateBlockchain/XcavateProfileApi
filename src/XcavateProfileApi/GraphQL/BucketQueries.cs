using Microsoft.EntityFrameworkCore;
using XcavateBuckets.Domain.Data;
using XcavateBuckets.Domain.Entities;

// HotChocolate.Types also defines Tag; the domain entity is what we mean throughout.
using Tag = XcavateBuckets.Domain.Entities.Tag;

namespace XcavateProfileApi.GraphQL;

/// <summary>
/// Read side of the bucket API. Field names and the connection shape match what the SubQuery
/// indexer served, so existing selection sets keep working. Reads need no signature.
/// </summary>
[GraphQLName("Query")]
public class BucketQueries
{
    [UsePaging(IncludeTotalCount = true)]
    [UseFiltering]
    [UseSorting]
    public IQueryable<Namespace> GetNamespaces(BucketDbContext db) =>
        db.Namespaces.AsNoTracking();

    public Task<Namespace?> GetNamespace(
        [GraphQLType<NonNullType<IdType>>] string id, BucketDbContext db, CancellationToken ct) =>
        long.TryParse(id, out var namespaceId)
            ? db.Namespaces.AsNoTracking()
                .FirstOrDefaultAsync(n => n.NamespaceId == namespaceId, ct)
            : Task.FromResult<Namespace?>(null);

    [UsePaging(IncludeTotalCount = true)]
    [UseFiltering]
    [UseSorting]
    public IQueryable<Bucket> GetBuckets(BucketDbContext db) =>
        db.Buckets.AsNoTracking();

    public Task<Bucket?> GetBucket(
        [GraphQLType<NonNullType<IdType>>] string id, BucketDbContext db, CancellationToken ct) =>
        long.TryParse(id, out var bucketId)
            ? db.Buckets.AsNoTracking().FirstOrDefaultAsync(b => b.BucketId == bucketId, ct)
            : Task.FromResult<Bucket?>(null);

    [UsePaging(IncludeTotalCount = true)]
    [UseFiltering]
    [UseSorting]
    public IQueryable<Message> GetMessages(BucketDbContext db) =>
        db.Messages.AsNoTracking();

    public Task<Message?> GetMessage(
        [GraphQLType<NonNullType<IdType>>] string id, BucketDbContext db, CancellationToken ct)
    {
        if (!EntityId.TrySplit(id, out var bucketId, out var rest)
            || !long.TryParse(rest, out var messageId))
        {
            return Task.FromResult<Message?>(null);
        }

        return db.Messages.AsNoTracking()
            .FirstOrDefaultAsync(m => m.BucketId == bucketId && m.MessageId == messageId, ct);
    }

    [UsePaging(IncludeTotalCount = true)]
    [UseFiltering]
    [UseSorting]
    public IQueryable<Tag> GetTags(BucketDbContext db) =>
        db.Tags.AsNoTracking();

    public Task<Tag?> GetTag(
        [GraphQLType<NonNullType<IdType>>] string id, BucketDbContext db, CancellationToken ct)
    {
        // Tag names can contain hyphens, so only the first separator is structural.
        if (!EntityId.TrySplit(id, out var bucketId, out var tagName))
        {
            return Task.FromResult<Tag?>(null);
        }

        return db.Tags.AsNoTracking()
            .FirstOrDefaultAsync(t => t.BucketId == bucketId && t.TagName == tagName, ct);
    }

    [UsePaging(IncludeTotalCount = true)]
    [UseFiltering]
    [UseSorting]
    public IQueryable<NamespaceManager> GetNamespaceManagers(BucketDbContext db) =>
        db.NamespaceManagers.AsNoTracking();

    [UsePaging(IncludeTotalCount = true)]
    [UseFiltering]
    [UseSorting]
    public IQueryable<BucketAdmin> GetBucketAdmins(BucketDbContext db) =>
        db.BucketAdmins.AsNoTracking();

    [UsePaging(IncludeTotalCount = true)]
    [UseFiltering]
    [UseSorting]
    public IQueryable<BucketContributor> GetBucketContributors(BucketDbContext db) =>
        db.BucketContributors.AsNoTracking();

    [UsePaging(IncludeTotalCount = true)]
    [UseFiltering]
    [UseSorting]
    public IQueryable<BucketViewer> GetBucketViewers(BucketDbContext db) =>
        db.BucketViewers.AsNoTracking();

    [UsePaging(IncludeTotalCount = true)]
    [UseFiltering]
    [UseSorting]
    public IQueryable<TagMessageCount> GetTagMessageCounts(BucketDbContext db) =>
        db.TagMessageCounts.AsNoTracking();
}
