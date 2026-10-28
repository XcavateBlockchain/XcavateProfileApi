using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using XcavateBuckets.Domain;
using XcavateBuckets.Domain.Data;
using XcavateBuckets.Domain.Entities;
using XcavateBuckets.Domain.Services;
using XcavateProfileApi.Data;
using XcavateProfileApi.Services;
using XcavateProfileApi.Services.Indexer;

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
/// lawyer-election / agent-appointment / transfer webhooks manage its membership, and the
/// deal-executed webhook removes the lawyers when the sale settles.
///
/// A second group — the voting webhooks — announces on-chain governance votes as messages in the
/// property's legal-process bucket, so the messenger clients can render vote state without an
/// indexer round-trip: terms-vote open/finalize (marketplace, pre-settlement), letting-agent
/// election open, agent spending-proposal create/finalize and challenge create/finalize
/// (property program, post-settlement). Messages are plaintext JSON (the server holds no bucket
/// X25519 key) posted under a synthetic system identity — see the group remark below.
/// </summary>
[ApiController]
[Route("webhooks")]
public class WebhooksController : ControllerBase
{
    private readonly BucketDbContext _db;
    private readonly ProfileDbContext _profileDb;
    private readonly ViewerKeyResolver _viewerKeys;
    private readonly NamespaceService _namespaces;
    private readonly BucketService _buckets;
    private readonly MembershipService _memberships;
    private readonly MessageService _messages;
    private readonly IIndexerClient _indexer;
    private readonly IS3Service _s3;
    private readonly IConfiguration _config;
    private readonly ILogger<WebhooksController> _logger;

