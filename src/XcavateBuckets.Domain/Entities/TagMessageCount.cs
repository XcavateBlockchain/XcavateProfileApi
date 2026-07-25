namespace XcavateBuckets.Domain.Entities;

/// <summary>
/// Tracks how many messages reference a tag within a bucket. Ports the pallet's
/// <c>TagMessages: (BucketId, Tag) -&gt; u32</c> double map, which guards tag deletion.
/// </summary>
public class TagMessageCount
{
    public long BucketId { get; set; }

    public Bucket Bucket { get; set; } = null!;

    public string TagName { get; set; } = string.Empty;

    public int Count { get; set; }

    public DateTime UpdatedAt { get; set; }
}
