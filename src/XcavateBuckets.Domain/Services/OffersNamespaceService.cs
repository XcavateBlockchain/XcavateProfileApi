using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using XcavateBuckets.Domain.Data;
using XcavateBuckets.Domain.Entities;

namespace XcavateBuckets.Domain.Services;

/// <summary>
/// Idempotent creation of the property's secondary-market offers namespace. Shared between
/// the wallet-signed GraphQL mutation and the shared-secret indexer webhook so both paths create
/// the same namespace shape and agree on the same idempotency key.
/// </summary>
public static class OffersNamespaceService
{
    public const string AssetIdPropertyKey = "assetId";

    private const string AssetIdSnakeCasePropertyKey = "asset_id";

    private const string OffersSuffix = " offers";

    /// <summary>
    /// The property's offers namespace: the existing one carrying the assetId, else a new one
    /// under the caller. The properties map is a JSON document the entity model exposes as
    /// a string, so the category/cluster columns narrow the candidates in the query and the
    /// assetId match is checked on the parsed document.
    /// </summary>
    /// <returns>The namespace and a flag that is <c>true</c> when it already existed.</returns>
    public static async Task<(Namespace Namespace, bool Existed)> FindOrCreateOffersNamespaceAsync(
        string caller,
        string propertyName,
        string assetId,
        string cluster,
        BucketDbContext db,
        NamespaceService namespaces,
        CancellationToken ct)
    {
        var candidates = await db.Namespaces
            .Where(n => n.Category == BucketService.OffersNamespaceCategory && n.Cluster == cluster)
            .ToListAsync(ct);

        foreach (var candidate in candidates)
        {
            if (ReadAssetId(candidate.Properties) == assetId)
            {
                return (candidate, true);
            }
        }

        var created = await namespaces.CreateAsync(
            caller,
            OffersNamespaceName(propertyName),
            schemaUri: null,
            properties: [new KeyValuePair<string, string>(AssetIdPropertyKey, assetId)],
            ct,
            new NamespaceAttributes(
                Category: BucketService.OffersNamespaceCategory,
                Cluster: cluster));

        return (created, false);
    }

    /// <summary>"&lt;propertyName&gt; offers", appending the suffix only when it is not already there.</summary>
    public static string OffersNamespaceName(string propertyName) =>
        propertyName.EndsWith(OffersSuffix, StringComparison.OrdinalIgnoreCase)
            ? propertyName
            : propertyName + OffersSuffix;

    /// <summary>The assetId in a namespace's JSON properties map, or null when absent/malformed.
    /// Accepts either the camelCase key written by the offers namespace path or the snake_case
    /// key stored on the property namespace by <c>init_property_asset_devnet</c>.</summary>
    public static string? ReadAssetId(string? propertiesJson)
    {
        if (string.IsNullOrWhiteSpace(propertiesJson))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(propertiesJson);
            if (document.RootElement.TryGetProperty(AssetIdPropertyKey, out var value)
                || document.RootElement.TryGetProperty(AssetIdSnakeCasePropertyKey, out value))
            {
                return value.GetString();
            }

            return null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