    public WebhooksController(
        BucketDbContext db,
        ProfileDbContext profileDb,
        ViewerKeyResolver viewerKeys,
        NamespaceService namespaces,
        BucketService buckets,
        MembershipService memberships,
        MessageService messages,
        IIndexerClient indexer,
        IS3Service s3,
        IConfiguration config,
        ILogger<WebhooksController> logger)
    {
        _db = db;
        _profileDb = profileDb;
        _viewerKeys = viewerKeys;
        _namespaces = namespaces;
        _buckets = buckets;
        _memberships = memberships;
        _messages = messages;
        _indexer = indexer;
        _s3 = s3;
        _config = config;
        _logger = logger;
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
            new("asset_id", payload.AssetId?.ToString(CultureInfo.InvariantCulture) ?? string.Empty),
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

    /// <summary>
    /// Receives the <c>offers_namespace_created</c> event (a property became eligible for
    /// secondary-market offers through <c>claim_shares</c> or <c>relist_shares</c>) and creates
    /// the property's offers namespace named "&lt;property&gt; offers". Idempotent per
    /// (asset_id, cluster), so redeliveries and re-listings do not create duplicates.
    /// </summary>
    /// <remarks>
    /// Gated by <c>INDEXER_WEBHOOK_SECRET</c> because it is a write endpoint called by the
    /// indexer, not a wallet-signed GraphQL mutation. The property name and asset id are resolved
    /// from the property namespace created by <c>init_property_asset_devnet</c>; a missing
    /// property namespace answers 404 so the indexer retry loop redelivers. Idempotent via both
    /// the namespace (asset_id, cluster) lookup and the <c>ProcessedWebhookEvents</c> dedupe row.
    /// </remarks>
    // POST: webhooks/offers_namespace_created_devnet
    [HttpPost("offers_namespace_created_devnet")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    public async Task<IActionResult> OffersNamespaceCreatedDevnet(
        [FromBody] OffersNamespaceCreatedPayload payload, CancellationToken ct)
    {
        if (CheckWebhookSecret() is { } rejected)
        {
            return rejected;
        }

        if (payload is null
            || !string.Equals(payload.Event, OffersNamespaceCreatedEventName, StringComparison.Ordinal)
            || (payload.ListingId is null && string.IsNullOrWhiteSpace(payload.Property))
            || string.IsNullOrWhiteSpace(payload.Cluster)
            || string.IsNullOrWhiteSpace(payload.TxSignature))
        {
            return BadRequest("The body is not a valid 'offers_namespace_created' webhook payload.");
        }

        var eventKey = OffersNamespaceCreatedEventKey(payload);
        if (await _profileDb.ProcessedWebhookEvents.AnyAsync(e => e.EventKey == eventKey, ct))
        {
            return Ok(new { status = "duplicate", reason = "this event was already accepted" });
        }

        var ns = await FindNamespaceAsync(payload.ListingId, payload.Property, ct);
        if (ns is null || string.IsNullOrWhiteSpace(ns.Creator) || string.IsNullOrWhiteSpace(ns.Name))
        {
            return NotFound("No webhook-created property namespace matches this listing yet.");
        }

        var assetId = OffersNamespaceService.ReadAssetId(ns.Properties)
            ?? payload.AssetId?.ToString(CultureInfo.InvariantCulture);

        if (string.IsNullOrWhiteSpace(assetId))
        {
            return BadRequest("The property namespace has no asset_id and the payload provides none.");
        }

        var (offersNs, existed) = await OffersNamespaceService.FindOrCreateOffersNamespaceAsync(
            ns.Creator,
            ns.Name,
            assetId,
            payload.Cluster,
            _db,
            _namespaces,
            ct);

        _profileDb.ProcessedWebhookEvents.Add(new ProcessedWebhookEvent
        {
            EventKey = eventKey,
            EventType = OffersNamespaceCreatedEventName,
            TxSignature = payload.TxSignature ?? string.Empty,
            ProcessedAt = DateTime.UtcNow
        });

        await _profileDb.SaveChangesAsync(ct);

        return Ok(existed
            ? new { status = "ignored", reason = "offers namespace already exists for this asset", namespaceId = offersNs.NamespaceId }
            : new { status = "created", namespaceId = offersNs.NamespaceId });
    }

    // ---- Property lifecycle webhooks (the "Property namespace & buckets" flow) ----------------
    //
    // The five events that continue the flow the asset webhook starts:
    //
    //   property_claim_started       -> create the property's legal-process bucket (implemented;
    //                                   also reconciles the current holder set into its viewers)
    //   spv_lawyer_elected           -> lawyer write + holder read access (implemented: the vote
    //                                   result message, then the membership grants)
    //   deal_executed                -> remove the lawyers from the bucket (implemented)
    //   letting_agent_appointed      -> add the letting agent (write) (implemented)
    //   property_shares_transferred  -> swap seller -> buyer read access (implemented: wallets
    //                                   resolve to viewer X25519 keys through the profiles)
    //   property_holder_changed      -> primary-market holder joins/leaves (implemented; sale-phase
    //                                   events defer to the claim-started reconciliation)
    //
    // The design decisions behind them are recorded in
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
    /// The current holder set (reservers and buyers, resolved from the indexer's mirror) is then
    /// seeded as viewers — the reconciliation that makes the sale phase's deferred holder events
    /// lossless; an indexer failure answers 502 and the whole event (bucket included) retries.
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

        // Seed the bucket's readers from the CURRENT holder set (the indexer's mirror):
        // holder events during the sale phase answered "deferred" because the bucket did not
        // exist yet — this reconciliation is what makes those deferrals lossless. It runs inside
        // the transaction, so an indexer failure (502 via ResolveAsync) rolls the bucket back
        // with it and the event retries whole. Wallets without a profile X25519 key are skipped
        // (counted), never retried: they appear on their next qualifying holder event.
        var holdersAdded = 0;
        var holdersSkipped = 0;
        if (payload.ListingId is { } reconcileListingId)
        {
            var resolve = await ResolveAsync(
                ct, async c => await _indexer.GetCurrentHolderWalletsAsync(reconcileListingId, c));
            if (resolve.Failed)
            {
                return resolve.Failure!;
            }

            foreach (var wallet in resolve.Value!)
            {
                var key = await ResolveViewerKeyAsync(wallet, ct);
                if (key is null)
                {
                    holdersSkipped++;
                    continue;
                }

                await _memberships.AddViewerAsync(ns.Creator, ns.NamespaceId, bucket.BucketId, key, ct);
                holdersAdded++;
            }
        }
        await transaction.CommitAsync(ct);

        return Ok(new
        {
            status = "created",
            bucketId = bucket.BucketId,
            holdersAdded,
            holdersSkippedNoKey = holdersSkipped
        });
    }

    /// <summary>
    /// Receives the <c>spv_lawyer_elected</c> event (the SPV lawyer election finalized) and posts
    /// a <c>voting/result</c> message: "ratified" when the listing's row shows an assigned SPV
    /// lawyer, "rejected" when the round failed open (the seat stays empty and a new round may
    /// open). The payload's <c>winner_registry</c> is the winner's on-chain Lawyer REGISTRY PDA,
    /// not their wallet — the wallet is the listing row's <c>spvLawyer</c>, which the chain's
    /// <c>finalize_spv_election</c> sets to the winning candidacy's wallet, and the indexer
    /// commits the event and the listing row in one transaction, so the announcement and the
    /// grant below never disagree.
    /// </summary>
    /// <remarks>
    /// When the election ratified, the handler additionally grants access on the property's legal
    /// bucket, atomically in one transaction: the winning lawyer becomes a contributor (WRITE)
    /// and the current token holders are re-synced as viewers (READ) — the same reconciliation
    /// the claim-started webhook runs at bucket creation, so a holder who registered their X25519
    /// key only after their last holder event is picked up here (wallets without a key are
    /// skipped and counted, never retried). Idempotent: the message duplicate-guard plus the
    /// membership services' return-existing semantics make a redelivery a harmless re-run, and
    /// the counters report what THIS delivery changed. A resolution failure (indexer down, the
    /// listing row missing, or the holder enumeration failing) answers 502 so the indexer
    /// retries — a failure mid-grant rolls the transaction back, and the redelivery completes it
    /// with the already-committed message skipped by the guard. 404 while the namespace or bucket
    /// is missing; 400 when the body is not a valid <c>spv_lawyer_elected</c> event.
    /// </remarks>
    // POST: webhooks/spv_lawyer_elected_devnet
    [HttpPost("spv_lawyer_elected_devnet")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status502BadGateway)]
    public async Task<IActionResult> SpvLawyerElectedDevnet(
        [FromBody] SpvLawyerElectedPayload payload, CancellationToken ct)
    {
        if (payload is null
            || !string.Equals(payload.Event, LawyerElectedEventName, StringComparison.Ordinal)
            || payload.ListingId is null)
        {
            return BadRequest("The body is not a valid 'spv_lawyer_elected' webhook payload.");
        }

        var resolve = await ResolveAsync(
            ct, async c => await _indexer.GetListingSpvElectionAsync(payload.ListingId.Value, c));
        if (resolve.Failed)
        {
            return resolve.Failure!;
        }
        var election = resolve.Value!;
        var ratified = election.SpvLawyer is { Length: > 0 } lawyer
                    && lawyer != SystemDefaultPubkey;
        var status = ratified ? "ratified" : "rejected";

        var result = await PostVotingMessageAsync(
            payload.ListingId, payload.Property,
            tag: VotingResultTag,
            reference: $"voting/result/terms/{payload.ListingId}/round/{election.Round}",
            description: $"SPV lawyer election round {election.Round} {status}",
            buildBody: listingId => new JsonObject
            {
                ["kind"] = "terms",
                ["listingId"] = listingId,
                ["round"] = election.Round,
                ["status"] = status
            },
            ct);

        // A rejected round grants no access, and a non-Ok answer (the 404 retry paths) means the
        // bucket is not there to grant on — either way there is no membership work to do.
        if (!ratified || result is not OkObjectResult)
        {
            return WithMembershipCounts(result, lawyerContributorAdded: false, 0, 0);
        }

        // The Ok answer implies the namespace and legal bucket both exist (the post 404s
        // otherwise); re-fetch them as the grant's subjects.
        var ns = await FindNamespaceAsync(payload.ListingId, payload.Property, ct);
        if (ns is null || string.IsNullOrWhiteSpace(ns.Creator))
        {
            return WithMembershipCounts(result, lawyerContributorAdded: false, 0, 0);
        }

        var bucket = await FindLegalBucketAsync(ns.NamespaceId, ct);
        if (bucket is null)
        {
            return WithMembershipCounts(result, lawyerContributorAdded: false, 0, 0);
        }

        await using var transaction = await _db.Database.BeginTransactionAsync(ct);
        // A bucket created before the claim webhook existed may lack the operator admin row the
        // membership caller checks need; adding it is idempotent.
        await _memberships.AddAdminAsync(ns.Creator, ns.NamespaceId, bucket.BucketId, ns.Creator, ct);

        // The lawyer gets WRITE. The add itself is idempotent (the service returns the existing
        // row), so the "added" flag is computed beforehand: a redelivery reports the grant as
        // already in place instead of double-counting it.
        var lawyerContributorAdded = !await _db.BucketContributors
            .AnyAsync(c => c.BucketId == bucket.BucketId && c.SubjectId == election.SpvLawyer, ct);
        await _memberships.AddContributorAsync(
            ns.Creator, ns.NamespaceId, bucket.BucketId, election.SpvLawyer, ct);

        // The holders get READ, re-synced from the indexer's CURRENT holder set — the
        // claim-started reconciliation block, with the same semantics: it runs inside the
        // transaction, so an indexer failure answers 502, rolls the lawyer's grant back with it,
        // and the event retries whole.
        var holderResolve = await ResolveAsync(
            ct,
            async c => await _indexer.GetCurrentHolderWalletsAsync(payload.ListingId.Value, c),
            what: "the current holder set");
        if (holderResolve.Failed)
        {
            return holderResolve.Failure!;
        }

        var existingViewers = (await _db.BucketViewers
                .Where(v => v.BucketId == bucket.BucketId)
                .Select(v => v.ViewerId)
                .ToListAsync(ct))
            .ToHashSet(StringComparer.Ordinal);

        var holdersAdded = 0;
        var holdersSkipped = 0;
        foreach (var wallet in holderResolve.Value!)
        {
            var key = await ResolveViewerKeyAsync(wallet, ct);
            if (key is null)
            {
                holdersSkipped++;
                continue;
            }

            await _memberships.AddViewerAsync(ns.Creator, ns.NamespaceId, bucket.BucketId, key, ct);
            if (existingViewers.Add(key))
            {
                holdersAdded++;
            }
        }
        await transaction.CommitAsync(ct);

        return WithMembershipCounts(result, lawyerContributorAdded, holdersAdded, holdersSkipped);
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
    /// finalized) and adds the winning letting agent as a contributor (WRITE) on the property's
    /// legal bucket. The payload's <c>winner_entry</c> is the winner's LettingAgent ENTRY PDA, not
    /// their wallet; the wallet is resolved from the indexer's <c>lettingAgents</c> mirror (the
    /// row whose <c>id</c> is the entry PDA).
    /// </summary>
    /// <remarks>
    /// A payload without <c>winner_entry</c> is a round that seated no agent: acknowledged
    /// (200 <c>ignored</c>) without acting, because the omitted optional account can never appear
    /// on retry. A resolution failure (indexer down, or no row for the entry) answers 502 so the
    /// indexer retries rather than the write-grant being silently skipped. Idempotent: the
    /// contributor-add returns the existing row, so a redelivery answers 200 and changes nothing.
    /// 404 while the namespace or bucket is missing; 400 when the body is not a valid
    /// <c>letting_agent_appointed</c> event.
    /// </remarks>
    // POST: webhooks/letting_agent_appointed_devnet
    [HttpPost("letting_agent_appointed_devnet")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status502BadGateway)]
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

        if (string.IsNullOrWhiteSpace(payload.WinnerEntry))
        {
            return Ok(new
            {
                status = "ignored",
                acted = false,
                reason = "the payload carries no winner_entry — the election seated no agent",
                bucketId = bucket.BucketId
            });
        }

        var resolve = await ResolveAsync(
            ct,
            async c => await _indexer.GetLettingAgentWalletAsync(payload.WinnerEntry, c),
            what: "the letting agent's wallet");
        if (resolve.Failed)
        {
            return resolve.Failure!;
        }
        var agentWallet = resolve.Value!;

        await using var transaction = await _db.Database.BeginTransactionAsync(ct);
        // A bucket created before the claim webhook existed may lack the operator admin row the
        // contributor-add caller check needs; adding it is idempotent.
        await _memberships.AddAdminAsync(ns.Creator, ns.NamespaceId, bucket.BucketId, ns.Creator, ct);
        await _memberships.AddContributorAsync(ns.Creator, ns.NamespaceId, bucket.BucketId, agentWallet, ct);
        await transaction.CommitAsync(ct);

