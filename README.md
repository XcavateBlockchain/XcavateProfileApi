# XcavateProfile

An ASP.NET Core service exposing two APIs over one PostgreSQL database:

- **Profiles (REST)** — Substrate/Polkadot profile registration and management, with profile
  pictures on S3-compatible object storage.
- **Buckets (GraphQL)** — a C# port of the Substrate `pallet-bucket`, serving the same entity
  types and field names the SubQuery indexer used to serve, so existing selection sets keep
  working. There is no chain: the pallet's rules are reimplemented as domain services.

Both APIs authenticate state-changing requests with wallet signatures — **Substrate sr25519** or
**Solana ed25519** — and share one signature validator. Reads are public on both.

- **Runtime**: .NET 10, ASP.NET Core, Entity Framework Core, PostgreSQL
- **GraphQL server**: Hot Chocolate 16
- **Client SDK**: `XcavateProfileApiClient` (published to NuGet) — REST client, a
  StrawberryShake-generated GraphQL client, and the signing primitives both use
- **Storage**: Hetzner Object Storage (S3-compatible) for profile pictures
- **CI/CD**: Docker image to ghcr.io + deploy to Hetzner; NuGet package publishing

## Architecture

```
┌──────────────────────────────────────────────────────────────────────┐
│                       XcavateProfileApiClient                        │
│  IRequestSigner: SubstrateRequestSigner (sr25519, hex)               │
│                  SolanaRequestSigner    (ed25519, base58)            │
│  XcavateProfileClient ......... REST profile client                  │
│  XcavateBucketsClient ........ StrawberryShake GraphQL client        │
│  SigningHttpMessageHandler ... signs outgoing /graphql requests      │
└──────────────────────────────────────────────────────────────────────┘
                             │  X-SS58-Address / X-Signature / X-Timestamp
                             ▼
┌──────────────────────────────────────────────────────────────────────┐
│                          XcavateProfileApi                           │
│  REST      /api/profiles/*   → ProfilesController                    │
│            └─ ISignatureValidator (per-action, explicit)             │
│  GraphQL   /graphql          → Query / Mutation (Hot Chocolate)       │
│            └─ GraphQLSignatureMiddleware → ICallerContext             │
│               [RequireSignature] (15)  [RequireAdmin] (5)            │
│  SignatureValidator → Sr25519SignatureScheme | SolanaSignatureScheme │
│  S3Service → profile pictures                                        │
└──────────────────────────────────────────────────────────────────────┘
             │                                        │
             │ ProfileDbContext                       │ BucketDbContext
             ▼                                        ▼
┌──────────────────────────────────────────────────────────────────────┐
│                        PostgreSQL (one database)                     │
│  profiles                     │  namespaces, buckets, messages,      │
│  __EFMigrationsHistory        │  tags, memberships, tag counts       │
│                               │  __EFMigrationsHistory_Buckets       │
└──────────────────────────────────────────────────────────────────────┘
```

`XcavateBuckets.Domain` holds the ported pallet rules and takes no dependency on ASP.NET Core,
Hot Chocolate, or `Substrate.NET.API`, so every rule is unit-testable without a web host or a
keypair.

## Projects

| Project | Purpose |
|---|---|
| `src/XcavateProfileApi` | Web host: REST controllers, GraphQL schema, signature middleware, S3 |
| `src/XcavateBuckets.Domain` | Bucket entities, `BucketDbContext`, migrations, domain services |
| `src/XcavateProfileApiClient` | Client SDK (NuGet): REST client, generated GraphQL client, signing |
| `tests/XcavateBuckets.Tests` | 176 domain, schema, GraphQL and signature tests (in-memory SQLite) |
| `tests/XcavateProfile.ApiTests` | End-to-end REST tests against a running API |

## REST API — profiles

