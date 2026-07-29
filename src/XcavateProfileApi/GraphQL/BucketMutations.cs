using XcavateBuckets.Domain.Data;
using XcavateBuckets.Domain.Entities;
using XcavateBuckets.Domain.Services;
using XcavateProfileApi.GraphQL.Auth;

// HotChocolate.Types also defines Tag; the domain entity is what we mean throughout.
using Tag = XcavateBuckets.Domain.Entities.Tag;

namespace XcavateProfileApi.GraphQL;

/// <summary>
/// Write side of the bucket API: one mutation per pallet extrinsic, in call-index order. Each is a
/// thin wrapper that resolves the signed caller, runs the domain service inside a transaction, and
/// returns the result. The 15 role-based mutations require a signature; the 5 <c>force*</c>
/// mutations require an admin address, standing in for the pallet's ForceOrigin.
/// </summary>
[GraphQLName("Mutation")]
public class BucketMutations
{
    // call_index 0
    [RequireSignature]
    public Task<Namespace> CreateNamespace(
        NamespaceMetadataInput metadata,
        ICallerContext caller,
        BucketDbContext db,
        NamespaceService namespaces,
        CancellationToken ct) =>
        InTransaction(db, ct, () => namespaces.CreateAsync(
            caller.RequireAddress(), metadata.Name, metadata.SchemaUri,
            metadata.Properties.ToPairs(), ct));

    // call_index 1
    [RequireSignature]
    public Task<BucketContributor> AddContributor(
        long? namespaceId,
        long bucketId,
        string contributor,
        ICallerContext caller,
        BucketDbContext db,
        MembershipService memberships,
        CancellationToken ct) =>
        InTransaction(db, ct, () => memberships.AddContributorAsync(
            caller.RequireAddress(), namespaceId, bucketId, contributor, ct));

    // call_index 2
    [RequireSignature]
    public Task<bool> RemoveContributor(
        long? namespaceId,
        long bucketId,
        string contributor,
        ICallerContext caller,
        BucketDbContext db,
        MembershipService memberships,
        CancellationToken ct) =>
        InTransaction(db, ct, () => memberships.RemoveContributorAsync(
            caller.RequireAddress(), namespaceId, bucketId, contributor, ct));

    // call_index 3
    [RequireSignature]
    public Task<BucketAdmin> AddAdmin(
        long? namespaceId,
        long bucketId,
        string admin,
        ICallerContext caller,
        BucketDbContext db,
        MembershipService memberships,
        CancellationToken ct) =>
        InTransaction(db, ct, () => memberships.AddAdminAsync(
            caller.RequireAddress(), namespaceId, bucketId, admin, ct));

    // call_index 4
    [RequireSignature]
    public Task<bool> RemoveAdmin(
        long? namespaceId,
        long bucketId,
        string admin,
        ICallerContext caller,
        BucketDbContext db,
        MembershipService memberships,
        CancellationToken ct) =>
        InTransaction(db, ct, () => memberships.RemoveAdminAsync(
            caller.RequireAddress(), namespaceId, bucketId, admin, ct));

    // call_index 5
    [RequireSignature]
    public Task<NamespaceManager> AddManager(
        long namespaceId,
        string newManager,
        ICallerContext caller,
        BucketDbContext db,
        NamespaceService namespaces,
        CancellationToken ct) =>
        InTransaction(db, ct, () => namespaces.AddManagerAsync(
            caller.RequireAddress(), namespaceId, newManager, ct));

    // call_index 6
    [RequireSignature]
    public Task<bool> RemoveManager(
        long namespaceId,
        string oldManager,
        ICallerContext caller,
        BucketDbContext db,
        NamespaceService namespaces,
        CancellationToken ct) =>
        InTransaction(db, ct, () => namespaces.RemoveManagerAsync(
            caller.RequireAddress(), namespaceId, oldManager, ct));

    // call_index 7
    [RequireSignature]
    public Task<Bucket> CreateBucket(
        long? namespaceId,
        BucketMetadataInput metadata,
        ICallerContext caller,
        BucketDbContext db,
        BucketService buckets,
        CancellationToken ct) =>
        InTransaction(db, ct, () => buckets.CreateAsync(
            caller.RequireAddress(), namespaceId, metadata.Name, metadata.Category,
            metadata.Properties.ToPairs(), ct));

    // call_index 8
    [RequireSignature]
    public Task<Bucket> PauseWriting(
        long? namespaceId,
        long bucketId,
        ICallerContext caller,
        BucketDbContext db,
        BucketService buckets,
        CancellationToken ct) =>
        InTransaction(db, ct, () => buckets.PauseWritingAsync(
            caller.RequireAddress(), namespaceId, bucketId, ct));

