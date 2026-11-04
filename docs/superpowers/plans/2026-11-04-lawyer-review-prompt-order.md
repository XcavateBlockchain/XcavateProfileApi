# Lawyer Review Prompt Order Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Create each lawyer document-review bucket with the admin-authored SPV PDF, developer PDF, and review prompt in that order, after the key-sharing message.

**Architecture:** Keep the existing Indexer triggers and ProfileApi idempotent bucket-creation transaction unchanged. Reorder only ProfileApi's initial message writes, pin the wire result through the existing webhook integration host, and add a dashboard browser regression for presentation order and sender identity. Update the existing contract documentation in all three repositories; no Indexer runtime or schema change is needed.

**Tech Stack:** ASP.NET Core/EF Core, NUnit, Nuxt/Vue, Playwright, Rust/Carbon webhook documentation.

**Spec:** `docs/superpowers/specs/2026-10-31-lawyer-review-bucket-design.md`

## Global Constraints

- Exact initial order: `didcomm/key-sharing-v1`, SPV `lawyer/terms-document` (or its missing-document fallback), developer `lawyer/terms-document` (or its fallback), then `lawyer/document-review`.
- The configured bucket admin authors all four initial messages, including the exact existing review prompt.
- Bucket creation, both trigger directions, redelivery, encryption keys, and message counts remain unchanged.
- Preserve all pre-existing uncommitted dashboard edits.
- Do not use worktrees; commit directly in each repository as explicitly requested.
- Do not edit generated Indexer decoders, legacy SubQuery files, or migrations; never run `cargo fmt --all`.

## Review Focus

- Missing PDFs must retain their role positions while the prompt remains last; pin through `Missing_pdfs_send_exact_fallbacks`.
- Legacy plaintext-prompt repair must find the prompt at its new position without duplicating messages; pin through `Legacy_redelivery_adds_exact_metadata_without_key_or_message_changes`.
- Concurrent creation must still produce one key and four messages; retain `Concurrent_triggers_converge_on_one_bucket_key_and_four_messages` and add an explicit tag-order assertion.
- Governance `voting/*` cards must remain incoming System announcements; the new browser test concerns only the ordinary `lawyer/document-review` prompt.
- Existing dashboard changes unrelated to this request must remain intact and be checked with the full targeted test file.

---

### Task 1: ProfileApi creation order and authorship

**Files:**
- Modify: `/home/rosta/XcavateProfileApi/src/XcavateProfileApi/Controllers/WebhooksController.cs`
- Modify: `/home/rosta/XcavateProfileApi/tests/XcavateBuckets.Tests/LawyerReviewBucketWebhookTests.cs`
- Modify: `/home/rosta/XcavateProfileApi/tests/XcavateBuckets.Tests/LawyerReviewBucketPostgresTests.cs`
- Modify: `/home/rosta/XcavateProfileApi/docs/superpowers/specs/2026-10-31-lawyer-review-bucket-design.md`
- Modify: `/home/rosta/XcavateProfileApi/docs/superpowers/plans/2026-10-31-lawyer-review-bucket.md`

**Interfaces:**
- Consumes: existing `MessageService.WriteAsync`, `LawyerTermsBucketProtocol`, and `PrepareLawyerReviewDocumentAsync` contracts.
- Produces: four initial messages ordered by `MessageId` as key sharing, SPV document/fallback, developer document/fallback, prompt; every row's `Contributor` is `BUCKET_ADMIN_ADDRESS`.

- [ ] **Step 1: Write the failing regression expectations**
  Change `Developer_assignment_creates_complete_bucket_and_ordered_messages` to assert documents at indexes 1 and 2 and the encrypted admin prompt at index 3. Change fallback indexes to SPV 1/developer 2, update legacy prompt lookup to index 3, and assert the PostgreSQL race's exact tag sequence.
- [ ] **Step 2: Run the focused tests and verify RED**
  Run `dotnet test tests/XcavateBuckets.Tests --filter "Developer_assignment_creates_complete_bucket_and_ordered_messages|Missing_pdfs_send_exact_fallbacks|Legacy_redelivery_adds_exact_metadata_without_key_or_message_changes"`; expect the old prompt-first order to fail.
- [ ] **Step 3: Implement the minimal reorder**
  In `EnsureLawyerReviewBucketAsync`, write the key first, both prepared documents second/third, and the already-encrypted prompt fourth. Keep `admin` as every contributor and keep all writes in the existing transaction. Bring the existing spec and historical implementation plan's stated order in line with the behavior.
- [ ] **Step 4: Run focused and full bucket tests**
  Re-run the focused filter, then `dotnet test tests/XcavateBuckets.Tests`; expect all enabled tests to pass, with only environment-gated PostgreSQL skips when no test connection is configured.

### Task 2: Dashboard prompt presentation regression

**Files:**
- Modify: `/home/rosta/AssetDidCommDashboard/tests/e2e/lawyer-review.spec.ts`
- Modify: `/home/rosta/AssetDidCommDashboard/docs/lawyer-document-review.md`

**Interfaces:**
- Consumes: the existing `ApiMessage` fixtures and `/indexed-bucket/39` chat rendering; no production interface changes.

- [ ] **Step 1: Write the failing browser contract**
  Add a test with SPV/developer PDF messages followed by an admin-authored `lawyer/document-review` plaintext prompt. Assert those three conversation rows appear in that order and the prompt is not labeled System. This is a characterization regression for the requested contract; if the current renderer already passes, record that no dashboard production change is necessary.
- [ ] **Step 2: Run the browser test**
  Run `npx playwright test tests/e2e/lawyer-review.spec.ts --grep "admin-authored review prompt"`; expect PASS against the existing renderer. Document the initial four-message producer order and that only governance voting cards are forced to System.
- [ ] **Step 3: Run dashboard verification**
  Run the complete lawyer-review browser file, unit/integration tests, typecheck, lint, and production build; expect PASS without discarding any pre-existing edits.

### Task 3: Indexer contract documentation and final commits

**Files:**
- Modify: `/home/rosta/Indexer/RUNBOOK.md`
- Modify: `/home/rosta/Indexer/MIGRATION_LOG.md`

**Interfaces:**
- Consumes: unchanged `spv_lawyer_elected` and `developer_lawyer_assigned` webhook payloads and routes.

- [ ] **Step 1: Document the downstream creation contract**
  State beside the two lawyer-trigger payloads that ProfileApi creates the initial admin-authored messages in key/SPV/developer/prompt order. Add a dated migration-log entry noting that no Indexer runtime, payload, schema, decoder, IDL, or migration change is required.
- [ ] **Step 2: Verify Indexer remains unchanged at runtime**
  Run plain `cargo fmt --check`, `bash scripts/lint-migrations.sh`, and the available standard checks needed for this documentation-only change; expect PASS and no generated diff.
- [ ] **Step 3: Review and commit each repository directly**
  Inspect `git diff --check`, `git status --short`, and the complete diff in each repository. Commit the coordinated changes directly on the current branches with repository-scoped paths, preserving and including the dashboard's pre-existing edits only after their tests pass. Do not push.
