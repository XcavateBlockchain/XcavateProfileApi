using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using XcavateProfileApi.Data;
using XcavateProfileApi.Services.Indexer;
using XcavateProfileApi.Services.Notifications;

namespace XcavateProfileApi.Controllers;

/// <summary>
/// The indexer webhooks that fan out into push notifications (the investor-income work): the
/// property program's <c>income_distributed</c> event (the letting agent paid rental income into
/// a property's vault) pushes "rent received" to every current holder with a registered profile,
/// and the marketplace's <c>shares_transferred</c> event (a wallet directly sent shares to
/// another wallet) pushes "you received shares" to the receiver.
/// </summary>
/// <remarks>
/// Unlike the bucket webhooks in <see cref="WebhooksController"/> these endpoints CHANGE nothing
/// a client can read — the fanout is a best-effort push — so they are the ones a forged POST
/// could abuse for push spam. They are gated by a shared secret (the
/// <c>INDEXER_WEBHOOK_SECRET</c> env var): the indexer's delivery loop POSTs a bare JSON body and
/// cannot set headers, so the secret travels in the route URL's query string
/// (<c>?secret=…</c> — the channel the indexer's URL convention already blesses) or, for direct
/// callers, in the <c>X-Webhook-Secret</c> header. Unset secret = the endpoints fail CLOSED
/// (503), so a push-spam hole is never open by default; the indexer's outbox keeps retrying,
/// which surfaces the misconfiguration on its side.
///
/// Delivery is at-least-once, so both handlers dedupe on the event's natural key
/// (<c>ProcessedWebhookEvents</c>, recorded before the 2xx) and answer 200 on a redelivery
/// without re-sending. The dedupe row is written only once the fanout is fully resolved: a
/// resolution failure (indexer down) answers 502 so the outbox retries, while pushes are
/// enqueued after the record — a crash between the two loses pushes (best-effort) instead of
/// duplicating them on retry.
/// </remarks>
[ApiController]
[Route("webhooks")]
public class NotificationWebhooksController : ControllerBase
{
    private readonly ProfileDbContext _db;
    private readonly IIndexerClient _indexer;
    private readonly IConfiguration _config;
    private readonly ILogger<NotificationWebhooksController> _logger;

    public NotificationWebhooksController(
        ProfileDbContext db,
        IIndexerClient indexer,
        IConfiguration config,
        ILogger<NotificationWebhooksController> logger)
    {
        _db = db;
        _indexer = indexer;
        _config = config;
        _logger = logger;
    }

