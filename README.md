# XcavateProfile - Substrate/Polkadot Profile Management System

A comprehensive system for Substrate/Polkadot profile registration and management, featuring:

- **API Backend**: ASP.NET Core Web API with Entity Framework Core (PostgreSQL)
- **Client SDK**: C# class library with Substrate.NET for Sr25519 signature verification
- **Authentication**: Signature-based auth using Blake2 hashing (aligned with Substrate)
- **Storage**: Hetzner Object Storage (S3-compatible) for profile pictures
- **CI/CD**: Docker deployment to Hetzner servers + NuGet package publishing

## Architecture

```
┌─────────────────────────────────────────────────────────────┐
│                        XcavateProfileClient                 │
│  - C# SDK with Substrate.NET API                            │
│  - Sr25519 signature generation                             │
│  - Blake2 hashing for body content                          │
│  - X-* headers for authentication                           │
└─────────────────────────────────────────────────────────────┘
                             │
                             │ HTTP Requests (with auth headers)
                             ▼
┌─────────────────────────────────────────────────────────────┐
│                        XcavateProfileApi                    │
│  - ASP.NET Core Web API (.NET 10)                          │
│  - Profile CRUD endpoints                                   │
│  - Authentication middleware (signature verification)       │
│  - Image upload to S3 (Hetzner Object Storage)             │
└─────────────────────────────────────────────────────────────┘
                             │
                             │ EF Core ORM
                             ▼
┌─────────────────────────────────────────────────────────────┐
│                        PostgreSQL                            │
│  - Profile data storage                                     │
│  - SS58 address as primary key                             │
│  - Unique nickname constraint                               │
└─────────────────────────────────────────────────────────────┘
```

## API Endpoints

| Method | Endpoint | Description |
|--------|----------|-------------|
| GET | `/api/profiles` | List all profiles |
| GET | `/api/profiles/{ss58address}` | Get profile by address |
| GET | `/api/profiles/nickname/{nickname}` | Get profile by nickname |
| POST | `/api/profiles` | Create profile (requires auth) |
| PUT | `/api/profiles/{ss58address}` | Update profile, or create it if it does not exist yet (requires auth) |
| DELETE | `/api/profiles/{ss58address}` | Delete profile (requires auth) |
| POST | `/api/profiles/{ss58address}/image` | Upload profile image (requires auth) |

## Authentication

All state-changing requests (POST/PUT/DELETE) require a signature — Sr25519 (Substrate SS58) or
Solana ed25519:

### Authentication Headers

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

**Signing from a browser wallet:**

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

### Signature Verification
1. Server computes Blake2 hash of the request body
2. Constructs the payload string with method, path, hash, and timestamp
3. Verifies the signature using the public key from the profile
4. Checks timestamp is within 5 minutes (replay attack prevention)
5. Authorizes based on profile ownership or admin status

## Running Locally

### Prerequisites
- .NET 10.0 SDK
- Docker & Docker Compose
- PostgreSQL (or use Docker)

### Setup

1. **Clone the repository**
```bash
git clone <repository-url>
cd XcavateProfile
```

2. **Copy environment template**
```bash
cp .env.example .env
```

3. **Configure environment variables**
Edit `.env` with your settings:
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
ADMIN_ADDRESSES=5GrwvaEF5zKbXCEe9qGjZL23Y641mot2Ff6hS3s8jF3g3k3W
```

4. **Start the stack with Docker**
```bash
docker-compose up -d
```

5. **Run database migrations**
```bash
dotnet ef database update --project src/XcavateProfileApi --context ProfileDbContext
```

6. **Run the API**
```bash
dotnet run --project src/XcavateProfileApi
```

7. **Access Swagger UI**
- Navigate to `http://localhost:5000/swagger`

## Using the C# Client SDK

### Installation

```bash
dotnet add package XcavateProfileApiClient
```

### Basic Usage

```csharp
using XcavateProfileApiClient;
using Substrate.NET.API;

// Generate a keypair
var keypair = new KeyPair();
var secretKey = keypair.PrivateKey;
var address = keypair.Address;

// Create client
var client = new XcavateProfileApiClient(new XcavateProfileApiClientOptions
{
    ApiUrl = "http://localhost:5000",
    SecretKey = secretKey,
    SS58Address = address
});

// Create a profile
var profile = new Profile
{
    Ss58Address = address,
    Nickname = "myprofile",
    Bio = "My Substrate profile"
};

await client.CreateProfileAsync(profile, secretKey);

// Get the profile
var retrieved = await client.GetProfileAsync(address);

// Update the profile
profile.Bio = "Updated bio";
await client.UpdateProfileAsync(address, profile, secretKey);

// Upload an image
using (var imageStream = File.OpenRead("profile.jpg"))
{
    var imageUrl = await client.UploadImageAsync(address, imageStream, "profile.jpg", secretKey);
}

// Delete the profile
await client.DeleteProfileAsync(address, secretKey);
```