| Method | Endpoint | Auth | Description |
|--------|----------|------|-------------|
| GET | `/api/profiles` | — | List all profiles |
| GET | `/api/profiles/{address}` | — | Get profile by address |
| GET | `/api/profiles/nickname/{nickname}` | — | Get profile by nickname |
| POST | `/api/profiles` | signature | Create profile |
| PUT | `/api/profiles/{address}` | signature | Update, or create if absent |
| DELETE | `/api/profiles/{address}` | signature | Delete profile |
| POST | `/api/profiles/{address}/image` | signature | Upload profile picture (multipart) |
| GET | `/health` | — | Liveness probe |
| GET | `/swagger` | — | Swagger UI |

Callers may only modify their own profile unless their address is in `ADMIN_ADDRESSES`.

**Image upload**: 25 MB of image, 26 MB request limit (the extra megabyte covers multipart
overhead). The content type is derived from the file extension against an allow-list —
`.jpg`, `.jpeg`, `.png`, `.gif`, `.webp`, `.bmp` — and never from the client, because the bucket
serves objects publicly. **SVG is deliberately rejected**: it can embed scripts. Uploading a file
whose name matches an existing object overwrites it.

## GraphQL API — buckets

Endpoint: `POST /graphql`. The committed schema snapshot is
[`docs/graphql/schema.graphql`](docs/graphql/schema.graphql), and `GraphQLSchemaTests` fails if
the live schema drifts from the shapes consumers depend on. The full design rationale — pallet-to-table mapping,
which behaviours are preserved on purpose, what is deliberately not ported — is in
[`docs/superpowers/specs/2026-07-25-bucket-pallet-graphql-api-design.md`](docs/superpowers/specs/2026-07-25-bucket-pallet-graphql-api-design.md).

**Entity types** (the 9 the indexer produced for `pallet-bucket`): `Namespace`,
`NamespaceManager`, `Bucket`, `BucketAdmin`, `BucketContributor`, `BucketViewer`, `Tag`,
`TagMessageCount`, `Message`.

**Queries** — all public. Collection fields (`namespaces`, `buckets`, `messages`, `tags`,
`namespaceManagers`, `bucketAdmins`, `bucketContributors`, `bucketViewers`, `tagMessageCounts`)
support cursor paging with `totalCount`, filtering and sorting. Single-entity fields
(`namespace`, `bucket`, `message`, `tag`) take an `ID` and return `null` when it does not parse.

**Mutations** — one per pallet extrinsic, in call-index order. 15 require a valid signature; the
5 `force*` mutations require an address listed in `ADMIN_ADDRESSES`, standing in for the pallet's
`ForceOrigin`:

| Requires a signature | Requires an admin address |
|---|---|
| `createNamespace`, `addManager`, `removeManager` | `forceRemoveNamespace` |
| `createBucket`, `pauseWriting`, `resumeWriting`, `rotateKey` | `forceRemoveBucket` |
| `addAdmin`, `removeAdmin`, `addContributor`, `removeContributor` | `forceRemoveTag` |
| `addViewer`, `removeViewer`, `createTag` | `forceRemoveMessage` |
| `write` | `forceAddManager` |

Each mutation runs in a transaction, because a pallet extrinsic either applies wholly or not at
all and several touch more than one table — creating a namespace also inserts its first manager;
writing a message also moves a tag counter and the bucket's next message id.

**Behaviours carried over from the pallet deliberately:**

- **Bucket ids are global**, not per-namespace, because `NextBucketId` is a single storage value.
- **Message ids restart per bucket**, so a message's `id` is `"{bucketId}-{messageId}"`.
- **A freshly created bucket is locked** — `Status::default()` is `Locked` — so it rejects writes
  until `resumeWriting` supplies an encryption key.
- **On-chain fees are not ported.** `FeeNamespace`/`FeeBucket`/`FeeMessage`/`FeeTag` and the
  `UnableToPayFees` error have no off-chain analogue.

**Errors** carry a stable SCREAMING_SNAKE identifier in `errors[].extensions.code` — branch on
that rather than on message text. Domain codes port the pallet's `Error` enum
(`UNKNOWN_NAMESPACE`, `BUCKET_IS_LOCKED`, `NOT_CONTRIBUTOR`, `LAST_MANAGER_REMOVAL`, …), plus
`INVALID_INPUT` for a bound or format check. Auth failures use `UNAUTHORIZED`,
`INVALID_SIGNATURE`, `TIMESTAMP_OUT_OF_RANGE` and `FORBIDDEN`.