    /// <summary>
    /// Receives the <c>income_distributed</c> event and enqueues one "rent received" push per
    /// wallet currently holding the property's shares, resolved from the indexer's active
    /// <c>shareHoldings</c> rows with a positive amount. The copy is amount-free on purpose: the
    /// payload's amount is base units of a mint whose decimals/symbol this service does not
    /// reliably know.
    /// </summary>
    /// <remarks>
    /// Only wallets with a registered profile (direct, or via <c>WalletMigrations</c> for a
    /// migrated account) are pushed to; the rest are logged and counted, never retried. One
    /// holder's failure cannot break the others: the profile mapping is a single batch read and
    /// the enqueue is in-memory (delivery itself is isolated per push by
    /// <see cref="NotificationsApiClient"/>). A holder-resolution failure answers 502 so the
    /// indexer retries. Idempotent via the dedupe record: a redelivery answers 200
    /// <c>duplicate</c> and sends nothing. 400 when the body is not a valid
    /// <c>income_distributed</c> event; 401/503 on the shared-secret check.
    /// </remarks>
    // POST: webhooks/income_distributed_devnet
    [HttpPost("income_distributed_devnet")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status502BadGateway)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    public async Task<IActionResult> IncomeDistributedDevnet(
        [FromBody] IncomeDistributedPayload payload, CancellationToken ct)
    {
        if (CheckWebhookSecret() is { } rejected)
        {
            return rejected;
        }

        if (payload is null
            || !string.Equals(payload.Event, IncomeDistributedEventName, StringComparison.Ordinal)
            || payload.AssetId is null
            || payload.Amount is null
            || string.IsNullOrWhiteSpace(payload.Mint)
            || string.IsNullOrWhiteSpace(payload.Agent)
            || string.IsNullOrWhiteSpace(payload.TxSignature))
        {
            return BadRequest("The body is not a valid 'income_distributed' webhook payload.");
        }

        var queue = PushQueue();
        if (queue is null)
        {
            return await RecordOnlyAsync(
                payload.Event!, payload.TxSignature!,
                IncomeEventKey(payload),
                "push notifications are not configured on this service",
                ct);
        }

        IReadOnlyList<string> holders;
        try
        {
            holders = await _indexer.GetShareHolderWalletsAsync(payload.AssetId.Value, ct);
        }
        catch (IndexerQueryException ex)
        {
            return StatusCode(
                StatusCodes.Status502BadGateway,
                $"Could not resolve the property's current holders: {ex.Message}");
        }

        var registered = await RegisteredWalletsAsync(holders, ct);

        var eventKey = IncomeEventKey(payload);
        if (!await TryRecordAsync(eventKey, payload.Event!, payload.TxSignature!, ct))
        {
            return Ok(new { status = "duplicate", reason = "this event was already accepted" });
        }

        var pushed = 0;
        foreach (var wallet in holders)
        {
            if (!registered.Contains(wallet))
            {
                _logger.LogDebug(
                    "income_distributed {TxSignature}: holder {Wallet} has no registered profile — skipped",
                    payload.TxSignature, wallet);
                continue;
            }

            queue.Enqueue(new PushNotification(
                PushNotification.SolanaChain,
                wallet,
                RentReceivedTitle,
                RentReceivedBody,
                PushNotification.IncomeDistributedType,
                BucketId: string.Empty));
            pushed++;
        }

        var skipped = holders.Count - pushed;
        if (skipped > 0)
        {
            _logger.LogInformation(
                "income_distributed {TxSignature} (asset {AssetId}): pushed to {Pushed} of {Holders} holders; {Skipped} without a registered profile",
                payload.TxSignature, payload.AssetId, pushed, holders.Count, skipped);
        }

        return Ok(new
        {
            status = "processed",
            holders = holders.Count,
            pushed,
            skippedNoProfile = skipped
        });
    }

