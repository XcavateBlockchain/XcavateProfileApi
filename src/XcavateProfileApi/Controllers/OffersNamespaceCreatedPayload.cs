using System.Text.Json.Serialization;

namespace XcavateProfileApi.Controllers;

/// <summary>
/// Payload of the <c>offers_namespace_created</c> webhook the indexer posts when a property
/// becomes eligible for secondary-market offers (<c>claim_shares</c> or <c>relist_shares</c>).
/// </summary>
public sealed class OffersNamespaceCreatedPayload
{
    /// <summary>Always <c>offers_namespace_created</c>.</summary>
    [JsonPropertyName("event")]
    public string? Event { get; set; }

    /// <summary>The marketplace listing id (the on-chain property/listing identifier).</summary>
    [JsonPropertyName("listing_id")]
    public long? ListingId { get; set; }

    /// <summary>The base58 Listing PDA.</summary>
    [JsonPropertyName("listing")]
    public string? Listing { get; set; }

    /// <summary>The base58 PropertyAsset PDA.</summary>
    [JsonPropertyName("property")]
    public string? Property { get; set; }

    /// <summary>The on-chain asset id (a u64 JSON number).</summary>
    [JsonPropertyName("asset_id")]
    public long? AssetId { get; set; }

    /// <summary>The Solana cluster this event belongs to, e.g. <c>devnet</c>.</summary>
    [JsonPropertyName("cluster")]
    public string? Cluster { get; set; }

    /// <summary>The base58 transaction signature.</summary>
    [JsonPropertyName("tx_signature")]
    public string? TxSignature { get; set; }

    /// <summary>The slot the transaction landed in.</summary>
    [JsonPropertyName("slot")]
    public long Slot { get; set; }

    /// <summary>The block time, ISO-8601.</summary>
    [JsonPropertyName("block_time")]
    public DateTime? BlockTime { get; set; }
}
