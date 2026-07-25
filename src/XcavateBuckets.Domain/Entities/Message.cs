namespace XcavateBuckets.Domain.Entities;

/// <summary>
/// A message written into a bucket. Ports the pallet's
/// <c>Messages: (BucketId, MessageId) -&gt; Message</c> double map.
/// </summary>
public class Message
{
    public long BucketId { get; set; }

    public Bucket Bucket { get; set; } = null!;

    /// <summary>
    /// Per-bucket message id, assigned from <see cref="Bucket.NextMessageId"/>. Composite primary
    /// key together with <see cref="BucketId"/>, so ids restart at 0 in every bucket.
    /// </summary>
    public long MessageId { get; set; }

    /// <summary>SS58 address of the contributor who wrote the message.</summary>
    public string Contributor { get; set; } = string.Empty;

    /// <summary>Reference to the storage layer holding the encrypted content, e.g. an IPFS CID.</summary>
    public string? Reference { get; set; }

    public string? Tag { get; set; }

    public string? Description { get; set; }

    public string? ContentType { get; set; }

    /// <summary>32-byte hex hash of the content.</summary>
    public string? ContentHash { get; set; }

    /// <summary>JSON-encoded key/value map, from the pallet's <c>BoundedBTreeMap</c>.</summary>
    public string? Properties { get; set; }

    /// <summary>
    /// Resolved text content, stored as supplied by the caller at write time. The API never fetches
    /// it, and does not check that it matches <see cref="Reference"/> or <see cref="ContentHash"/>.
    /// </summary>
    public string? IpfsContent { get; set; }

    public DateTime CreatedAt { get; set; }
}