    /// <summary>
    /// Receives the <c>shares_transferred</c> event (a wallet directly sent property shares to
    /// another wallet — the narrow ADR-41 sibling of the bucket-access
    /// <c>property_shares_transferred</c> feed) and pushes "you received shares" to the
    /// RECEIVER, when the receiver's wallet maps to a registered profile. The property's display
    /// name is resolved from the indexer's <c>propertyAssets</c> mirror on a best-effort basis:
    /// a lookup failure omits the name from the copy rather than retrying the event.
    /// </summary>
    /// <remarks>
    /// A receiver without a registered profile is logged and skipped (200), never retried; a
    /// self-transfer is ignored. Idempotent via the dedupe record. 400 when the body is not a
    /// valid <c>shares_transferred</c> event; 401/503 on the shared-secret check.
    /// </remarks>
    // POST: webhooks/shares_received_devnet
    [HttpPost("shares_received_devnet")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    public async Task<IActionResult> SharesReceivedDevnet(
        [FromBody] SharesReceivedPayload payload, CancellationToken ct)
    {
        if (CheckWebhookSecret() is { } rejected)
        {
            return rejected;
        }

        if (payload is null
            || !string.Equals(payload.Event, SharesTransferredEventName, StringComparison.Ordinal)
            || payload.AssetId is null
            || payload.Amount is null
            || string.IsNullOrWhiteSpace(payload.Sender)
            || string.IsNullOrWhiteSpace(payload.Receiver)
            || string.IsNullOrWhiteSpace(payload.TxSignature))
        {
            return BadRequest("The body is not a valid 'shares_transferred' webhook payload.");
        }

        if (string.Equals(payload.Sender, payload.Receiver, StringComparison.Ordinal))
        {
            return Ok(new { status = "ignored", reason = "self-transfer" });
        }

        var queue = PushQueue();
        if (queue is null)
        {
            return await RecordOnlyAsync(
                payload.Event!, payload.TxSignature!,
                SharesEventKey(payload),
                "push notifications are not configured on this service",
                ct);
        }

        var registered = await RegisteredWalletsAsync([payload.Receiver!], ct);
        if (!registered.Contains(payload.Receiver!))
        {
            _logger.LogInformation(
                "shares_transferred {TxSignature}: receiver {Wallet} has no registered profile — skipped",
                payload.TxSignature, payload.Receiver);
            return Ok(new
            {
                status = "skipped",
                acted = false,
                reason = "no registered profile for the receiver wallet"
            });
        }

        var eventKey = SharesEventKey(payload);
        if (!await TryRecordAsync(eventKey, payload.Event!, payload.TxSignature!, ct))
        {
            return Ok(new { status = "duplicate", reason = "this event was already accepted" });
        }

        var propertyName = await TryResolvePropertyNameAsync(payload.AssetId.Value, ct);
        var body = propertyName is null
            ? string.Format(SharesReceivedBodyAnonymous, payload.Amount)
            : string.Format(SharesReceivedBodyNamed, payload.Amount, propertyName);

        queue.Enqueue(new PushNotification(
            PushNotification.SolanaChain,
            payload.Receiver!,
            SharesReceivedTitle,
            body,
            PushNotification.SharesReceivedType,
            BucketId: string.Empty));

        return Ok(new { status = "processed", pushed = true });
    }

    /// <summary>
    /// The shared-secret gate: null when the request may proceed, else the rejection result.
    /// The secret is read per request from <c>INDEXER_WEBHOOK_SECRET</c>; compared in constant
    /// time, and accepted from the <c>X-Webhook-Secret</c> header or the <c>secret</c> query
    /// parameter (the only channel the indexer's header-less delivery loop can carry it on).
    /// </summary>
    private IActionResult? CheckWebhookSecret()
    {
        var secret = _config[WebhookSecretConfigKey];
        if (string.IsNullOrWhiteSpace(secret))
        {
            return StatusCode(
                StatusCodes.Status503ServiceUnavailable,
                $"The indexer webhook secret ({WebhookSecretConfigKey}) is not configured — "
                + "the notification webhooks are disabled.");
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

    /// <summary>
    /// The push queue, or null when push notifications are not configured (no
    /// <c>NOTIFICATIONS_API_KEY</c> — see Program.cs): the queue/dispatcher/client triple is
    /// registered only with the feature, so the endpoint resolves it per request instead of
    /// demanding it in the constructor.
    /// </summary>
    private NotificationQueue? PushQueue() =>
        HttpContext.RequestServices.GetService<NotificationQueue>();

    /// <summary>
    /// Records the event without fanning out (push not configured) and answers 200: the event is
    /// durably accepted, so the indexer does not retry what this service could never act on; the
    /// dedupe row also makes a later redelivery a no-op, matching the enabled path.
    /// </summary>
    private async Task<IActionResult> RecordOnlyAsync(
        string eventType, string txSignature, string eventKey, string reason, CancellationToken ct)
    {
        _logger.LogInformation(
            "{EventType} {TxSignature}: {Reason} — recording the event without fanout",
            eventType, txSignature, reason);

        var recorded = await TryRecordAsync(eventKey, eventType, txSignature, ct);
        return Ok(new
        {
            status = recorded ? "accepted" : "duplicate",
            reason,
            pushed = 0
        });
    }

    /// <summary>
    /// The dedupe insert: true when this delivery is the first (row recorded), false on an
    /// at-least-once redelivery. The check-then-insert race (two deliveries in flight at once)
    /// is closed by the primary key: the loser's <c>SaveChanges</c> violates it, and the
    /// follow-up existence read turns the violation into the duplicate answer — a different
    /// <c>DbUpdateException</c> (the row genuinely absent afterwards) still throws.
    /// </summary>
    private async Task<bool> TryRecordAsync(
        string eventKey, string eventType, string txSignature, CancellationToken ct)
    {
        if (await _db.ProcessedWebhookEvents.AnyAsync(e => e.EventKey == eventKey, ct))
        {
            return false;
        }

        var row = new ProcessedWebhookEvent
        {
            EventKey = eventKey,
            EventType = eventType,
            TxSignature = txSignature,
            ProcessedAt = DateTime.UtcNow
        };
        _db.ProcessedWebhookEvents.Add(row);
        try
        {
            await _db.SaveChangesAsync(ct);
            return true;
        }
        catch (DbUpdateException)
        {
            _db.Entry(row).State = EntityState.Detached;
            if (await _db.ProcessedWebhookEvents.AnyAsync(e => e.EventKey == eventKey, ct))
            {
                return false;
            }

            throw;
        }
    }

    /// <summary>
    /// The wallets among <paramref name="wallets"/> that map to a registered user: the profile
    /// keyed directly by the wallet, or — the <c>WalletMigrations</c> fallback — a migrated
    /// account whose profile still sits under the legacy SS58 address. Same mapping semantics as
    /// <see cref="Services.ViewerKeyResolver"/>, batched for the fanout.
    /// </summary>
    private async Task<HashSet<string>> RegisteredWalletsAsync(
        IReadOnlyCollection<string> wallets, CancellationToken ct)
    {
        if (wallets.Count == 0)
        {
            return new HashSet<string>(StringComparer.Ordinal);
        }

        var direct = await _db.Profiles
            .Where(p => wallets.Contains(p.Ss58Address))
            .Select(p => p.Ss58Address)
            .ToListAsync(ct);
        var migrated = await _db.WalletMigrations
            .Where(m => wallets.Contains(m.SolanaAddress))
            .Select(m => m.SolanaAddress)
            .ToListAsync(ct);

        return direct.Concat(migrated).ToHashSet(StringComparer.Ordinal);
    }

    /// <summary>
    /// Best-effort property-name resolution for the shares push copy: the indexer's mirror, or
    /// null when it cannot answer — a naming failure must not hold the receiver's push hostage
    /// (502 would retry the whole event for an enrichment).
    /// </summary>
    private async Task<string?> TryResolvePropertyNameAsync(long assetId, CancellationToken ct)
    {
        try
        {
            var name = await _indexer.GetPropertyAssetNameAsync(assetId, ct);
            return string.IsNullOrWhiteSpace(name) ? null : name;
        }
        catch (IndexerQueryException ex)
        {
            _logger.LogWarning(
                ex, "Could not resolve the property name for asset {AssetId}; sending without it",
                assetId);
            return null;
        }
    }

    private static string IncomeEventKey(IncomeDistributedPayload payload) =>
        $"{IncomeDistributedEventName}:{payload.TxSignature}:{payload.AssetId}:{payload.Mint}:{payload.Amount}:{payload.Agent}";

    private static string SharesEventKey(SharesReceivedPayload payload) =>
        $"{SharesTransferredEventName}:{payload.TxSignature}:{payload.AssetId}:{payload.Sender}:{payload.Receiver}:{payload.Amount}";

    private const string IncomeDistributedEventName = "income_distributed";
    private const string SharesTransferredEventName = "shares_transferred";

    /// <summary>The env var holding the shared secret both notification webhooks require.</summary>
    public const string WebhookSecretConfigKey = "INDEXER_WEBHOOK_SECRET";

    private const string WebhookSecretHeader = "X-Webhook-Secret";
    private const string WebhookSecretQueryKey = "secret";

    private const string RentReceivedTitle = "Rent received";
    private const string RentReceivedBody = "Your property paid out rent — open the app to claim it";

    private const string SharesReceivedTitle = "You received property shares";
    private const string SharesReceivedBodyNamed = "{0} shares of {1} were transferred to your wallet";
    private const string SharesReceivedBodyAnonymous = "{0} shares of a property were transferred to your wallet";
}

/// <summary>
/// The <c>income_distributed</c> webhook payload the indexer POSTs verbatim (the property
/// program's <c>distribute_income</c>). Field names are the indexer's snake_case wire names,
/// pinned with <c>JsonPropertyName</c> so they do not depend on the serializer's naming policy.
/// </summary>
public sealed class IncomeDistributedPayload
{
    /// <summary>Always <c>income_distributed</c>.</summary>
    [JsonPropertyName("event")]
    public string? Event { get; set; }

    /// <summary>The on-chain asset id whose property received the income.</summary>
    [JsonPropertyName("asset_id")]
    public long? AssetId { get; set; }

    /// <summary>The base58 payment mint the income was paid in.</summary>
    [JsonPropertyName("mint")]
    public string? Mint { get; set; }

    /// <summary>The distributed amount in base units of <see cref="Mint"/> (a u64 JSON number).</summary>
    [JsonPropertyName("amount")]
    public long? Amount { get; set; }

    /// <summary>The base58 wallet of the letting agent that paid.</summary>
    [JsonPropertyName("agent")]
    public string? Agent { get; set; }

    /// <summary>The base58 PropertyLetting PDA (provenance; not used by the fanout).</summary>
    [JsonPropertyName("letting")]
    public string? Letting { get; set; }

    /// <summary>The base58 PropertyAsset PDA (provenance; not used by the fanout).</summary>
    [JsonPropertyName("property")]
    public string? Property { get; set; }

    /// <summary>The base58 PropertyIncome PDA (provenance; not used by the fanout).</summary>
    [JsonPropertyName("income")]
    public string? Income { get; set; }

    /// <summary>The slot the distribution landed in.</summary>
    [JsonPropertyName("slot")]
    public long Slot { get; set; }

    /// <summary>The base58 transaction signature — half of the dedupe key.</summary>
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
/// The <c>shares_transferred</c> webhook payload the indexer POSTs verbatim (the marketplace
/// program's <c>send_property_shares</c> — a direct wallet-to-wallet share send; the narrow
/// notification sibling of <c>property_shares_transferred</c>'s <c>direct_send</c>). Field names
/// are the indexer's snake_case wire names, pinned with <c>JsonPropertyName</c>.
/// </summary>
public sealed class SharesReceivedPayload
{
    /// <summary>Always <c>shares_transferred</c>.</summary>
    [JsonPropertyName("event")]
    public string? Event { get; set; }

    /// <summary>The on-chain asset id the shares belong to.</summary>
    [JsonPropertyName("asset_id")]
    public long? AssetId { get; set; }

    /// <summary>How many shares moved (the instruction's u32 amount — whole shares).</summary>
    [JsonPropertyName("amount")]
    public long? Amount { get; set; }

    /// <summary>The base58 wallet the shares left.</summary>
    [JsonPropertyName("sender")]
    public string? Sender { get; set; }

    /// <summary>The base58 wallet the shares landed with — the push's recipient.</summary>
    [JsonPropertyName("receiver")]
    public string? Receiver { get; set; }

    /// <summary>The slot the send landed in.</summary>
    [JsonPropertyName("slot")]
    public long Slot { get; set; }

    /// <summary>The base58 transaction signature — half of the dedupe key.</summary>
    [JsonPropertyName("tx_signature")]
    public string? TxSignature { get; set; }

    /// <summary>The block time, ISO-8601.</summary>
    [JsonPropertyName("block_time")]
    public DateTime? BlockTime { get; set; }

    /// <summary>The program that produced the event, e.g. <c>marketplace</c>.</summary>
    [JsonPropertyName("program")]
    public string? Program { get; set; }
}
