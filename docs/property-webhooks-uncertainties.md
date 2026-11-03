# Property webhooks — current contracts

The indexer and this API keep property namespaces, offers namespaces, encrypted SPV lawyer-terms
buckets, lawyer document-review buckets, and push notifications in sync with on-chain transitions.
The former property-wide legal-process bucket and its dedicated creation, membership, and governance
webhooks are retired: no route in this service creates or updates that bucket.

Every payload carries `event`, its subject keys, `slot`, `tx_signature`, `block_time` (ISO-8601),
and `program`. Delivery is at-least-once with per-event backoff; a non-2xx answer requests a retry.
Every webhook requires `INDEXER_WEBHOOK_SECRET` as either `?secret=` or `X-Webhook-Secret`. Missing
or wrong credentials return 401; leaving the server configuration unset returns 503.

## Namespace and lawyer-bucket events

| Indexer event | Endpoint here | Current behavior |
|---|---|---|
| `property_asset_registered` | `POST /webhooks/init_property_asset_devnet` | Creates the property namespace idempotently. |
| `offers_namespace_created` | `POST /webhooks/offers_namespace_created_devnet` | Creates the property's offers namespace on token claim or relisting. |
| `spv_case_claimed` | `POST /webhooks/spv_case_claimed_devnet` | Creates the candidacy's encrypted SPV lawyer-terms bucket and posts its election-open message. |
| `spv_lawyer_vote_cast` | `POST /webhooks/spv_lawyer_vote_cast_devnet` | Writes the voter-authored encrypted approval to the selected terms bucket. |
| `spv_lawyer_elected` | `POST /webhooks/spv_lawyer_elected_devnet` | Closes every terms bucket in the finalized round and ensures document review once both lawyer seats exist. A rejected round with no terms bucket answers 404 for retry. |
| `developer_lawyer_assigned` | `POST /webhooks/developer_lawyer_assigned_devnet` | Ensures document review once both lawyer seats exist. |
| `documents_confirmed` | `POST /webhooks/documents_confirmed_devnet` | Records the lawyer's verdict and reconciles review status. |
| `silent_verdict_resolved` | `POST /webhooks/silent_verdict_resolved_devnet` | Reconciles review status without inventing a lawyer verdict. |
| `deal_executed` | `POST /webhooks/deal_executed_devnet` | Posts settlement status to an existing review bucket, or answers 404 so an early delivery retries. |

The property namespace is found by `PropertyId == listing_id`, falling back to
`Creator == <PropertyAsset PDA>`. Lawyer buckets are always selected explicitly by their deterministic
identity and category; no fallback property-wide bucket exists.

## SPV lawyer-terms buckets

`spv_case_claimed` creates one `spv-lawyer-terms` bucket per candidacy, named after the property and
candidacy.

- **Authentication and admin.** The route requires `INDEXER_WEBHOOK_SECRET`. `BUCKET_ADMIN_ADDRESS`
  is required and becomes admin and contributor. `BUCKET_ADMIN_X25519_KEY` supplies the admin viewer
  key; the private JWK is used only for recovery and is never logged.
- **Participants.** Current positive `shareHoldings` wallets and the claiming lawyer are contributors,
  with their profile/migrated X25519 keys installed as viewers. Missing required keys answer 404 before
  any bucket write; duplicates remain idempotent by decoded key bytes.
- **Encryption.** Each bucket gets a fresh X25519 keypair. The first message is
  `didcomm/key-sharing-v1`, wrapping the private bucket key separately for every participant.
- **Messages and voting.** The admin writes the encrypted vote prompt and terms document, followed by
  the plaintext deterministic `voting/terms` announcement. Election finalization writes
  `voting/result` to every terms bucket in that round only. A late candidacy copies an existing result
  from another terms bucket in its round or derives it from the election mirror, so it does not depend
  on the retired property-wide bucket.
