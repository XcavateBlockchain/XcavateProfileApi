namespace XcavateBuckets.Domain.Entities;

/// <summary>
/// An admin of a bucket. Ports the pallet's <c>Admins: (BucketId, SubjectId) -&gt; ()</c> double map.
/// Admins create tags, manage contributors and viewers, and lock or unlock the bucket.
/// </summary>
public class BucketAdmin
{
    public long BucketId { get; set; }

    public Bucket Bucket { get; set; } = null!;

    /// <summary>SS58 address of the admin (the pallet's SubjectId).</summary>
    public string SubjectId { get; set; } = string.Empty;

    public DateTime AddedAt { get; set; }
}