        return Ok(new
        {
            status = "agent_added",
            bucketId = bucket.BucketId,
            agent = agentWallet
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
    /// Receives the <c>property_holder_changed</c> event (the primary market's holder lifecycle:
    /// <c>reserve_shares</c>/<c>buy_property_shares</c> add, <c>unreserve_shares</c>,
    /// <c>release_reservation</c>, <c>close_cancelled_position</c> and the three
    /// <c>withdraw_*</c> exits remove) and mirrors it as bucket read access: the investor's
    /// viewer is added or removed, the wallet resolved to its X25519 key through the profiles
    /// (the same <see cref="ResolveViewerKeyAsync"/> the transfer webhook uses).
    /// </summary>
    /// <remarks>
    /// The legal bucket only exists from claim start, so events from the sale phase arrive long
    /// before it: those answer 200 <c>deferred</c> — NOT 404 — because the claim-started handler
    /// reconciles the whole current holder set from the indexer at bucket-creation time, making
    /// the deferral lossless without a weeks-long retry storm per property. A wallet without a
    /// profile key is skipped, never retried (the investor appears on their next qualifying event
    /// or a later reconciliation). Idempotent: viewer adds return the existing row and removes
    /// are no-ops when absent. 404 only while the namespace is missing; 400 when the body is not
    /// a valid <c>property_holder_changed</c> event.
    /// </remarks>
    // POST: webhooks/property_holder_changed_devnet
    [HttpPost("property_holder_changed_devnet")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> PropertyHolderChangedDevnet(
        [FromBody] PropertyHolderChangedPayload payload, CancellationToken ct)
    {
        if (payload is null
            || !string.Equals(payload.Event, HolderChangedEventName, StringComparison.Ordinal)
            || payload.Action is not ("added" or "removed")
            || string.IsNullOrWhiteSpace(payload.Investor)
            || (payload.ListingId is null && string.IsNullOrWhiteSpace(payload.Property)))
        {
            return BadRequest("The body is not a valid 'property_holder_changed' webhook payload.");
        }

        var ns = await FindNamespaceAsync(payload.ListingId, payload.Property, ct);
        if (ns is null || string.IsNullOrWhiteSpace(ns.Creator))
        {
            return NotFound("No webhook-created namespace matches this listing yet.");
        }

        var bucket = await FindLegalBucketAsync(ns.NamespaceId, ct);
        if (bucket is null)
        {
            return Ok(new
            {
                status = "deferred",
                reason = "the legal bucket does not exist yet; the claim-started reconciliation "
                    + "adds every current holder when it is created"
            });
        }

        var key = await ResolveViewerKeyAsync(payload.Investor, ct);
        if (key is null)
        {
            return Ok(new
            {
                status = "skipped",
                acted = false,
                reason = "no profile X25519 key for the investor wallet"
            });
        }

        await using var transaction = await _db.Database.BeginTransactionAsync(ct);
        // A bucket created before the claim webhook existed may lack the operator admin row the
        // viewer-add caller check needs; adding it is idempotent.
        await _memberships.AddAdminAsync(ns.Creator, ns.NamespaceId, bucket.BucketId, ns.Creator, ct);
        if (payload.Action == "added")
        {
            await _memberships.AddViewerAsync(ns.Creator, ns.NamespaceId, bucket.BucketId, key, ct);
        }
        else
        {
            await _memberships.RemoveViewerAsync(ns.Creator, ns.NamespaceId, bucket.BucketId, key, ct);
        }
        await transaction.CommitAsync(ct);

        return Ok(new
        {
            status = "processed",
            action = payload.Action,
            bucketId = bucket.BucketId
        });
    }

    // ---- Voting webhooks: on-chain governance announced into the legal-process bucket ---------
    //
    // Seven events, two shapes: open/update messages (tagged voting/terms, voting/election,
    // voting/proposal or voting/challenge, status "open") and result messages (tagged
    // voting/result, carrying the outcome and the vote powers). The indexer's webhook payloads
    // carry only instruction-derivable evidence (ids, PDAs, args — its mapper is pure and
    // DB-less), so every handler first RESOLVES the display facts — rounds, expiries, quorums,
    // tallies, the passed/failed outcome — from the indexer's own GraphQL mirror via
    // IIndexerClient. That read is consistent for free: the event row and the account rows it
    // reports commit in one indexer transaction. A resolution failure (indexer unreachable, or the
    // expected row missing) answers 502 so delivery retries with backoff instead of posting a
    // hollow announcement. One deliberate skip: a `proposal_created` whose Proposal PDA never
    // survived its own transaction is an auto-approved (low-tier) spend — no vote ever opens, so
    // nothing is announced.
    //
    // The message body is plaintext JSON — the server holds no bucket X25519 key, and the clients
    // tolerate plaintext voting announcements; the body's content hash is the sha256 of the exact
    // bytes stored, so a client can still verify integrity.
    //
    // Posting runs under a synthetic system identity (VotingSystemIdentity), installed as a
    // bucket contributor by each handler — idempotently, so the deal_executed sweep removing
    // every contributor cannot permanently evict it: the next voting webhook simply re-adds it.
    // Because webhook-created legal buckets start locked (the pallet's Status::default()) and the
    // only admin is the PropertyAsset PDA no wallet can sign for, the handler also unlocks the
    // bucket server-side (ResumeWriting as the namespace creator) with a PLACEHOLDER encryption
    // key before the first post. The domain has no "system writes while locked" bypass, and
    // unlocking grants no capability the lifecycle does not already intend — the legal bucket's
    // contributors are meant to write. The placeholder key means bucket content is NOT
    // end-to-end-encrypted until a real key ceremony runs (which, the operator admin being a PDA,
    // must itself be server-mediated); voting messages are plaintext by design either way.
    //
    // Idempotency: every handler builds its message body deterministically (same event -> same
    // bytes) and skips the post when a message with the same tag and identical body already
    // exists, so an at-least-once redelivery never duplicates a user-visible announcement, while
    // a genuinely new event (a new round, a new proposal id) produces a different body and posts.
    // 404 while the namespace or bucket is missing, so the indexer retries (deliveries across
    // event types are unordered); 400 when the body is not a valid payload for the event.

    /// <summary>
    /// Receives the <c>spv_case_claimed</c> event, creates the candidacy's encrypted lawyer-terms
    /// bucket, and retains the one-per-round <c>voting/terms</c> announcement in the property's
    /// legal-process bucket. Each candidacy gets its own bucket, key ceremony, admin prompt, and
    /// the claiming lawyer's current terms PDF.
    /// </summary>
    // POST: webhooks/spv_case_claimed_devnet
    [HttpPost("spv_case_claimed_devnet")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status502BadGateway)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    public async Task<IActionResult> SpvCaseClaimedDevnet(
        [FromBody] SpvCaseClaimedPayload payload, CancellationToken ct)
    {
        var secretFailure = CheckWebhookSecret();
        if (secretFailure is not null)
        {
            return secretFailure;
        }

        if (payload is null
            || !string.Equals(payload.Event, SpvCaseClaimedEventName, StringComparison.Ordinal)
            || payload.PropertyId is null
            || payload.Round is null
            || string.IsNullOrWhiteSpace(payload.Lawyer)
            || payload.Lawyer.Length > 128
            || string.IsNullOrWhiteSpace(payload.Candidacy)
            || payload.Candidacy.Length > 128)
        {
            return BadRequest("The body is not a valid 'spv_case_claimed' webhook payload.");
        }

        var admin = _config[BucketAdminAddressConfigKey]?.Trim();
        if (string.IsNullOrWhiteSpace(admin))
        {
            return StatusCode(
                StatusCodes.Status503ServiceUnavailable,
                $"The bucket admin address ({BucketAdminAddressConfigKey}) is not configured.");
        }

        var ns = await FindNamespaceAsync(payload.PropertyId, payload.Property, ct);
        if (ns is null || string.IsNullOrWhiteSpace(ns.Creator))
        {
            return NotFound("No webhook-created namespace matches this property yet.");
        }

        var resolve = await ResolveAsync(
            ct, async c => await _indexer.GetListingSpvElectionAsync(payload.PropertyId.Value, c));
        if (resolve.Failed)
        {
            return resolve.Failure!;
        }
        var election = resolve.Value!;

        var setup = await EnsureLawyerTermsBucketAsync(ns, payload, admin, ct);
        if (setup.Failure is not null)
        {
            return setup.Failure;
        }

        var votingResult = await PostVotingMessageAsync(
            payload.PropertyId, payload.Property,
            tag: TermsVotingTag,
            reference: $"voting/terms/{payload.PropertyId}/round/{payload.Round}/open",
            description: $"SPV lawyer election round {payload.Round} opened",
            buildBody: listingId => new JsonObject
            {
                ["kind"] = "terms",
                ["listingId"] = listingId,
                ["round"] = payload.Round.Value,
                ["expiresAt"] = IndexerClient.Iso8601(election.Expiry),
                ["status"] = "open"
            },
            ct);

        return WithLawyerTermsSetup(
            votingResult, setup.BucketId!.Value, setup.Created, setup.HolderCount);
    }

    private async Task<(IActionResult? Failure, long? BucketId, bool Created, int? HolderCount)>
        EnsureLawyerTermsBucketAsync(
            Namespace ns, SpvCaseClaimedPayload payload, string admin, CancellationToken ct)
    {
        var name = LawyerTermsBucketName(ns, payload.Candidacy!);
        var existing = await _db.Buckets.FirstOrDefaultAsync(
            bucket => bucket.NamespaceId == ns.NamespaceId
                && bucket.Category == LawyerTermsBucketCategory
                && bucket.Name == name,
            ct);
        if (existing is not null)
        {
            return (null, existing.BucketId, false, null);
        }

        var resolve = await ResolveAsync(
            ct,
            async c => await _indexer.GetShareHolderWalletsAsync(payload.PropertyId!.Value, c),
            what: "claimed property token holders");
        if (resolve.Failed)
        {
            return (resolve.Failure, null, false, null);
        }

        var holders = resolve.Value!
            .Where(wallet => !string.IsNullOrWhiteSpace(wallet))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(wallet => wallet, StringComparer.Ordinal)
            .ToList();
        var document = await _profileDb.LawyerTermsDocuments.AsNoTracking()
            .FirstOrDefaultAsync(row => row.Ss58Address == payload.Lawyer, ct);
        if (document is null)
        {
            return (NotFound("The claiming lawyer has no terms PDF yet."), null, false, null);
        }

        byte[] pdf;
        try
        {
            pdf = await _s3.DownloadObjectAsync(
                TermsDocumentController.Bucket,
                TermsDocumentController.ObjectKey(payload.Lawyer!),
                ct);
        }
        catch (Amazon.S3.AmazonS3Exception ex)
        {
            return (StatusCode(
                StatusCodes.Status502BadGateway,
                $"Could not download the lawyer's terms PDF: {ex.Message}"), null, false, null);
        }

        var viewers = new List<KeyValuePair<string, string>>();
        foreach (var wallet in holders.Append(payload.Lawyer!).Distinct(StringComparer.Ordinal))
        {
            var key = await ResolveViewerKeyAsync(wallet, ct);
            if (key is null)
            {
                return (NotFound($"No profile X25519 key for required participant '{wallet}'."),
                    null, false, null);
            }
            viewers.Add(new(wallet, key));
        }

        string snapshotUrl;
        try
        {
            using var content = new MemoryStream(pdf, writable: false);
            snapshotUrl = await _s3.UploadImageAsync(
                TermsDocumentController.Bucket,
                TermsDocumentController.SnapshotObjectKey(payload.Candidacy!),
                content,
                document.ContentType);
        }
        catch (Amazon.S3.AmazonS3Exception ex)
        {
            return (StatusCode(
                StatusCodes.Status502BadGateway,
                $"Could not snapshot the lawyer's terms PDF: {ex.Message}"), null, false, null);
        }

        var keySharing = LawyerTermsBucketProtocol.CreateKeySharing(admin, viewers);
        var properties = new List<KeyValuePair<string, string>>
        {
            new("kind", "spv_lawyer_terms"),
            new("listing_id", payload.PropertyId!.Value.ToString(CultureInfo.InvariantCulture)),
            new("round", payload.Round!.Value.ToString(CultureInfo.InvariantCulture)),
            new("lawyer", payload.Lawyer!),
            new("candidacy", payload.Candidacy!),
            new("listing", payload.Listing ?? string.Empty),
            new("property", payload.Property ?? string.Empty),
            new("tx_signature", payload.TxSignature ?? string.Empty),
            new("program", payload.Program ?? string.Empty)
        };

        await using var transaction = await _db.Database.BeginTransactionAsync(ct);
        var bucket = await _buckets.CreateAsync(
            ns.Creator!,
            ns.NamespaceId,
            name,
            LawyerTermsBucketCategory,
            properties,
            ct);
        await _memberships.AddAdminAsync(ns.Creator!, ns.NamespaceId, bucket.BucketId, admin, ct);
        await _memberships.AddContributorAsync(admin, ns.NamespaceId, bucket.BucketId, admin, ct);
        foreach (var viewer in viewers)
        {
            await _memberships.AddContributorAsync(
                admin, ns.NamespaceId, bucket.BucketId, viewer.Key, ct);
            await _memberships.AddViewerAsync(
                admin, ns.NamespaceId, bucket.BucketId, viewer.Value, ct);
        }

        await _buckets.ResumeWritingAsync(
            admin, ns.NamespaceId, bucket.BucketId, keySharing.PublicKeyHex, ct);
        await _messages.WriteAsync(
            admin,
            ns.NamespaceId,
            bucket.BucketId,
            new MessageWriteRequest(
                keySharing.Reference,
                LawyerTermsBucketProtocol.KeySharingTag,
                keySharing.Body,
                "Lawyer terms bucket encryption key",
                LawyerTermsBucketProtocol.KeySharingContentType,
                keySharing.ContentHash,
                null),
            ct);
        await _messages.WriteAsync(
            admin,
            ns.NamespaceId,
            bucket.BucketId,
            new MessageWriteRequest(
                $"lawyer-terms/{payload.Candidacy}/vote-prompt",
                LawyerTermsBucketProtocol.VotePromptTag,
                LawyerTermsBucketProtocol.VotePrompt,
                "Vote on the lawyer's terms",
                "text/plain",
                LawyerTermsBucketProtocol.ContentHash(LawyerTermsBucketProtocol.VotePrompt),
                null),
            ct);
        await _messages.WriteAsync(
            admin,
            ns.NamespaceId,
            bucket.BucketId,
            new MessageWriteRequest(
                snapshotUrl,
                LawyerTermsBucketProtocol.DocumentTag,
                null,
                document.FileName,
                document.ContentType,
                LawyerTermsBucketProtocol.ContentHash(pdf),
                null),
            ct);
        await transaction.CommitAsync(ct);

        return (null, bucket.BucketId, true, holders.Count);
    }

    private static IActionResult WithLawyerTermsSetup(
        IActionResult result, long bucketId, bool created, int? holderCount)
    {
        if (result is not OkObjectResult ok)
        {
            return result;
        }

        var body = JsonSerializer.SerializeToNode(ok.Value)!.AsObject();
        body["lawyerTermsBucketId"] = bucketId;
        body["lawyerTermsBucketCreated"] = created;
        if (holderCount is not null)
        {
            body["lawyerTermsHolderCount"] = holderCount;
        }
        return new OkObjectResult(body);
    }

    private static string LawyerTermsBucketName(Namespace ns, string candidacy)
    {
        var baseName = string.IsNullOrWhiteSpace(ns.Name) ? $"namespace {ns.NamespaceId}" : ns.Name!;
        var suffix = $" - SPV lawyer terms {candidacy}";
        if (baseName.Length + suffix.Length > MaxBucketNameLen)
        {
            baseName = baseName[..(MaxBucketNameLen - suffix.Length)];
        }
        return baseName + suffix;
    }

    /// <summary>
    /// Receives the <c>agent_election_opened</c> event (a letting agent stood for the property's
    /// seat election — the indexer fires it per candidacy) and posts a <c>voting/election</c> open
    /// message with the round plus the expiry resolved from the letting row. The deterministic
    /// body collapses redeliveries AND later candidacies of the same round into one announcement:
    /// same property + same round = same bytes = duplicate-guarded.
    /// </summary>
    // POST: webhooks/agent_election_opened_devnet
    [HttpPost("agent_election_opened_devnet")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status502BadGateway)]
    public async Task<IActionResult> AgentElectionOpenedDevnet(
        [FromBody] AgentElectionOpenedPayload payload, CancellationToken ct)
    {
        if (payload is null
            || !string.Equals(payload.Event, AgentElectionOpenedEventName, StringComparison.Ordinal)
            || payload.PropertyId is null
            || payload.Round is null)
        {
            return BadRequest("The body is not a valid 'agent_election_opened' webhook payload.");
        }

        var resolve = await ResolveAsync(
            ct, async c => await _indexer.GetPropertyLettingAsync(payload.PropertyId.Value, c));
        if (resolve.Failed)
        {
            return resolve.Failure!;
        }
        var letting = resolve.Value!;

        return await PostVotingMessageAsync(
            payload.PropertyId, payload.Property,
            tag: ElectionVotingTag,
            reference: $"voting/election/{payload.PropertyId}/round/{payload.Round}/open",
            description: $"Letting-agent election round {payload.Round} opened",
            buildBody: listingId => new JsonObject
            {
                ["kind"] = "election",
                ["listingId"] = listingId,
                ["round"] = payload.Round.Value,
                ["expiresAt"] = IndexerClient.Iso8601(letting.ElectionExpiry),
                ["quorumBps"] = letting.ElectionQuorumBps,
                ["status"] = "open"
            },
            ct);
    }

    /// <summary>
    /// Receives the <c>proposal_created</c> event (the letting agent opened a spending proposal)
    /// and posts a <c>voting/proposal</c> open message with the proposal id plus the expiry,
    /// quorum and threshold resolved from the indexer. A low-tier spend is auto-approved BY THE
    /// CHAIN in the same transaction, so its Proposal PDA never survives and no vote can ever be
    /// cast — the handler answers 200 "ignored" instead of advertising a phantom vote. The
    /// amount and details hash stay readable through the indexer GraphQL <c>proposals</c> query.
    /// </summary>
    // POST: webhooks/proposal_created_devnet
    [HttpPost("proposal_created_devnet")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status502BadGateway)]
    public async Task<IActionResult> ProposalCreatedDevnet(
        [FromBody] ProposalCreatedPayload payload, CancellationToken ct)
    {
        if (payload is null
            || !string.Equals(payload.Event, ProposalCreatedEventName, StringComparison.Ordinal)
            || payload.PropertyId is null
            || payload.ProposalId is null)
        {
            return BadRequest("The body is not a valid 'proposal_created' webhook payload.");
        }

        var resolve = await ResolveAsync(
            ct,
            async c => await _indexer.GetOpenProposalAsync(
                payload.PropertyId.Value, payload.ProposalId.Value, c),
            allowMissing: true);
        if (resolve.Failed)
        {
            return resolve.Failure!;
        }
        if (resolve.Value is null)
        {
            return Ok(new
            {
                status = "ignored",
                reason = "auto-approved proposal leaves no vote open on-chain"
            });
        }
        var proposal = resolve.Value;

        return await PostVotingMessageAsync(
            payload.PropertyId, payload.Letting,
            tag: ProposalVotingTag,
            reference: $"voting/proposal/{payload.PropertyId}/{payload.ProposalId}/open",
            description: $"Spending proposal {payload.ProposalId} opened",
            buildBody: listingId => new JsonObject
            {
                ["kind"] = "proposal",
                ["listingId"] = listingId,
                ["id"] = payload.ProposalId.Value,
                ["expiresAt"] = IndexerClient.Iso8601(proposal.Expiry),
                ["quorumBps"] = proposal.QuorumBps,
                ["thresholdBps"] = proposal.ThresholdBps,
                ["status"] = "open"
            },
            ct);
    }

    /// <summary>
    /// Receives the <c>proposal_finalized</c> event (a spending proposal's vote closed and its
    /// PDA closed) and posts a <c>voting/result</c> message. The outcome is derived from the
    /// frozen tallies against the snapshotted quorum/threshold and the share supply — the same
    /// rule the chain applied — since the payload carries only the closed PDA's key.
    /// </summary>
    // POST: webhooks/proposal_finalized_devnet
    [HttpPost("proposal_finalized_devnet")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status502BadGateway)]
    public async Task<IActionResult> ProposalFinalizedDevnet(
        [FromBody] ProposalFinalizedPayload payload, CancellationToken ct)
    {
        if (payload is null
            || !string.Equals(payload.Event, ProposalFinalizedEventName, StringComparison.Ordinal)
            || payload.PropertyId is null
            || string.IsNullOrWhiteSpace(payload.Proposal))
        {
            return BadRequest("The body is not a valid 'proposal_finalized' webhook payload.");
        }

        var resolve = await ResolveAsync(
            ct,
            async c =>
            {
                var proposal = await _indexer.GetFinalizedProposalAsync(
                    payload.PropertyId.Value, payload.Proposal, c);
                var shareAmount = await _indexer.GetShareAmountAsync(payload.PropertyId.Value, c);
                return proposal is null || shareAmount is null
                    ? null
                    : new VoteAndSupply<ProposalResultInfo>(proposal, shareAmount.Value);
            });
        if (resolve.Failed)
        {
            return resolve.Failure!;
        }
        var (proposal, shareAmount) = (resolve.Value!.Vote, resolve.Value.ShareAmount);
        var status = IndexerClient.VotePassed(
            proposal.TallyYes, proposal.TallyNo, proposal.TallyAbstain,
            proposal.QuorumBps, proposal.ThresholdBps, shareAmount)
            ? "passed"
            : "failed";

        return await PostVotingMessageAsync(
            payload.PropertyId, payload.Property,
            tag: VotingResultTag,
            reference: $"voting/result/proposal/{payload.PropertyId}/{proposal.ProposalId}",
            description: $"Spending proposal {proposal.ProposalId} {status}",
            buildBody: listingId => new JsonObject
            {
                ["kind"] = "proposal",
                ["listingId"] = listingId,
                ["id"] = proposal.ProposalId,
                ["status"] = status,
                ["yes"] = proposal.TallyYes,
                ["no"] = proposal.TallyNo,
                ["abstain"] = proposal.TallyAbstain
            },
            ct);
    }

