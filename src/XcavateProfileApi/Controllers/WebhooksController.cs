using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using XcavateBuckets.Domain;
using XcavateBuckets.Domain.Data;
using XcavateBuckets.Domain.Entities;
using XcavateBuckets.Domain.Services;
using XcavateProfileApi.Data;

namespace XcavateProfileApi.Controllers;

/// <summary>
/// Webhooks the indexer calls to notify the API of on-chain events. These are server-to-server
/// endpoints: the indexer posts a plain JSON document, so — unlike the profile and company
/// endpoints — they carry no wallet signature. Delivery is at-least-once, so every handler is
/// idempotent and answers 2xx whether it acted or deliberately ignored a redelivery, so the
/// indexer never retries a webhook the API already handled.
///
/// The set implements the "Property namespace &amp; buckets" lifecycle: the asset webhook creates
/// the property's namespace, the claim-started webhook creates its legal-process bucket, the
/// lawyer-election / agent-appointment / transfer webhooks manage its membership (partly
/// placeholder — see docs/property-webhooks-uncertainties.md), and the deal-executed webhook
/// removes the lawyers when the sale settles.
/// </summary>
[ApiController]
[Route("webhooks")]
public class WebhooksController : ControllerBase
{
    private readonly BucketDbContext _db;
    private readonly ProfileDbContext _profiles;
    private readonly NamespaceService _namespaces;
    private readonly BucketService _buckets;
    private readonly MembershipService _memberships;

