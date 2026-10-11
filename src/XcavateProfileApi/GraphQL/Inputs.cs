namespace XcavateProfileApi.GraphQL;

/// <summary>One entry of a properties map. Mirrors the pallet's BoundedBTreeMap entries.</summary>
public sealed record PropertyInput(string Key, string Value);

public sealed record NamespaceMetadataInput(
    string Name,
    string? SchemaUri,
    IReadOnlyList<PropertyInput>? Properties,
    string? Category = null,
    string? Cluster = null,
    long? PropertyId = null,
    long? RealXhubId = null,
    long? Slot = null);

public sealed record BucketMetadataInput(
    string Name,
    string Category,
    IReadOnlyList<PropertyInput>? Properties);

public sealed record MessageMetadataInput(
    string? Description,
    string ContentType,
    string ContentHash,
    IReadOnlyList<PropertyInput>? Properties);

public sealed record MessageInput(
    string Reference,
    string? Tag,
    string? IpfsContent,
    MessageMetadataInput Metadata);

/// <summary>
/// Find-or-create input for a property's realXmarket offers namespace. <see cref="Name"/> is the
/// property name; the mutation appends " offers" when it is not already suffixed. The namespace is
/// keyed for reuse by <see cref="AssetId"/> (a u64 decimal string, stored in its properties map)
/// and <see cref="Cluster"/>.
/// </summary>
public sealed record CreateOffersNamespaceInput(
    string Name,
    string AssetId,
    string Cluster);

/// <summary>
/// One-call setup of an offer bucket in the property's offers namespace: namespace find-or-create,
/// bucket creation, buyer/seller membership, bucket unlock and the opening key-sharing and offer
/// messages. The caller signs as the buyer. <see cref="BucketEncryptionKey"/> is the bucket's
/// X25519 public key (32-byte hex); <see cref="BuyerViewerKey"/> and <see cref="SellerViewerKey"/>
/// override the parties' viewer X25519 keys, which otherwise resolve from their profiles.
/// </summary>
public sealed record SetupOfferBucketInput(
    string PropertyName,
    string AssetId,
    string ShareListingId,
    string Cluster,
    string Seller,
    string BucketEncryptionKey,
    string? BuyerViewerKey,
    string? SellerViewerKey,
    MessageInput KeySharingMessage,
    MessageInput OfferMessage);

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
