# Lawyer Document-Review Bucket Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Create an encrypted, participant-ready lawyer document-review bucket after both lawyer seats are filled, with the required admin messages and deployment wiring.

**Architecture:** Indexer adds one durable `developer_lawyer_assigned` outbox event alongside the existing SPV-election event. ProfileApi routes both events into one deterministic ensure operation, resolving the complete lawyer pair and claimed holders from Indexer GraphQL and using the existing bucket, membership, encryption, S3, and message services.

**Tech Stack:** Rust/Carbon/sqlx, ASP.NET Core/EF Core, NUnit, SQLite test host, Docker Compose, GitHub Actions, GitHub CLI.

**Spec:** `docs/superpowers/specs/2026-10-31-lawyer-review-bucket-design.md`

## Global Constraints

- Preserve existing SPV voting, legal-bucket, and per-candidacy terms-bucket behavior.
- Exact bucket category: `lawyer-document-review`; exact suffix: ` - Lawyer document review`.
- Exact prompt: `The SPV Lawyer and Developer Lawyer must now review each other's documents and finalize the terms. Please feel free to discuss any questions here.`
- Exact fallbacks: `SPV Lawyer's terms document is not available. Please send it here.` and `Developer Lawyer's terms document is not available. Please send it here.`
- Claimed holders are current positive `shareHoldings`; holders are viewers only, lawyers are contributors plus X25519 viewers, and the configured admin is admin plus contributor.
- Reuse `LawyerTermsBucketProtocol` for key sharing and `lawyer/terms-document` for both PDF messages.
- Do not edit generated Indexer decoders, legacy SubQuery files, or existing migrations; never run `cargo fmt --all`.
- Do not deploy, commit, push, or mutate production data. GitHub secret changes are limited to the requested webhook/admin configuration.

## Review Focus

- Empty/all-zero lawyer seats and either trigger order must not create a partial bucket; pin with `Incomplete_lawyer_pair_returns_404_without_a_bucket` and `Either_trigger_order_creates_exactly_one_complete_bucket`.
- Overlapping holder/lawyer wallets or identical viewer keys must not downgrade lawyer contributor roles or duplicate memberships; pin with `Duplicate_wallets_and_keys_preserve_roles_and_single_viewers`.
- A long namespace name must still produce a valid deterministic bucket name; pin with `Long_namespace_name_is_truncated_to_256_characters`.
- Missing PDF rows produce fallbacks, but S3 failures for existing rows remain retryable and create no bucket; pin with `Missing_pdfs_send_exact_fallbacks` and `Object_storage_failure_returns_502_without_a_bucket`.
- Redelivery after a complete commit must retain the original encryption key and four messages; pin with `Redelivery_and_the_second_trigger_reuse_bucket_key_and_messages`.

---

### Task 1: Indexer developer-lawyer event

**Files:**
- Modify: `/home/rosta/Indexer/crates/indexer/src/mapping/mod.rs`
- Modify: `/home/rosta/Indexer/crates/indexer/src/mapping/marketplace.rs`
- Test: `/home/rosta/Indexer/crates/indexer/src/mapping/sibling_tests.rs`

**Interfaces:**
- Produces: `event_type::DEVELOPER_LAWYER_ASSIGNED = "developer_lawyer_assigned"` and a transaction-scoped `WebhookEvent` consumed by the existing outbox.
- Payload: `event`, `listing_id`, `listing`, `property`, `developer_lawyer`, `developer_lawyer_registry`, `slot`, `tx_signature`, `block_time`, `program`.

- [ ] **Step 1: Write the failing mapper test**
  Add `assign_developer_lawyer_emits_the_developer_lawyer_assigned_event` using `AssignDeveloperLawyer { listing_id: 7, lawyer: pk(20) }` and six accounts. Assert one event; id `developer_lawyer_assigned:<sig>:0`; lawyer `pk(20)`; registry `pk(4)` (account 3); listing `pk(5)` (account 4); property `pk(6)` (account 5); listing id 7; and slot/signature/block-time/program provenance.
- [ ] **Step 2: Run the test and verify RED**
  Run `cargo test -p indexer assign_developer_lawyer_emits_the_developer_lawyer_assigned_event -- --exact` from `/home/rosta/Indexer`; expect no matching test or a compile failure because the event variant is not implemented. If the filter needs the full module path for `--exact`, rerun with the reported full test name.
- [ ] **Step 3: Implement the event**
  Add the event-type constant and a `MarketplaceInstruction::AssignDeveloperLawyer(args)` mapper branch using account positions above and the existing `WebhookEvent`/JSON conventions. Update the producer module docs to include the new trigger.
- [ ] **Step 4: Run focused tests and formatting**
  Run the new test, the existing `finalize_spv_election_emits_the_lawyer_elected_event` test, and plain `cargo fmt --check`; expect PASS.