    /// <summary>
    /// Receives the <c>challenge_created</c> event (a token holder challenged the letting agent)
    /// and posts a <c>voting/challenge</c> open message with the challenge id plus the expiry and
    /// quorum resolved from the indexer.
    /// </summary>
    // POST: webhooks/challenge_created_devnet
    [HttpPost("challenge_created_devnet")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status502BadGateway)]
    public async Task<IActionResult> ChallengeCreatedDevnet(
        [FromBody] ChallengeCreatedPayload payload, CancellationToken ct)
    {
        if (payload is null
            || !string.Equals(payload.Event, ChallengeCreatedEventName, StringComparison.Ordinal)
            || payload.PropertyId is null
            || payload.ChallengeId is null)
        {
            return BadRequest("The body is not a valid 'challenge_created' webhook payload.");
        }

        var resolve = await ResolveAsync(
            ct, async c => await _indexer.GetOpenChallengeAsync(
                payload.PropertyId.Value, payload.ChallengeId.Value, c));
        if (resolve.Failed)
        {
            return resolve.Failure!;
        }
        var challenge = resolve.Value!;

        return await PostVotingMessageAsync(
            payload.PropertyId, payload.Letting,
            tag: ChallengeVotingTag,
            reference: $"voting/challenge/{payload.PropertyId}/{payload.ChallengeId}/open",
            description: $"Agent challenge {payload.ChallengeId} opened",
            buildBody: listingId => new JsonObject
            {
                ["kind"] = "challenge",
                ["listingId"] = listingId,
                ["id"] = payload.ChallengeId.Value,
                ["expiresAt"] = IndexerClient.Iso8601(challenge.Expiry),
                ["quorumBps"] = challenge.QuorumBps,
                ["status"] = "open"
            },
            ct);
    }

    /// <summary>
    /// Receives the <c>challenge_finalized</c> event (a challenge's vote closed and its PDA
    /// closed) and posts a <c>voting/result</c> message with the outcome — derived from the
    /// frozen tallies like the proposal path — and the yes/no/abstain powers. A passed challenge
    /// at the third strike also removes the agent on-chain; the seat's current state stays
    /// readable through the indexer GraphQL <c>propertyLettings</c> query.
    /// </summary>
    // POST: webhooks/challenge_finalized_devnet
    [HttpPost("challenge_finalized_devnet")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status502BadGateway)]
    public async Task<IActionResult> ChallengeFinalizedDevnet(
        [FromBody] ChallengeFinalizedPayload payload, CancellationToken ct)
    {
        if (payload is null
            || !string.Equals(payload.Event, ChallengeFinalizedEventName, StringComparison.Ordinal)
            || payload.PropertyId is null
            || string.IsNullOrWhiteSpace(payload.Challenge))
        {
            return BadRequest("The body is not a valid 'challenge_finalized' webhook payload.");
        }

        var resolve = await ResolveAsync(
            ct,
            async c =>
            {
                var challenge = await _indexer.GetFinalizedChallengeAsync(
                    payload.PropertyId.Value, payload.Challenge, c);
                var shareAmount = await _indexer.GetShareAmountAsync(payload.PropertyId.Value, c);
                return challenge is null || shareAmount is null
                    ? null
                    : new VoteAndSupply<ChallengeResultInfo>(challenge, shareAmount.Value);
            });
        if (resolve.Failed)
        {
            return resolve.Failure!;
        }
        var (challenge, shareAmount) = (resolve.Value!.Vote, resolve.Value.ShareAmount);
        var status = IndexerClient.VotePassed(
            challenge.TallyYes, challenge.TallyNo, challenge.TallyAbstain,
            challenge.QuorumBps, null, shareAmount)
            ? "passed"
            : "failed";

        return await PostVotingMessageAsync(
            payload.PropertyId, payload.Property,
            tag: VotingResultTag,
            reference: $"voting/result/challenge/{payload.PropertyId}/{challenge.ChallengeId}",
            description: $"Agent challenge {challenge.ChallengeId} {status}",
            buildBody: listingId => new JsonObject
            {
                ["kind"] = "challenge",
                ["listingId"] = listingId,
                ["id"] = challenge.ChallengeId,
                ["status"] = status,
                ["yes"] = challenge.TallyYes,
                ["no"] = challenge.TallyNo,
                ["abstain"] = challenge.TallyAbstain
            },
            ct);
    }

    /// <summary>A finalized vote's frozen row plus the share supply its quorum is measured against.</summary>
    private sealed record VoteAndSupply<T>(T Vote, long ShareAmount) where T : class;

    /// <summary>
    /// Runs one enrichment query, translating its outcomes into the webhook vocabulary: a thrown
    /// <see cref="IndexerQueryException"/> (indexer down/erroring) or a missing expected row both
    /// become a 502, so the indexer's at-least-once delivery retries with backoff until the mirror
    /// can answer. <paramref name="allowMissing"/> marks the lookups where absence is a real state
    /// (the auto-approved proposal) rather than a retryable gap. <paramref name="what"/> names the
    /// thing being resolved in the 502 body (the vote state, a wallet, the holder set).
    /// </summary>
    private async Task<(bool Failed, IActionResult? Failure, T? Value)> ResolveAsync<T>(
        CancellationToken ct, Func<CancellationToken, Task<T?>> query, bool allowMissing = false,
        string what = "vote state")
    {
        T? value;
        try
        {
            value = await query(ct);
        }
        catch (IndexerQueryException ex)
        {
            return (true,
                StatusCode(StatusCodes.Status502BadGateway, $"Could not resolve {what}: {ex.Message}"),
                default);
        }

        if (value is null && !allowMissing)
        {
            return (true,
                StatusCode(StatusCodes.Status502BadGateway,
                    $"Could not resolve {what}: the indexer has no row for it yet."),
                default);
        }

        return (false, null, value);
    }

    /// <summary>
    /// Adds the membership-grant counters (<c>lawyerContributorAdded</c>, <c>holdersAdded</c>,
    /// <c>holdersSkippedNoKey</c>) to a voting handler's Ok body, so the lawyer-election response
    /// reports the announcement and the grants together. A non-Ok answer (the 404 retry paths)
    /// passes through untouched.
    /// </summary>
    private static IActionResult WithMembershipCounts(
        IActionResult result, bool lawyerContributorAdded, int holdersAdded, int holdersSkippedNoKey)
    {
        if (result is not OkObjectResult ok)
        {
            return result;
        }

        var body = JsonSerializer.SerializeToNode(ok.Value)!.AsObject();
        body["lawyerContributorAdded"] = lawyerContributorAdded;
        body["holdersAdded"] = holdersAdded;
        body["holdersSkippedNoKey"] = holdersSkippedNoKey;
        return new OkObjectResult(body);
    }

    /// <summary>
    /// The shared flow behind every voting webhook: resolve the property's namespace and legal
    /// bucket (404 while either is missing), skip an exact duplicate (idempotent redelivery), then
    /// post the message under the system identity — installing the operator admin and the system
    /// contributor and unlocking the bucket first, all idempotently, inside one transaction.
    /// <paramref name="buildBody"/> receives the numeric listing id (the payload's
    /// <c>property_id</c>, or the namespace's as a fallback) and returns the deterministic
    /// plaintext JSON body.
    /// </summary>
    private async Task<IActionResult> PostVotingMessageAsync(
        long? propertyId,
        string? propertyPda,
        string tag,
        string reference,
        string description,
        Func<long, JsonObject> buildBody,
        CancellationToken ct)
    {
        var ns = await FindNamespaceAsync(propertyId, propertyPda, ct);
        if (ns is null || string.IsNullOrWhiteSpace(ns.Creator))
        {
            return NotFound("No webhook-created namespace matches this property yet.");
        }

        var bucket = await FindLegalBucketAsync(ns.NamespaceId, ct);
        if (bucket is null)
        {
            return NotFound("The property's legal bucket does not exist yet.");
        }

        var listingId = propertyId ?? ns.PropertyId;
        if (listingId is null)
        {
            return BadRequest("Neither the payload nor the namespace carries a numeric property id.");
        }

        var body = buildBody(listingId.Value).ToJsonString();

        // Redelivery guard: the same event rebuilds the same body bytes, so an identical tagged
        // message already in the bucket means this exact announcement was posted before.
        var duplicate = await _db.Messages
            .AnyAsync(m => m.BucketId == bucket.BucketId && m.Tag == tag && m.IpfsContent == body, ct);
        if (duplicate)
        {
            return Ok(new
            {
                status = "ignored",
                reason = "an identical voting message was already posted",
                bucketId = bucket.BucketId
            });
        }

        await using var transaction = await _db.Database.BeginTransactionAsync(ct);
        // A bucket created before the claim webhook existed may lack the operator admin row the
        // contributor-add and resume-writing caller checks need; adding it is idempotent.
        await _memberships.AddAdminAsync(ns.Creator, ns.NamespaceId, bucket.BucketId, ns.Creator, ct);
        // The system poster. Idempotent too, which doubles as the guard against the deal_executed
        // sweep: if it removed every contributor, the next voting webhook re-adds this one.
        await _memberships.AddContributorAsync(
            ns.Creator, ns.NamespaceId, bucket.BucketId, VotingSystemIdentity, ct);
        if (!bucket.IsWritable)
        {
            // See the group remark: the only admin is the PropertyAsset PDA, so unlocking can only
            // ever happen server-side. The placeholder key is no E2E key — voting messages are
            // plaintext by design.
            await _buckets.ResumeWritingAsync(
                ns.Creator, ns.NamespaceId, bucket.BucketId, PlaceholderEncryptionKey, ct);
        }

        var message = await _messages.WriteAsync(
            VotingSystemIdentity,
            ns.NamespaceId,
            bucket.BucketId,
            new MessageWriteRequest(
                Reference: reference,
                Tag: tag,
                IpfsContent: body,
                Description: description,
                ContentType: VotingContentType,
                ContentHash: VotingContentHash(body),
                Properties: null),
            ct);
        await transaction.CommitAsync(ct);

        return Ok(new { status = "posted", bucketId = bucket.BucketId, messageId = message.MessageId });
    }

    /// <summary>
    /// The message content hash: sha256 over the exact body bytes stored, as 0x-prefixed uppercase
    /// hex — the house hex style (CryptoHelper.HashHex).
    /// </summary>
    private static string VotingContentHash(string body) =>
        "0x" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(body)));

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
    /// usable one. Delegates to the shared <see cref="ViewerKeyResolver"/>: the profile keyed
    /// directly by the wallet first, then the <c>WalletMigrations</c> fallback, and a stored key
    /// that is not well-formed 32-byte hex never reaches
    /// <c>MembershipService.AddViewerAsync</c> — it would be an <c>InvalidInput</c> failure there
    /// and, worse, an endless retry of a permanently-unactionable event.
    /// </summary>
    private Task<string?> ResolveViewerKeyAsync(string wallet, CancellationToken ct) =>
        _viewerKeys.ResolveAsync(wallet, ct);

    /// <summary>The bucket name, after the namespace's, clamped to the domain's 256-char bound.</summary>
    private static string LegalBucketName(Namespace ns)
    {
        var baseName = string.IsNullOrWhiteSpace(ns.Name) ? $"namespace {ns.NamespaceId}" : ns.Name!;
        var name = $"{baseName} - legal process";
        return name.Length <= MaxBucketNameLen ? name : name[..MaxBucketNameLen];
    }

    /// <summary>
    /// The shared-secret gate: null when the request may proceed, else the rejection result.
    /// Same model as <see cref="NotificationWebhooksController"/> — the secret is read from
    /// <c>INDEXER_WEBHOOK_SECRET</c>, compared in constant time, and accepted from the
    /// <c>X-Webhook-Secret</c> header or the <c>secret</c> query parameter.
    /// </summary>
    private IActionResult? CheckWebhookSecret()
    {
        var secret = _config[WebhookSecretConfigKey];
        if (string.IsNullOrWhiteSpace(secret))
        {
            return StatusCode(
                StatusCodes.Status503ServiceUnavailable,
                $"The indexer webhook secret ({WebhookSecretConfigKey}) is not configured — "
                + "this webhook is disabled.");
        }

        var presented = Request.Headers[WebhookSecretHeader].FirstOrDefault()
            ?? Request.Query[WebhookSecretQueryKey].FirstOrDefault();
        if (string.IsNullOrEmpty(presented)
            || presented.Length != secret.Length
            || !CryptographicOperations.FixedTimeEquals(
                Encoding.UTF8.GetBytes(presented), Encoding.UTF8.GetBytes(secret)))
        {
            return Unauthorized("Missing or wrong webhook secret.");
        }

        return null;
    }

    private static string OffersNamespaceCreatedEventKey(OffersNamespaceCreatedPayload payload)
    {
        var id = payload.ListingId?.ToString(CultureInfo.InvariantCulture)
            ?? payload.Property
            ?? payload.Listing
            ?? string.Empty;
        return $"{OffersNamespaceCreatedEventName}:{id}:{payload.TxSignature ?? string.Empty}";
    }

    private const string EventName = "property_asset_registered";
    private const string ClaimStartedEventName = "property_claim_started";
    private const string LawyerElectedEventName = "spv_lawyer_elected";
    private const string DealExecutedEventName = "deal_executed";
    private const string AgentAppointedEventName = "letting_agent_appointed";
    private const string SharesTransferredEventName = "property_shares_transferred";
    private const string SpvCaseClaimedEventName = "spv_case_claimed";
    private const string AgentElectionOpenedEventName = "agent_election_opened";
    private const string ProposalCreatedEventName = "proposal_created";
    private const string ProposalFinalizedEventName = "proposal_finalized";
    private const string ChallengeCreatedEventName = "challenge_created";
    private const string ChallengeFinalizedEventName = "challenge_finalized";
    private const string HolderChangedEventName = "property_holder_changed";
    private const string OffersNamespaceCreatedEventName = "offers_namespace_created";

    /// <summary>The env var holding the shared secret the offers namespace webhook requires.</summary>
    private const string WebhookSecretConfigKey = "INDEXER_WEBHOOK_SECRET";

    private const string BucketAdminAddressConfigKey = "BUCKET_ADMIN_ADDRESS";

    private const string WebhookSecretHeader = "X-Webhook-Secret";
    private const string WebhookSecretQueryKey = "secret";

    /// <summary>The all-zero pubkey: what an unassigned <c>LawyerAssignment.lawyer</c> reads as.</summary>
    private const string SystemDefaultPubkey = "11111111111111111111111111111111";

    /// <summary>The bucket category marking a property's legal-process bucket.</summary>
    private const string LegalBucketCategory = "legal-process";

    private const string LawyerTermsBucketCategory = "spv-lawyer-terms";

    /// <summary>The tags the voting announcements are posted under (open per kind, one result tag).</summary>
    private const string TermsVotingTag = "voting/terms";
    private const string ElectionVotingTag = "voting/election";
    private const string ProposalVotingTag = "voting/proposal";
    private const string ChallengeVotingTag = "voting/challenge";
    private const string VotingResultTag = "voting/result";

    /// <summary>
    /// The synthetic contributor identity the voting announcements are posted under. Not a wallet:
    /// it exists only as a bucket-contributor row, satisfies no chain format (so it never receives
    /// pushes), and no signature can ever be produced for it.
    /// </summary>
    private const string VotingSystemIdentity = "system:voting";

    /// <summary>The content type of every voting announcement: the body is plaintext JSON.</summary>
    private const string VotingContentType = "application/json";

    /// <summary>
    /// The key a voting webhook unlocks a still-locked legal bucket with: 32 zero bytes, an obvious
    /// placeholder. Bucket content is not end-to-end-encrypted under it; a real key ceremony has to
    /// be server-mediated anyway, because the bucket's only admin is the PropertyAsset PDA.
    /// </summary>
    private const string PlaceholderEncryptionKey =
        "0x0000000000000000000000000000000000000000000000000000000000000000";

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

    /// <summary>The on-chain asset id (the PropertyAsset account's own id).</summary>
    [JsonPropertyName("asset_id")]
    public long? AssetId { get; set; }

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
    /// The base58 Lawyer REGISTRY PDA of the election winner — NOT the lawyer's wallet (the wallet
    /// is the listing row's <c>spvLawyer</c>, which the handler resolves; this stays provenance).
    /// Null when the instruction omitted the optional account.
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
    /// The on-chain asset id, assumed to equal the marketplace listing id (the namespace's
    /// PropertyId); when no namespace carries it, the lookup falls back to the PropertyAsset PDA.
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
    /// The base58 LettingAgent ENTRY PDA of the winner — NOT the agent's wallet; the handler
    /// resolves the wallet from the indexer's <c>lettingAgents</c> mirror (the row whose
    /// <c>id</c> is this PDA). Null when the instruction omitted the optional account — a round
    /// that seated no agent.
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

