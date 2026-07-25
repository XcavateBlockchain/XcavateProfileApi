namespace XcavateBuckets.Domain.Entities;

/// <summary>
/// A message label scoped to one bucket. Ports the pallet's
/// <c>Tags: (BucketId, Tag) -&gt; ()</c> double map.
/// </summary>
public class Tag
{
    public long BucketId { get; set; }

    public Bucket Bucket { get; set; } = null!;

    public string TagName { get; set; } = string.Empty;

    /// <summary>SS58 address of the creator.</summary>
    public string? Creator { get; set; }

    public DateTime CreatedAt { get; set; }
}