## Authentication

All state-changing requests — REST and GraphQL alike — require a signature, either Sr25519
(Substrate SS58) or Solana ed25519.

### Authentication headers

| Header | Value |
|--------|-------|
| `X-SS58-Address` | The signer's address — a Substrate **SS58** address or a Solana **base58** address |
| `X-Signature` | The signature, as `0x`-prefixed hex **or** base58. Must decode to 64 bytes |
| `X-Timestamp` | ISO-8601 UTC, within 5 minutes of server time |

The server infers the scheme from the address format — the two are unambiguous, so there is no
scheme header to set.

**Both schemes sign the same payload string:**

```
{METHOD}:{path}:{blake2b_128_hex_of_body}:{timestamp}
```

They differ only in what is passed to the signing function:

| Scheme | Address | Signed bytes |
|--------|---------|--------------|
| sr25519 | SS58 | `blake2b(utf8(payload), 128)` — a 16-byte digest |
| Solana ed25519 | base58, 32 bytes | `utf8(payload)` — the string itself, unhashed |

Solana signs the string unhashed so a wallet's approval popup shows readable text rather than
binary. Note that the body-hash *segment* is still Blake2b-128 in both cases — a JS caller needs
`blakejs` even on the Solana signing path.

Two details are easy to get wrong, and both break signature verification silently, because the
payload has to match the server's reconstruction byte-for-byte, not just represent the same value:

- **Body hash casing**: the server hex-encodes it as uppercase, `0x`-prefixed digits (what
  `Utils.Bytes2HexString` emits) — JavaScript's `Number.prototype.toString(16)` produces lowercase,
  so it must be upper-cased.
- **Timestamp precision**: the server re-serializes `X-Timestamp` using .NET's round-trip format,
  which always pads to 7 fractional-second digits (`.fffffffZ`). `Date.prototype.toISOString()`
  gives only 3, so pad it before signing.

The example below handles both.

**Signing GraphQL requests from a browser wallet:**

```javascript
// npm install bs58 blakejs
import bs58 from 'bs58';
import { blake2b } from 'blakejs';

// Browser-native hex encoding (avoids a Buffer polyfill) — upper-cased to match what the
// server's Utils.Bytes2HexString produces.
const hex = (bytes) =>
  '0x' + Array.from(bytes, (b) => b.toString(16).padStart(2, '0')).join('').toUpperCase();

const body = JSON.stringify({ query: '...' });
const bodyHash = hex(blake2b(new TextEncoder().encode(body), null, 16));

// Pad to 7 fractional digits so this matches the payload the server reconstructs (see above).
const timestamp = new Date().toISOString().replace('Z', '0000Z');

const payload = `POST:/graphql:${bodyHash}:${timestamp}`;

const { signature } = await window.solana.signMessage(
  new TextEncoder().encode(payload), 'utf8');

const headers = {
  'X-SS58-Address': window.solana.publicKey.toBase58(),
  'X-Signature': bs58.encode(signature),
  'X-Timestamp': timestamp,
};
```

**REST is different from GraphQL.** The example above hashes the literal request body, which
is what `GraphQLSignatureMiddleware.cs` does on `/graphql`. The REST controllers do not:
`ProfilesController.cs` hands the *deserialized* `Profile` object to the validator, and
`Profile.Hash()` (`Profile.cs`) re-serializes it with `System.Text.Json` before hashing —
it never sees the bytes you actually sent. To compute a matching hash from a browser you must
reproduce that serialization exactly:

- **Field order** is declaration order, not alphabetical or object-literal order:
  `ss58address`, `nickname`, `bio`, `profilePicture`, `x25519Key`.
- **Nulls are emitted, not omitted** — an unset `bio` or `profilePicture` must serialize as
  `"bio":null`. `JSON.stringify` drops `undefined` properties instead of writing `null`, which
  will not match.
