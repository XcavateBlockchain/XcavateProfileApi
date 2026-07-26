# XcavateProfile - Authentication System Documentation

This document explains the Sr25519 authentication system, Blake2 hashing implementation, HTTP header construction, and admin authorization mechanism.

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

## Sr25519 Signature Verification

The system implements Sr25519 signature verification using the `Substrate.NET.API` package. This ensures compatibility with all Substrate-based blockchains (Polkadot, Kusama, and custom chains).

### Signature Generation (Client Side)

```csharp
using Substrate.NET.API;
using XcavateProfileApiClient;

// Create keypair
var keypair = new KeyPair(secretKey);
var address = keypair.Address;

// Sign payload
var (signature, signedAddress) = CryptoHelper.SignPayload(payload, secretKey, address);
```

### Signature Verification (Server Side)

```csharp
// The server receives the signature and verifies it
var profile = await _context.Profiles.FindAsync(ss58address);

// Load public key from profile
var keypair = new KeyPair(profile.Ss58Address);
var publicKey = keypair.PublicKey;

// Verify signature
var isValid = CryptoVerify.Sr25519.Verify(hash, signatureBytes, publicKey);
```

## Blake2 Body Hashing

The Blake2 hashing algorithm is used for hashing request bodies in the signature. This is the same hashing algorithm used by Substrate for data hashing.

### Why Blake2?

- **Natively supported by Substrate**: Ensures cryptographic compatibility
- **Fast and secure**: Modern cryptographic hash function
- **Deterministic**: Same input always produces same output

### Implementation

The Blake2 implementation comes from `Substrate.NET.API`:

```csharp
using Substrate.NET.API;

// Hash a string
var hashBytes = CryptoFactory.Blake2b(inputString);
var hashHex = BitConverter.ToString(hashBytes).Replace("-", "").ToLowerInvariant();

// Hash a stream
using (var stream = new MemoryStream(data))
{
    var hashBytes = CryptoFactory.Blake2b(stream.ToArray());
}
```

### Empty Body Hashing

For requests with no body (DELETE), an empty string hash is used:

```csharp
var emptyHash = CryptoFactory.Blake2b(string.Empty);
var emptyHashHex = BitConverter.ToString(emptyHash).Replace("-", "").ToLowerInvariant();
```

## HTTP Header Construction

All state-changing requests must include specific authentication headers:

### Required Headers

| Header | Description | Format |
|--------|-------------|--------|
| `X-SS58-Address` | The signer's address — Substrate SS58 or Solana base58 | `5...` or `DQJZ...` |
| `X-Signature` | The 64-byte signature, `0x`-hex or base58 | `0xB8AA...` or `4h96nSR...` |
| `X-Timestamp` | ISO 8601 UTC timestamp | `2024-01-15T10:30:45.123Z` |

### Example Request

```http
POST /api/profiles HTTP/1.1
Host: localhost:5000
X-SS58-Address: 5GrwvaEF5zKbXCEe9qGjZL23Y641mot2Ff6hS3s8jF3g3k3W
X-Signature: a1b2c3d4e5f67890123456789012345678901234567890123456789012345678901234567890123456789012345678901234567890123456789012345678901234567890123456789012345678901234567890123456789012345678901234567890123456789012345678901234567890123456789012345678901234567890
X-Timestamp: 2024-01-15T10:30:45.123Z
Content-Type: application/json

{
  "ss58address": "5GrwvaEF5zKbXCEe9qGjZL23Y641mot2Ff6hS3s8jF3g3k3W",
  "nickname": "myprofile"
}
```

### Header Construction (Client SDK)

The C# client library automatically handles header construction:

```csharp
var timestamp = DateTime.UtcNow;
var bodyJson = JsonSerializer.Serialize(profile);
var bodyHash = CryptoHelper.ComputeBlake2bHash(bodyJson);
var payload = CryptoHelper.ConstructPayload("POST", "/api/profiles", bodyHash, timestamp);
var (signature, signedAddress) = CryptoHelper.SignPayload(payload, secretKey, address);

// Headers are set automatically
httpClient.DefaultRequestHeaders.Add("X-SS58-Address", signedAddress);
httpClient.DefaultRequestHeaders.Add("X-Signature", signature);
httpClient.DefaultRequestHeaders.Add("X-Timestamp", timestamp.ToString("o"));
```

## Signed Payload Format

The payload string follows this strict format:

```
<method>:<path>:<body_hash>:<timestamp>
```

### Components

1. **`<method>`**: HTTP method (GET, POST, PUT, DELETE)
2. **`<path>`**: Request path (e.g., `/api/profiles`)
3. **`<body_hash>`**: Hex-encoded Blake2 hash of request body
4. **`<timestamp>`**: ISO 8601 UTC timestamp

### Examples

```text
# Create profile
POST:/api/profiles:a1b2c3d4e5f6...:2024-01-15T10:30:45.123Z

# Update profile
PUT:/api/profiles/5GrwvaEF5zKbXCEe9qGjZL23Y641mot2Ff6hS3s8jF3g3k3W:b2c3d4e5f6a7...:2024-01-15T10:31:00.456Z

# Delete profile (empty body)
DELETE:/api/profiles/5GrwvaEF5zKbXCEe9qGjZL23Y641mot2Ff6hS3s8jF3g3k3W:e3f4a5b6c7d8...:2024-01-15T10:32:00.789Z

# Image upload
POST:/api/profiles/5GrwvaEF5zKbXCEe9qGjZL23Y641mot2Ff6hS3s8jF3g3k3W/image:f4a5b6c7d8e9...:2024-01-15T10:33:00.123Z
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

// In controller
if (address != ss58address && !_signatureValidator.IsAdmin(address))
{
    return Forbid("You can only update your own profile");
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
// Log the constructed payload for verification
var payload = CryptoHelper.ConstructPayload(method, path, bodyHash, timestamp);
Console.WriteLine($"Payload: {payload}");

// Log the signature (for debugging only)
Console.WriteLine($"Signature: {signature}");
```

### Verification Tool

```csharp
public static class SignatureDebugger
{
    public static (bool valid, string reason) VerifySignature(
        string payload,
        string signatureHex,
        string ss58Address,
        string secretKey)
    {
        try
        {
            var (verifiedSig, verifiedAddr) = CryptoHelper.SignPayload(payload, secretKey);
            
            if (verifiedSig != signatureHex)
                return (false, "Signature doesn't match");
                
            if (verifiedAddr != ss58Address)
                return (false, "Address mismatch");
                
            return (true, "Valid");
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }
}
```

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
5. Client signs payload using Sr25519
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
4. Check signature hex encoding (no prefix)

### 401 Unauthorized

- Missing required headers
- Invalid timestamp (outside 5-minute window)
- Signature verification failure
- Profile not found

### 403 Forbidden

- Not admin and trying to modify another's profile
- Admin address not configured or incorrect
