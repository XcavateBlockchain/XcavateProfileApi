namespace XcavateProfileApi.GraphQL;

/// <summary>One entry of a properties map. Mirrors the pallet's BoundedBTreeMap entries.</summary>
public sealed record PropertyInput(string Key, string Value);

public sealed record NamespaceMetadataInput(
    string Name,
    string? SchemaUri,
    IReadOnlyList<PropertyInput>? Properties);

public sealed record BucketMetadataInput(
    string Name,
    string Category,
    IReadOnlyList<PropertyInput>? Properties);

public sealed record MessageMetadataInput(
    string Description,
    string ContentType,
    string ContentHash,
    IReadOnlyList<PropertyInput>? Properties);

public sealed record MessageInput(
    string Reference,
    string? Tag,
    string? IpfsContent,
    MessageMetadataInput Metadata);

internal static class PropertyInputExtensions
{
    /// <summary>
    /// Flattens property inputs for the domain layer. Returns null rather than an empty sequence so
    /// the stored column stays null when no properties were supplied.
    /// </summary>
    public static IEnumerable<KeyValuePair<string, string>>? ToPairs(
        this IReadOnlyList<PropertyInput>? properties) =>
        properties?.Select(p => new KeyValuePair<string, string>(p.Key, p.Value));
}
