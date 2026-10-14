namespace XcavateProfileApi.Data;

/// <summary>
/// One indexer webhook event this API has accepted and acted on (or deliberately ignored) — the
/// dedupe store behind the notification webhooks (income distributions, direct share sends). The
/// indexer's outbox delivers AT LEAST ONCE (a lost 2xx is retried with backoff), so the endpoint
/// records the event's natural key before answering 2xx and a redelivery that carries the same
/// key is a no-op. Unlike the bucket webhooks, the push fanout has no domain state of its own to
/// dedupe against — this table is that state.
/// </summary>
/// <remarks>
/// The key is built from the parsed payload's identifying fields (event type, transaction
/// signature, and the event's subject fields — asset, amount, parties), NOT from the raw body
/// bytes: one transaction can legitimately carry two same-type instructions (two sends, two
/// distributions), and those differ in their subject fields while sharing the signature.
/// </remarks>
public class ProcessedWebhookEvent
{
    /// <summary>
    /// The dedupe key and primary key, e.g.
    /// <c>shares_transferred:&lt;tx&gt;:&lt;asset&gt;:&lt;sender&gt;:&lt;receiver&gt;:&lt;amount&gt;</c>.
    /// </summary>
    public required string EventKey { get; set; }

    /// <summary>The indexer's event label (<c>income_distributed</c>, <c>shares_transferred</c>).</summary>
    public required string EventType { get; set; }

    /// <summary>The base58 transaction signature the event was produced by (provenance).</summary>
    public required string TxSignature { get; set; }

    /// <summary>When this API first accepted the event (UTC).</summary>
    public DateTime ProcessedAt { get; set; }
}
