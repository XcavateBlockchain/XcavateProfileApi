namespace XcavateBuckets.Domain.Entities;

/// <summary>
/// A contributor of a bucket. Ports the pallet's
/// <c>Contributors: (BucketId, SubjectId) -&gt; ()</c> double map.
/// Contributors are the only subjects allowed to write messages.
/// </summary>
public class BucketContributor
{
    public long BucketId { get; set; }

    public Bucket Bucket { get; set; } = null!;

    /// <summary>SS58 address of the contributor (the pallet's SubjectId).</summary>
    public string SubjectId { get; set; } = string.Empty;

    public DateTime AddedAt { get; set; }
}
