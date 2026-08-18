# XcavateProfile — Project Summary

An orientation document: what the pieces are, why they are shaped that way, and where to look.
For usage instructions see [README.md](README.md); for the authentication mechanics see
[ADMIN_AUTH.md](ADMIN_AUTH.md).

## What this is

One ASP.NET Core host serving two independent APIs over one PostgreSQL database:

1. **Profiles and companies**, a REST API — wallet-keyed user profiles and the companies those
   users register, with pictures and logos on S3-compatible object storage. Profiles were the
   original purpose of the project.
2. **Buckets**, a GraphQL API — a C# reimplementation of the Substrate `pallet-bucket`. It owns
   its data and ports the pallet's rules into domain services. There is no chain and no extrinsic
   submission. Read operations expose the same entity types and field names the
   [SubQuery indexer](https://github.com/XcavateBlockchain/xcavate-indexer) served, so existing
   consumers keep their selection sets.

Both authenticate writes with wallet signatures — Substrate sr25519 or Solana ed25519 — through
one shared validator. Reads are public on both.

## Project structure

```
XcavateProfile/
├── src/
│   ├── XcavateProfileApi/              # ASP.NET Core host (net10.0)
│   │   ├── Controllers/
│   │   │   ├── ProfilesController.cs
│   │   │   ├── CompaniesController.cs
│   │   │   ├── MigrationsController.cs
│   │   │   ├── FieldValidation.cs      # wallet/email/length checks both controllers share
│   │   │   └── ImageUploads.cs         # the extension allow-list both upload endpoints share
│   │   ├── Data/                       # ProfileDbContext, ModelBuilderExtensions, JsonColumn
│   │   ├── GraphQL/
│   │   │   ├── BucketQueries.cs        # [GraphQLName("Query")]
│   │   │   ├── BucketMutations.cs      # [GraphQLName("Mutation")], one per extrinsic
│   │   │   ├── BucketTypes.cs          # ObjectType<T> configs, EntityId id formatting
│   │   │   ├── BucketRegistration.cs   # AddBucketGraphQL()
│   │   │   ├── BucketErrorFilter.cs    # domain errors -> extensions.code
│   │   │   ├── BigIntType.cs, Inputs.cs
│   │   │   └── Auth/                   # GraphQLSignatureMiddleware, CallerContext,
│   │   │                               # RequireSignature / RequireAdmin attributes
│   │   ├── Middleware/                 # ISignatureValidator, SignatureValidator, options
│   │   ├── Services/                   # S3Service, IdGenerator, Timestamps
│   │   ├── Migrations/                 # ProfileDbContext migrations
│   │   └── Program.cs
│   │
│   ├── XcavateBuckets.Domain/          # Ported pallet rules (net10.0, no ASP.NET dependency)
│   │   ├── Entities/                   # 9 indexer entity types
│   │   ├── Data/BucketDbContext.cs
│   │   ├── Services/                   # Namespace, Bucket, Membership, Tag, Message,
│   │   │                               # Authorization
│   │   ├── Migrations/                 # BucketDbContext migrations
│   │   ├── BucketErrorCode.cs          # ports the pallet's Error enum
│   │   ├── BucketException.cs, BucketOptions.cs, InputValidator.cs
│   │
│   ├── XcavateProfileApiClient/        # Client SDK, published to NuGet
│   │   ├── XcavateProfileClient.cs     # REST client
│   │   ├── XcavateProfileClientOptions.cs
│   │   ├── Profile.cs                  # the REST models; each also an IPayloadBody
│   │   ├── Company.cs
│   │   ├── UserRole.cs, Permissions.cs # the role set and the clearance maps
│   │   ├── CryptoHelper.cs             # Blake2b-128 hashing, payload construction
│   │   ├── Hex.cs, JsonDefaults.cs     # the wire encodings, in one place each
│   │   ├── IPayloadBody.cs, EmptyPayloadBody.cs
│   │   ├── SigningHttpMessageHandler.cs  # signs outgoing /graphql requests
│   │   ├── Signing/
│   │   │   ├── ISignatureScheme.cs     # server side: verify
│   │   │   ├── IRequestSigner.cs       # client side: sign
│   │   │   ├── SolanaSignatureScheme.cs, SolanaRequestSigner.cs
│   │   │   ├── RequestSigning.cs       # header names + attaching them
│   │   │   └── SignatureEncoding.cs    # hex or base58, fails closed
│   │   ├── Substrate/                  # everything touching Substrate.NET.API, excluded
│   │   │   │                           # from the Solana package
│   │   │   ├── Sr25519SignatureScheme.cs, SubstrateRequestSigner.cs
│   │   │   ├── CryptoHelper.Substrate.cs         # sr25519 sign / verify
│   │   │   ├── XcavateProfileClient.Substrate.cs # Account overloads
│   │   │   └── SigningHttpMessageHandler.Substrate.cs
│   │   └── Buckets/                    # StrawberryShake config + Operations.graphql
│   │
│   └── XcavateProfileApiSolanaClient/  # The same SDK minus Substrate/, published to NuGet.
│       └── (project file + .graphqlrc.json only — no sources of its own)
│
├── tests/
│   ├── XcavateBuckets.Tests/           # 288 tests, in-memory SQLite, no server needed
│   ├── XcavateProfileApiSolanaClient.Tests/  # 26 tests over the Solana package alone
│   └── XcavateProfile.ApiTests/        # E2E REST tests against a running API
│
├── docs/
│   ├── graphql/schema.graphql          # committed schema snapshot, drift-tested
│   └── superpowers/                    # design specs and implementation plans
│
├── .github/workflows/                  # deploy.yml (Hetzner), nuget.yml (package)
├── Directory.Build.props               # solution-wide build settings
├── docker-compose.yml, Dockerfile, .dockerignore
├── run_e2e_tests.sh                    # E2E orchestration
├── .env.example                        # environment template
└── README.md, ADMIN_AUTH.md, PROJECT_SUMMARY.md
```

## Technology stack

| Component | Technology |
|-----------|------------|
| Runtime | .NET 10 / ASP.NET Core |
| GraphQL server | Hot Chocolate 16.5 |
| GraphQL client | StrawberryShake 16.5 |
| ORM | Entity Framework Core 10 |
| Database | PostgreSQL 15 |
| sr25519 / SS58 | Substrate.NET.API 0.9.24 |
| ed25519 / Solana base58 | Solnet.Wallet 6.1 |
| Object storage | AWSSDK.S3 against Hetzner Object Storage |
| REST docs | Swashbuckle / Swagger |
| Testing | NUnit 4 |
| CI/CD | GitHub Actions, Docker Compose |

## Component notes

### Profiles and companies (REST)

`ProfilesController` and `CompaniesController` call `ISignatureValidator` explicitly per action
rather than through a filter, because each action authorizes differently: create checks that the
signer matches the body, update and delete check ownership or admin status, and the upload endpoints
additionally require the record to exist. Endpoints are listed in
[README.md](README.md#rest-api--profiles).

Three groups of fields are server-owned on both entities: the ids (`userId` mirrors the wallet
address, `companyId` is generated), the timestamps, and `permission`. `permission` is admin-only
because a wallet signature proves who is calling and nothing about their compliance — a caller that
could set its own clearance would make the field meaningless. All of them are *ignored* rather than
refused when a caller sends them, so reading a record, editing one field and PUTting it back works
for any caller; the single exception is a `userId` that contradicts its wallet address, which is a
400 rather than a silent correction.

A company separates its two wallet addresses on purpose: `userId` is the current owner and may be
reassigned to transfer the company, while `companyWalletAddress` is fixed and still identifies the
creator afterwards.

`roles` and `permission` are stored as JSON text columns through `Data/JsonColumn.cs` rather than as
`jsonb` or EF owned entities, because the same model is created on PostgreSQL in production and on
SQLite in the test suite, and text is the one mapping both providers spell identically. Nothing
queries inside those values.

Every attribute added to `Profile` after the original five is omitted from the JSON when null. That
is a compatibility guarantee, not a style choice: the server re-serializes the body it bound and
hashes that, so a field emitted only on the server side would change the hash and 401 every write
from an already-published SDK build. `ProfileAttributeEndpointTests` pins it with a body from an
older client.

Timestamps come from `Services/Timestamps.cs`, which truncates to microseconds — PostgreSQL's
`timestamptz` resolution. Storing the full 100-nanosecond tick would mean a create response carrying
three digits the next read cannot return.

The image endpoint derives the stored content type from an extension allow-list, never from the
client, because the bucket serves objects publicly — a client-supplied `text/html` would be a
stored-XSS vector. SVG is excluded for the same reason. The S3 object key is derived from the
filename, so re-uploading the same name overwrites.

### Buckets (GraphQL)

`XcavateBuckets.Domain` takes no dependency on ASP.NET Core, Hot Chocolate, or
`Substrate.NET.API`, so every ported pallet rule is unit-testable without a web host or a keypair.
That boundary is why 176 tests run against in-memory SQLite in seconds.

`BucketDbContext` targets the same PostgreSQL database as `ProfileDbContext` but keeps its own
migrations history table (`__EFMigrationsHistory_Buckets`), so the two contexts' migrations stay
independent. `Program.cs` migrates both on startup, with five retries and exponential backoff.

Mutations map one-to-one onto pallet extrinsics, in call-index order, and each runs inside a
transaction — several touch more than one table, so partial application would diverge from
extrinsic semantics. Authorization is role-based (manager, admin, contributor, viewer) in
`AuthorizationService`; the five `force*` mutations require an admin address instead, standing in
for the pallet's `ForceOrigin`.

Deliberate carry-overs from the pallet — global bucket ids, per-bucket message ids, a fresh bucket
starting locked — are documented in [README.md](README.md#graphql-api--buckets) and in the entity
XML docs. Fees are not ported, since there is no currency off-chain.

### Client SDK

Published as `XcavateProfileApiClient`. Two type families matter:

- **`ISignatureScheme`** — server side, "can this scheme verify this address, and does the
  signature check out". `SignatureValidator` holds one instance per scheme and dispatches on
  address format. The formats never overlap: a checksummed SS58 address decodes to 32 bytes via
  `Utils.GetPublicKeyFrom`, while Solnet's `PublicKey` yields 32 bytes only for a genuine Solana
  address and 35 for an SS58 string.
- **`IRequestSigner`** — client side, "sign this payload and encode it for the header". Each
  implementation owns its wire conventions, so callers stay chain-agnostic.

`SignatureEncoding.TryDecode` accepts `0x` hex or base58 and returns false on every failure path
rather than throwing, because it runs on unauthenticated input — previously a malformed signature
escaped as a 500 instead of a 401.

Two namespaces coexist in the assembly for historical reasons: `XcavateProfile.Client`
(`XcavateProfileClient`, `XcavateProfileClientOptions`, `Profile`, `CryptoHelper`) and
`XcavateProfileApiClient` (`IPayloadBody`, `SigningHttpMessageHandler`, `Signing/*`). Consolidating
them would be a breaking change for package consumers, so it has not been done.

The SDK ships as two packages from this one source tree. `XcavateProfileApiSolanaClient` compiles
the same files minus `Substrate/`, giving Solana consumers the identical API without
`Substrate.NET.API` — and so without StreamJsonRpc, Serilog, Newtonsoft.Json or MessagePack. That
split is why `CryptoHelper` reaches Blake2Core and `Convert.ToHexString` directly instead of
Substrate's `HashExtension` and `Utils`: the digest is identical (pinned by
`PayloadHashCompatibilityTests`) but no longer requires the Substrate stack to compute.

## Authentication flow

```
Client                                    Server
──────                                    ──────
1. Build the request body                 1. Read X-SS58-Address / X-Signature / X-Timestamp
2. Blake2b-128 hash it, hex uppercase     2. Parse the timestamp as UTC, reject skew > 5 min
3. payload = METHOD:path:hash:timestamp   3. Recompute the body hash
4. Sign:                                  4. Rebuild the payload string
     sr25519 -> sign the 16-byte digest   5. Pick the scheme matching the address format
     Solana  -> sign the raw UTF-8        6. Verify — the address carries the public key,
5. Send with the three X-* headers           so no database lookup is needed
                                          7. Authorize: ownership, bucket role, or admin
```

REST and GraphQL differ in step 3: `GraphQLSignatureMiddleware` hashes the literal request body,
while the REST controllers hash a re-serialization of the deserialized `Profile`. Browser clients
have to reproduce `System.Text.Json`'s exact output — see
[README.md](README.md#authentication).

## Testing

| Suite | What it covers | Needs |
|---|---|---|
| `tests/XcavateBuckets.Tests` (176) | Domain rules per service, EF schema/keys, GraphQL schema drift, GraphQL integration through a real Hot Chocolate host, the generated StrawberryShake client end to end, signature validation and encoding for both schemes | Nothing — in-memory SQLite |
| `tests/XcavateProfile.ApiTests` (25) | 19 sr25519 tests: REST CRUD, auth rejection (bad signature, stale timestamp), cross-profile authorization, admin override, nickname uniqueness, image upload. Plus 6 Solana tests covering create, update, delete, image upload, admin override and non-admin rejection | PostgreSQL + a running API, via `run_e2e_tests.sh` |

The E2E suite signs real requests, so the address derived from `TestMnemonics.AdminMnemonic` must
appear in `ADMIN_ADDRESSES`, or admin tests fail with 403. `TestMnemonics` derives personas from
fixed entropy so addresses are stable across runs; `SolanaAccounts` derives a Solana address from
each of the same phrases.

## Key design decisions

1. **Signature auth instead of JWT** — aligns with the wallet ecosystem, needs no token issuance
   or refresh, and the address doubles as the public key.
2. **Blake2b-128 for body hashing** — Substrate's native hash, so clients can reuse chain tooling.
3. **Solana signs the payload unhashed** — wallets render what they are asked to sign as UTF-8, so
   signing a digest would show the user binary garbage, which is exactly the prompt users are
   trained to reject.
4. **The pallet ported as a standalone API, not an indexer** — the API owns its data, so it can
   serve writes without a chain while keeping the indexer's read contract.
5. **Two DbContexts, one database** — separate migration histories keep profile and bucket schema
   changes independent without operating a second database.
6. **Domain layer free of web and crypto dependencies** — makes pallet rules testable in
   isolation, which is where most of the test suite lives.
7. **Admin list in the environment** — changing admins needs no migration and no deploy of code.

## Known rough edges

- `XcavateProfileClient` sets auth headers on the shared `HttpClient.DefaultRequestHeaders` and
  clears them per signed call, so a single instance is not safe for concurrent writes.
- Message `IpfsContent` is stored as supplied; the API never fetches the reference or checks it
  against `ContentHash`.
- Image upload signatures do not cover the uploaded bytes, because multipart bodies are not
  hashed.
- The client's image content-type map still lists `.svg`, which the server rejects by design.
- `AWSSDK.S3` (3.7) and `Swashbuckle.AspNetCore` (6.6) are a major version behind current.

## Possible next steps

- Rate limiting in front of the signature validator
- Caching for profile lookups
- Cover the uploaded file in the image upload signature
- Subscriptions for bucket message writes
- Consolidate the SDK's two namespaces at the next major package version
