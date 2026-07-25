# Bucket Pallet → C# GraphQL API — Design

**Date:** 2026-07-25
**Status:** Approved

## Goal

Reimplement the Substrate pallet [`pallet-bucket`](https://github.com/XcavateBlockchain/xcavate-node-paseo/tree/dev/pallets/pallet-bucket)
as a C# GraphQL API inside the XcavateProfile solution.

The API is **standalone**: it owns its data in PostgreSQL and ports the pallet's rules into C#
domain services. There is no chain and no extrinsic submission. Read operations expose the same
entity types and field names that the [SubQuery indexer](https://github.com/XcavateBlockchain/xcavate-indexer)
served for this pallet, so existing consumers keep their selection sets. Write operations are
authenticated with sr25519 signatures using the scheme XcavateProfileApi already implements.

**GraphQL server library is Hot Chocolate**, not StrawberryShake. StrawberryShake is ChilliCream's
*client* library; it is used in this design for the typed client in `XcavateProfileApiClient`.

## Scope

In scope — the 9 indexer entity types produced by `pallet-bucket`:

`Namespace`, `NamespaceManager`, `Bucket`, `BucketAdmin`, `BucketContributor`, `BucketViewer`,
`Tag`, `TagMessageCount`, `Message`.

Out of scope — the indexer's other 12 types, which come from different pallets:
`RealEstateNft`, `RealWorldAsset`, `RealWorldAssetOwner`, `NftFractionalized`, `NftUnified`,
`MarketplaceOngoingObjectListings`, `MarketplaceShareOwners`, `MarketplaceShareListings`,
`MarketplaceOngoingOffers`, `MarketplaceListingSpvProposals`, `MarketplaceOngoingLawyerVotings`,
`MarketplaceUserLawyerVotes`, `MarketplacePropertyLawyers`.

Also out of scope: on-chain fees. `FeeNamespace`, `FeeBucket`, `FeeMessage`, `FeeTag` and the
`UnableToPayFees` error have no off-chain analogue and are not ported.

## Project layout

| Piece | Location |
|---|---|
| Entities, `BucketDbContext`, migrations, domain services, error types | `src/XcavateBuckets.Domain/` (new class library, `net10.0`) |
| Hot Chocolate `Query`/`Mutation` types, object type configs, DataLoaders, signature middleware | `src/XcavateProfileApi/GraphQL/` |
| StrawberryShake typed client + signing `DelegatingHandler` | `src/XcavateProfileApiClient/` |
| Domain rule tests | `tests/XcavateBuckets.Tests/` (new) |
| GraphQL E2E tests | `tests/XcavateProfile.ApiTests/` (existing project) |

`XcavateBuckets.Domain` takes no dependency on ASP.NET Core, Hot Chocolate, or `Substrate.NET.API`,
so every ported pallet rule is unit-testable without a web host or a keypair.

The GraphQL endpoint is `/graphql` on the **existing** `XcavateProfileApi` host. It reuses the
host's CORS policy, Docker image, deploy pipeline, and `ISignatureValidator`.

`BucketDbContext` is separate from `ProfileDbContext` and targets the same PostgreSQL database with
its own migrations history table (`__EFMigrationsHistory_Buckets`), so profile migrations and
bucket migrations stay independent.

## Data model

Mapping from pallet storage to tables:

| Pallet storage | Table | Key |
|---|---|---|
| `Namespaces: NamespaceId → NamespaceMetadata` | `namespaces` | `namespace_id` (identity) |
| `Managers: (NamespaceId, SubjectId) → ()` | `namespace_managers` | (`namespace_id`, `manager`) |
| `Buckets: (NamespaceId, BucketId) → Bucket` | `buckets` | `bucket_id` (identity) + `namespace_id` FK |
| `Admins: (BucketId, SubjectId) → ()` | `bucket_admins` | (`bucket_id`, `subject_id`) |
| `Contributors: (BucketId, SubjectId) → ()` | `bucket_contributors` | (`bucket_id`, `subject_id`) |
| `Viewers: (BucketId, ViewerId) → ()` | `bucket_viewers` | (`bucket_id`, `viewer_id`) |
| `Tags: (BucketId, Tag) → ()` | `tags` | (`bucket_id`, `tag_name`) |
| `TagMessages: (BucketId, Tag) → u32` | `tag_message_counts` | (`bucket_id`, `tag_name`) |
| `Messages: (BucketId, MessageId) → Message` | `messages` | (`bucket_id`, `message_id`) |
| `NextNamespaceId`, `NextBucketId` | PostgreSQL identity columns | — |
| `Bucket.next_message_id` | `buckets.next_message_id` | per-bucket counter |

Behaviours preserved deliberately:

- **Bucket ids are global.** `NextBucketId` is a single `StorageValue`, not per-namespace, so
  `Bucket.id` is the bucket id alone. Matches the indexer (`Bucket.id = bucketId.toString()`).
- **Message ids restart per bucket.** `next_message_id` lives on the bucket record, so
  `Message.id` is `"{bucketId}-{messageId}"`. Matches the indexer.
- **A fresh bucket is Locked.** `Status::default()` is `Locked`, so a newly created bucket rejects
  messages until `resumeWriting` supplies an encryption key.
- **Status flattening.** `Status::Writable(KeyId) | Locked` becomes `is_writable bool` +
  `encryption_key text NULL`, exactly as the indexer stored it.
- **Namespace/bucket mismatch is an error.** `Admins`/`Contributors`/`Viewers` are keyed by bucket
  id alone, but every mutation that takes both ids checks `Buckets::contains_key(namespace_id,
  bucket_id)` first. Passing a bucket id that belongs to a different namespace yields
  `UNKNOWN_BUCKET`.

`properties` (the pallet's `BoundedBTreeMap<key, value, MaxProperties>`) is stored as `jsonb` and
exposed as a JSON-encoded `String`, matching the indexer's `Namespace.properties`.

`TagMessageCount` is a separate table mirroring the pallet's `TagMessages` storage, as the indexer
had it. `Tag.messageCount` resolves from it.

## Schema deltas from the indexer's schema

### Removed

All block-height fields, replaced by wall-clock timestamps:

| Indexer field | Replacement |
|---|---|
| `Bucket.createdBlock: Int!` | `Bucket.createdAt: DateTime!` |
| `Namespace.createdAt: Int` (block number) | `Namespace.createdAt: DateTime!` |
| `NamespaceManager.addedBlock: Int!` | `NamespaceManager.addedAt: DateTime!` |
| `BucketAdmin.addedBlock: Int!` | `BucketAdmin.addedAt: DateTime!` |
| `BucketContributor.addedBlock: Int!` | `BucketContributor.addedAt: DateTime!` |
| `BucketViewer.addedBlock: Int!` | `BucketViewer.addedAt: DateTime!` |
| `Tag.createdBlock: Int!` | `Tag.createdAt: DateTime!` |
| `TagMessageCount.updatedBlock: Int!` | `TagMessageCount.updatedAt: DateTime!` |
| `Message.createdBlock: Int!` | `Message.createdAt: DateTime!` |

This is a breaking change to the schema shape: a query selecting `createdBlock` will fail.

### Fixed

`Bucket.namespace: Namespace!` now exists as a real relation. The indexer declared
`Namespace.buckets: [Bucket!] @derivedFrom(field: "namespace")` against a `namespace` field that
`Bucket` never had — `Bucket` only carried a scalar `namespaceId`, assigned directly in
`upsertBucketFromStorage`.

### Unified

The indexer had `Namespace.namespaceId: Int!` but `Bucket.namespaceId: BigInt!`. All ids
(`namespaceId`, `bucketId`, `messageId`) use `BigInt` — a custom Hot Chocolate scalar backed by
`long`, serialized as a string on the wire to match SubQuery's `BigInt`. This is one breaking wire
change (`Namespace.namespaceId` goes number → string) instead of three the other way.

### Added (additive; breaks no existing query)

- `Bucket.properties: String` and `Message.properties: String` — the pallet stores properties on
  bucket and message metadata; the indexer never decoded them.
- `Bucket.tags: [Tag!]!` — the indexer gave `Tag` a `bucket` FK but no back-reference.
- `Namespace.updatedAt`, `Bucket.updatedAt`.

### Retained redundancy

For wire compatibility, these indexer quirks are kept as-is:

- `Message.messageId` and `Message.messageIdNumber` hold the same value (the indexer indexed only
  the latter).
- `BucketAdmin`/`BucketContributor`/`BucketViewer` keep `bucketIdNumber` alongside the `bucket`
  relation.
- `Tag.messageCount` and `TagMessageCount.count` hold the same value.

## Target GraphQL schema

```graphql
scalar BigInt    # backed by long; string on the wire, matching SubQuery
scalar DateTime

type Namespace {
  id: ID!                          # "{namespaceId}"
  namespaceId: BigInt!
  name: String
  schemaUri: String
  properties: String               # JSON-encoded key/value map
  creator: String                  # SS58 of creator
  createdAt: DateTime!
  updatedAt: DateTime!
  buckets: [Bucket!]!
  managers: [NamespaceManager!]!
}

type NamespaceManager {
  id: ID!                          # "{namespaceId}-{manager}"
  namespace: Namespace!
  manager: String!                 # SS58
  addedAt: DateTime!
}

type Bucket {
  id: ID!                          # "{bucketId}"
  namespaceId: BigInt!
  bucketId: BigInt!
  namespace: Namespace!
  creator: String                  # SS58
  name: String
  category: String
  properties: String
  isWritable: Boolean!
  encryptionKey: String            # 32-byte hex, non-null iff isWritable
  createdAt: DateTime!
  updatedAt: DateTime!
  admins: [BucketAdmin!]!
  contributors: [BucketContributor!]!
  viewers: [BucketViewer!]!
  tags: [Tag!]!
  messages: [Message!]!
}

type BucketAdmin {
  id: ID!                          # "{bucketId}-{subjectId}"
  bucket: Bucket!
  bucketIdNumber: BigInt!
  subjectId: String!               # SS58
  addedAt: DateTime!
}

type BucketContributor {
  id: ID!                          # "{bucketId}-{subjectId}"
  bucket: Bucket!
  bucketIdNumber: BigInt!
  subjectId: String!               # SS58
  addedAt: DateTime!
}

type BucketViewer {
  id: ID!                          # "{bucketId}-{viewerId}"
  bucket: Bucket!
  bucketIdNumber: BigInt!
  viewerId: String!                # 32-byte hex X25519 public key
  addedAt: DateTime!
}

type Tag {
  id: ID!                          # "{bucketId}-{tagName}"
  bucket: Bucket!
  tagName: String!
  creator: String                  # SS58
  messageCount: Int
  createdAt: DateTime!
}

type TagMessageCount {
  id: ID!                          # "{bucketId}-{tagName}"
  bucket: Bucket!
  tagName: String!
  count: Int!
  updatedAt: DateTime!
}

type Message {
  id: ID!                          # "{bucketId}-{messageId}"
  bucket: Bucket!
  messageId: BigInt!
  messageIdNumber: BigInt!
  contributor: String!             # SS58 of the writer
  reference: String                # storage-layer reference, e.g. IPFS CID
  tag: String
  description: String
  contentType: String
  contentHash: String              # 32-byte hex
  properties: String
  ipfsContent: String              # see "IPFS content resolution"
  createdAt: DateTime!
}
```

Root queries — plural connection plus singular-by-id per entity. Every plural field takes the same
`where` / `order` / paging arguments spelled out on `namespaces` below; `(...)` elides that
repetition:

```graphql
type Query {
  namespaces(where: NamespaceFilterInput, order: [NamespaceSortInput!],
             first: Int, after: String, last: Int, before: String): NamespacesConnection
  namespace(id: ID!): Namespace

  buckets(...): BucketsConnection
  bucket(id: ID!): Bucket

  messages(...): MessagesConnection
  message(id: ID!): Message

  tags(...): TagsConnection
  tag(id: ID!): Tag

  namespaceManagers(...): NamespaceManagersConnection
  bucketAdmins(...): BucketAdminsConnection
  bucketContributors(...): BucketContributorsConnection
  bucketViewers(...): BucketViewersConnection
  tagMessageCounts(...): TagMessageCountsConnection
}
```

Connections expose `nodes`, `totalCount` and `pageInfo` via `[UsePaging]`. Filtering uses
`[UseFiltering]` (`where:`), sorting `[UseSorting]` (`order:`), and `[UseProjection]` pushes
selections into SQL. Nested relations are plain lists resolved through DataLoaders, so a nested
selection does not N+1.

Reads require no signature, matching the existing profile API's GET endpoints.

## Mutations

20 mutations, one per pallet extrinsic (call indices 0–19).

```graphql
input PropertyInput { key: String!  value: String! }

input NamespaceMetadataInput {
  name: String!
  schemaUri: String
  properties: [PropertyInput!]
}

input BucketMetadataInput {
  name: String!
  category: String!
  properties: [PropertyInput!]
}

input MessageMetadataInput {
  description: String!
  contentType: String!
  contentHash: String!             # 32-byte hex
  properties: [PropertyInput!]
}

input MessageInput {
  reference: String!
  tag: String
  metadata: MessageMetadataInput!
}

type Mutation {
  createNamespace(metadata: NamespaceMetadataInput!): Namespace!
  addManager(namespaceId: BigInt!, newManager: String!): NamespaceManager!
  removeManager(namespaceId: BigInt!, oldManager: String!): Boolean!
  createBucket(namespaceId: BigInt!, metadata: BucketMetadataInput!): Bucket!
  addAdmin(namespaceId: BigInt!, bucketId: BigInt!, admin: String!): BucketAdmin!
  removeAdmin(namespaceId: BigInt!, bucketId: BigInt!, admin: String!): Boolean!
  addContributor(namespaceId: BigInt!, bucketId: BigInt!, contributor: String!): BucketContributor!
  removeContributor(namespaceId: BigInt!, bucketId: BigInt!, contributor: String!): Boolean!
  addViewer(namespaceId: BigInt!, bucketId: BigInt!, viewer: String!): BucketViewer!
  removeViewer(namespaceId: BigInt!, bucketId: BigInt!, viewer: String!): Boolean!
  pauseWriting(namespaceId: BigInt!, bucketId: BigInt!): Bucket!
  resumeWriting(namespaceId: BigInt!, bucketId: BigInt!, newEncryptionKey: String!): Bucket!
  rotateKey(namespaceId: BigInt!, bucketId: BigInt!, newEncryptionKey: String!): Bucket!
  createTag(bucketId: BigInt!, newTag: String!): Tag!
  write(namespaceId: BigInt!, bucketId: BigInt!, message: MessageInput!): Message!

  forceRemoveNamespace(namespaceId: BigInt!): Boolean!
  forceRemoveBucket(namespaceId: BigInt!, bucketId: BigInt!): Boolean!
  forceRemoveTag(bucketId: BigInt!, tag: String!): Boolean!
  forceRemoveMessage(bucketId: BigInt!, messageId: BigInt!): Boolean!
  forceAddManager(namespaceId: BigInt!, manager: String!): NamespaceManager!
}
```

### Authorization and preconditions

Ported verbatim from `functions.rs`. Note the deliberate asymmetry: `addAdmin` requires a
**namespace manager**, while `addContributor` requires a **bucket admin**.

| Mutation | Requires | Preconditions |
|---|---|---|
| `createNamespace` | any signed caller | — (caller becomes first manager) |
| `addManager` | manager of namespace | namespace exists |
| `removeManager` | manager of namespace | namespace exists; at least two managers exist before removal |
| `createBucket` | manager of namespace | namespace exists |
| `addAdmin` | **manager of namespace** | bucket exists in that namespace |
| `removeAdmin` | **manager of namespace** | bucket exists in that namespace |
| `addContributor` | **admin of bucket** | bucket exists in that namespace |
| `removeContributor` | **admin of bucket** | bucket exists in that namespace |
| `addViewer` | admin of bucket | bucket exists in that namespace |
| `removeViewer` | admin of bucket | bucket exists in that namespace |
| `pauseWriting` | admin of bucket | bucket exists |
| `resumeWriting` | admin of bucket | bucket exists (locked is allowed) |
| `rotateKey` | admin of bucket | bucket exists **and is writable** |
| `createTag` | admin of bucket | — (see note) |
| `write` | **contributor of bucket** | bucket exists; bucket is writable; tag exists if given |
| `forceRemoveNamespace` | admin address | no buckets and no managers for the namespace |
| `forceRemoveBucket` | admin address | no messages, admins, contributors, viewers or tags |
| `forceRemoveTag` | admin address | tag message count is 0 |
| `forceRemoveMessage` | admin address | message exists |
| `forceAddManager` | admin address | namespace exists |

Notes:

- `createNamespace` inserts the namespace and the caller as its first manager, mirroring
  `do_create_namespace` calling `do_add_manager`.
- `resumeWriting` is `do_set_key(allow_locked: true)`; `rotateKey` is
  `do_set_key(allow_locked: false)` and raises `BUCKET_IS_LOCKED` on a locked bucket.
- `write` increments `tag_message_counts` when a tag is supplied; `forceRemoveMessage` decrements
  it. Both are checked-arithmetic in the pallet, so overflow/underflow map to
  `ARITHMETIC_OVERFLOW` / `ARITHMETIC_UNDERFLOW`.
- `createTag` in the pallet checks only bucket-admin, never bucket existence. Off-chain the
  `tags.bucket_id` foreign key enforces existence, so behaviour matches without extra code.
- `removeManager` counts managers and refuses the last one (`LAST_MANAGER_REMOVAL`).

Each mutation runs inside a single database transaction, so multi-table writes —
`createNamespace` plus its first manager, and `write` plus the tag counter plus `next_message_id`
— are atomic like a pallet extrinsic.

### Input validation

The pallet's `BoundedVec`/`BoundedBTreeMap` limits become validated options on
`BucketOptions`, with these defaults (to be reconciled against the runtime's actual `Config`
values):

| Option | Pallet constant | Default |
|---|---|---|
| `MaxNameLen` | `T::MaxNameLen` | 256 |
| `MaxUriLen` | `T::MaxUriLen` | 512 |
| `MaxCategoryLen` | `T::MaxCategoryLen` | 64 |
| `MaxProperties` | `T::MaxProperties` | 32 |
| `MaxPropertyKeyLen` | `T::MaxPropertyKeyLen` | 64 |
| `MaxPropertyValueLen` | `T::MaxPropertyValueLen` | 512 |
| `MaxTagLen` | `T::MaxStringInputLengthTag` | 64 |

Fixed-width hex fields, from `[u8; 32]` in `types.rs`:

- `newEncryptionKey` (`BucketPublicKey`) — exactly 32 bytes hex.
- `viewer` (`X25519PublicKey`) — exactly 32 bytes hex.
- `contentHash` — exactly 32 bytes hex.

Violations raise `INVALID_INPUT`.

## Authentication

Mutations reuse the scheme XcavateProfileApi already implements — same headers, same payload
format, same `SignatureValidator`.

Headers on `POST /graphql`:

- `X-SS58-Address` — caller's SS58 address
- `X-Signature` — hex sr25519 signature
- `X-Timestamp` — ISO-8601 UTC

Signed payload: `POST:/graphql:<blake2_128(raw request body)>:<timestamp:o>`, matching
`CryptoHelper.ConstructPayload`. The signature itself is over `Blake2b-128` of that payload
string, and the server's existing `<Bytes>…</Bytes>` wallet fallback is retained.

Server flow:

1. An ASP.NET middleware on `POST /graphql` reads the buffered request body — `Program.cs`
   already calls `EnableBuffering()` — and, when the X-\* headers are present, verifies the
   signature and timestamp freshness through `ISignatureValidator`.
2. The outcome populates a scoped `ICallerContext` with either an authenticated SS58 address plus
   an `IsAdmin` flag, or anonymous.
3. A Hot Chocolate field middleware gates resolvers: `[RequireSignature]` on the 15 role-based
   mutations, `[RequireAdmin]` on the 5 `force*` mutations. Queries carry neither.

`SubjectId` is the caller's SS58 address directly — no DID resolution layer. `ViewerId` stays a
hex X25519 public key, as in the pallet and in the existing `Profile.X25519Key` field.

The pallet's `ForceOriginCheck` (sudo) maps to the existing `ADMIN_ADDRESSES` environment list via
`ISignatureValidator.IsAdmin`.

Consequence to document: because the signature covers the whole request body, every mutation in a
multi-mutation document is attributed to that one signer. Two different signers cannot share a
request.

## Errors

Each pallet `Error` variant becomes a domain exception, surfaced by a Hot Chocolate error filter
that attaches a stable `code` extension:

```json
{ "errors": [ { "message": "The origin is not authorized to perform the admin action for the bucket.",
                "extensions": { "code": "NOT_ADMIN" } } ] }
```

Ported codes: `NAMESPACE_ALREADY_EXISTS`, `UNKNOWN_NAMESPACE`, `UNKNOWN_BUCKET`,
`BUCKET_IS_LOCKED`, `UNKNOWN_MESSAGE`, `UNKNOWN_TAG`, `NOT_MANAGER`, `NOT_ADMIN`,
`NOT_CONTRIBUTOR`, `DANGLING_BUCKETS`, `DANGLING_MESSAGES`, `DANGLING_ADMINS`,
`DANGLING_CONTRIBUTORS`, `DANGLING_VIEWERS`, `DANGLING_MANAGERS`, `DANGLING_TAGS`,
`ARITHMETIC_OVERFLOW`, `ARITHMETIC_UNDERFLOW`, `LAST_MANAGER_REMOVAL`.

API-layer codes: `UNAUTHORIZED` (missing or invalid signature), `INVALID_SIGNATURE`,
`TIMESTAMP_OUT_OF_RANGE`, `FORBIDDEN` (authenticated but not an admin address), `INVALID_INPUT`.

Not ported: `UNABLE_TO_PAY_FEES` — there are no fees off-chain.

## IPFS content resolution

`Message.ipfsContent` exists for schema compatibility. The indexer fetched message content from
IPFS and stored the text for `text/plain` messages only, leaving it null otherwise or on fetch
failure.

Here it resolves lazily through an optional gateway: when `BucketOptions.IpfsGatewayUrl` is
configured and the message's `contentType` is `text/plain`, the resolver fetches
`{gateway}/{reference}` and returns the text; otherwise, or on any fetch failure, it returns null.
Unconfigured gateway means the field is always null and no outbound requests are made.

## Testing

NUnit, matching the repo's existing test project.

1. **Domain rule tests** (`tests/XcavateBuckets.Tests/`) — one fixture per domain service,
   covering every row of the authorization/precondition table above. The pallet's own cases in
   `pallets/pallet-bucket/src/tests/` are ported here; they are the parity evidence that the C#
   rules match the Rust ones. No web host or keypair needed.
2. **GraphQL integration tests** — schema snapshot test (guards against accidental schema drift),
   plus resolver tests over an in-process host with a test database, covering paging, filtering,
   sorting, and nested-relation DataLoader behaviour.
3. **E2E tests** (`tests/XcavateProfile.ApiTests/`) — signed mutations against the docker stack
   alongside the existing `ProfileApiTests`, covering the happy path per mutation plus rejection
   cases: missing headers, bad signature, stale timestamp, non-admin calling a `force*` mutation,
   and a non-manager/non-admin/non-contributor calling role-gated mutations.

## Open items

- The `MaxNameLen`/`MaxUriLen`/etc. defaults above are guesses; reconcile them with the Xcavate
  runtime's `pallet_bucket::Config` before release.