### Task 2: ProfileApi lawyer-pair GraphQL resolution

**Files:**
- Modify: `/home/rosta/XcavateProfileApi/src/XcavateProfileApi/Services/Indexer/IndexerClient.cs`
- Test: `/home/rosta/XcavateProfileApi/tests/XcavateBuckets.Tests/TestIndexerClient.cs`
- Test: `/home/rosta/XcavateProfileApi/tests/XcavateBuckets.Tests/LawyerReviewBucketWebhookTests.cs`

**Interfaces:**
- Produces: `sealed record ListingLawyersInfo(string SpvLawyer, string DeveloperLawyer)`.
- Produces: `Task<ListingLawyersInfo?> IIndexerClient.GetListingLawyersAsync(long listingId, CancellationToken ct)`.
- Consumes: existing `listings(listingId:, first:, offset:)` connection and `I64Var`/`FirstNode` helpers.

- [ ] **Step 1: Write the failing prerequisite tests**
  In the new webhook suite, seed `TestIndexerClient.ListingLawyers` and add `Incomplete_lawyer_pair_returns_404_without_a_bucket` cases for a missing row, empty SPV seat, all-zero SPV seat, empty developer seat, and all-zero developer seat. Initially fail compilation on the missing record/method.
- [ ] **Step 2: Run and verify RED**
  Run `dotnet test tests/XcavateBuckets.Tests --filter Incomplete_lawyer_pair_returns_404_without_a_bucket`; expect the new interface member to be absent.
- [ ] **Step 3: Implement the GraphQL query**
  Query `nodes { spvLawyer developerLawyer }`, return null for no row, and populate the new record using the existing string/scalar parsing style. Add the settable stub property and interface implementation.
- [ ] **Step 4: Compile the focused suite**
  Run the same filtered test; expect it to reach the webhook and fail for the missing review flow rather than missing types. Keep it RED until Task 3.

### Task 3: Atomic lawyer-review bucket and dual webhook triggers

**Files:**
- Modify: `/home/rosta/XcavateProfileApi/src/XcavateProfileApi/Controllers/WebhooksController.cs`
- Modify: `/home/rosta/XcavateProfileApi/src/XcavateProfileApi/Controllers/TermsDocumentController.cs`
- Test: `/home/rosta/XcavateProfileApi/tests/XcavateBuckets.Tests/LawyerReviewBucketWebhookTests.cs`
- Test: `/home/rosta/XcavateProfileApi/tests/XcavateBuckets.Tests/WebhookAuthenticationTests.cs`

**Interfaces:**
- Consumes: `GetListingLawyersAsync`, `GetShareHolderWalletsAsync`, `ResolveViewerKeyAsync`, `FindNamespaceAsync`, `LawyerTermsBucketProtocol`, and the existing domain services.
- Produces: `POST /webhooks/developer_lawyer_assigned_devnet` accepting `DeveloperLawyerAssignedPayload` with the Task 1 wire fields.
- Produces: `EnsureLawyerReviewBucketAsync(long listingId, string? listing, string? property, string? txSignature, string? program, CancellationToken ct)` returning the existing lawyer-terms-style setup tuple (`Failure`, `BucketId`, `Created`, `HolderCount`).
- Produces: `TermsDocumentController.ReviewSnapshotObjectKey(long listingId, string role)` using `terms-documents/by-listing/{listingId}/{role}.pdf` with constant roles `spv-lawyer` and `developer-lawyer`.

- [ ] **Step 1: Complete the failing integration tests**
  Cover complete setup; exact name/category/properties; admin/contributor/viewer roles; encryption and recipient count; four-message order; exact prompt and PDF bytes/hashes/references; both missing-PDF fallbacks; both trigger orders; redelivery; incomplete pairs; missing admin/namespace/keys; GraphQL/holder/S3 failures; long names; duplicate wallets/keys; and invalid payloads. Add the new route to all authentication data-driven tests. Seed the SPV route's existing legal bucket and election prerequisites without changing its legacy assertions.
- [ ] **Step 2: Run the new suite and verify RED**
  Run `dotnet test tests/XcavateBuckets.Tests --filter LawyerReviewBucketWebhookTests`; expect failures for the missing developer route and absent review bucket.
- [ ] **Step 3: Implement the shared ensure flow**
  Validate admin and namespace, reuse an existing deterministic bucket, resolve and validate both lawyer seats, enumerate/deduplicate claimed holders, and resolve all required viewer keys before writes. Preload available PDFs and snapshot them under deterministic role keys; absent rows produce the exact fallback while storage errors return 502. Create bucket/memberships/key/messages in one transaction. The message order is key sharing, prompt, SPV PDF/fallback, developer PDF/fallback; the admin authors all four.
