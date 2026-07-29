# Buckets Without a Namespace — Design

**Date:** 2026-07-29
**Status:** Implemented.
**Builds on:** [2026-07-25-bucket-pallet-graphql-api-design.md](2026-07-25-bucket-pallet-graphql-api-design.md)

## Goal

Allow `createBucket` to be called without a `namespaceId`, so a bucket can exist outside every
namespace. This is an off-chain extension: the pallet keys `Buckets` by the
`(NamespaceId, BucketId)` double map and cannot represent a bucket with no namespace.

## Semantics

- `createBucket(namespaceId: BigInt, metadata: BucketMetadataInput!)` — `namespaceId` is now
  optional. With it, behaviour is unchanged: the namespace must exist and the caller must be one
  of its managers. Without it, **any signed caller** may create a standalone bucket; the bucket is
  stored with `NamespaceId = NULL`.
- **Addressing.** Every mutation that identifies a bucket as `(namespaceId, bucketId)` —
  `addAdmin`, `removeAdmin`, `addContributor`, `removeContributor`, `addViewer`, `removeViewer`,
  `pauseWriting`, `resumeWriting`, `rotateKey`, `write`, `forceRemoveBucket` — now takes a
  nullable `namespaceId`. The pair must match exactly, in both directions: a namespaced bucket
  addressed with null, or a standalone bucket addressed with any namespace id, reads as
  `UNKNOWN_BUCKET`. This keeps the pallet's double-map mismatch rule.
- **Creator stands in for the manager.** The pallet's asymmetry — admins are appointed by a
  namespace manager, never by a bucket admin — is preserved. For a standalone bucket there is no
  manager, so the bucket's `creator` takes that role: only the creator may `addAdmin` /
  `removeAdmin` (anyone else gets `NOT_MANAGER`). Everything admin- or contributor-gated
  (contributors, viewers, keys, tags, `write`) is unchanged.
- The creator is **not** auto-added as admin, matching namespaced buckets, where the creating
  manager is not an admin either. The creator appoints admins explicitly, including themselves.
- Namespace-scoped mutations (`addManager`, `removeManager`, `forceRemoveNamespace`,
  `forceAddManager`) keep a required `namespaceId`.

## Schema deltas

- `createBucket` and the eleven bucket-addressed mutations: `namespaceId: BigInt!` → `BigInt`.
  Non-breaking for existing clients — they keep passing a value.
- `Bucket.namespaceId: BigInt!` → `BigInt` and `Bucket.namespace: Namespace!` → `Namespace`.
  Breaking only for consumers that require non-null there; existing namespaced buckets still
  return values.

## Storage

`buckets.NamespaceId` becomes nullable (migration `AllowBucketWithoutNamespace`); the FK to
`namespaces` remains, enforced only when non-null. No data changes.

## Testing

Domain rule tests cover creation without a namespace, both addressing-mismatch directions, and
the creator-as-manager rule. Integration and generated-client tests run the full standalone
lifecycle — create, self-appoint admin, add contributor, resume, write, read back with null
`namespaceId`/`namespace` — over HTTP with signatures.
