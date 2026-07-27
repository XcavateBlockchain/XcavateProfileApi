# XcavateProfileApiClient

C# client SDK for the [Xcavate Profile API](https://github.com/Xcavate/XcavateProfile): a REST
client for profiles, a StrawberryShake-generated GraphQL client for buckets, and the request
signing both use.

Signs with **Substrate sr25519** (hex) or **Solana ed25519** (base58). Reads are public on both
APIs; every write is signed.

> Building a Solana-only application? [**XcavateProfileApiSolanaClient**](https://www.nuget.org/packages/XcavateProfileApiSolanaClient)
> exposes this same API under the same namespaces without the `Substrate.NET.API` dependency —
> and therefore without StreamJsonRpc, Serilog, Newtonsoft.Json or MessagePack. Reference one or
> the other, never both: they define the same types.

```bash
dotnet add package XcavateProfileApiClient
```

## Profiles (REST)

```csharp
using Substrate.NetApi.Model.Types;
using XcavateProfile.Client;
using XcavateProfileApiClient.Signing;

using var client = new XcavateProfileClient(new XcavateProfileClientOptions
{
    ApiUrl = "https://profile-api.xcavate.io"
});

// Reads need no signer.
var all    = await client.GetProfilesAsync();
var one    = await client.GetProfileAsync(address);        // null when absent
var byNick = await client.GetProfileByNicknameAsync("myprofile");

// Writes take the signing account. Ss58Address and X25519Key are required.
var profile = new Profile
{
    Ss58Address = account.Value,
    X25519Key   = "0x0123…",
    Nickname    = "myprofile",
    Bio         = "My Substrate profile"
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

Every write also has an `IRequestSigner` overload, which is how a non-Substrate scheme is selected:

```csharp
await client.CreateProfileAsync(profile, new SolanaRequestSigner(solanaAccount));
```

All methods take an optional `CancellationToken`. The client is safe to use from concurrent calls,
and a second constructor accepts your own `HttpClient` — an `IHttpClientFactory` one, for
instance — which it will not dispose:

```csharp
var client = new XcavateProfileClient(options, httpClientFactory.CreateClient("xcavate"));
```

Failed requests throw `HttpRequestException` carrying the status code and the server's explanation.

## Buckets (GraphQL)

The generated client is registered through DI. `SigningHttpMessageHandler` signs the outgoing
request body, so it must be the outermost handler that touches it — supply a signer only when the
client sends mutations, since queries are public:

```csharp
using XcavateProfileApiClient;
using XcavateProfileApiClient.Buckets;

services
    .AddXcavateBucketsClient()
    .ConfigureHttpClient(
        client => client.BaseAddress = new Uri("https://profile-api.xcavate.io/graphql"),
        builder => builder.ConfigurePrimaryHttpMessageHandler(
            _ => new SigningHttpMessageHandler(account)));   // or any IRequestSigner
```

```csharp
var result = await client.CreateNamespace.ExecuteAsync(
    new NamespaceMetadataInput { Name = "my-namespace", SchemaUri = "ipfs://…" });

result.EnsureNoErrors();
```

## Signing primitives

`CryptoHelper` is the seam the client, the server and both schemes share:

```csharp
byte[] digest    = CryptoHelper.Hash(input);                            // Blake2b-128
string hex       = CryptoHelper.HashHex(input);                         // 0x-prefixed, uppercase
string payload   = CryptoHelper.ConstructPayload(method, path, body, timestamp);
byte[] signature = await CryptoHelper.SignAsync(payload, account);      // sr25519
bool ok          = CryptoHelper.VerifySignature(payload, signature, address);
```

`body` is an `IPayloadBody` — the `Profile` being sent, or `EmptyPayloadBody.Instance` — not a
pre-computed hash string. `path` is the decoded path, matching the route the server binds.

Requests carry three headers: `X-SS58-Address`, `X-Signature` and `X-Timestamp` (ISO-8601 UTC,
accepted within five minutes of the server's clock).

## License and source

See the [repository](https://github.com/Xcavate/XcavateProfile).