- **No whitespace** — compact JSON, same as the GraphQL example above.

```javascript
// Field order, nulls, and compactness all have to match System.Text.Json's output exactly.
const canonical = JSON.stringify({
  ss58address: ss58address,
  nickname: nickname ?? null,
  bio: bio ?? null,
  profilePicture: profilePicture ?? null,
  x25519Key: x25519Key,
});
const bodyHash = hex(blake2b(new TextEncoder().encode(canonical), null, 16));
```

Safest approach: build this exact object, in this exact field order, and both send it as the
request body and hash it for the payload — do not construct the request body separately from
the hash input. A plain `JSON.stringify(profileObject)` on a hand-built object will only match
by coincidence. Any mismatch here fails as a plain 401 with no further diagnostic; the server
does not report which part of the payload it disagreed with.

### Signature verification

1. Server reconstructs the body hash (Blake2b-128) from the request
2. Constructs the payload string from method, path, hash and timestamp
3. Checks the timestamp is within 5 minutes, rejecting replays
4. Picks the scheme that recognises the address format and verifies the signature — the address
   itself carries the public key, so no database lookup is involved
5. Authorizes based on profile ownership, bucket role, or admin status

DELETE requests and image uploads sign an `EmptyPayloadBody`, whose hash is a literal empty
string rather than a hash of one, leaving two adjacent colons in the payload. Multipart bodies
are never hashed, so an image upload's signature does not cover the uploaded file.
See [ADMIN_AUTH.md](ADMIN_AUTH.md) for worked payload examples and the admin authorization model.

## Running locally

### Prerequisites
- .NET 10.0 SDK
- Docker & Docker Compose

### Setup

1. **Copy the environment template**

```bash
cp .env.example .env
```

2. **Configure `.env`** — the values the API actually reads:

```env
POSTGRES_HOST=localhost
POSTGRES_PORT=5432
POSTGRES_DB=xcavate_profile
POSTGRES_USER=xcavate_user
POSTGRES_PASSWORD=your_secure_password
ASPNETCORE_ENVIRONMENT=Development
ASPNETCORE_URLS=http://localhost:5000
S3_ENDPOINT=https://fsn1.your-storagebox.de
S3_REGION=fsn1
S3_ACCESS_KEY=your-access-key
S3_SECRET_KEY=your-secret-key
# Comma-separated; SS58 and Solana base58 addresses can be mixed freely
ADMIN_ADDRESSES=5GrwvaEF5zKbXCEe9qGjZL23Y641mot2Ff6hS3s8jF3g3k3W
```

3. **Start the stack**

```bash
docker-compose up -d
```

4. **Run the API**

```bash
dotnet run --project src/XcavateProfileApi
```

Migrations for **both** `ProfileDbContext` and `BucketDbContext` are applied automatically at
startup, with retry and exponential backoff, so no manual `dotnet ef database update` is needed.
To create a new migration, name the context explicitly:

```bash
dotnet ef migrations add <Name> --project src/XcavateProfileApi --context ProfileDbContext
dotnet ef migrations add <Name> --project src/XcavateBuckets.Domain \
    --startup-project src/XcavateProfileApi --context BucketDbContext
```

5. **Browse the APIs** — Swagger UI at `http://localhost:5000/swagger`, GraphQL (Nitro IDE) at
   `http://localhost:5000/graphql`.

## Testing

```bash
# Domain, schema, GraphQL and signature tests — no database or server needed (in-memory SQLite)
dotnet test tests/XcavateBuckets.Tests

# End-to-end REST tests: starts PostgreSQL and the API, runs the suite, tears everything down
./run_e2e_tests.sh
```

The E2E suite signs real requests, so `ADMIN_ADDRESSES` in `.env` must contain the address
derived from `TestMnemonics.AdminMnemonic`, or the admin authorization tests fail with 403.
`SolanaAccounts` derives a second, Solana address for each of the same personas, so both signing
schemes are exercised end to end.

