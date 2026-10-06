# Property lifecycle webhooks — uncertainties & placeholder work

The indexer (`~/Indexer`, branch `agent/20261006-property-webhooks`, ADR-36) emits five new
webhook event types for the "Property namespace & buckets" flow, and this API hosts their
endpoints (`src/XcavateProfileApi/Controllers/WebhooksController.cs`). What was **not** certain
is collected here: each entry says what the code does today, why it is a placeholder, and how to
complete it. Entries are numbered; the controller's `TODO(uncertainties #N)` comments point here.

Wire format note: every payload carries `event`, the event's subject keys, `slot`,
`tx_signature`, `block_time` (ISO-8601), and `program`. Delivery is at-least-once with per-event
backoff; a non-2xx answer means "retry forever" (30 s doubling, 1 h cap).

## The webhook set

| Indexer event type | Trigger (on-chain) | Env var (indexer) | Endpoint here | Status |
|---|---|---|---|---|
| `property_asset_registered` | marketplace `init_property_assets` | `INIT_PROPERTY_ASSET_WEBHOOK_URL` | `POST /webhooks/init_property_asset_devnet` | done (pre-existing) |
| `property_claim_started` | first `claim_shares` per listing | `PROPERTY_CLAIM_STARTED_WEBHOOK_URL` | `POST /webhooks/property_claim_started_devnet` | **implemented** (creates the legal bucket) |
| `spv_lawyer_elected` | `finalize_spv_election` | `SPV_LAWYER_ELECTED_WEBHOOK_URL` | `POST /webhooks/spv_lawyer_elected_devnet` | **placeholder** (#2, #3) |
| `deal_executed` | `execute_deal` | `DEAL_EXECUTED_WEBHOOK_URL` | `POST /webhooks/deal_executed_devnet` | **implemented** (removes lawyers) |
| `letting_agent_appointed` | property `finalize_agent_election` | `LETTING_AGENT_APPOINTED_WEBHOOK_URL` | `POST /webhooks/letting_agent_appointed_devnet` | **placeholder** (#4) |
| `property_shares_transferred` | `buy_relisted_shares` / `accept_offer` / `send_property_shares` | `SHARES_TRANSFERRED_WEBHOOK_URL` | `POST /webhooks/shares_transferred_devnet` | **implemented** (viewer swap via profile X25519 keys — see #5) |

Conventions the endpoints rely on: the property's namespace is found by `PropertyId == listing_id`
(falling back to `Creator == <PropertyAsset PDA>`), and its legal-process bucket by
`Category == "legal-process"` inside that namespace. The namespace creator (the PropertyAsset PDA)
is installed as the bucket's operator admin at creation and is the caller for all later
webhook-driven membership changes.

## 1. Bucket-creation trigger is the first claim, not the sold-out transition

The flowcard's trigger is "Last property token is reserved → Property token claim starts".
Implemented: the **first `claim_shares`** on the listing (instruction-level detection, full
provenance, deduped by listing PDA). **Risk:** if a sold-out listing's investors never claim, the
legal bucket is never created. If that is real, move detection to the listing→`SOLD_OUT`
account-state transition in the indexer's batcher (next to the ADR-35 `sold_out_notifications`
record in `crates/indexer/src/batcher.rs`); note the account upsert path has no tx signature or
block time, so `webhook_events.tx_signature`/`block_time` would need placeholders or a new
migration relaxing those NOT NULL columns.

## 2. SPV lawyer wallet resolution (placeholder in `SpvLawyerElectedDevnet`)

`finalize_spv_election` carries only the winner's Lawyer **registry PDA** (`winner_registry`,
optional, null when omitted) — not the lawyer's wallet. To complete: query the indexer GraphQL
`marketplaceListing(...)` for `spvLawyer` (a base58 wallet; it is set on the listing row by the
same finalization), then
`_memberships.AddContributorAsync(ns.Creator, ns.NamespaceId, bucket.BucketId, lawyerWallet, ct)`.
Open questions: should the lawyer also be a bucket **admin** (they steer the doc review), and
should the developer lawyer (`developerLawyer` on the same listing) be added here or only after
`assign_developer_lawyer` (not currently a webhook — add one if needed)?

## 3. Token-holder read access — viewer keys solved, holder ENUMERATION still placeholder

"Add property token holders (read) to bucket" maps to the bucket **Viewer** role — a viewer id
is a 32-byte X25519 encryption key (`InputValidator.Hex32Value`), not a wallet. **Resolved:**
wallets map to keys through the profiles (`Profile.X25519Key`, direct wallet key with the
`WalletMigrations` SS58 fallback — the same `ResolveViewerKeyAsync` the transfer webhook uses).
**Still missing:** the current holder set — enumerating holders needs the indexer GraphQL
`shareHoldings(assetId, active: true)`, and this API has no indexer client yet. Once one exists,
`SpvLawyerElectedDevnet` can add every current holder with a registered key as a viewer.

## 4. Letting agent appointment (placeholder in `LettingAgentAppointedDevnet`)

Two uncertainties. (a) **Identity:** the property program's `finalize_agent_election` arg is
`asset_id`; unverified whether it numerically equals the marketplace `listing_id` stored as the
namespace's `PropertyId`. The lookup therefore falls back to the PropertyAsset PDA (namespace
`Creator`) — verify on devnet and tighten. (b) **Wallet:** `winner_entry` is the winner's
LettingAgent **entry PDA**; resolve the agent wallet via the indexer GraphQL letting-agent
accounts, then `AddContributorAsync(...)`. Also confirm `finalize_agent_election` is the intended
"appointed" signal (not `add_letting_agent`, which registers an agent for a *postcode*, not a
property), and whether the agent goes into the legal bucket or a separate management bucket —
the flowchart shows one bucket, so the legal bucket is used.

