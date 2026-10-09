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
| `spv_case_claimed` | marketplace `claim_spv_case` | `SPV_CASE_CLAIMED_WEBHOOK_URL` | `POST /webhooks/spv_case_claimed_devnet` | **implemented** (posts a `voting/terms` message — see #9) |
| `agent_election_opened` | property program opens a letting-agent election | *(indexer env var)* | `POST /webhooks/agent_election_opened_devnet` | **implemented** (posts a `voting/election` message — see #9) |
| `proposal_created` | property program creates an agent spending proposal | *(indexer env var)* | `POST /webhooks/proposal_created_devnet` | **implemented** (posts a `voting/proposal` message — see #9) |
| `proposal_finalized` | property program finalizes a spending proposal | *(indexer env var)* | `POST /webhooks/proposal_finalized_devnet` | **implemented** (posts a `voting/result` message — see #9) |
| `challenge_created` | property program creates an agent challenge | *(indexer env var)* | `POST /webhooks/challenge_created_devnet` | **implemented** (posts a `voting/challenge` message — see #9) |
| `challenge_finalized` | property program finalizes a challenge | *(indexer env var)* | `POST /webhooks/challenge_finalized_devnet` | **implemented** (posts a `voting/result` message — see #9) |
| `property_holder_changed` | marketplace `reserve_shares` / `buy_property_shares` (added) and `unreserve_shares` / `release_reservation` / `close_cancelled_position` / `withdraw_*` (removed) | `HOLDER_CHANGED_WEBHOOK_URL` | `POST /webhooks/property_holder_changed_devnet` | **implemented** (viewer add/remove + creation-time reconcile — see #3) |

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

## 3. Token-holder read access — IMPLEMENTED (holder events + creation-time reconcile)

"Add property token holders (read) to bucket" maps to the bucket **Viewer** role — a viewer id
is a 32-byte X25519 encryption key (`InputValidator.Hex32Value`), not a wallet. Both halves are
now solved:

- **Wallet → key:** through the profiles (`Profile.X25519Key`, direct wallet key with the
  `WalletMigrations` SS58 fallback — `ResolveViewerKeyAsync`).
- **Holder set:** event-driven via the indexer's `property_holder_changed` (ADR-38:
  reserve/buy → added, unreserve/release/close-cancelled/withdraw → removed), applied
  immediately once the bucket exists. Events from the sale phase (the bucket does not exist
  before claim start) answer **200 "deferred"** — not 404, so no weeks-long retry storm — and
  the claim-started handler reconciles the CURRENT holder set at bucket-creation time via the
  new `IndexerClient.GetCurrentHolderWalletsAsync` (`investorPositions` + `shareHoldings`,
  paged), atomically with the bucket; a reconcile failure is a 502 and the whole event retries.
  Wallets without a registered key are skipped (counted in the response), never retried.

Remaining gaps, both deliberate: (a) the two crank closes that never name the investor on-chain
(`close_reservation`, `close_share_holding`) emit no event — their wallets keep access until
another exit event or a manual cleanup (a batcher-side hook could cover them later); (b) a
holder who registers their X25519 key only after their last event never gets picked up — a
periodic re-reconcile (diff current holders vs viewers) would close this if it ever matters in
practice. `SpvLawyerElectedDevnet` no longer needs to add holders itself: membership is
continuous from reserve time, earlier than the flowchart's lawyer-election step.

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
The voting endpoints (#9) take the same treatment: the indexer routes
`SPV_CASE_CLAIMED_WEBHOOK_URL` (new, ADR-37) plus the existing
`SPV_LAWYER_ELECTED_WEBHOOK_URL`, `AGENT_ELECTION_OPENED_WEBHOOK_URL`,
`PROPOSAL_CREATED_WEBHOOK_URL`, `PROPOSAL_FINALIZED_WEBHOOK_URL`,
`CHALLENGE_CREATED_WEBHOOK_URL`, `CHALLENGE_FINALIZED_WEBHOOK_URL` to these `*_devnet`
paths, plus `HOLDER_CHANGED_WEBHOOK_URL` for the holder-membership route (#3), and this
service needs `INDEXER_GRAPHQL_URL` pointing at the indexer's GraphQL for
the resolution reads.

## 10. IndexerClient I64 wire format — BUG FOUND AND FIXED (affected every resolution read)

The indexer's juniper `I64` scalar is **string-typed on the wire** (`"8"`, not `8`), in both
directions. The IndexerClient as built sent `id` variables as JSON numbers and parsed I64
response fields with `GetInt64()` — every resolution query (`GetListingSpvElectionAsync`,
`GetPropertyLettingAsync`, proposals/challenges, `GetShareAmountAsync`) failed against the
real GraphQL with variable-coercion errors, and would have failed parsing even with correct
variables. The unit tests never caught it: `TestIndexerClient` mocks the interface, so the
client itself was never exercised live. Fixed: variables go out as strings (`I64Var`), and
parsing accepts either scalar flavor (`LongOf`). Verified live against
`https://indexer-devnet.xcavate.io/graphql`: the claim-started reconcile enumerated a real
sold-out listing's holders, and a `spv_case_claimed` call resolved its real election row.
Related gap found in the same pass: the indexer repo's `deploy.yml` never rendered the six
ADR-37 governance `*_WEBHOOK_URL` secrets into `server.env` — added there (with the holder
route) on branch `agent/20261009-holder-membership-webhook`.

## 9. Voting webhooks — IMPLEMENTED, with the design decisions recorded here

Six governance events are announced as plaintext JSON messages in the property's
legal-process bucket, so the messenger SPA and the mobile app can render vote state from
the bucket they already read, without an indexer round-trip: `spv_case_claimed` +
`spv_lawyer_elected` on the marketplace (the SPV lawyer election — the flow the apps
surface as the "terms" vote; it uses only instructions the deployed program already has —
an earlier terms-ratification instruction design was deliberately NOT shipped on-chain),
and `agent_election_opened`, `proposal_created`/`proposal_finalized`,
`challenge_created`/`challenge_finalized` on the property program. Decisions baked into the implementation (`VotingWebhookTests` pins them):

- **System identity.** Messages are posted under the synthetic contributor `system:voting` — not
  a wallet, no signature can exist for it, and it matches no chain format so it never receives
  pushes. Each handler (re-)installs it idempotently, which is also the answer to the
  `deal_executed` sweep (#6): settlement evicts it along with every other contributor, and the
  next post-settlement voting webhook simply re-adds it.
- **Locked bucket.** Webhook-created legal buckets start `IsWritable=false`, and their only admin
  is the PropertyAsset PDA — an address no wallet can sign for — so unlocking can only ever
  happen server-side. The domain has no "write while locked" bypass, and unlocking grants nothing
  the lifecycle does not already intend (contributors are meant to write; viewers stay read-only
  either way), so each handler calls `ResumeWritingAsync` as the namespace creator before its
  first post. The encryption key it sets is **32 zero bytes, an explicit placeholder**: bucket
  content is not end-to-end-encrypted under it. Voting announcements are plaintext by design
  (the server holds no bucket X25519 key); if the legal-document flow later wants real E2E, the
  key ceremony must itself be server-mediated (a future webhook rotating the key as the PDA
  admin) — see #2/#3.
- **Tags need no pre-creation.** `MessageService.WriteAsync` registers an unknown tag on the fly
  (recording the writer as its creator), so the `voting/terms`, `voting/election`,
  `voting/proposal`, `voting/challenge` and `voting/result` tags appear with the first message.
- **Idempotency.** Each handler builds its body deterministically (same event → same bytes) and
  skips the post when a message with the same tag and identical body already exists. A redelivery
  duplicates nothing; a genuinely new event (new round, new proposal/challenge id) produces a
  different body and posts. The guard is exact-match on `(bucket, tag, body)` — cheap, and it
  also means a late redelivered *open* after the *final* does not re-announce anything (different
  tag and body from the result message, but identical to the already-posted open).
- **Payloads are instruction evidence; bodies are resolved.** The indexer's mapper is pure (no
  DB), so a webhook payload carries only ids, PDAs and instruction args. The display facts —
  round/expiry/quorum for opens, tallies + passed/failed for finals — are resolved HERE from the
  indexer's GraphQL (`Services/Indexer/IndexerClient.cs`, env var `INDEXER_GRAPHQL_URL`, first
  outbound indexer client in this service). Consistency is free: the event row and the account
  rows commit in one indexer transaction. A resolution failure (indexer down, or the row
  missing) answers **502 so delivery retries**; `VotingWebhookTests` pins that too. This also
  resolves the older notes above that wanted an outbound indexer client for wallet resolution —
  the mechanism now exists and those endpoints can adopt it when they leave placeholder status.
- **Auto-approved proposals are skipped, not announced.** The property program's `propose` fires
  the webhook on the low-tier path too, but that path closes the just-created Proposal inside the
  same transaction, so no vote ever opens. `proposal_created` with no live row answers 200
  "ignored" rather than advertising a phantom vote.
- **`agent_election_opened` fires per CANDIDACY**, not just at round open (the mapper cannot
  distinguish); the deterministic body (property + round only) makes later candidacies of the
  same round duplicate-guarded, so one round still produces exactly one announcement.
- **Body shape is exactly contract B** (the cross-repo agreement): open messages carry `kind`,
  `listingId`, `id?`/`round?`, `documentsHash?` (terms only), `expiresAt`, `quorumBps?`,
  `thresholdBps?` (proposals only) and `status: "open"`; result messages carry `kind`,
  `listingId`, `id?`/`round?`, `status` (`ratified`/`rejected` for terms, `passed`/`failed` for
  proposals and challenges) and `yes`/`no` (/`abstain`). The outcome is derived here from the
  frozen tallies with the chain's own rule (`IndexerClient.VotePassed`: turnout strictly over
  quorum × supply, yes strictly over no, plus the high-tier threshold for proposals). Nothing
  else — the proposal `amount`/`details_hash` and the challenge `agent_removed` stay readable
  through the indexer GraphQL (`proposals`/`challenges`/`propertyLettings` queries).
- **`expiresAt` is ISO-8601** (the messenger voting schema requires a `Date.parse`-able string);
  it is converted server-side from the on-chain unix `i64`. The webhook payloads themselves keep
  whatever the indexer sent.
- **`documents_hash` / `details_hash` are passed through verbatim** as strings; this API never
  interprets their encoding (the indexer emits them base58-encoded).
- **Content integrity without encryption:** `ContentHash` is the sha256 of the exact stored body
  bytes (`0x`-prefixed uppercase hex), `ContentType` is `application/json`, and `Reference` is a
  stable synthetic path (e.g. `voting/terms/12/round/1/open`) since there is no IPFS CID.
- **`property_id` on property-program events is assumed to equal the marketplace listing id**
  (the same assumption as #4a); the namespace lookup still falls back to the PropertyAsset PDA,
  and when both a payload id and a namespace id exist the payload's own value is what the body
  echoes. Verify on devnet and tighten.
- **Push type:** messages tagged `voting/…` fan out with push type `voting` (next to
  `bucket_message` / `member_added`) so the mobile app can deep-link to the vote. Note the legal
  bucket's membership after settlement is just the operator PDA plus the system identity, so
  election/proposal/challenge pushes currently reach no human wallet — holder push fan-out needs
  the holder-enumeration work in #3 like everything else.
