# Push Notifications for Bucket Events — Design

Date: 2026-08-01

## Goal

Send push notifications to users (identified by their Polkadot SS58 or Solana base58
address) through the external notifications service at
`https://notifications-api.xcavate.io`
([realXmarketNotificationsApi](https://github.com/XcavateBlockchain/realXmarketNotificationsApi)),
when:

1. A new message is written in a bucket — notify every bucket admin and contributor
   except the sender.
2. A user is added to a bucket or their role changes — notify the added user.

The mobile app (a separate project) registers FCM device tokens and links wallet
addresses with that service; this API only *sends*.

## External API contract

- `POST {base}/api/fcm/send-notification/` (trailing slash required)
- Header: `Authorization: Api-Key <key>`
- Body: `{ "chain": "polkadot" | "solana", "address": "<wallet address>",
  "title": "<= 150 chars", "body": "<= 500 chars" }`
- The service resolves the address to the user's registered devices. Addresses with
  no linked device fail on their side; sends are best-effort.

## Event mapping

| Domain event | Recipients | Notes |
| --- | --- | --- |
| `MessageService.WriteAsync` succeeds | bucket admins + contributors, minus sender, deduplicated | Message content is E2E-encrypted, so the body never includes it — only the sender's display name. |
| `MembershipService.AddAdminAsync` inserts a row | the added address | Fires only on the insert path, not the idempotent early-return. |
| `MembershipService.AddContributorAsync` inserts a row | the added address | Same. |

Role *changes* need no extra hook: this codebase models a role change as remove + add
across the per-role tables, so a promotion/demotion always passes through one of the
two add methods above ("You are now an admin/contributor of this bucket"). Removals
alone (kick-outs) intentionally send nothing — not requested. Viewers are X25519
encryption keys, not wallet addresses, so viewer changes are not notifiable. The
recipient address is used as stored: chain is detected from its format, reusing the
signature schemes' `CanVerify` (same pattern as `MigrationsController`).

## Architecture

**Domain (`XcavateBuckets.Domain`)** — new `Services/IBucketNotifier.cs`:

- `IBucketNotifier` with `MessageWrittenAsync(Bucket, Message, CancellationToken)`
  and `MemberAddedAsync(Bucket, string subjectId, BucketMemberRole, CancellationToken)`;
  `BucketMemberRole` is a new enum (`Admin`, `Contributor`).
- `NullBucketNotifier` no-op default so the domain works without any host wiring.
- `MessageService` and `MembershipService` gain an `IBucketNotifier` constructor
  parameter and call it right after their `SaveChangesAsync`. Implementations must
  never throw and must only enqueue (the call happens inside the mutation's
  transaction, before commit; a phantom notification on a failed commit is accepted —
  pushes are best-effort and non-transactional by nature).

**API host (`XcavateProfileApi/Services/Notifications/`)**:

- `PushNotification` — record `(Chain, Address, Title, Body)`.
- `NotificationQueue` — singleton over a bounded `Channel<PushNotification>`
  (capacity 10 000, drops + logs when full).
- `PushBucketNotifier` — scoped `IBucketNotifier`. Resolves recipients from
  `BucketDbContext`, the sender's display name from `ProfileDbContext.Profiles`
  (nickname, falling back to a truncated address), detects each recipient's chain,
  and enqueues. Whole body wrapped in try/catch: a notification failure never fails
  the mutation.
- `NotificationsApiClient` — typed `HttpClient` that POSTs one payload to
  `api/fcm/send-notification/`; non-success responses are logged, never thrown.
- `NotificationDispatcher` — `BackgroundService` draining the queue sequentially.

**Configuration** (flat env vars, matching house style): `NOTIFICATIONS_API_KEY`
(absent/empty ⇒ feature disabled, `NullBucketNotifier` stays active) and
`NOTIFICATIONS_API_URL` (default `https://notifications-api.xcavate.io`). Wired in
`Program.cs` before `AddBucketDomain()`; `AddBucketDomain()` registers the no-op via
`TryAddScoped` so hosts/tests without the feature keep working. `.env.example`,
`docker-compose.yml` and `.github/workflows/deploy.yml` gain the two variables.

## Notification copy

- Message written — title: bucket name (fallback `Bucket #<id>`), body:
  `New message from <nickname or truncated address>`.
- Member added — title: bucket name, body: `You are now an admin of this bucket.` /
  `You are now a contributor of this bucket.`
- Title truncated to 150 chars, body to 500 (API limits).

## Testing

- Domain: hand-written `RecordingNotifier` (house style — no mocking libraries) held
  by `TestDb`; assert the write path fires with the right message, the add paths fire
  only on insert (idempotent re-add fires nothing).
- `PushBucketNotifier`: real SQLite `BucketDbContext` + `ProfileDbContext` (separate
  in-memory connections), assert enqueued set — recipients minus sender, correct
  chain per address format, nickname fallback.
- `NotificationsApiClient`: `StubHandler` (existing pattern from
  `SolanaSigningTests`) asserting URL, `Authorization: Api-Key` header and exact JSON
  field names.
- `GraphQLSchemaTests` hand-rolled DI gains the `NullBucketNotifier` registration;
  `GraphQLHost` picks it up via `AddBucketDomain()`.
- E2E docker tests are unaffected: no `NOTIFICATIONS_API_KEY` in the test env means
  the feature is off.