/// <summary>
/// The <c>spv_case_claimed</c> webhook payload the indexer POSTs verbatim (marketplace
/// <c>claim_spv_case</c> — fires per candidacy, not just at round open). The election's
/// expiry is resolved through the indexer GraphQL — the payload carries instruction evidence.
/// </summary>
public sealed class SpvCaseClaimedPayload
{
    /// <summary>Always <c>spv_case_claimed</c>.</summary>
    [JsonPropertyName("event")]
    public string? Event { get; set; }

    /// <summary>The on-chain listing id (the namespace's PropertyId).</summary>
    [JsonPropertyName("property_id")]
    public long? PropertyId { get; set; }

    /// <summary>The election round the candidacy belongs to (1-based).</summary>
    [JsonPropertyName("round")]
    public int? Round { get; set; }

    /// <summary>The lawyer's proposed costs for the case (provenance; not re-posted).</summary>
    [JsonPropertyName("costs")]
    public long? Costs { get; set; }

    /// <summary>The base58 wallet of the claiming lawyer.</summary>
    [JsonPropertyName("lawyer")]
    public string? Lawyer { get; set; }

    /// <summary>The base58 Listing PDA.</summary>
    [JsonPropertyName("listing")]
    public string? Listing { get; set; }

    /// <summary>The base58 PropertyAsset PDA (the namespace's creator; the lookup fallback).</summary>
    [JsonPropertyName("property")]
    public string? Property { get; set; }