- **PDF delivery.** The PDF is encrypted as compact JWE and stored at
  `terms-documents/by-candidacy/{candidacy}.pdf`. The message references
  `/api/terms-documents/by-candidacy/{candidacy}` on the CORS-enabled Profile API, preferring
  `PROFILE_API_PUBLIC_URL` over the request origin. The stored ciphertext, not the plaintext PDF, is
  hashed. Later uploads cannot alter the snapshot voters received.
- **Redelivery.** Deterministic bucket identity reuses the key and messages. A per-candidacy PostgreSQL
  advisory lock serializes first delivery, and legacy plaintext references are repaired without
  replacing the bucket or adding duplicate messages.

## Lawyer document-review buckets

A ratified SPV election and a completed developer-lawyer assignment ensure one
`lawyer-document-review` bucket per property namespace. Either trigger can arrive first; the other
reuses the committed bucket.

- **Resolved pair and namespace.** Both lawyer wallets come from the Indexer `listings` connection
  through `GetListingLawyersAsync`; empty and all-zero seats are not ready. Resolution failures answer
  502. `INDEXER_GRAPHQL_URL` points at the Indexer GraphQL API.
- **Property and cluster metadata.** Both triggers accept additive `property_id` and `cluster` fields.
  New values must keep `property_id` equal to `listing_id` and use exactly `devnet` or `mainnet`.
  Redelivery fills only missing metadata, preserving the original key and initial messages.
- **Membership.** `BUCKET_ADMIN_ADDRESS` is admin and contributor. Current positive claimed holders
  have only their X25519 keys added as viewers; both lawyers are contributors and viewers. Missing
  required keys answer 404 before any bucket write.
- **Encryption and messages.** A fresh X25519 keypair unlocks the bucket immediately. The admin
  writes the key-sharing message, then the exact review prompt, then the SPV and developer documents
  in that order. Available PDFs are encrypted under the bucket key and snapshotted content-addressed
  to `terms-documents/by-listing/{listingId}/{spv-lawyer|developer-lawyer}-{lowercase-sha256-of-the-ciphertext}.pdf`,
  referenced as `/api/terms-documents/by-listing/{listingId}/{role}-{hash}` on the CORS-enabled
  Profile API. The object host's own URL is not used because it sends no
  `Access-Control-Allow-Origin` header. An absent document produces the exact plaintext fallback;
  object-storage failures for an existing document answer 502.
- **Verdicts and settlement.** Confirmation messages are encrypted under the review bucket key and
  attributed to the assigned lawyer. The ordered verdict ledger distinguishes a pending second seat
  from a split reset. `deal_executed` posts a distinct finalized result rather than duplicating the
  preceding legal-status result.
- **Idempotency and concurrency.** The deterministic name
  `<namespace> - Lawyer document review` (256-character maximum), category, and namespace identify
  the bucket. A partial unique PostgreSQL index enforces that identity; all writes share one
  transaction. Redelivery and overlapping triggers retain the original key and four setup messages.

## Notification events

These endpoints use the same authentication policy and dedupe natural event keys in
`ProcessedWebhookEvents`; they create push notifications rather than bucket state.

| Indexer event | Endpoint here | Current behavior |
|---|---|---|
| `income_distributed` | `POST /webhooks/income_distributed_devnet` | Pushes rent-received notifications to current holders with profiles. |
| `shares_transferred` | `POST /webhooks/shares_received_devnet` | Pushes a direct-share-received notification to the receiver. |

## Deployment

Each indexer `*_WEBHOOK_URL` has this form:

`https://<this API's host>/webhooks/<endpoint>_devnet?secret=<INDEXER_WEBHOOK_SECRET>`

Configure the same secret in both services and URL-encode the query value when necessary. Unset event
routes accumulate durable outbox rows and drain when configured. This service also needs
`INDEXER_GRAPHQL_URL` for resolution reads. Roll out a newly enforced secret indexer-first, then deploy
this API and monitor the outbox for 401/503 responses.
