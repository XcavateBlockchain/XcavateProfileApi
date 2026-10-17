using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using XcavateBuckets.Domain.Data;
using XcavateBuckets.Domain.Entities;

namespace XcavateBuckets.Domain.Services;

/// <summary>
/// Idempotent creation of the property's secondary-market offers namespace. Shared between
/// the signed GraphQL mutation and the unsigned indexer webhook so both paths create the
/// same namespace shape and agree on the same idempotency key.
/// </summary>
public static class OffersNamespaceService
{
    public const string AssetIdPropertyKey = "assetId";

    private const string OffersSuffix = " offers";

    /// <summary>
    /// The property's offers namespace: the existing one carrying the assetId, else a new one
    /// under the caller. The properties map is a JSON document the entity model exposes as
    /// a string, so the category/cluster columns narrow the candidates in the query and the
    /// assetId match is checked on the parsed document.
    /// </summary>
    public static async Task<Namespace> FindOrCreateOffersNamespaceAsync(
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
                return candidate;
            }
        }

        return await namespaces.CreateAsync(
            caller,
            OffersNamespaceName(propertyName),
            schemaUri: null,
            properties: [new KeyValuePair<string, string>(AssetIdPropertyKey, assetId)],
            ct,
            new NamespaceAttributes(
                Category: BucketService.OffersNamespaceCategory,
                Cluster: cluster));
    }

    /// <summary>"&lt;propertyName&gt; offers", appending the suffix only when it is not already there.</summary>
    public static string OffersNamespaceName(string propertyName) =>
        propertyName.EndsWith(OffersSuffix, StringComparison.OrdinalIgnoreCase)
            ? propertyName
            : propertyName + OffersSuffix;

    /// <summary>The assetId in a namespace's JSON properties map, or null when absent/malformed.</summary>
    public static string? ReadAssetId(string? propertiesJson)
    {
        if (string.IsNullOrWhiteSpace(propertiesJson))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(propertiesJson);
            return document.RootElement.TryGetProperty(AssetIdPropertyKey, out var value)
                ? value.GetString()
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