    /// <summary>The base58 LawyerCandidacy PDA the claim created.</summary>
    [JsonPropertyName("candidacy")]
    public string? Candidacy { get; set; }

    /// <summary>The slot the candidacy landed in.</summary>
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
/// The <c>agent_election_opened</c> webhook payload the indexer POSTs verbatim (the property
/// program's <c>claim_property</c> — fires per candidacy, not just at round open).
/// </summary>
public sealed class AgentElectionOpenedPayload
{
    /// <summary>Always <c>agent_election_opened</c>.</summary>
    [JsonPropertyName("event")]
    public string? Event { get; set; }

    /// <summary>The property program's asset id — equals the marketplace listing id (the
    /// namespace's PropertyId).</summary>
    [JsonPropertyName("property_id")]
    public long? PropertyId { get; set; }

    /// <summary>The election round (1-based).</summary>
    [JsonPropertyName("round")]
    public int? Round { get; set; }

    /// <summary>The base58 wallet of the agent this candidacy belongs to.</summary>
    [JsonPropertyName("agent")]
    public string? Agent { get; set; }

    /// <summary>The base58 PropertyAsset PDA (the namespace's creator; the lookup fallback).</summary>
    [JsonPropertyName("property")]
    public string? Property { get; set; }

    /// <summary>The base58 PropertyLetting PDA.</summary>
    [JsonPropertyName("letting")]
    public string? Letting { get; set; }

