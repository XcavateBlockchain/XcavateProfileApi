# Lawyer document-review bucket design

## Intent

Once a listing has both a ratified SPV lawyer and an assigned developer lawyer, give the lawyers and claimed property-token holders an immediately usable discussion bucket in that property's namespace. The bucket admin starts the review with the required instruction and each lawyer's current LawyerMainPage terms PDF, or the requested fallback text when a PDF has not been uploaded.

## Goals

- Preserve the existing SPV election voting and legal-bucket behavior.
- Handle either lawyer transition being indexed and delivered first.
- Create one deterministic lawyer document-review bucket per property namespace.
- Install the configured bucket admin, claimed-holder viewers, lawyer contributors/viewers, and real X25519 key sharing.
- Send the key-sharing, SPV document, developer document, and instruction messages in that order, all authored by the bucket admin.
- Make redelivery and retries reuse the completed bucket without duplicate messages or key rotation.
- Wire configuration through the existing durable-outbox, Compose, and GitHub Actions patterns.

## Non-goals and anti-goals

- Do not replace or change the existing per-candidacy `spv-lawyer-terms` buckets.
- Do not add lawyer profiles, X25519 keys, PDFs, namespaces, or bucket state to Indexer.
- Do not add a second partial-assignment store to ProfileApi.
- Do not silently omit a required participant whose X25519 key is missing.
- Do not make missing terms PDFs prevent bucket creation or the remaining messages.
- No mobile/UI changes or Indexer database migration are required.

## Approaches

1. **Dual triggers with one downstream ensure operation (selected).** Add `developer_lawyer_assigned` to the existing Indexer mapper/outbox and retain `spv_lawyer_elected` as the other trigger. Both ProfileApi handlers call the same idempotent ensure method, which resolves the current listing's two lawyer wallets through GraphQL. This follows the established event boundary and handles either arrival order. The tradeoff is one additional event type and route.
2. **SPV-election trigger only.** This adds no event type, but misses properties where the developer lawyer is assigned after SPV finalization unless partial state is persisted and a second trigger is added anyway. It is therefore incomplete.
3. **Enrich one webhook with both lawyers.** This avoids a GraphQL lookup but makes the producer depend on account state the pure instruction mapper does not have, and still needs a second trigger for the opposite order. It weakens the existing architecture without removing downstream work.

## Indexer contract

Add the stable event type `developer_lawyer_assigned`, emitted by marketplace `assign_developer_lawyer` with a transaction-and-instruction-path event id. Its snake_case payload contains:

- `event`, `listing_id`, `listing`, `property`
- `developer_lawyer` (instruction lawyer wallet), `developer_lawyer_registry`
- `slot`, `tx_signature`, `block_time`, `program`

Add `DEVELOPER_LAWYER_ASSIGNED_WEBHOOK_URL` to route configuration, `.env.example`, Compose, deployment rendering, and operator documentation. The configured value is the full authenticated URL:

`https://profile-api.xcavate.io/webhooks/developer_lawyer_assigned_devnet?secret=<INDEXER_WEBHOOK_SECRET>`

The existing `SPV_LAWYER_ELECTED_WEBHOOK_URL` remains the other trigger. No decoder, IDL, schema, migration, or generated-file change is involved.

## ProfileApi flow

Add `POST /webhooks/developer_lawyer_assigned_devnet`, protected by the existing `IndexerWebhookSecret` attribute. Extend the existing SPV-election handler so a ratified election also invokes the review-bucket ensure operation after its existing voting/membership work succeeds.

The shared ensure operation:

1. Validates the trigger, configured `BUCKET_ADMIN_ADDRESS`, and property namespace. Missing admin configuration answers 503; a namespace or lawyer assignment that is not yet available answers a retryable non-2xx, consistent with the existing webhooks.
2. Resolves `spvLawyer` and `developerLawyer` from the Indexer `listings` connection. Empty and all-zero seats mean the pair is not ready. GraphQL/query failures answer 502.
3. Looks up the bucket by namespace, category `lawyer-document-review`, and deterministic name `<property namespace name> - Lawyer document review`, truncated to the existing 256-character limit. An existing bucket is returned unchanged.
4. Resolves current positive claimed `shareHoldings` wallets through the existing `GetShareHolderWalletsAsync` method. This matches the established definition of claimed property-token holders and excludes mere reservations.
5. Resolves X25519 viewer keys for all claimed holders and both lawyers, deduplicating wallets and keys. A missing required key answers 404 before bucket writes. The admin is both admin and contributor so it can author messages; claimed holders receive viewer memberships only, while the two lawyers are contributors and have their X25519 keys added as viewers. Role unions remain idempotent if wallets overlap.
6. Creates a fresh bucket key with the existing `LawyerTermsBucketProtocol.CreateKeySharing`, installs the public key with `ResumeWritingAsync`, and writes the encrypted private-key-sharing message first.
7. Writes the SPV lawyer's document second and developer lawyer's document third. For each, use the wallet's current `LawyerTermsDocument`; download and snapshot the bytes under a stable per-listing/role object key, then reference the snapshot with its original filename, PDF content type, and SHA-256 hash. Reuse the existing `lawyer/terms-document` tag so current document consumers recognize both PDFs.
8. Writes the exact admin instruction fourth, after both documents:
   `The SPV Lawyer and Developer Lawyer must now review each other's documents and finalize the terms. Please feel free to discuss any questions here.`
9. If a document row is absent, write the exact required fallback as a plaintext admin message instead:
   - `SPV Lawyer's terms document is not available. Please send it here.`
   - `Developer Lawyer's terms document is not available. Please send it here.`
   Object-storage failures for a document that should exist remain retryable 502 errors rather than being misreported as an absent upload.

All bucket/membership/key/message writes share one transaction. S3 snapshots occur before the transaction under deterministic keys. A retry after commit reuses the bucket and does not rotate its key or duplicate messages. A partial unique PostgreSQL index on `(NamespaceId, Category, Name)` for `lawyer-document-review` closes the overlapping-trigger race; the loser rolls back and returns the winner's committed bucket. A deterministic PostgreSQL integration test pauses both requests at insertion and proves one bucket, one key, and four messages.

## Configuration correction

The ProfileApi GitHub environment already contains `BUCKET_ADMIN_ADDRESS`. Change `deploy.yml` to render that secret directly instead of the absent `HETZNER_BUCKET_ADMIN_ADDRESS`. Compose already passes `BUCKET_ADMIN_ADDRESS` and the controller already reads it; no new runtime variable is needed. Also pass the already-supported `INDEXER_GRAPHQL_URL` through Compose explicitly and render it in deployment so the lawyer-resolution dependency has the same configuration path as the other runtime settings.

## Verification

- Indexer mapper tests pin the new payload, account positions, event id, and provenance.
- ProfileApi integration tests exercise real controller/domain services against SQLite and stubbed GraphQL/S3 for: complete setup, exact role membership, four-message order and content, each missing-PDF fallback, either trigger order, duplicate delivery, missing lawyer/namespace/admin/viewer key, indexer and storage failures, and authentication.
- Existing lawyer-terms, voting, authentication, and full solution suites remain green.
- Validate Compose and workflow YAML, run the Indexer formatting/clippy/locked build/test/sqlx/migration checks, and perform the available end-to-end webhook exercises without deploying or mutating production data.
