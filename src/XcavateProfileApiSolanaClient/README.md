# XcavateProfileApiSolanaClient

C# client SDK for the [Xcavate Profile API](https://github.com/Xcavate/XcavateProfile), for
**Solana** applications: a REST client for profiles, a StrawberryShake-generated GraphQL client for
buckets, and ed25519 request signing over base58 addresses.

Same API and same namespaces as
[XcavateProfileApiClient](https://www.nuget.org/packages/XcavateProfileApiClient), built from the
same sources — but without `Substrate.NET.API`, and so without StreamJsonRpc, Serilog,
Newtonsoft.Json or MessagePack. The dependencies are `Solnet.Wallet`, `StrawberryShake` and
`Blake2Core`.

> Reference this package **or** `XcavateProfileApiClient`, never both: they define the same types in
> the same namespaces. Moving between them is a one-line change to the package reference — no code
> changes — as long as you sign through `IRequestSigner` rather than the sr25519 `Account`
> overloads, which exist only in the other package.

```bash
dotnet add package XcavateProfileApiSolanaClient
```

## Profiles (REST)

```csharp
using Solnet.Wallet;
using XcavateProfile.Client;
using XcavateProfileApiClient.Signing;

var account = new Wallet(mnemonic).Account;
var signer  = new SolanaRequestSigner(account);

using var client = new XcavateProfileClient(new XcavateProfileClientOptions
{
    ApiUrl = "https://profile-api.xcavate.io"
});

// Reads need no signer.
var all    = await client.GetProfilesAsync();
var one    = await client.GetProfileAsync(signer.Address);   // null when absent
var byNick = await client.GetProfileByNicknameAsync("myprofile");

// Ss58Address holds the Solana base58 address; the field keeps its name across both chains.
var profile = new Profile
{
    Ss58Address = signer.Address,
    X25519Key   = "0x0123…",
    Nickname    = "myprofile",
    Bio         = "My Solana profile"
};

await client.CreateProfileAsync(profile, signer);

profile.Bio = "Updated bio";
await client.UpdateProfileAsync(signer.Address, profile, signer);

using (var image = File.OpenRead("profile.jpg"))
{
    var imageUrl = await client.UploadImageAsync(signer.Address, image, "profile.jpg", signer);
}

await client.DeleteProfileAsync(signer.Address, signer);
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
            _ => new SigningHttpMessageHandler(new SolanaRequestSigner(account))));
```

```csharp
var result = await client.CreateNamespace.ExecuteAsync(
    new NamespaceMetadataInput { Name = "my-namespace", SchemaUri = "ipfs://…" });

result.EnsureNoErrors();
```

## How the signing works

`SolanaRequestSigner` signs the **raw UTF-8 payload string** with ed25519 and encodes the signature
as base58 — the same bytes and the same encoding a browser wallet produces via `signMessage` plus
`bs58.encode`, so this path and a real frontend are verified by identical server code.

The payload is signed unhashed on purpose: wallets render the bytes handed to `signMessage` as UTF-8
in the approval popup, so signing a digest would show the user binary garbage.

```csharp
string payload = CryptoHelper.ConstructPayload(method, path, body, timestamp);
// => "POST:/api/profiles:0xAB12…:2026-07-27T10:00:00.0000000Z"
```

`body` is an `IPayloadBody` — the `Profile` being sent, or `EmptyPayloadBody.Instance` — whose hash
is Blake2b-128 as `0x`-prefixed uppercase hex (`CryptoHelper.HashHex`). `path` is the decoded path,
matching the route the server binds; the client percent-encodes the URI separately.

Requests carry three headers: `X-SS58-Address` (your base58 address — the name is historical),
`X-Signature` and `X-Timestamp` (ISO-8601 UTC, accepted within five minutes of the server's clock).

## License and source

See the [repository](https://github.com/Xcavate/XcavateProfile).