    /// <summary>The base58 AgentCandidacy PDA the claim created.</summary>
    [JsonPropertyName("candidacy")]
    public string? Candidacy { get; set; }

    /// <summary>The slot the candidacy landed in.</summary>
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
/// The <c>proposal_created</c> webhook payload the indexer POSTs verbatim (the property
/// program's <c>propose</c> — including the auto-approval path, where no Proposal PDA survives).
/// </summary>
public sealed class ProposalCreatedPayload
{
    /// <summary>Always <c>proposal_created</c>.</summary>
    [JsonPropertyName("event")]
    public string? Event { get; set; }

    /// <summary>The property program's asset id (the namespace's PropertyId).</summary>
    [JsonPropertyName("property_id")]
    public long? PropertyId { get; set; }

    /// <summary>The on-chain proposal id.</summary>
    [JsonPropertyName("proposal_id")]
    public long? ProposalId { get; set; }

    /// <summary>The spending amount under vote (provenance; the message does not re-post it).</summary>
    [JsonPropertyName("amount")]
    public long? Amount { get; set; }

    /// <summary>The 32-byte hash of the proposal details, base58 (provenance).</summary>
    [JsonPropertyName("details_hash")]
    public string? DetailsHash { get; set; }

    /// <summary>The base58 PropertyLetting PDA.</summary>
    [JsonPropertyName("letting")]
    public string? Letting { get; set; }