    public WebhooksController(
        BucketDbContext db,
        ProfileDbContext profiles,
        NamespaceService namespaces,
        BucketService buckets,
        MembershipService memberships)
    {
        _db = db;
        _profiles = profiles;
        _namespaces = namespaces;
        _buckets = buckets;
        _memberships = memberships;
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

    // ---- Property lifecycle webhooks (the "Property namespace & buckets" flow) ----------------
    //
    // The five events that continue the flow the asset webhook starts:
    //
    //   property_claim_started       -> create the property's legal-process bucket (implemented)
    //   spv_lawyer_elected           -> lawyer write + holder read access (PLACEHOLDER, see below)
    //   deal_executed                -> remove the lawyers from the bucket (implemented)
    //   letting_agent_appointed      -> add the letting agent (write) (PLACEHOLDER)
    //   property_shares_transferred  -> swap seller -> buyer read access (implemented: wallets
    //                                   resolve to viewer X25519 keys through the profiles)
    //
    // The placeholders and exactly what they are waiting for are documented in
    // docs/property-webhooks-uncertainties.md. Like the asset webhook, every endpoint is
    // idempotent and answers 2xx whether it acted or deliberately ignored a redelivery. The one
    // new status is 404: the event's namespace or bucket is not there YET (deliveries are
    // unordered across event types), so the indexer's retry-with-backoff delivers it again once
    // the prerequisite exists.

    /// <summary>
    /// Receives the <c>property_claim_started</c> event (the first successful token claim on a
    /// sold-out listing, deduped by the indexer to one event per listing) and creates the
    /// property's legal-process bucket inside its namespace.
    /// </summary>
    /// <remarks>
    /// The bucket is created by the namespace's creator (the PropertyAsset PDA, its first
    /// manager), named after the namespace, categorised <c>legal-process</c> (the marker the
    /// other lifecycle webhooks look the bucket up by), and the creator is also installed as a
    /// bucket admin so the later membership webhooks have a caller that passes the admin check.
    /// Idempotent via the bucket lookup: a redelivery finds the existing bucket and is ignored.
    /// 404 while the namespace is missing (the asset webhook has not landed yet — the indexer
    /// retries); 400 when the body is not a valid <c>property_claim_started</c> event.
    /// </remarks>
    // POST: webhooks/property_claim_started_devnet
    [HttpPost("property_claim_started_devnet")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> PropertyClaimStartedDevnet(
        [FromBody] PropertyClaimStartedPayload payload, CancellationToken ct)
    {
        if (payload is null
            || !string.Equals(payload.Event, ClaimStartedEventName, StringComparison.Ordinal)
            || (payload.ListingId is null && string.IsNullOrWhiteSpace(payload.Property)))
        {
            return BadRequest("The body is not a valid 'property_claim_started' webhook payload.");
        }

        var ns = await FindNamespaceAsync(payload.ListingId, payload.Property, ct);
        if (ns is null || string.IsNullOrWhiteSpace(ns.Creator))
        {
            return NotFound("No webhook-created namespace matches this listing yet.");
        }

        var existing = await FindLegalBucketAsync(ns.NamespaceId, ct);
        if (existing is not null)
        {
            return Ok(new
            {
                status = "ignored",
                reason = "legal bucket already exists for this property",
                bucketId = existing.BucketId
            });
        }

        var properties = new List<KeyValuePair<string, string>>
        {
            new("kind", "legal_process"),
            new("listing_id", payload.ListingId?.ToString() ?? string.Empty),
            new("listing", payload.Listing ?? string.Empty),
            new("property", payload.Property ?? string.Empty),
            new("started_by", payload.Investor ?? string.Empty),
            new("tx_signature", payload.TxSignature ?? string.Empty),
            new("program", payload.Program ?? string.Empty)
        };
        if (payload.BlockTime is { } blockTime)
        {
            properties.Add(new("block_time", blockTime.ToString("O")));
        }

        await using var transaction = await _db.Database.BeginTransactionAsync(ct);
        var bucket = await _buckets.CreateAsync(
            caller: ns.Creator,
            namespaceId: ns.NamespaceId,
            name: LegalBucketName(ns),
            category: LegalBucketCategory,
            properties: properties,
            ct: ct);
        // The operator admin: the namespace creator (the PropertyAsset PDA, already a namespace
        // manager) as a BucketAdmin row, so later membership webhooks can pass it as the caller
        // the admin check expects.
        await _memberships.AddAdminAsync(ns.Creator, ns.NamespaceId, bucket.BucketId, ns.Creator, ct);
        await transaction.CommitAsync(ct);

        return Ok(new { status = "created", bucketId = bucket.BucketId });
    }

    /// <summary>
    /// Receives the <c>spv_lawyer_elected</c> event (the SPV lawyer election finalized with a
    /// winner). PLACEHOLDER: the payload's <c>winner_registry</c> is the winner's on-chain Lawyer
    /// REGISTRY PDA, not their wallet, and the token holders' read access needs X25519 viewer keys
    /// the chain does not carry — see docs/property-webhooks-uncertainties.md entries 2 and 3.
    /// The endpoint validates, resolves the namespace and bucket (404 while either is missing, so
    /// the indexer retries), and records the event as accepted; the membership changes themselves
    /// are the documented TODO.
    /// </summary>
    // POST: webhooks/spv_lawyer_elected_devnet
    [HttpPost("spv_lawyer_elected_devnet")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> SpvLawyerElectedDevnet(
        [FromBody] SpvLawyerElectedPayload payload, CancellationToken ct)
    {
        if (payload is null
            || !string.Equals(payload.Event, LawyerElectedEventName, StringComparison.Ordinal)
            || (payload.ListingId is null && string.IsNullOrWhiteSpace(payload.Property)))
        {
            return BadRequest("The body is not a valid 'spv_lawyer_elected' webhook payload.");
        }

        var ns = await FindNamespaceAsync(payload.ListingId, payload.Property, ct);
        if (ns is null || string.IsNullOrWhiteSpace(ns.Creator))
        {
            return NotFound("No webhook-created namespace matches this listing yet.");
        }

        var bucket = await FindLegalBucketAsync(ns.NamespaceId, ct);
        if (bucket is null)
        {
            return NotFound("The property's legal bucket does not exist yet.");
        }

        // TODO(uncertainties #2/#3): resolve WinnerRegistry -> the lawyer's wallet via the indexer
        // GraphQL `marketplaceListing(...).spvLawyer`, then
        //   await _memberships.AddContributorAsync(ns.Creator, ns.NamespaceId, bucket.BucketId, lawyerWallet, ct);
        // and add each current token holder as a viewer once the wallet -> X25519 viewer-key
        // mapping exists. Until then the event is only acknowledged.
        return Ok(new
        {
            status = "accepted",
            acted = false,
            pending = "lawyer wallet resolution and holder viewer keys (placeholder)",
            bucketId = bucket.BucketId
        });
    }

    /// <summary>
    /// Receives the <c>deal_executed</c> event (the sale settled on-chain) and removes the
    /// lawyers from the property's legal bucket: every contributor, and every admin except the
    /// operator admin (the namespace creator). Token-holder viewers are deliberately untouched —
    /// they still own tokens after the sale.
    /// </summary>
    /// <remarks>
    /// Idempotent: membership removals are no-ops when the member is already gone, so a
    /// redelivery re-runs harmlessly and answers 200. 404 while the namespace or bucket is
    /// missing; 400 when the body is not a valid <c>deal_executed</c> event.
    /// </remarks>
    // POST: webhooks/deal_executed_devnet
    [HttpPost("deal_executed_devnet")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> DealExecutedDevnet(
        [FromBody] DealExecutedPayload payload, CancellationToken ct)
    {
        if (payload is null
            || !string.Equals(payload.Event, DealExecutedEventName, StringComparison.Ordinal)
            || (payload.ListingId is null && string.IsNullOrWhiteSpace(payload.Property)))
        {
            return BadRequest("The body is not a valid 'deal_executed' webhook payload.");
        }

        var ns = await FindNamespaceAsync(payload.ListingId, payload.Property, ct);
        if (ns is null || string.IsNullOrWhiteSpace(ns.Creator))
        {
            return NotFound("No webhook-created namespace matches this listing yet.");
        }

        var bucket = await FindLegalBucketAsync(ns.NamespaceId, ct);
        if (bucket is null)
        {
            return NotFound("The property's legal bucket does not exist yet.");
        }

        await using var transaction = await _db.Database.BeginTransactionAsync(ct);
        // A bucket created before the claim webhook existed may lack the operator admin row the
        // contributor-removal caller check needs; adding it is idempotent.
        await _memberships.AddAdminAsync(ns.Creator, ns.NamespaceId, bucket.BucketId, ns.Creator, ct);

        var contributors = await _db.BucketContributors
            .Where(c => c.BucketId == bucket.BucketId)
            .Select(c => c.SubjectId)
            .ToListAsync(ct);
        var admins = await _db.BucketAdmins
            .Where(a => a.BucketId == bucket.BucketId)
            .Select(a => a.SubjectId)
            .ToListAsync(ct);

        var removedContributors = 0;
        foreach (var contributor in contributors.Where(s => s != ns.Creator))
        {
            await _memberships.RemoveContributorAsync(
                ns.Creator, ns.NamespaceId, bucket.BucketId, contributor, ct);
            removedContributors++;
        }

        var removedAdmins = 0;
        foreach (var admin in admins.Where(s => s != ns.Creator))
        {
            await _memberships.RemoveAdminAsync(ns.Creator, ns.NamespaceId, bucket.BucketId, admin, ct);
            removedAdmins++;
        }
        await transaction.CommitAsync(ct);

        return Ok(new
        {
            status = "lawyers_removed",
            removedContributors,
            removedAdmins,
            bucketId = bucket.BucketId
        });
    }

    /// <summary>
    /// Receives the <c>letting_agent_appointed</c> event (the property program's agent election
    /// finalized). PLACEHOLDER: the payload's <c>winner_entry</c> is the winner's LettingAgent
    /// ENTRY PDA, not the agent's wallet — the wallet must be resolved via the indexer GraphQL
    /// before the agent can be added as a contributor (docs/property-webhooks-uncertainties.md
    /// entry 4). Validates and resolves the namespace and bucket (404 while missing), then
    /// acknowledges without acting.
    /// </summary>
    // POST: webhooks/letting_agent_appointed_devnet
    [HttpPost("letting_agent_appointed_devnet")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> LettingAgentAppointedDevnet(
        [FromBody] LettingAgentAppointedPayload payload, CancellationToken ct)
    {
        if (payload is null
            || !string.Equals(payload.Event, AgentAppointedEventName, StringComparison.Ordinal)
            || (payload.AssetId is null && string.IsNullOrWhiteSpace(payload.Property)))
        {
            return BadRequest("The body is not a valid 'letting_agent_appointed' webhook payload.");
        }

        var ns = await FindNamespaceAsync(payload.AssetId, payload.Property, ct);
        if (ns is null || string.IsNullOrWhiteSpace(ns.Creator))
        {
            return NotFound("No webhook-created namespace matches this property yet.");
        }

        var bucket = await FindLegalBucketAsync(ns.NamespaceId, ct);
        if (bucket is null)
        {
            return NotFound("The property's legal bucket does not exist yet.");
        }

        // TODO(uncertainties #4): resolve WinnerEntry -> the agent's wallet via the indexer
        // GraphQL, then
        //   await _memberships.AddContributorAsync(ns.Creator, ns.NamespaceId, bucket.BucketId, agentWallet, ct);
        return Ok(new
        {
            status = "accepted",
            acted = false,
            pending = "letting-agent wallet resolution (placeholder)",
            bucketId = bucket.BucketId
        });
    }

    /// <summary>
    /// Receives the <c>property_shares_transferred</c> event (a resale: a relisted purchase, an
    /// accepted offer, or a direct send) and swaps read access on the property's legal bucket:
    /// the seller's viewer is removed and the buyer is added as a viewer. Wallets are resolved to
    /// viewer ids through the profiles: a bucket viewer id is a 32-byte X25519 key, and the
    /// profile of a wallet stores it (<see cref="ProfileDbContext"/> — directly keyed by the
    /// wallet, with <c>WalletMigrations</c> as the fallback for profiles still keyed by a legacy
    /// SS58 address).
    /// </summary>
    /// <remarks>
    /// A side whose wallet has no profile key (or a malformed legacy one) is skipped and reported
    /// — never an error, so the indexer does not retry a permanently-unactionable event forever
    /// (the buyer simply appears in the bucket once they register a key AND a later transfer or
    /// reconciliation pass adds them — see docs/property-webhooks-uncertainties.md entry 5).
    /// Idempotent: viewer adds return the existing row and removes are no-ops when absent, so a
    /// redelivery re-runs harmlessly. A self-transfer (<c>from</c> == <c>to</c>) changes nothing.
    /// 404 while the namespace or bucket is missing; 400 when the body is not a valid
    /// <c>property_shares_transferred</c> event.
    /// </remarks>
    // POST: webhooks/shares_transferred_devnet
    [HttpPost("shares_transferred_devnet")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> SharesTransferredDevnet(
        [FromBody] SharesTransferredPayload payload, CancellationToken ct)
    {
        if (payload is null
            || !string.Equals(payload.Event, SharesTransferredEventName, StringComparison.Ordinal)
            || payload.Kind is not ("relisted_purchase" or "offer_accepted" or "direct_send")
            || string.IsNullOrWhiteSpace(payload.From)
            || string.IsNullOrWhiteSpace(payload.To))
        {
            return BadRequest("The body is not a valid 'property_shares_transferred' webhook payload.");
        }

        var ns = await FindNamespaceAsync(payload.AssetId, payload.Property, ct);
        if (ns is null || string.IsNullOrWhiteSpace(ns.Creator))
        {
            return NotFound("No webhook-created namespace matches this property yet.");
        }

        var bucket = await FindLegalBucketAsync(ns.NamespaceId, ct);
        if (bucket is null)
        {
            return NotFound("The property's legal bucket does not exist yet.");
        }

        // A self-transfer (same wallet on both sides) changes no access.
        if (string.Equals(payload.From, payload.To, StringComparison.Ordinal))
        {
            return Ok(new { status = "ignored", reason = "self-transfer", bucketId = bucket.BucketId });
        }

        var buyerKey = await ResolveViewerKeyAsync(payload.To, ct);
        var sellerKey = await ResolveViewerKeyAsync(payload.From, ct);

        await using var transaction = await _db.Database.BeginTransactionAsync(ct);
        // A bucket created before the claim webhook existed may lack the operator admin row the
        // viewer-add caller check needs; adding it is idempotent.
        await _memberships.AddAdminAsync(ns.Creator, ns.NamespaceId, bucket.BucketId, ns.Creator, ct);

        var buyerAdded = false;
        if (buyerKey is not null)
        {
            await _memberships.AddViewerAsync(ns.Creator, ns.NamespaceId, bucket.BucketId, buyerKey, ct);
            buyerAdded = true;
        }

        var sellerRemoved = false;
        if (sellerKey is not null)
        {
            await _memberships.RemoveViewerAsync(ns.Creator, ns.NamespaceId, bucket.BucketId, sellerKey, ct);
            sellerRemoved = true;
        }
        await transaction.CommitAsync(ct);

        return Ok(new
        {
            status = "processed",
            bucketId = bucket.BucketId,
            buyerViewerAdded = buyerAdded,
            buyerNote = buyerAdded ? null : "no profile X25519 key for the buyer wallet — skipped",
            sellerViewerRemoved = sellerRemoved,
            sellerNote = sellerRemoved ? null : "no profile X25519 key for the seller wallet — skipped"
        });
    }

    /// <summary>
    /// The namespace one of these events belongs to: by on-chain property id first (the asset
    /// webhook stores the listing id as <see cref="Namespace.PropertyId"/>), falling back to the
    /// PropertyAsset PDA — which the asset webhook records as the namespace's creator.
    /// </summary>
    private async Task<Namespace?> FindNamespaceAsync(
        long? propertyId, string? propertyPda, CancellationToken ct)
    {
        if (propertyId is { } id)
        {
            var byPropertyId = await _db.Namespaces
                .FirstOrDefaultAsync(n => n.PropertyId == id, ct);
            if (byPropertyId is not null)
            {
                return byPropertyId;
            }
        }

        if (!string.IsNullOrWhiteSpace(propertyPda))
        {
            return await _db.Namespaces
                .FirstOrDefaultAsync(n => n.Creator == propertyPda, ct);
        }

        return null;
    }

    /// <summary>
    /// The property's legal-process bucket, identified by the <see cref="LegalBucketCategory"/>
    /// the claim-started webhook creates it with (there is at most one per namespace).
    /// </summary>
    private async Task<Bucket?> FindLegalBucketAsync(long namespaceId, CancellationToken ct)
    {
        return await _db.Buckets
            .FirstOrDefaultAsync(b => b.NamespaceId == namespaceId && b.Category == LegalBucketCategory, ct);
    }

    /// <summary>
    /// The bucket viewer id (a 32-byte X25519 key) for a wallet, or null when the wallet has no
    /// usable one: the profile keyed directly by the wallet first, then the
    /// <c>WalletMigrations</c> fallback for a profile still keyed by the legacy SS58 address of a
    /// migrated account. A stored key that is not well-formed 32-byte hex never reaches
    /// <c>MembershipService.AddViewerAsync</c> — it would be an <c>InvalidInput</c> failure there
    /// and, worse, an endless retry of a permanently-unactionable event.
    /// </summary>
    private async Task<string?> ResolveViewerKeyAsync(string wallet, CancellationToken ct)
    {
        var direct = await _profiles.Profiles
            .Where(p => p.Ss58Address == wallet)
            .Select(p => p.X25519Key)
            .FirstOrDefaultAsync(ct);
        var key = direct ?? await _profiles.WalletMigrations
            .Where(m => m.SolanaAddress == wallet)
            .Join(
                _profiles.Profiles,
                m => m.Ss58Address,
                p => p.Ss58Address,
                (m, p) => p.X25519Key)
            .FirstOrDefaultAsync(ct);

        return IsHex32Key(key) ? key : null;
    }

    /// <summary>The domain's viewer-id shape: exactly 32 bytes of hex, optional <c>0x</c> prefix.</summary>
    private static bool IsHex32Key(string? key)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            return false;
        }
        var hex = key.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? key[2..] : key;
        return hex.Length == 64 && hex.All(Uri.IsHexDigit);
    }

    /// <summary>The bucket name, after the namespace's, clamped to the domain's 256-char bound.</summary>
    private static string LegalBucketName(Namespace ns)
    {
        var baseName = string.IsNullOrWhiteSpace(ns.Name) ? $"namespace {ns.NamespaceId}" : ns.Name!;
        var name = $"{baseName} - legal process";
        return name.Length <= MaxBucketNameLen ? name : name[..MaxBucketNameLen];
    }

    private const string EventName = "property_asset_registered";
    private const string ClaimStartedEventName = "property_claim_started";
    private const string LawyerElectedEventName = "spv_lawyer_elected";
    private const string DealExecutedEventName = "deal_executed";
    private const string AgentAppointedEventName = "letting_agent_appointed";
    private const string SharesTransferredEventName = "property_shares_transferred";

    /// <summary>The bucket category marking a property's legal-process bucket.</summary>
    private const string LegalBucketCategory = "legal-process";

    /// <summary>The domain's bucket-name bound (InputValidator.MaxNameLen).</summary>
    private const int MaxBucketNameLen = 256;
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

/// <summary>
/// The <c>property_claim_started</c> webhook payload the indexer POSTs verbatim (the first
/// successful <c>claim_shares</c> on a listing, deduped to one event per listing). Field names
/// are the indexer's snake_case wire names, pinned with <c>JsonPropertyName</c>.
/// </summary>
public sealed class PropertyClaimStartedPayload
{
    /// <summary>Always <c>property_claim_started</c>.</summary>
    [JsonPropertyName("event")]
    public string? Event { get; set; }

    /// <summary>The base58 Listing PDA — the event's dedup subject.</summary>
    [JsonPropertyName("listing")]
    public string? Listing { get; set; }

    /// <summary>The base58 PropertyAsset PDA (the namespace's creator).</summary>
    [JsonPropertyName("property")]
    public string? Property { get; set; }

    /// <summary>The on-chain listing id (the namespace's PropertyId).</summary>
    [JsonPropertyName("listing_id")]
    public long? ListingId { get; set; }

    /// <summary>The base58 wallet of the investor whose claim opened the claim phase.</summary>
    [JsonPropertyName("investor")]
    public string? Investor { get; set; }

    /// <summary>The slot the claim landed in.</summary>
    [JsonPropertyName("slot")]
    public long Slot { get; set; }

    /// <summary>The base58 transaction signature of that first claim.</summary>
    [JsonPropertyName("tx_signature")]
    public string? TxSignature { get; set; }

    /// <summary>The block time of the claim, ISO-8601.</summary>
    [JsonPropertyName("block_time")]
    public DateTime? BlockTime { get; set; }

    /// <summary>The program that produced the event, e.g. <c>marketplace</c>.</summary>
    [JsonPropertyName("program")]
    public string? Program { get; set; }
}

/// <summary>
/// The <c>spv_lawyer_elected</c> webhook payload the indexer POSTs verbatim (marketplace
/// <c>finalize_spv_election</c>). Field names are the indexer's snake_case wire names.
/// </summary>
public sealed class SpvLawyerElectedPayload
{
    /// <summary>Always <c>spv_lawyer_elected</c>.</summary>
    [JsonPropertyName("event")]
    public string? Event { get; set; }

    /// <summary>The on-chain listing id (the namespace's PropertyId).</summary>
    [JsonPropertyName("listing_id")]
    public long? ListingId { get; set; }

    /// <summary>The base58 Listing PDA.</summary>
    [JsonPropertyName("listing")]
    public string? Listing { get; set; }

    /// <summary>The base58 PropertyAsset PDA (the namespace's creator).</summary>
    [JsonPropertyName("property")]
    public string? Property { get; set; }

    /// <summary>
    /// The base58 Lawyer REGISTRY PDA of the election winner — NOT the lawyer's wallet; resolving
    /// it is placeholder work (see docs/property-webhooks-uncertainties.md entry 2). Null when the
    /// instruction omitted the optional account.
    /// </summary>
    [JsonPropertyName("winner_registry")]
    public string? WinnerRegistry { get; set; }

    /// <summary>The slot the election finalized in.</summary>
    [JsonPropertyName("slot")]
    public long Slot { get; set; }

    /// <summary>The base58 transaction signature.</summary>
    [JsonPropertyName("tx_signature")]
    public string? TxSignature { get; set; }

    /// <summary>The block time, ISO-8601.</summary>
    [JsonPropertyName("block_time")]
    public DateTime? BlockTime { get; set; }

    /// <summary>The program that produced the event, e.g. <c>marketplace</c>.</summary>
    [JsonPropertyName("program")]
    public string? Program { get; set; }
}

/// <summary>
/// The <c>deal_executed</c> webhook payload the indexer POSTs verbatim (marketplace
/// <c>execute_deal</c> — the sale settled). Field names are the indexer's snake_case wire names.
/// </summary>
public sealed class DealExecutedPayload
{
    /// <summary>Always <c>deal_executed</c>.</summary>
    [JsonPropertyName("event")]
    public string? Event { get; set; }

    /// <summary>The on-chain listing id (the namespace's PropertyId).</summary>
    [JsonPropertyName("listing_id")]
    public long? ListingId { get; set; }

    /// <summary>The base58 Listing PDA.</summary>
    [JsonPropertyName("listing")]
    public string? Listing { get; set; }

    /// <summary>The base58 PropertyAsset PDA (the namespace's creator).</summary>
    [JsonPropertyName("property")]
    public string? Property { get; set; }

    /// <summary>The base58 developer-lawyer REGISTRY PDA (provenance; the removal does not key on it).</summary>
    [JsonPropertyName("developer_lawyer_registry")]
    public string? DeveloperLawyerRegistry { get; set; }

    /// <summary>The base58 SPV-lawyer REGISTRY PDA (provenance; the removal does not key on it).</summary>
    [JsonPropertyName("spv_lawyer_registry")]
    public string? SpvLawyerRegistry { get; set; }

    /// <summary>The slot the deal executed in.</summary>
    [JsonPropertyName("slot")]
    public long Slot { get; set; }

    /// <summary>The base58 transaction signature.</summary>
    [JsonPropertyName("tx_signature")]
    public string? TxSignature { get; set; }

    /// <summary>The block time, ISO-8601.</summary>
    [JsonPropertyName("block_time")]
    public DateTime? BlockTime { get; set; }

    /// <summary>The program that produced the event, e.g. <c>marketplace</c>.</summary>
    [JsonPropertyName("program")]
    public string? Program { get; set; }
}

/// <summary>
/// The <c>letting_agent_appointed</c> webhook payload the indexer POSTs verbatim (the property
/// program's <c>finalize_agent_election</c>). Field names are the indexer's snake_case wire names.
/// </summary>
public sealed class LettingAgentAppointedPayload
{
    /// <summary>Always <c>letting_agent_appointed</c>.</summary>
    [JsonPropertyName("event")]
    public string? Event { get; set; }

    /// <summary>
    /// The on-chain asset id. Whether this equals the marketplace listing id (the namespace's
    /// PropertyId) is unverified — see docs/property-webhooks-uncertainties.md entry 4; the
    /// lookup falls back to the PropertyAsset PDA.
    /// </summary>
    [JsonPropertyName("asset_id")]
    public long? AssetId { get; set; }

    /// <summary>The base58 PropertyLetting PDA.</summary>
    [JsonPropertyName("letting")]
    public string? Letting { get; set; }

    /// <summary>The base58 PropertyAsset PDA (the namespace's creator).</summary>
    [JsonPropertyName("property")]
    public string? Property { get; set; }

    /// <summary>
    /// The base58 LettingAgent ENTRY PDA of the winner — NOT the agent's wallet; resolving it is
    /// placeholder work (docs/property-webhooks-uncertainties.md entry 4). Null when the
    /// instruction omitted the optional account.
    /// </summary>
    [JsonPropertyName("winner_entry")]
    public string? WinnerEntry { get; set; }

    /// <summary>The slot the election finalized in.</summary>
    [JsonPropertyName("slot")]
    public long Slot { get; set; }

    /// <summary>The base58 transaction signature.</summary>
    [JsonPropertyName("tx_signature")]
    public string? TxSignature { get; set; }

    /// <summary>The block time, ISO-8601.</summary>
    [JsonPropertyName("block_time")]
    public DateTime? BlockTime { get; set; }

    /// <summary>The program that produced the event, e.g. <c>property</c>.</summary>
    [JsonPropertyName("program")]
    public string? Program { get; set; }
}

/// <summary>
/// The <c>property_shares_transferred</c> webhook payload the indexer POSTs verbatim (one of
/// <c>buy_relisted_shares</c>, <c>accept_offer</c>, <c>send_property_shares</c>). Field names are
/// the indexer's snake_case wire names.
/// </summary>
public sealed class SharesTransferredPayload
{
    /// <summary>Always <c>property_shares_transferred</c>.</summary>
    [JsonPropertyName("event")]
    public string? Event { get; set; }

    /// <summary><c>relisted_purchase</c>, <c>offer_accepted</c>, or <c>direct_send</c>.</summary>
    [JsonPropertyName("kind")]
    public string? Kind { get; set; }

    /// <summary>The base58 PropertyAsset PDA (the namespace's creator).</summary>
    [JsonPropertyName("property")]
    public string? Property { get; set; }

    /// <summary>The base58 Listing PDA.</summary>
    [JsonPropertyName("listing")]
    public string? Listing { get; set; }

    /// <summary>The base58 wallet of the seller/sender (loses read access).</summary>
    [JsonPropertyName("from")]
    public string? From { get; set; }

    /// <summary>The base58 wallet of the buyer/receiver (gains read access).</summary>
    [JsonPropertyName("to")]
    public string? To { get; set; }

    /// <summary>
    /// The transferred amount — null for <c>offer_accepted</c>, where the amount lives in the
    /// on-chain Offer account and the pure indexer mapper cannot read it.
    /// </summary>
    [JsonPropertyName("amount")]
    public long? Amount { get; set; }

    /// <summary>The on-chain asset id — null for <c>offer_accepted</c>.</summary>
    [JsonPropertyName("asset_id")]
    public long? AssetId { get; set; }

    /// <summary>The base58 Offer PDA — set only for <c>offer_accepted</c>, else null.</summary>
    [JsonPropertyName("offer")]
    public string? Offer { get; set; }

    /// <summary>The slot the transfer landed in.</summary>
    [JsonPropertyName("slot")]
    public long Slot { get; set; }

    /// <summary>The base58 transaction signature.</summary>
    [JsonPropertyName("tx_signature")]
    public string? TxSignature { get; set; }

    /// <summary>The block time, ISO-8601.</summary>
    [JsonPropertyName("block_time")]
    public DateTime? BlockTime { get; set; }

    /// <summary>The program that produced the event, e.g. <c>marketplace</c>.</summary>
    [JsonPropertyName("program")]
    public string? Program { get; set; }
}
