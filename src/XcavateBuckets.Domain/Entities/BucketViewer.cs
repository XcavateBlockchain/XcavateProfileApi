namespace XcavateBuckets.Domain.Entities;

/// <summary>
/// A viewer of a bucket. Ports the pallet's
/// <c>Viewers: (BucketId, ViewerId) -&gt; ()</c> double map.
/// Viewers are identified by an encryption key rather than an account, so they can decrypt bucket
/// contents off-chain without holding any write permission.
/// </summary>
public class BucketViewer
{
    public long BucketId { get; set; }

    public Bucket Bucket { get; set; } = null!;

    /// <summary>32-byte hex X25519 public key (the pallet's ViewerId).</summary>
    public string ViewerId { get; set; } = string.Empty;

    public DateTime AddedAt { get; set; }
}