    /// <summary>The base58 Proposal PDA — absent on-chain by the time the event delivers when
    /// the chain auto-approved the spend.</summary>
    [JsonPropertyName("proposal")]
    public string? Proposal { get; set; }

    /// <summary>The slot the proposal was created in.</summary>
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
/// The <c>proposal_finalized</c> webhook payload the indexer POSTs verbatim (a spending
/// proposal closed — the payload names the CLOSED PDA whose frozen row has the outcome).
/// </summary>
public sealed class ProposalFinalizedPayload
{
    /// <summary>Always <c>proposal_finalized</c>.</summary>
    [JsonPropertyName("event")]
    public string? Event { get; set; }

    /// <summary>The property program's asset id (the namespace's PropertyId).</summary>
    [JsonPropertyName("property_id")]
    public long? PropertyId { get; set; }

    /// <summary>The base58 PropertyLetting PDA.</summary>
    [JsonPropertyName("letting")]
    public string? Letting { get; set; }

    /// <summary>The base58 PropertyAsset PDA (the namespace's creator; the lookup fallback).</summary>
    [JsonPropertyName("property")]
    public string? Property { get; set; }

    /// <summary>The base58 Proposal PDA that was finalized (and closed).</summary>
    [JsonPropertyName("proposal")]
    public string? Proposal { get; set; }

    /// <summary>The slot the proposal finalized in.</summary>
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
/// The <c>challenge_created</c> webhook payload the indexer POSTs verbatim (a token holder
/// challenging the letting agent).
/// </summary>
public sealed class ChallengeCreatedPayload
{
    /// <summary>Always <c>challenge_created</c>.</summary>
    [JsonPropertyName("event")]
    public string? Event { get; set; }

    /// <summary>The property program's asset id (the namespace's PropertyId).</summary>
    [JsonPropertyName("property_id")]
    public long? PropertyId { get; set; }

    /// <summary>The on-chain challenge id.</summary>
    [JsonPropertyName("challenge_id")]
    public long? ChallengeId { get; set; }

    /// <summary>The base58 wallet of the challenger.</summary>
    [JsonPropertyName("challenger")]
    public string? Challenger { get; set; }

    /// <summary>The base58 PropertyLetting PDA.</summary>
    [JsonPropertyName("letting")]
    public string? Letting { get; set; }

    /// <summary>The base58 Challenge PDA the challenge created.</summary>
    [JsonPropertyName("challenge")]
    public string? Challenge { get; set; }

    /// <summary>The slot the challenge was created in.</summary>
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
/// The <c>challenge_finalized</c> webhook payload the indexer POSTs verbatim (a challenge's
/// vote closed — the payload names the CLOSED PDA whose frozen row has the outcome; the
/// optional agent entry is provenance for the seat affected).
/// </summary>
public sealed class ChallengeFinalizedPayload
{
    /// <summary>Always <c>challenge_finalized</c>.</summary>
    [JsonPropertyName("event")]
    public string? Event { get; set; }

    /// <summary>The property program's asset id (the namespace's PropertyId).</summary>
    [JsonPropertyName("property_id")]
    public long? PropertyId { get; set; }

    /// <summary>The base58 PropertyLetting PDA.</summary>
    [JsonPropertyName("letting")]
    public string? Letting { get; set; }

    /// <summary>The base58 PropertyAsset PDA (the namespace's creator; the lookup fallback).</summary>
    [JsonPropertyName("property")]
    public string? Property { get; set; }

    /// <summary>The base58 Challenge PDA that was finalized (and closed).</summary>
    [JsonPropertyName("challenge")]
    public string? Challenge { get; set; }

    /// <summary>The base58 LettingAgent ENTRY PDA of the seat holder at close — provenance,
    /// not the wallet; null when the instruction omitted the optional account.</summary>
    [JsonPropertyName("agent_entry")]
    public string? AgentEntry { get; set; }

    /// <summary>The slot the challenge finalized in.</summary>
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
/// The <c>property_holder_changed</c> webhook payload the indexer POSTs verbatim (the primary
/// market's holder lifecycle: <c>reserve_shares</c> / <c>buy_property_shares</c> add,
/// <c>unreserve_shares</c>, <c>release_reservation</c>, <c>close_cancelled_position</c> and the
/// three <c>withdraw_*</c> exits remove). Field names are the indexer's snake_case wire names,
/// pinned with <c>JsonPropertyName</c>.
/// </summary>
public sealed class PropertyHolderChangedPayload
{
    /// <summary>Always <c>property_holder_changed</c>.</summary>
    [JsonPropertyName("event")]
    public string? Event { get; set; }

    /// <summary><c>added</c> (reserve/buy) or <c>removed</c> (cancel/release/withdraw).</summary>
    [JsonPropertyName("action")]
    public string? Action { get; set; }

    /// <summary>The instruction that produced the event, e.g. <c>reserve_shares</c>.</summary>
    [JsonPropertyName("reason")]
    public string? Reason { get; set; }

    /// <summary>The base58 wallet whose membership changed.</summary>
    [JsonPropertyName("investor")]
    public string? Investor { get; set; }

    /// <summary>The base58 Listing PDA — null for <c>unreserve_shares</c> variants without one.</summary>
    [JsonPropertyName("listing")]
    public string? Listing { get; set; }

    /// <summary>The base58 PropertyAsset PDA (the namespace's creator) — null when the instruction does not carry it.</summary>
    [JsonPropertyName("property")]
    public string? Property { get; set; }

    /// <summary>The on-chain listing id (the namespace's PropertyId).</summary>
    [JsonPropertyName("listing_id")]
    public long? ListingId { get; set; }

    /// <summary>The reserved/bought amount — set only on the two add paths.</summary>
    [JsonPropertyName("amount")]
    public long? Amount { get; set; }

    /// <summary>The slot the change landed in.</summary>
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
