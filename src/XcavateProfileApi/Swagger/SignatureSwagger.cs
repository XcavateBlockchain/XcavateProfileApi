using System.Reflection;
using Microsoft.OpenApi;
using Microsoft.OpenApi.Models;
using Swashbuckle.AspNetCore.SwaggerGen;

namespace XcavateProfileApi.Swagger;

/// <summary>
/// Marks a REST action as requiring a signed request — a valid
/// <c>X-SS58-Address</c> / <c>X-Signature</c> / <c>X-Timestamp</c> triple — so the OpenAPI
/// document advertises the three headers as a security requirement on that operation.
/// Purely documentation: the controllers keep verifying the headers themselves, exactly as
/// before.
/// </summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class SignedRequestAttribute : Attribute;

/// <summary>
/// Attaches the signature security requirement to every operation carrying
/// <see cref="SignedRequestAttribute"/>, so Swagger UI renders the three header fields on the
/// write operations and leaves the public reads unmarked.
/// </summary>
public sealed class SignedRequestOperationFilter : IOperationFilter
{
    public void Apply(OpenApiOperation operation, OperationFilterContext context)
    {
        if (context.MethodInfo.GetCustomAttribute<SignedRequestAttribute>() is null)
        {
            return;
        }

        // OpenApiSecurityRequirement is a dictionary keyed by OpenApiSecurityScheme; the
        // scheme must carry a Reference (its Id becomes the property name) or it is skipped
        // when the document serializes.
        var requirement = new OpenApiSecurityRequirement
        {
            [Reference(
                ApiInfoDocumentFilter.AddressScheme)] = new List<string>(),
            [Reference(
                ApiInfoDocumentFilter.SignatureScheme)] = new List<string>(),
            [Reference(
                ApiInfoDocumentFilter.TimestampScheme)] = new List<string>()
        };

        operation.Security ??= new List<OpenApiSecurityRequirement>();
        operation.Security.Add(requirement);
    }

    private static OpenApiSecurityScheme Reference(string id) => new()
    {
        Reference = new OpenApiReference
        {
            Type = ReferenceType.SecurityScheme,
            Id = id
        },
        UnresolvedReference = true
    };
}

/// <summary>
/// The API's front page in the OpenAPI document: the info description and the three header
/// security schemes. The description is the single place the signature scheme is documented
/// end to end — what to sign, how the body is hashed, how the server verifies and what the
/// error codes mean — and mirrors the Authentication section of README.md and ADMIN_AUTH.md.
/// </summary>
public sealed class ApiInfoDocumentFilter : IDocumentFilter
{
    public const string AddressScheme = "X-SS58-Address";
    public const string SignatureScheme = "X-Signature";
    public const string TimestampScheme = "X-Timestamp";

    public void Apply(OpenApiDocument document, DocumentFilterContext context)
    {
        document.Info.Description = ApiDescription;

        document.Components ??= new OpenApiComponents();
        document.Components.SecuritySchemes ??= new Dictionary<string, OpenApiSecurityScheme>();
        document.Components.SecuritySchemes[AddressScheme] = HeaderScheme(
            AddressScheme,
            "The signer's address: a Substrate SS58 address or a Solana base58 address.");
        document.Components.SecuritySchemes[SignatureScheme] = HeaderScheme(
            SignatureScheme,
            "The signature over the payload described in the API description, as 0x-prefixed hex or base58 "
            + "(64 bytes).");
        document.Components.SecuritySchemes[TimestampScheme] = HeaderScheme(
            TimestampScheme,
            "ISO-8601 UTC time, within 5 minutes of server time. The exact value is part of the signed payload.");
    }

    private static OpenApiSecurityScheme HeaderScheme(string name, string description) => new()
    {
        Type = SecuritySchemeType.ApiKey,
        In = ParameterLocation.Header,
        Name = name,
        Description = description
    };