- [ ] **Step 4: Wire both triggers**
  Add the authenticated developer-assignment action and snake_case payload model. Invoke the same ensure flow from the successful/ratified SPV-election path after existing membership work, returning any setup failure so Indexer retries. Add response fields `lawyerReviewBucketId`, `lawyerReviewBucketCreated`, and `lawyerReviewHolderCount` without removing existing fields.
- [ ] **Step 5: Run focused and regression tests**
  Run the new suite, `LawyerTermsBucketWebhookTests`, `VotingWebhookTests`, and `WebhookAuthenticationTests`; expect all PASS.

### Task 4: Deployment, configuration, secrets, and operator docs

**Files:**
- Modify: `/home/rosta/Indexer/crates/indexer/src/config.rs`
- Modify: `/home/rosta/Indexer/.env.example`
- Modify: `/home/rosta/Indexer/docker-compose.yml`
- Modify: `/home/rosta/Indexer/.github/workflows/deploy.yml`
- Modify: `/home/rosta/Indexer/docs/deployment.md`
- Modify: `/home/rosta/Indexer/RUNBOOK.md`
- Modify: `/home/rosta/Indexer/DECISIONS.md` (new ADR-48)
- Modify: `/home/rosta/Indexer/MIGRATION_LOG.md`
- Modify: `/home/rosta/XcavateProfileApi/docker-compose.yml`
- Modify: `/home/rosta/XcavateProfileApi/.github/workflows/deploy.yml`
- Modify: `/home/rosta/XcavateProfileApi/docs/property-webhooks-uncertainties.md`

**Interfaces:**
- Produces: `DEVELOPER_LAWYER_ASSIGNED_WEBHOOK_URL` routed to `event_type::DEVELOPER_LAWYER_ASSIGNED`.
- Consumes: existing ProfileApi environment secret `BUCKET_ADMIN_ADDRESS` and existing Indexer repository secret `SPV_LAWYER_ELECTED_WEBHOOK_URL`.

- [ ] **Step 1: Wire Indexer configuration**
  Add the route and every Compose/deploy render path, including `dotenv_optional`; document the event, payload, dedup subject, authenticated URL, and dual-trigger/idempotent consumer contract. Add ADR-48 and a dated migration-log entry after verification results are known.
- [ ] **Step 2: Correct ProfileApi environment loading**
  Render `BUCKET_ADMIN_ADDRESS=${{ secrets.BUCKET_ADMIN_ADDRESS }}` rather than the absent `HETZNER_BUCKET_ADMIN_ADDRESS`. Render/pass `INDEXER_GRAPHQL_URL` consistently with the existing runtime variables. Update the deployment and lawyer-review documentation to match actual behavior.
- [ ] **Step 3: Validate configuration**
  Parse both workflow files as YAML and run `docker compose config --quiet` with non-secret placeholder environment values in each repository; expect no errors. Inspect rendered text/code to ensure both new webhook URL and admin mappings occur in every required path.
- [ ] **Step 4: Update GitHub secrets**
  Confirm `SPV_LAWYER_ELECTED_WEBHOOK_URL` and `BUCKET_ADMIN_ADDRESS` remain present. Set Indexer repository secret `DEVELOPER_LAWYER_ASSIGNED_WEBHOOK_URL` to `https://profile-api.xcavate.io/webhooks/developer_lawyer_assigned_devnet?secret=<the existing INDEXER_WEBHOOK_SECRET>` without logging the value. If the existing shared secret is not available through an authorized source, do not rotate it or invent a placeholder: finish all other work and report this exact secret-value blocker.

### Task 5: Full verification and review

**Files:**
- Update verification results in the two documentation logs from Task 4.

**Interfaces:**
- Consumes all prior tasks; produces the tested deliverable, not a deployment.

- [ ] **Step 1: Run Indexer checks**
  From `/home/rosta/Indexer`, run plain `cargo fmt && cargo fmt --check`, `cargo clippy --workspace --all-targets -- -D warnings`, `SQLX_OFFLINE=true cargo build --workspace --locked`, `cargo test --workspace --locked`, both documented `cargo sqlx prepare --check` commands, and `bash scripts/lint-migrations.sh`. Use the documented test Postgres if required. Run `scripts/agent/verify-devnet.sh` if the environment permits; report any unavailable external dependency plainly.
- [ ] **Step 2: Run ProfileApi checks**
  Run `dotnet test tests/XcavateBuckets.Tests`, `dotnet test tests/XcavateProfileApiSolanaClient.Tests`, and `./run_e2e_tests.sh`; expect all PASS. Exercise the new webhook through the real HTTP test host, including both event orders and fallback messages, rather than relying only on compilation.
- [ ] **Step 3: Review the final diff**
  Check both repositories for unintended/generated changes, stale comments/docs, secret leakage, exact required strings, and a clean final `git diff --check`. Request one fresh code review and address evidence-backed findings before completion.