## 5. Share-transfer access swap — IMPLEMENTED, with two caveats to review

Implemented in `SharesTransferredDevnet`: the seller's viewer is removed and the buyer is added,
resolving wallets to viewer keys through the profiles (direct wallet key, then the
`WalletMigrations` SS58 fallback). Idempotent under redelivery; a self-transfer is a no-op.
Caveats:

- **A side without a profile key is skipped, not retried** (200 with a `buyerNote`/`sellerNote`).
  A buyer who registers a key only AFTER the resale never gets added by this event — closing that
  gap needs the reconcile-from-current-state pass (diff `shareHoldings` against bucket viewers,
  same indexer-GraphQL dependency as #3).
- **Partial sales remove the seller entirely.** The flowchart says "remove the seller", but a
  seller who sold only SOME shares still holds tokens and loses read access anyway. If partial
  sellers should keep access, the removal must be gated on the seller's remaining balance being
  zero — again an indexer GraphQL lookup (`shareHoldings(assetId, owner: from)`).
- `accept_offer` payloads carry `amount: null` (the amount lives in the on-chain Offer account;
  `offer` carries its PDA) — only matters if the amount is ever needed here.
- `send_property_shares` fires for gifts and self-transfers too, not only sales; self-transfers
  are already no-ops, but if access should only swap on *sales*, drop the `direct_send` kind in
  the indexer mapper (`crates/indexer/src/mapping/marketplace.rs`) or filter here.

## 6. `deal_executed` removes *all* write/manage members, not just lawyers

Implemented as: remove every contributor and every admin except the operator admin (namespace
creator). This assumes only lawyers hold write/manage on the legal bucket at deal time. If entry
#4 later adds the letting agent as a contributor *before* the deal executes, the agent is swept
too — either order the flow so agents join after `deal_executed`, or key removal to resolved
lawyer wallets (needs #2's resolution) instead of clearing the bucket.

## 7. 404-retry semantics and permanently-unactionable events

Endpoints answer 404 while the namespace or bucket is missing so the indexer retries (deliveries
across event types are unordered). If a property's namespace was never created (e.g.
`INIT_PROPERTY_ASSET_WEBHOOK_URL` was unset when the asset registered), its later lifecycle events
retry forever at the 1 h backoff cap — visible in `webhook_events.last_error`. Fix by hand:
`UPDATE webhook_events SET delivered_at = now() WHERE event_id = '...';` (see the indexer
RUNBOOK's webhook section), or set the missing route and let the backlog drain.

## 8. Deployment wiring still to do

The indexer reads the five new `*_WEBHOOK_URL` variables at startup (restart required). Set them
to `https://<this API's host>/webhooks/<endpoint>_devnet`, add the pass-through lines in the
indexer's `docker-compose.yml` / `deploy.yml` (already added on the indexer branch), and create
the matching GitHub secrets (`PROPERTY_CLAIM_STARTED_WEBHOOK_URL`, `SPV_LAWYER_ELECTED_WEBHOOK_URL`,
`DEAL_EXECUTED_WEBHOOK_URL`, `LETTING_AGENT_APPOINTED_WEBHOOK_URL`, `SHARES_TRANSFERRED_WEBHOOK_URL`).
Unset = that event type accumulates undelivered in `webhook_events` and drains when configured.