## Using the C# client SDK

```bash
dotnet add package XcavateProfileApiClient
```

### Profiles (REST)

```csharp
using Substrate.NetApi.Model.Types;
using XcavateProfile.Client;
using XcavateProfileApiClient.Signing;

using var client = new XcavateProfileClient(new XcavateProfileClientOptions
{
    ApiUrl = "http://localhost:5000"
});

// Reads need no signer.
var all = await client.GetProfilesAsync();
var one = await client.GetProfileAsync(address);      // null when absent
var byNick = await client.GetProfileByNicknameAsync("myprofile");

// Writes take the signing account. Ss58Address and X25519Key are required.
var profile = new Profile
{
    Ss58Address = account.Value,
    X25519Key = "0x0123…",
    Nickname = "myprofile",
    Bio = "My Substrate profile"
};

await client.CreateProfileAsync(profile, account);

profile.Bio = "Updated bio";
await client.UpdateProfileAsync(account.Value, profile, account);

using (var image = File.OpenRead("profile.jpg"))
{
    var imageUrl = await client.UploadImageAsync(account.Value, image, "profile.jpg", account);
}

await client.DeleteProfileAsync(account.Value, account);
```

Every write also has an `IRequestSigner` overload, which is how a non-Substrate scheme is
selected. `SubstrateRequestSigner` signs sr25519 and emits hex; `SolanaRequestSigner` signs
ed25519 and emits base58:

```csharp
await client.CreateProfileAsync(profile, new SolanaRequestSigner(solanaAccount));
```

### Buckets (GraphQL)

The generated client is registered through DI. `SigningHttpMessageHandler` signs the outgoing
request body, so it must be the outermost handler that touches it — supply a signer only when the
client sends mutations, since queries are public:

```csharp
using XcavateProfileApiClient;
using XcavateProfileApiClient.Buckets;

services
    .AddXcavateBucketsClient()
    .ConfigureHttpClient(
        client => client.BaseAddress = new Uri("http://localhost:5000/graphql"),
        builder => builder.ConfigurePrimaryHttpMessageHandler(
            _ => new SigningHttpMessageHandler(account)));   // or any IRequestSigner
```

```csharp
var result = await client.CreateNamespace.ExecuteAsync(
    new NamespaceMetadataInput { Name = "my-namespace", SchemaUri = "ipfs://…" });

result.EnsureNoErrors();
```

The operations the generated client exposes are defined in
`src/XcavateProfileApiClient/Buckets/Operations.graphql` and are generated against the schema
snapshot in `docs/graphql/schema.graphql`. Adding an operation means editing that file and
rebuilding.

### Signing primitives

`CryptoHelper` is the shared seam both APIs and the server use:

```csharp
byte[] digest    = CryptoHelper.Hash(input);                            // Blake2b-128
string payload   = CryptoHelper.ConstructPayload(method, path, body, timestamp);
byte[] signature = await CryptoHelper.SignAsync(payload, account);      // sr25519
bool ok          = CryptoHelper.VerifySignature(payload, signature, address);
```

`body` is an `IPayloadBody` — the `Profile` being sent, or `EmptyPayloadBody` — not a
pre-computed hash string.

## Data model

### Profile

| Field | Type | Notes |
|-------|------|-------|
| `ss58address` | string (PK) | Required |
| `nickname` | string? | Unique when set |
| `bio` | string? | |
| `profilePicture` | string? | URL, set by the image upload endpoint |
| `x25519Key` | string | Required |

### Buckets

`Namespace` (managers, buckets) → `Bucket` (admins, contributors, viewers, tags, messages) →
`Message`. Bucket `Properties` maps are stored as JSON columns. Input bounds live in
`BucketOptions` and correspond to the pallet's `BoundedVec`/`BoundedBTreeMap` limits — generous
sanity bounds rather than mirrors of the runtime constants, since there is no weight budget to
protect off-chain. See the entity XML docs in `src/XcavateBuckets.Domain/Entities/` for the
per-field mapping back to pallet storage.

