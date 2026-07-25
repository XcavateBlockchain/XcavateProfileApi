namespace XcavateBuckets.Domain.Entities;

/// <summary>
/// A manager of a namespace. Ports the pallet's
/// <c>Managers: (NamespaceId, SubjectId) -&gt; ()</c> double map.
/// Managers create buckets and assign bucket admins.
/// </summary>
public class NamespaceManager
{
    public long NamespaceId { get; set; }

    public Namespace Namespace { get; set; } = null!;

    /// <summary>SS58 address of the manager (the pallet's SubjectId).</summary>
    public string Manager { get; set; } = string.Empty;

    public DateTime AddedAt { get; set; }
}
