namespace XcavateBuckets.Domain.Entities;

/// <summary>
/// A bucket holds a collection of messages. Ports the pallet's
/// <c>Buckets: (NamespaceId, BucketId) -&gt; Bucket</c> double map.
/// </summary>
public class Bucket
{
    /// <summary>
    /// Identity primary key, replacing the pallet's <c>NextBucketId</c> counter. Bucket ids are
    /// global rather than per-namespace, because <c>NextBucketId</c> is a single storage value.
    /// </summary>
    public long BucketId { get; set; }

    public long NamespaceId { get; set; }

    public Namespace Namespace { get; set; } = null!;

    /// <summary>SS58 address of the creator.</summary>
    public string? Creator { get; set; }

    public string? Name { get; set; }

    public string? Category { get; set; }

    /// <summary>JSON-encoded key/value map, from the pallet's <c>BoundedBTreeMap</c>.</summary>
    public string? Properties { get; set; }

    /// <summary>
    /// Flattens the pallet's <c>Status::Writable(KeyId) | Locked</c>. A newly created bucket is
    /// locked, because <c>Status::default()</c> is <c>Locked</c>.
    /// </summary>
    public bool IsWritable { get; set; }

    /// <summary>32-byte hex encryption key. Non-null exactly when the bucket is writable.</summary>
    public string? EncryptionKey { get; set; }

    /// <summary>Per-bucket message counter, from the pallet's <c>Bucket.next_message_id</c>.</summary>
    public long NextMessageId { get; set; }

    public DateTime CreatedAt { get; set; }

    public DateTime UpdatedAt { get; set; }

    public List<BucketAdmin> Admins { get; set; } = [];

    public List<BucketContributor> Contributors { get; set; } = [];

    public List<BucketViewer> Viewers { get; set; } = [];

    public List<Tag> Tags { get; set; } = [];

    public List<Message> Messages { get; set; } = [];
}
