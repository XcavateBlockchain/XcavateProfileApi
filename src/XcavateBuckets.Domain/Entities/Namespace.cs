namespace XcavateBuckets.Domain.Entities;

/// <summary>
/// A namespace groups buckets for one entity. Ports the pallet's
/// <c>Namespaces: NamespaceId -&gt; NamespaceMetadata</c> storage map.
/// </summary>
public class Namespace
{
    /// <summary>Identity primary key, replacing the pallet's <c>NextNamespaceId</c> counter.</summary>
    public long NamespaceId { get; set; }

    public string? Name { get; set; }

    public string? SchemaUri { get; set; }

    /// <summary>JSON-encoded key/value map, from the pallet's <c>BoundedBTreeMap</c>.</summary>
    public string? Properties { get; set; }

    /// <summary>Free-form category label.</summary>
    public string? Category { get; set; }

    /// <summary>Cluster identifier the namespace belongs to.</summary>
    public string? Cluster { get; set; }

    /// <summary>On-chain property id. Admin-only; the API layer enforces who may set it.</summary>
    public long? PropertyId { get; set; }

    /// <summary>On-chain XHub id. Admin-only; the API layer enforces who may set it.</summary>
    public long? RealXhubId { get; set; }

    /// <summary>Block slot the namespace is tied to.</summary>
    public long? Slot { get; set; }

    /// <summary>SS58 address of the creator.</summary>
    public string? Creator { get; set; }

    public DateTime CreatedAt { get; set; }

    public DateTime UpdatedAt { get; set; }

    public List<NamespaceManager> Managers { get; set; } = [];

    public List<Bucket> Buckets { get; set; } = [];
}
