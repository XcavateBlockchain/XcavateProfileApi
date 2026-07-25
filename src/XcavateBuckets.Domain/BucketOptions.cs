namespace XcavateBuckets.Domain;

/// <summary>
/// Input bounds corresponding to the pallet's <c>BoundedVec</c> and <c>BoundedBTreeMap</c> limits.
/// Off-chain there is no weight budget to protect, so these are generous sanity bounds rather than
/// mirrors of the runtime's constants.
/// </summary>
public class BucketOptions
{
    /// <summary>Pallet <c>MaxNameLen</c>: names and message descriptions.</summary>
    public int MaxNameLen { get; set; } = 256;

    /// <summary>Pallet <c>MaxUriLen</c>: namespace schema URIs.</summary>
    public int MaxUriLen { get; set; } = 512;

    /// <summary>Pallet <c>MaxCategoryLen</c>: bucket categories and message content types.</summary>
    public int MaxCategoryLen { get; set; } = 64;

    /// <summary>Pallet <c>MaxProperties</c>: entries in a properties map.</summary>
    public int MaxProperties { get; set; } = 32;

    /// <summary>Pallet <c>MaxPropertyKeyLen</c>.</summary>
    public int MaxPropertyKeyLen { get; set; } = 64;

    /// <summary>Pallet <c>MaxPropertyValueLen</c>.</summary>
    public int MaxPropertyValueLen { get; set; } = 512;

    /// <summary>Pallet <c>MaxStringInputLengthTag</c>.</summary>
    public int MaxTagLen { get; set; } = 64;

    /// <summary>Bounds the pallet's runtime-defined <c>Reference</c> type.</summary>
    public int MaxReferenceLen { get; set; } = 512;

    /// <summary>No pallet equivalent: bounds the caller-supplied message text.</summary>
    public int MaxIpfsContentLen { get; set; } = 1_048_576;
}