    private const string ApiDescription = """
A Substrate/Polkadot profile registration and management API. Two surfaces share one signature scheme:

- **This REST API** — user profiles, companies and wallet migrations, documented here.
- **The bucket GraphQL API** at `POST /graphql` — namespaces, buckets, messages and tags, ported from the `pallet-bucket` Substrate pallet. Its schema is served at `GET /graphql` (SDL) and checked in at `docs/graphql/schema.graphql`; browse it with any GraphQL IDE. Realtime bucket messages additionally stream over Socket.IO at `/socket.io/`.

## Authentication

All state-changing requests — REST and GraphQL alike — require a wallet signature: **Substrate sr25519** or **Solana ed25519**. Reads (the GET operations below, and GraphQL queries) are public. The scheme is inferred from the address format — the two formats are unambiguous, so there is no scheme header to set.

### Authentication headers

| Header | Value |
|--------|-------|
| `X-SS58-Address` | The signer's address — a Substrate **SS58** address or a Solana **base58** address |
| `X-Signature` | The signature, as `0x`-prefixed hex **or** base58. Must decode to 64 bytes |
| `X-Timestamp` | ISO-8601 UTC, within 5 minutes of server time (the replay window) |

### The signed payload

Both schemes sign the same payload string:

    {METHOD}:{path}:{body_hash}:{timestamp}

- `path` is the decoded route the server binds — e.g. `/api/profiles/5Grw...`, not the percent-encoded URI. Routes are case-insensitive, so the operations in this document (whose paths read `api/Profiles`, `api/Companies`, `api/Migrations`) also match when requested in lowercase — but the server always reconstructs the payload from the **lowercase** route. Sign the lowercase form, not whatever casing you typed into the URL.
- `body_hash` is the Blake2b-128 (16-byte digest) of the body, as `0x`-prefixed **uppercase** hex.
- `timestamp` is `X-Timestamp` re-serialized in round-trip form, which always carries **7 fractional digits** (`.fffffffZ`).

**The body hash differs by surface:**

- **REST** signs a re-serialization of the *deserialized* body, not the bytes sent: field order is declaration order, nulls are emitted and the JSON is compact (e.g. `Profile` serializes as `ss58address, nickname, bio, profilePicture, x25519Key, ...`). DELETE and image uploads sign an *empty payload* instead — the hash segment is a literal empty string, leaving two adjacent colons.
- **GraphQL** signs the exact raw request body bytes, with `path = /graphql`.
- Multipart bodies (image uploads) are never hashed, so a signature does not cover the uploaded file.

**The two schemes differ only in what is signed:**

| Scheme | Address | Signed bytes |
|--------|---------|--------------|
| sr25519 | SS58 | `blake2b(payload, 128)` — the 16-byte digest |
| Solana ed25519 | base58, 32 bytes | `utf8(payload)` — the string itself, unhashed, so wallet popups show readable text |

The body-hash segment is Blake2b-128 in both cases.

**Two details break verification silently** (the payload must match the server's reconstruction byte-for-byte):

- the body hash must be **uppercase** `0x`-prefixed hex — JavaScript's `toString(16)` emits lowercase;
- the timestamp must carry **7 fractional digits** — `Date.prototype.toISOString()` gives 3; pad before signing.

### How the server verifies

1. Parses `X-Timestamp` and rejects it when the skew from server time exceeds 5 minutes.
2. Reconstructs the payload string from method, path, body hash and timestamp.
3. Decodes `X-Signature` (64 bytes from `0x`-hex or base58) and picks the scheme that recognises the address format.
4. Verifies the signature — the address carries the public key, so no database lookup is involved.
5. Authorizes by role: profile/company ownership, or an admin address from `ADMIN_ADDRESSES`.

### Errors

- **REST** — auth failures are HTTP **401** with the reason in the body (missing headers, bad signature, stale timestamp); **403** is a signed request whose caller may not act (not the owner, not an admin).
- **GraphQL** — errors come back in `errors[]` with a stable `extensions.code`: `UNAUTHORIZED` (headers missing), `INVALID_SIGNATURE`, `TIMESTAMP_OUT_OF_RANGE`, `FORBIDDEN` (not an admin), plus the domain codes (`UNKNOWN_NAMESPACE`, `BUCKET_IS_LOCKED`, `NOT_CONTRIBUTOR`, `LAST_MANAGER_REMOVAL`, `INVALID_INPUT`, ...).
""";
}
