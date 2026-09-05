namespace XcavateBuckets.Domain;

/// <summary>
/// The optional attributes a namespace can carry. <see cref="PropertyId"/> and
/// <see cref="RealXhubId"/> are admin-only: the API layer enforces who may set them, while the
/// domain service simply stores whatever it is handed.
/// </summary>
public sealed record NamespaceAttributes(
    string? Category = null,
    string? Cluster = null,
    long? PropertyId = null,
    long? RealXhubId = null,
    long? Slot = null);