## CI/CD

### `.github/workflows/deploy.yml`
Triggers on push to `main`/`master` and on published releases. Builds a multi-stage Docker image,
pushes it to `ghcr.io`, then over SSH resets the Hetzner checkout, regenerates `.env` from GitHub
secrets, brings Docker Compose back up, and verifies `/health`.

### `.github/workflows/nuget.yml`
Same triggers. Packs `src/XcavateProfileApiClient` and pushes to NuGet.org. Versioning is
`1.0.<run_number>` for branch builds and the git tag for releases.

### Required GitHub secrets

| Secret | Description |
|--------|-------------|
| `HETZNER_HOST`, `HETZNER_USER`, `HETZNER_SSH_KEY`, `HETZNER_PORT` | SSH access |
| `HETZNER_DEPLOY_DIR` | Remote deployment directory |
| `HETZNER_POSTGRES_HOST`, `_PORT`, `_DB`, `_USER`, `_PASSWORD` | Database |
| `HETZNER_S3_ENDPOINT`, `_REGION`, `_ACCESS_KEY`, `_SECRET_KEY` | Object storage |
| `HETZNER_ADMIN_ADDRESSES` | Admin address list |
| `NUGET_API_KEY` | NuGet publishing |

### Reverse proxy configuration

The image upload endpoint accepts a 26 MB request. Any reverse proxy in front of the API must
allow at least the same body size, or uploads fail with `413 Request Entity Too Large` before
reaching the API. For nginx, whose default is 1 MB:

```nginx
# in the server/location block proxying to the API
client_max_body_size 26m;
```

Then reload: `nginx -t && systemctl reload nginx`. This configuration lives on the server only,
not in this repository.

## Security notes

- **Signature verification** on every state-changing request, sr25519 or Solana ed25519, with the
  public key taken from the address rather than from stored data.
- **Replay prevention** via a 5-minute timestamp window (`SignatureValidationOptions.TimestampSkew`).
- **Malformed signatures fail closed** — `SignatureEncoding.TryDecode` returns false instead of
  throwing, so garbage input is a 401 rather than a 500.
- **Authorization** is ownership-based for profiles and role-based for buckets (manager, admin,
  contributor, viewer), with admin addresses able to override.
- **Admin addresses come from the environment**, never the database, so changing them needs no
  migration.
- **Uploaded content types are derived server-side** from an image-only extension allow-list;
  SVG is rejected because the bucket serves objects publicly.

## Troubleshooting

**Signature verification fails (401).** Check the timestamp is within 5 minutes and in UTC; that
the body hash is uppercase `0x` hex of a Blake2b-**128** digest; that hex signatures carry the
`0x` prefix, since without it the value is parsed as base58; and for REST, that the body hash was
computed over `System.Text.Json`'s exact serialization. The server never reports which segment it
disagreed with.

**403 Forbidden.** The caller is neither the profile owner nor an admin, or lacks the bucket role
the mutation needs. Confirm the address is in `ADMIN_ADDRESSES` exactly as the client sends it.

**GraphQL mutation returns `BUCKET_IS_LOCKED`.** Newly created buckets start locked; call
`resumeWriting` with an encryption key first.

**Database connection errors.** `docker-compose ps` to confirm PostgreSQL is up, and check the
connection string in `.env`. Startup retries migrations five times with backoff before failing.

**Image upload returns 413.** The reverse proxy's body limit is below 26 MB — see above.

**Image upload returns 400.** The extension is not in the allow-list. SVG is rejected by design.

## License

MIT.

## Acknowledgments

- [Substrate.NET.API](https://github.com/SubstrateGaming/Substrate.NET.API) for sr25519 and SS58
- [Solnet](https://github.com/bmresearch/Solnet) for ed25519 and Solana base58
- [Hot Chocolate and StrawberryShake](https://chillicream.com) for the GraphQL server and client
- Ported from [`pallet-bucket`](https://github.com/XcavateBlockchain/xcavate-node-paseo/tree/dev/pallets/pallet-bucket)
