using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using XcavateBuckets.Domain;
using XcavateBuckets.Domain.Data;
using XcavateBuckets.Domain.Entities;
using XcavateBuckets.Domain.Services;

namespace XcavateProfileApi.Controllers;

/// <summary>
/// Webhooks the indexer calls to notify the API of on-chain events. These are server-to-server
/// endpoints: the indexer posts a plain JSON document, so — unlike the profile and company
/// endpoints — they carry no wallet signature. Delivery is at-least-once, so every handler is
/// idempotent and answers 2xx whether it acted or deliberately ignored a redelivery, so the
/// indexer never retries a webhook the API already handled.
/// </summary>
[ApiController]
[Route("webhooks")]
public class WebhooksController : ControllerBase
{
    private readonly BucketDbContext _db;
    private readonly NamespaceService _namespaces;

    public WebhooksController(BucketDbContext db, NamespaceService namespaces)
    {
        _db = db;
        _namespaces = namespaces;
    }

    /// <summary>
    /// Receives the <c>property_asset_registered</c> event and creates a namespace for the newly
    /// registered property asset: its <see cref="PropertyAssetRegisteredPayload.Name"/> becomes the
    /// namespace name, <see cref="PropertyAssetRegisteredPayload.MetadataUri"/> its schema URI,
    /// <see cref="PropertyAssetRegisteredPayload.Slot"/> its slot,
    /// <see cref="PropertyAssetRegisteredPayload.ListingId"/> its on-chain property id, and the
    /// remaining on-chain fields (the PropertyAsset PDA, share mint, tx signature, block time and
    /// program) are stored in its properties map. The PropertyAsset PDA is recorded as the creator.
    /// </summary>
    /// <remarks>
    /// Idempotent: one asset registers exactly once, so if a namespace already carries the same
    /// PropertyAsset PDA and slot the webhook is a redelivery and is ignored without creating
    /// anything. 200 on both the created and the ignored paths; 400 when the body is not a valid
    /// <c>property_asset_registered</c> event.
    /// </remarks>
    // POST: webhooks/init_property_asset_devnet
    [HttpPost("init_property_asset_devnet")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> InitPropertyAssetDevnet(
        [FromBody] PropertyAssetRegisteredPayload payload, CancellationToken ct)
    {
        if (payload is null
            || !string.Equals(payload.Event, EventName, StringComparison.Ordinal)
            || string.IsNullOrWhiteSpace(payload.Pubkey)
            || string.IsNullOrWhiteSpace(payload.Name))
        {
            return BadRequest("The body is not a valid 'property_asset_registered' webhook payload.");
        }

        // Delivery is at-least-once, so the same asset can arrive more than once. The PDA (stored
        // as the creator) plus its slot identifies the registration, so a match means this asset's
        // namespace already exists — ignore the redelivery rather than create a second one.
        var alreadyExists = await _db.Namespaces
            .AnyAsync(n => n.Creator == payload.Pubkey && n.Slot == payload.Slot, ct);

        if (alreadyExists)
        {
            return Ok(new { status = "ignored", reason = "namespace already exists for this asset" });
        }

        var properties = new List<KeyValuePair<string, string>>
        {
            new("pubkey", payload.Pubkey),
            new("share_mint", payload.ShareMint ?? string.Empty),
            new("tx_signature", payload.TxSignature ?? string.Empty),
            new("program", payload.Program ?? string.Empty)
        };

        if (payload.BlockTime is { } blockTime)
        {
            properties.Add(new("block_time", blockTime.ToString("O")));
        }

        Namespace created;

        await using var transaction = await _db.Database.BeginTransactionAsync(ct);
        created = await _namespaces.CreateAsync(
            caller: payload.Pubkey,
            name: payload.Name,
            schemaUri: payload.MetadataUri,
            properties: properties,
            ct: ct,
            attributes: new NamespaceAttributes(
                Category: null,
                Cluster: null,
                PropertyId: payload.ListingId,
                RealXhubId: null,
                Slot: payload.Slot));
        await transaction.CommitAsync(ct);

        return Ok(new { status = "created", namespaceId = created.NamespaceId });
    }

    private const string EventName = "property_asset_registered";
}

/// <summary>
/// The <c>property_asset_registered</c> webhook payload the indexer POSTs verbatim. Field names are
/// the on-chain indexer's snake_case wire names, pinned with <c>JsonPropertyName</c> attributes so
/// they do not depend on the serializer's naming policy.
/// </summary>
public sealed class PropertyAssetRegisteredPayload
{
    /// <summary>Always <c>property_asset_registered</c>.</summary>
    [JsonPropertyName("event")]
    public string? Event { get; set; }

    /// <summary>The base58 PropertyAsset PDA — the unique identity of the asset.</summary>
    [JsonPropertyName("pubkey")]
    public string? Pubkey { get; set; }

    /// <summary>The on-chain listing / property id.</summary>
    [JsonPropertyName("listing_id")]
    public long ListingId { get; set; }

    /// <summary>The asset's name, e.g. <c>42 Main Street</c>.</summary>
    [JsonPropertyName("name")]
    public string? Name { get; set; }

    /// <summary>The asset's metadata URI.</summary>
    [JsonPropertyName("metadata_uri")]
    public string? MetadataUri { get; set; }

    /// <summary>The base58 share mint.</summary>
    [JsonPropertyName("share_mint")]
    public string? ShareMint { get; set; }

    /// <summary>The slot the asset was registered in.</summary>
    [JsonPropertyName("slot")]
    public long Slot { get; set; }

    /// <summary>The base58 transaction signature.</summary>
    [JsonPropertyName("tx_signature")]
    public string? TxSignature { get; set; }

    /// <summary>The block time the asset was registered, ISO-8601.</summary>
    [JsonPropertyName("block_time")]
    public DateTime? BlockTime { get; set; }

    /// <summary>The program that registered the asset, e.g. <c>marketplace</c>.</summary>
    [JsonPropertyName("program")]
    public string? Program { get; set; }
}