### Manual Signature Construction (Advanced)

```csharp
using XcavateProfileApiClient;
using Substrate.NET.API;

// Construct payload manually
var timestamp = DateTime.UtcNow;
var payload = CryptoHelper.ConstructPayload(
    "POST", 
    "/api/profiles", 
    CryptoHelper.ComputeBlake2bHash(bodyJson), 
    timestamp);

// Sign the payload using Account
var (signature, signedAddress) = CryptoHelper.SignPayload(payload, account);

// Use signature in request headers
httpClient.DefaultRequestHeaders.Add("X-SS58-Address", signedAddress);
httpClient.DefaultRequestHeaders.Add("X-Signature", signature);
httpClient.DefaultRequestHeaders.Add("X-Timestamp", timestamp.ToString("o"));
```

## CI/CD Deployment

### Deployment Pipeline

1. **Build Docker Image** (GitHub Actions)
   - Multi-stage Docker build
   - Push to GitHub Container Registry (ghcr.io)

2. **Deploy to Hetzner** (GitHub Actions)
   - SSH to Hetzner server
   - Update repository
   - Generate `.env` from GitHub secrets
   - Run Docker Compose
   - Execute database migrations

3. **NuGet Publishing** (GitHub Actions)
   - Build and pack the client SDK
   - Publish to NuGet.org
   - Versioning: `1.0.<run_number>` for develop, git tags for releases

### Reverse Proxy Configuration

The image upload endpoint accepts images up to 25MB (26MB request limit including
multipart overhead). Any reverse proxy in front of the API must allow at least the
same request body size, or uploads fail with `413 Request Entity Too Large` before
reaching the API. For nginx (default limit is 1MB):

```nginx
# in the server/location block proxying to the API
client_max_body_size 26m;
```

Then reload: `nginx -t && systemctl reload nginx`

### Required GitHub Secrets

| Secret | Description |
|--------|-------------|
| `HETZNER_HOST` | Hetzner server IP/hostname |
| `HETZNER_USER` | SSH username |
| `HETZNER_SSH_KEY` | SSH private key |
| `HETZNER_DEPLOY_DIR` | Remote deployment directory |
| `HETZNER_POSTGRES_HOST` | PostgreSQL connection info |
| `HETZNER_S3_ENDPOINT` | Hetzner S3 endpoint |
| `NUGET_API_KEY` | NuGet API key |

## Admin Authentication

Admin addresses are configured via the `ADMIN_ADDRESSES` environment variable. Admins can:

- Update any profile
- Delete any profile
- Bypass profile ownership checks

**Format**: Comma-separated list of addresses. SS58 and Solana base58 addresses can be mixed freely.
```
ADMIN_ADDRESSES=5GrwvaEF5zKbXCEe9qGjZL23Y641mot2Ff6hS3s8jF3g3k3W,5DZ1xN32y6fV5bQ8j7K4m5L6n7M8o9P0q1R2s3T4u5V6
```

## Data Model

### Profile Entity

| Field | Type | Description |
|-------|------|-------------|
| `ss58address` | string (PK) | Substrate SS58 address (required) |
| `nickname` | string | Unique nickname (optional) |
| `bio` | string | Profile description (optional) |
| `profilePicture` | string | URL to profile picture (optional) |
| `x25519Key` | string | x25519 public key (optional) |

## Security Features

- **Signature Verification**: Sr25519 signatures verified on every state-changing request
- **Replay Attack Prevention**: Timestamp validation (5-minute window)
- **Authorization Checks**: Users can only modify their own profiles (unless admin)
- **Admin Override**: Environment-based admin list (not stored in database)
- **Hash Integrity**: Blake2 hashing for body content (Substrate-compatible)

## Troubleshooting

### Database Connection Issues
- Ensure PostgreSQL is running: `docker-compose ps`
- Check connection string in `.env`
- Verify network connectivity between containers

### Signature Verification Failures
- Verify timestamp is within 5 minutes of server time
- Ensure `X-SS58-Address`, `X-Signature`, and `X-Timestamp` headers are present
- Check signature was created with correct secret key

### Hetzner S3 Upload Issues
- Verify `S3_ENDPOINT`, `S3_ACCESS_KEY`, and `S3_SECRET_KEY` in `.env`
- Ensure bucket exists and credentials have write permissions
- Check firewall allows outbound connections to S3 endpoint

## Contributing

1. Fork the repository
2. Create a feature branch (`git checkout -b feature/amazing-feature`)
3. Commit changes (`git commit -m 'feat: add amazing feature'`)
4. Push to the branch (`git push origin feature/amazing-feature`)
5. Open a Pull Request

## License

This project is licensed under the MIT License.

## Acknowledgments

- Built with [Substrate.NET.API](https://github.com/paritytech/parity-substrate-dotnet) for cryptographic operations
- Uses Entity Framework Core for data persistence
- Deployed on Hetzner Cloud infrastructure