    // call_index 9
    [RequireSignature]
    public Task<Bucket> ResumeWriting(
        long? namespaceId,
        long bucketId,
        string newEncryptionKey,
        ICallerContext caller,
        BucketDbContext db,
        BucketService buckets,
        CancellationToken ct) =>
        InTransaction(db, ct, () => buckets.ResumeWritingAsync(
            caller.RequireAddress(), namespaceId, bucketId, newEncryptionKey, ct));

    // call_index 10
    [RequireSignature]
    public Task<Tag> CreateTag(
        long bucketId,
        string newTag,
        ICallerContext caller,
        BucketDbContext db,
        TagService tags,
        CancellationToken ct) =>
        InTransaction(db, ct, () => tags.CreateAsync(
            caller.RequireAddress(), bucketId, newTag, ct));

    // call_index 11
    [RequireSignature]
    public Task<Bucket> RotateKey(
        long? namespaceId,
        long bucketId,
        string newEncryptionKey,
        ICallerContext caller,
        BucketDbContext db,
        BucketService buckets,
        CancellationToken ct) =>
        InTransaction(db, ct, () => buckets.RotateKeyAsync(
            caller.RequireAddress(), namespaceId, bucketId, newEncryptionKey, ct));

    // call_index 12
    [RequireSignature]
    public Task<Message> Write(
        long? namespaceId,
        long bucketId,
        MessageInput message,
        ICallerContext caller,
        BucketDbContext db,
        MessageService messages,
        CancellationToken ct) =>
        InTransaction(db, ct, () => messages.WriteAsync(
            caller.RequireAddress(), namespaceId, bucketId,
            new MessageWriteRequest(
                message.Reference,
                message.Tag,
                message.IpfsContent,
                message.Metadata.Description,
                message.Metadata.ContentType,
                message.Metadata.ContentHash,
                message.Metadata.Properties.ToPairs()),
            ct));

    // call_index 13
    [RequireAdmin]
    public Task<bool> ForceRemoveNamespace(
        long namespaceId,
        BucketDbContext db,
        NamespaceService namespaces,
        CancellationToken ct) =>
        InTransaction(db, ct, () => namespaces.ForceRemoveAsync(namespaceId, ct));

    // call_index 14
    [RequireAdmin]
    public Task<bool> ForceRemoveBucket(
        long? namespaceId,
        long bucketId,
        BucketDbContext db,
        BucketService buckets,
        CancellationToken ct) =>
        InTransaction(db, ct, () => buckets.ForceRemoveAsync(namespaceId, bucketId, ct));

    // call_index 15
    [RequireAdmin]
    public Task<bool> ForceRemoveTag(
        long bucketId,
        string tag,
        BucketDbContext db,
        TagService tags,
        CancellationToken ct) =>
        InTransaction(db, ct, () => tags.ForceRemoveAsync(bucketId, tag, ct));

    // call_index 16
    [RequireAdmin]
    public Task<bool> ForceRemoveMessage(
        long bucketId,
        long messageId,
        BucketDbContext db,
        MessageService messages,
        CancellationToken ct) =>
        InTransaction(db, ct, () => messages.ForceRemoveAsync(bucketId, messageId, ct));

    // call_index 17
    [RequireAdmin]
    public Task<NamespaceManager> ForceAddManager(
        long namespaceId,
        string manager,
        BucketDbContext db,
        NamespaceService namespaces,
        CancellationToken ct) =>
        InTransaction(db, ct, () => namespaces.ForceAddManagerAsync(namespaceId, manager, ct));

    // call_index 18
    [RequireSignature]
    public Task<BucketViewer> AddViewer(
        long? namespaceId,
        long bucketId,
        string viewer,
        ICallerContext caller,
        BucketDbContext db,
        MembershipService memberships,
        CancellationToken ct) =>
        InTransaction(db, ct, () => memberships.AddViewerAsync(
            caller.RequireAddress(), namespaceId, bucketId, viewer, ct));

    // call_index 19
    [RequireSignature]
    public Task<bool> RemoveViewer(
        long? namespaceId,
        long bucketId,
        string viewer,
        ICallerContext caller,
        BucketDbContext db,
        MembershipService memberships,
        CancellationToken ct) =>
        InTransaction(db, ct, () => memberships.RemoveViewerAsync(
            caller.RequireAddress(), namespaceId, bucketId, viewer, ct));

    /// <summary>
    /// Runs a mutation atomically. A pallet extrinsic either applies wholly or not at all, and
    /// several of these touch more than one table — creating a namespace also inserts its first
    /// manager, writing a message also moves a tag counter and the bucket's message id.
    /// </summary>
    private static async Task<T> InTransaction<T>(
        BucketDbContext db, CancellationToken ct, Func<Task<T>> operation)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var result = await operation();
        await transaction.CommitAsync(ct);
        return result;
    }

    private static async Task<bool> InTransaction(
        BucketDbContext db, CancellationToken ct, Func<Task> operation)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        await operation();
        await transaction.CommitAsync(ct);
        return true;
    }
}
