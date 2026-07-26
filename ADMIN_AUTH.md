# XcavateProfile - Authentication System Documentation

This document explains the Sr25519 authentication system, Blake2 hashing implementation, HTTP header construction, and admin authorization mechanism. XcavateProfile also accepts Solana ed25519 signatures on the same endpoints — see [README.md](README.md#authentication) for the dual-scheme payload format and a browser-wallet signing example; the mechanics described here (hashing, headers, admin checks) apply to both schemes.

## Table of Contents
1. [Overview](#overview)
2. [Sr25519 Signature Verification](#sr25519-signature-verification)
3. [Blake2 Body Hashing](#blake2-body-hashing)
4. [HTTP Header Construction](#http-header-construction)
5. [Signed Payload Format](#signed-payload-format)
6. [Admin Authorization](#admin-authorization)
7. [Security Considerations](#security-considerations)

## Overview

XcavateProfile uses a signature-based authentication system that aligns with Substrate/Polkadot cryptographic standards. This approach:

- Uses Substrate's native cryptographic primitives
- Provides stateless authentication without JWT tokens
- Supports both regular users and administrators
- Includes replay attack prevention via timestamp validation
- Also accepts Solana ed25519 signatures on the same endpoints (see README.md)

## Sr25519 Signature Verification

The system implements Sr25519 signature verification using the `Substrate.NET.API` package. This ensures compatibility with all Substrate-based blockchains (Polkadot, Kusama, and custom chains).

### Signature Generation (Client Side)

```csharp
using Substrate.NetApi;
using Substrate.NetApi.Model.Types;
using XcavateProfile.Client;

// Any Substrate.NetApi Account works, e.g. loaded from a BIP-39 mnemonic:
Account account = MnemonicsModel.GetAccountFromMnemonics(mnemonic);
var address = account.Value; // the SS58 address

// CryptoHelper.SignAsync hashes the payload (Blake2b-128) and signs the hash — this is
// the sr25519 path. The Solana ed25519 path signs the raw payload instead; see README.md.
byte[] signature = await CryptoHelper.SignAsync(payload, account);
```

### Signature Verification (Server Side)

```csharp
using Substrate.NetApi;
using XcavateProfile.Client;

// The address itself carries the public key, so no database lookup is needed to verify —
// CryptoHelper.VerifySignature decodes the SS58 address internally.
var isValid = CryptoHelper.VerifySignature(payload, signatureBytes, address);
```

In production this call happens inside `Sr25519SignatureScheme.Verify`
(`src/XcavateProfileApiClient/Signing/Sr25519SignatureScheme.cs`), which also retries a
`<Bytes>...</Bytes>`-wrapped variant of the payload for compatibility with the polkadot-js
browser extension, which wraps whatever it is asked to sign.

## Blake2 Body Hashing

The Blake2 hashing algorithm is used for hashing request bodies in the signature. This is the same hashing algorithm used by Substrate for data hashing.

### Why Blake2?

- **Natively supported by Substrate**: Ensures cryptographic compatibility
- **Fast and secure**: Modern cryptographic hash function
- **Deterministic**: Same input always produces same output

### Implementation

`CryptoHelper.Hash` (`src/XcavateProfileApiClient/CryptoHelper.cs`) wraps
`Substrate.NET.Schnorrkel`'s Blake2 extension, hashing to 128 bits (16 bytes):

```csharp
using Substrate.NetApi;
using XcavateProfile.Client;

// Hash a string to a 16-byte Blake2b-128 digest
byte[] hashBytes = CryptoHelper.Hash(inputString);

// Hex-encode it the same way the server does: 0x-prefixed, UPPERCASE
var hashHex = Utils.Bytes2HexString(hashBytes); // e.g. "0xA1B2C3D4..."
```

This is exactly what `Profile.Hash()` does for REST request bodies, and what
`GraphQLSignatureMiddleware` does for the raw GraphQL request body.

### Empty Body Hashing

DELETE requests and image uploads have nothing meaningful to hash — for image uploads the
multipart body is never hashed at all, so the signature does not cover the uploaded file.
Both use `EmptyPayloadBody`, whose `Hash()` returns a literal empty string, **not** a hash of
one:

```csharp
using XcavateProfileApiClient;

IPayloadBody body = new EmptyPayloadBody();
body.Hash(); // == "" — not Blake2b("") or any 0x-prefixed value
```

That makes the body-hash segment of the payload empty, so the signed string has two colons
back to back where the hash would otherwise go — see the DELETE and image-upload examples in
[Signed Payload Format](#signed-payload-format) below.

## HTTP Header Construction

All state-changing requests must include specific authentication headers:

### Required Headers

| Header | Description | Format |
|--------|-------------|--------|
| `X-SS58-Address` | The signer's address — Substrate SS58 or Solana base58 | `5...` or `DQJZ...` |
| `X-Signature` | The 64-byte signature, `0x`-hex or base58 | `0xB8AA...` or `4h96nSR...` |
| `X-Timestamp` | ISO 8601 UTC — server re-serializes to 7 fractional-second digits before verifying | `2024-01-15T10:30:45.1234567Z` |

### Example Request

```http
POST /api/profiles HTTP/1.1
Host: localhost:5000
X-SS58-Address: 5GrwvaEF5zKbXCEe9qGjZL23Y641mot2Ff6hS3s8jF3g3k3W
X-Signature: 0x13549D7A4537AEB4F1D2DAA8B9510EF4EF351B49BB431BC4B53488C5B43A60A15A6CB943E2FC84DD850CBA951444AAE92288DBCAD1A28E2E4E7B148A7D01310C
X-Timestamp: 2024-01-15T10:30:45.1234567Z
Content-Type: application/json

{
  "ss58address": "5GrwvaEF5zKbXCEe9qGjZL23Y641mot2Ff6hS3s8jF3g3k3W",
  "nickname": "myprofile"
}
```

(The signature is 128 hex characters after `0x` — 64 bytes, matching
`SignatureEncoding.SignatureLength`.)

### Header Construction (Client SDK)

The C# client library builds and attaches the auth headers for you:

```csharp
using XcavateProfile.Client;
using XcavateProfileApiClient.Signing;

var client = new XcavateProfileClient(new XcavateProfileClientOptions { ApiUrl = apiUrl });

// sr25519, from a Substrate.NetApi Account:
await client.CreateProfileAsync(profile, account);

// Or explicitly via any IRequestSigner (this is also how Solana signing is selected):
await client.CreateProfileAsync(profile, new SubstrateRequestSigner(account));
```

Internally (`src/XcavateProfileApiClient/XcavateProfileApiClient.cs`) this builds the payload
via `CryptoHelper.ConstructPayload(method, path, body, timestamp)` — `body` is an
`IPayloadBody` such as the `Profile` itself, not a pre-computed hash string — signs it with the
chosen `IRequestSigner`, and sets `X-SS58-Address`, `X-Signature` and `X-Timestamp`.

## Signed Payload Format

The payload string follows this strict format:

```
<method>:<path>:<body_hash>:<timestamp>
```

### Components

1. **`<method>`**: HTTP method (GET, POST, PUT, DELETE)
2. **`<path>`**: Request path (e.g., `/api/profiles`)
3. **`<body_hash>`**: `0x`-prefixed, UPPERCASE hex-encoded Blake2b-128 hash of the request body — empty for requests with nothing to hash (DELETE, image upload)
4. **`<timestamp>`**: ISO 8601 UTC timestamp, re-serialized to 7 fractional-second digits

### Examples

```text
# Create profile
POST:/api/profiles:0xFA8847B0C33183273F5945508B31C320:2024-01-15T10:30:45.1230000Z

# Update profile
PUT:/api/profiles/5GrwvaEF5zKbXCEe9qGjZL23Y641mot2Ff6hS3s8jF3g3k3W:0x2937013F2181810606B2A799B05BDA28:2024-01-15T10:31:00.4560000Z

# Delete profile — EmptyPayloadBody.Hash() is "", not a hash, so the body-hash segment is
# empty (note the adjacent "::")
DELETE:/api/profiles/5GrwvaEF5zKbXCEe9qGjZL23Y641mot2Ff6hS3s8jF3g3k3W::2024-01-15T10:32:00.7890000Z

# Image upload — also EmptyPayloadBody: the multipart body is not hashed, so the signature
# covers only the method, path and timestamp, not the uploaded file
POST:/api/profiles/5GrwvaEF5zKbXCEe9qGjZL23Y641mot2Ff6hS3s8jF3g3k3W/image::2024-01-15T10:33:00.1230000Z
```

## Admin Authorization

Admin addresses are configured via the `ADMIN_ADDRESSES` environment variable.

### Configuration

```env
# Format: comma-separated addresses (SS58 and/or Solana base58)
ADMIN_ADDRESSES=5GrwvaEF5zKbXCEe9qGjZL23Y641mot2Ff6hS3s8jF3g3k3W,5DZ1xN32y6fV5bQ8j7K4m5L6n7M8o9P0q1R2s3T4u5V6
```

### Admin Capabilities

Admins have elevated privileges:

- **Can update any profile** (not just their own)
- **Can delete any profile** (not just their own)
- **Bypass ownership verification**

### Admin Check Implementation

```csharp
// In SignatureValidator.cs
public bool IsAdmin(string address)
{
    return _adminAddresses.Contains(address);
}

// In controller (see ProfilesController.cs)
if (address != ss58address && !_signatureValidator.IsAdmin(address))
{
    return StatusCode(StatusCodes.Status403Forbidden, "You can only update your own profile");
}
```

### Security Note

Admin addresses are loaded at application startup from the environment variable. They are **not stored in the database** to:

- Keep admin privileges centralized
- Avoid database migrations for admin changes
- Enable instant admin list updates via environment variable

## Security Considerations

### Replay Attack Prevention

The timestamp validation prevents replay attacks:

```csharp
// Server-side validation
var now = DateTime.UtcNow;
var skew = Math.Abs((now - timestamp).TotalSeconds);
if (skew > 300) // 5 minutes
{
    return Unauthorized("Timestamp too old or too far in the future");
}
```

### Signature Tampering Protection

1. The signature covers the entire payload
2. Any modification to method, path, body, or timestamp invalidates the signature
3. The hash ensures body integrity

### Rate Limiting Recommendation

For production deployments, consider adding rate limiting to prevent:

- Brute force signature attacks
- DDoS attacks
- Excessive API usage

## Debugging Signatures

### Common Issues

| Issue | Symptom | Solution |
|-------|---------|----------|
| Wrong timestamp | `Timestamp too old` | Use current UTC time |
| Wrong secret key | `Signature verification failed` | Verify keypair consistency |
| Body mismatch | `Signature verification failed` | Hash exact request body string |
| Invalid SS58 | `Invalid SS58 address format` | Use valid Substrate address |

### Debug Output

Enable detailed logging to diagnose signature issues:

```csharp
// Log the constructed payload for verification. body is an IPayloadBody — the Profile
// being sent, or EmptyPayloadBody for DELETE / image upload — not a pre-computed hash.
var payload = CryptoHelper.ConstructPayload(method, path, body, timestamp);
Console.WriteLine($"Payload: {payload}");

// Log the signature (for debugging only — never do this against production traffic)
Console.WriteLine($"Signature: {Utils.Bytes2HexString(signature)}");
```

### Worked Examples

For runnable, currently-passing examples of constructing and verifying both signature
schemes, see `tests/XcavateBuckets.Tests/SignatureValidatorTests.cs` — it builds real sr25519
and Solana signatures and validates them through the same `SignatureValidator` the API uses.

## API Format Examples

### Request Body

```json
{
  "ss58address": "5GrwvaEF5zKbXCEe9qGjZL23Y641mot2Ff6hS3s8jF3g3k3W",
  "nickname": "myprofile",
  "bio": "Substrate profile",
  "profilePicture": null,
  "x25519Key": "0x0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef"
}
```

### Signature Computation Flow

```
1. Client has keypair: secretKey, address
2. Client constructs request body JSON
3. Client computes Blake2 hash of body JSON
4. Client constructs payload: "POST:/api/profiles:{hash}:{timestamp}"
5. Client signs payload using Sr25519 (or Solana ed25519 — see README.md)
6. Client sends request with X-* headers
7. Server receives request
8. Server computes Blake2 hash of body
9. Server reconstructs payload with same values
10. Server verifies signature using public key from profile
11. Server checks timestamp (within 5 minutes)
12. Server authorizes based on ownership or admin status
```

## Migration Guide

### From JWT to Signature-based Auth

1. **Remove JWT middleware**
   - Remove token validation
   - Remove token refresh logic

2. **Add Signature Validator**
   - Implement signature verification
   - Add header parsing middleware

3. **Update Client Code**
   - Generate signatures for each request
   - Add X-* headers
   - Compute body hash

4. **Test Thoroughly**
   - Verify signature generation/verification
   - Test replay attack prevention
   - Test admin authorization

## Troubleshooting

### Signature Verification Fails

1. Check timestamp is current
2. Verify exact same body hash computation
3. Ensure SS58 address matches keypair
4. Check signature encoding: hex must be `0x`-prefixed, or it is parsed as base58 instead and will fail to decode

### 401 Unauthorized

- Missing required headers
- Invalid timestamp (outside 5-minute window)
- Signature verification failure
- Profile not found

### 403 Forbidden

- Not admin and trying to modify another's profile
- Admin address not configured or incorrect
