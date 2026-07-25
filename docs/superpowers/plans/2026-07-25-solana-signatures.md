# Solana Signature Support Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Let callers authenticate against XcavateProfileApi with a Solana keypair (ed25519, base58 address) in addition to the existing sr25519/SS58 scheme, on both the REST and GraphQL surfaces.

**Architecture:** The signed payload string is unchanged and shared by both schemes; only the bytes handed to the signature function differ. An `ISignatureScheme` abstraction is selected per-request by *inferring* the scheme from the address format (SS58 and Solana base58 are provably distinguishable), so no new headers are introduced and no existing client changes. `SignatureValidator` is the single integration point — `GraphQLSignatureMiddleware` and all four `ProfilesController` actions already delegate to it and need no edits.

**Tech Stack:** .NET 10, NUnit 4, Hot Chocolate 16.5.1, Substrate.NET.API 0.9.24-rc6, **Solnet.Wallet 6.1.0** (new).

**Spec:** `docs/superpowers/specs/2026-07-25-solana-signatures-design.md`

## Global Constraints

- **The sr25519 path must stay byte-for-byte identical.** No existing client re-signs anything. Any change to `CryptoHelper.ConstructPayload`, `CryptoHelper.Hash`, `CryptoHelper.SignAsync`, or the `<Bytes>…</Bytes>` retry is out of scope and a plan violation.
- **Payload format is shared and unchanged:** `{METHOD}:{path}:{blake2b_128_hex_of_body}:{timestamp:o}`, built by `CryptoHelper.ConstructPayload`.
- **Solana signs `utf8(payload)` raw** — never a hash. sr25519 signs `Blake2b(utf8(payload), 128)`. This asymmetry is deliberate: wallets render `signMessage` bytes as UTF-8 in the approval popup.
- **Headers are unchanged:** `X-SS58-Address`, `X-Signature`, `X-Timestamp`. No `X-Signature-Scheme`.
- **`X-Signature` accepts both encodings for both schemes:** `0x`-prefixed → hex, otherwise base58. Decoded length must be exactly **64** bytes.
- **New shared code lives in `src/XcavateProfileApiClient/Signing/`**, namespace `XcavateProfileApiClient.Signing`. The API project consumes it through the existing `ProjectReference` — do not add a Solnet reference to `XcavateProfileApi.csproj`.
- **Package version:** `Solnet.Wallet` exactly `6.1.0`.
- `CryptoHelper` keeps its current public surface. New schemes call into it; they do not replace it.
- Tests are NUnit (`[TestFixture]`, `[Test]`, `Assert.That`). `XcavateBuckets.Tests` runs offline; `XcavateProfile.ApiTests` requires the live docker stack on `http://localhost:5000`.

## Verified Library Behaviour

These were confirmed empirically against the pinned package versions. The code in this plan depends on them; do not "simplify" the guards away.

| Call | Input | Result |
|---|---|---|
| `Utils.GetPublicKeyFrom` | SS58 | 32-byte pubkey |
| `Utils.GetPublicKeyFrom` | Solana base58 (32B) | throws `NotSupportedException: Unsupported address size.` |
| `Utils.GetPublicKeyFrom` | corrupted SS58 | throws `NotSupportedException: Address checksum is wrong.` |
| `Utils.GetPublicKeyFrom` | non-base58 chars | throws **`FormatException`** |
| `new PublicKey(ss58)` | SS58 | **succeeds with 35 bytes**, `IsOnCurve=false`; throws only later inside `Verify` |
| `new PublicKey(s)` | non-base58 chars | throws **`ArgumentException`** |
| `Utils.HexToByteArray("0xZZ")` | invalid hex digits | **returns 1 byte, does not throw** |
| `Utils.HexToByteArray("0x123")` | odd digit count | throws `NotSupportedException` |

Two consequences: `CanVerify` must catch **both** `NotSupportedException` and `FormatException`/`ArgumentException` (catch `Exception`), and the **64-byte length check is the only thing that rejects garbage hex**.

## File Structure

| File | Responsibility |
|---|---|
| `src/XcavateProfileApiClient/Signing/SignatureEncoding.cs` | Decode `X-Signature`: hex or base58 → 64 bytes, never throws |
| `src/XcavateProfileApiClient/Signing/ISignatureScheme.cs` | Scheme contract: `Name`, `CanVerify`, `Verify` |
| `src/XcavateProfileApiClient/Signing/Sr25519SignatureScheme.cs` | Existing sr25519 behaviour behind the contract |
| `src/XcavateProfileApiClient/Signing/SolanaSignatureScheme.cs` | ed25519 verify + the mandatory 32-byte guard |
| `src/XcavateProfileApiClient/Signing/IRequestSigner.cs` | Client contract: `Address`, `SignAsync`, `EncodeSignature` |
| `src/XcavateProfileApiClient/Signing/SubstrateRequestSigner.cs` | sr25519 signer, emits hex |
| `src/XcavateProfileApiClient/Signing/SolanaRequestSigner.cs` | Solana signer, emits base58 |
| `src/XcavateProfileApi/Middleware/SignatureValidator.cs` | *Modify:* scheme dispatch + decode moved inside the guarded path |
| `src/XcavateProfileApiClient/SigningHttpMessageHandler.cs` | *Modify:* takes `IRequestSigner`, keeps `Account` overload |
| `src/XcavateProfileApiClient/XcavateProfileApiClient.cs` | *Modify:* `IRequestSigner` overloads on the 4 signing methods |
| `tests/XcavateBuckets.Tests/GraphQLHost.cs` | *Modify:* `IRequestSigner` overloads for signing |
| `tests/XcavateProfile.ApiTests/SolanaAccounts.cs` | Deterministic Solana test accounts |

---

### Task 1: Signature decoding (hex or base58)

Adds the Solnet dependency and the one piece of new code with no crypto in it, so it lands independently.

**Files:**
- Modify: `src/XcavateProfileApiClient/XcavateProfileApiClient.csproj`
- Create: `src/XcavateProfileApiClient/Signing/SignatureEncoding.cs`
- Test: `tests/XcavateBuckets.Tests/SignatureEncodingTests.cs`

**Interfaces:**
- Consumes: nothing.
- Produces: `XcavateProfileApiClient.Signing.SignatureEncoding` — `public const int SignatureLength = 64;` and `public static bool TryDecode(string? signature, out byte[] bytes)`. Returns `false` and sets `bytes` to `[]` on any failure; **never throws**.

- [ ] **Step 1: Add the Solnet package reference**

In `src/XcavateProfileApiClient/XcavateProfileApiClient.csproj`, add to the existing `ItemGroup` of `PackageReference`s:

```xml
    <PackageReference Include="Solnet.Wallet" Version="6.1.0" />
```

- [ ] **Step 2: Verify it restores**

Run: `dotnet restore src/XcavateProfileApiClient/XcavateProfileApiClient.csproj`
Expected: success. `Solnet.Wallet` 6.1.0 targets net6.0 and is net10.0-compatible; it pulls `Chaos.NaCl.Standard` (already in the graph via Substrate.NET.API) and `Portable.BouncyCastle`.

- [ ] **Step 3: Write the failing tests**

Create `tests/XcavateBuckets.Tests/SignatureEncodingTests.cs`:

```csharp
using XcavateProfileApiClient.Signing;

namespace XcavateBuckets.Tests;

/// <summary>
/// X-Signature carries hex from the existing clients and base58 from Solana wallets. Decoding must
/// accept both and fail cleanly — it runs before authentication, on unvalidated input.
/// </summary>
[TestFixture]
public class SignatureEncodingTests
{
    // A real 64-byte ed25519 signature, in both encodings.
    private const string Hex =
        "0xB8AA78CE847A5A127B5E97F747BFFB90B97AAB5A54811531985FED4ACE25BA54"
        + "AA3D488D27F132B155326DB97B6034EE9CA9D499AF6D25977D94EC1B062C9E0E";

    private const string Base58 =
        "4h96nSRXVA8XZYAhhDt44CHa2th3VaX3ZU15F6D1HEXkpmDmmrDo1iSANuy4eWNUkCh4Vk8ymLY6yDmQsmjFv8S1";

    [Test]
    public void Hex_and_base58_decode_to_the_same_64_bytes()
    {
        Assert.Multiple(() =>
        {
            Assert.That(SignatureEncoding.TryDecode(Hex, out var fromHex), Is.True);
            Assert.That(SignatureEncoding.TryDecode(Base58, out var fromBase58), Is.True);
            Assert.That(fromHex, Has.Length.EqualTo(64));
            Assert.That(fromBase58, Is.EqualTo(fromHex));
        });
    }

    [Test]
    public void Uppercase_hex_prefix_is_accepted()
    {
        Assert.That(SignatureEncoding.TryDecode("0X" + Hex[2..], out var bytes), Is.True);
        Assert.That(bytes, Has.Length.EqualTo(64));
    }

    // Utils.HexToByteArray("0xZZ") silently returns 1 byte rather than throwing, so the length
    // check — not the decoder — is what rejects garbage.
    [TestCase("0xZZ", TestName = "Invalid hex digits")]
    [TestCase("0x1234", TestName = "Hex too short")]
    [TestCase("0x123", TestName = "Odd hex digit count")]
    [TestCase("abc", TestName = "Base58 too short")]
    [TestCase("not-base58-0OIl!!", TestName = "Not base58 at all")]
    [TestCase("", TestName = "Empty")]
    [TestCase(null, TestName = "Null")]
    public void Malformed_input_fails_without_throwing(string? signature)
    {
        bool decoded = true;
        byte[] bytes = [0xFF];

        Assert.DoesNotThrow(() => decoded = SignatureEncoding.TryDecode(signature, out bytes));

        Assert.Multiple(() =>
        {
            Assert.That(decoded, Is.False);
            Assert.That(bytes, Is.Empty);
        });
    }
}
```

- [ ] **Step 4: Run the tests to verify they fail**

Run: `dotnet test tests/XcavateBuckets.Tests/XcavateBuckets.Tests.csproj --filter "FullyQualifiedName~SignatureEncodingTests"`
Expected: **compile error** — `SignatureEncoding` does not exist.

- [ ] **Step 5: Write the implementation**

Create `src/XcavateProfileApiClient/Signing/SignatureEncoding.cs`:

```csharp
using Solnet.Wallet.Utilities;
using Substrate.NetApi;

namespace XcavateProfileApiClient.Signing;

/// <summary>
/// Decodes the <c>X-Signature</c> header. Existing clients send 0x-prefixed hex; Solana wallets
/// hand a frontend a byte array that <c>bs58.encode</c> turns into base58, so both are accepted.
/// </summary>
/// <remarks>
/// Every failure path returns false rather than throwing: this runs on unauthenticated input, and
/// the previous code let a malformed signature escape as a 500 instead of a 401.
/// </remarks>
public static class SignatureEncoding
{
    /// <summary>Both sr25519 and ed25519 signatures are 64 bytes.</summary>
    public const int SignatureLength = 64;

    public static bool TryDecode(string? signature, out byte[] bytes)
    {
        bytes = [];

        if (string.IsNullOrWhiteSpace(signature))
        {
            return false;
        }

        try
        {
            var decoded = signature.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
                ? Utils.HexToByteArray(signature)
                : Encoders.Base58.DecodeData(signature);

            // Utils.HexToByteArray does not validate hex digits — "0xZZ" comes back as one byte
            // rather than an exception — so this length check is what actually rejects garbage.
            if (decoded.Length != SignatureLength)
            {
                return false;
            }

            bytes = decoded;
            return true;
        }
        catch (Exception)
        {
            // NotSupportedException from odd-length hex, FormatException from invalid base58.
            return false;
        }
    }
}
```

- [ ] **Step 6: Run the tests to verify they pass**

Run: `dotnet test tests/XcavateBuckets.Tests/XcavateBuckets.Tests.csproj --filter "FullyQualifiedName~SignatureEncodingTests"`
Expected: PASS, 9 tests.

- [ ] **Step 7: Commit**

```bash
git add src/XcavateProfileApiClient/XcavateProfileApiClient.csproj src/XcavateProfileApiClient/Signing/SignatureEncoding.cs tests/XcavateBuckets.Tests/SignatureEncodingTests.cs
git commit -m "Accept hex or base58 signatures via SignatureEncoding"
```

---

### Task 2: The scheme contract and the sr25519 implementation

A pure refactor: existing behaviour moves behind an interface with no functional change. Nothing consumes it yet.

**Files:**
- Create: `src/XcavateProfileApiClient/Signing/ISignatureScheme.cs`
- Create: `src/XcavateProfileApiClient/Signing/Sr25519SignatureScheme.cs`
- Test: `tests/XcavateBuckets.Tests/SignatureSchemeTests.cs`

**Interfaces:**
- Consumes: `XcavateProfile.Client.CryptoHelper` (existing).
- Produces:
  - `XcavateProfileApiClient.Signing.ISignatureScheme` — `string Name { get; }`, `bool CanVerify(string? address)`, `bool Verify(string payload, byte[] signature, string address)`.
  - `Sr25519SignatureScheme` — parameterless, `Name == "sr25519"`.

- [ ] **Step 1: Write the failing test**

Create `tests/XcavateBuckets.Tests/SignatureSchemeTests.cs`:

```csharp
using Substrate.NET.Wallet.Keyring;
using Substrate.NetApi.Model.Types;
using XcavateProfile.Client;
using XcavateProfileApiClient.Signing;
using static Substrate.NetApi.Mnemonic;

namespace XcavateBuckets.Tests;

/// <summary>
/// The scheme abstraction is what lets one validator serve two chains. These tests pin the two
/// things that matter: each scheme recognises only its own address format, and the sr25519 path
/// behaves exactly as it did before it was moved behind the interface.
/// </summary>
[TestFixture]
public class SignatureSchemeTests
{
    private const string Payload =
        "POST:/graphql:0xdeadbeefdeadbeefdeadbeefdeadbeef:2026-07-25T12:00:00.0000000Z";

    private static Account SubstrateAccount(byte entropyFill)
    {
        var mnemonic = string.Join(
            " ", MnemonicFromEntropy(Enumerable.Repeat(entropyFill, 16).ToArray(), BIP39Wordlist.English));

        return new Keyring()
            .AddFromMnemonic(mnemonic, new Meta { Name = "SchemeTests" }, KeyType.Sr25519)
            .Account;
    }

    [Test]
    public void Sr25519_recognises_an_ss58_address()
    {
        var scheme = new Sr25519SignatureScheme();

        Assert.That(scheme.CanVerify(SubstrateAccount(0x31).Value), Is.True);
    }

    [TestCase("AK7AACuihtCk6abEywXtg7sPW2Qh9iYg5C6BA38h9ciE", TestName = "Solana address")]
    [TestCase("not-base58-0OIl!!", TestName = "Not base58")]
    [TestCase("", TestName = "Empty")]
    [TestCase(null, TestName = "Null")]
    public void Sr25519_rejects_everything_that_is_not_ss58(string? address)
    {
        var scheme = new Sr25519SignatureScheme();

        // GetPublicKeyFrom throws NotSupportedException for a wrong-sized address and
        // FormatException for non-base58 input; neither may escape CanVerify.
        Assert.DoesNotThrow(() => scheme.CanVerify(address));
        Assert.That(scheme.CanVerify(address), Is.False);
    }

    [Test]
    public async Task Sr25519_verifies_a_signature_produced_by_CryptoHelper()
    {
        var account = SubstrateAccount(0x32);
        var signature = await CryptoHelper.SignAsync(Payload, account);

        Assert.That(
            new Sr25519SignatureScheme().Verify(Payload, signature, account.Value),
            Is.True);
    }

    [Test]
    public async Task Sr25519_rejects_a_signature_over_a_different_payload()
    {
        var account = SubstrateAccount(0x33);
        var signature = await CryptoHelper.SignAsync(Payload, account);

        Assert.That(
            new Sr25519SignatureScheme().Verify(Payload + "tampered", signature, account.Value),
            Is.False);
    }
}
```

- [ ] **Step 2: Run the test to verify it fails**

Run: `dotnet test tests/XcavateBuckets.Tests/XcavateBuckets.Tests.csproj --filter "FullyQualifiedName~SignatureSchemeTests"`
Expected: **compile error** — `Sr25519SignatureScheme` does not exist.

- [ ] **Step 3: Write the interface**

Create `src/XcavateProfileApiClient/Signing/ISignatureScheme.cs`:

```csharp
namespace XcavateProfileApiClient.Signing;

/// <summary>
/// One way of proving control of an address. Implementations share the payload *string* built by
/// <c>CryptoHelper.ConstructPayload</c> and differ only in which bytes of it get signed.
/// </summary>
public interface ISignatureScheme
{
    /// <summary>Stable identifier, used in log and error text.</summary>
    string Name { get; }

    /// <summary>
    /// Whether this scheme owns the given address format. Must never throw — it runs against
    /// unvalidated header input, and the address decoders in both ecosystems throw freely.
    /// </summary>
    bool CanVerify(string? address);

    /// <summary>
    /// Verifies the signature over the payload. Only called when <see cref="CanVerify"/> is true.
    /// </summary>
    bool Verify(string payload, byte[] signature, string address);
}
```

- [ ] **Step 4: Write the sr25519 implementation**

Create `src/XcavateProfileApiClient/Signing/Sr25519SignatureScheme.cs`:

```csharp
using Substrate.NetApi;
using XcavateProfile.Client;

namespace XcavateProfileApiClient.Signing;

/// <summary>
/// The original scheme, unchanged: sign the Blake2b-128 digest of the payload with sr25519, keyed
/// by an SS58 address.
/// </summary>
public sealed class Sr25519SignatureScheme : ISignatureScheme
{
    private const int PublicKeyLength = 32;

    public string Name => "sr25519";

    public bool CanVerify(string? address)
    {
        if (string.IsNullOrWhiteSpace(address))
        {
            return false;
        }

        try
        {
            // Validates the SS58 checksum, so this doubles as the format test.
            return Utils.GetPublicKeyFrom(address).Length == PublicKeyLength;
        }
        catch (Exception)
        {
            // NotSupportedException for a wrong size or bad checksum, FormatException for
            // non-base58 characters. Both simply mean "not an SS58 address".
            return false;
        }
    }

    public bool Verify(string payload, byte[] signature, string address)
    {
        if (CryptoHelper.VerifySignature(payload, signature, address))
        {
            return true;
        }

        // The polkadot-js extension wraps whatever it signs in <Bytes>…</Bytes>, so a browser
        // signature only matches on the second attempt.
        var wrapped = "<Bytes>"u8
            .ToArray()
            .Concat(CryptoHelper.Hash(payload))
            .Concat("</Bytes>"u8.ToArray())
            .ToArray();

        return CryptoHelper.VerifySignature(wrapped, signature, address);
    }
}
```

- [ ] **Step 5: Run the tests to verify they pass**

Run: `dotnet test tests/XcavateBuckets.Tests/XcavateBuckets.Tests.csproj --filter "FullyQualifiedName~SignatureSchemeTests"`
Expected: PASS, 7 tests.

- [ ] **Step 6: Commit**

```bash
git add src/XcavateProfileApiClient/Signing/ISignatureScheme.cs src/XcavateProfileApiClient/Signing/Sr25519SignatureScheme.cs tests/XcavateBuckets.Tests/SignatureSchemeTests.cs
git commit -m "Extract sr25519 verification behind ISignatureScheme"
```

---

### Task 3: The Solana scheme

**Files:**
- Create: `src/XcavateProfileApiClient/Signing/SolanaSignatureScheme.cs`
- Modify: `tests/XcavateBuckets.Tests/SignatureSchemeTests.cs` (append tests)

**Interfaces:**
- Consumes: `ISignatureScheme` (Task 2).
- Produces: `SolanaSignatureScheme` — parameterless, `Name == "solana-ed25519"`.

- [ ] **Step 1: Write the failing tests**

Append to `tests/XcavateBuckets.Tests/SignatureSchemeTests.cs`, inside the `SignatureSchemeTests` class. Also add these `using`s at the top of the file:

```csharp
using Solnet.Wallet;
using Account = Substrate.NetApi.Model.Types.Account;
using SolMnemonic = Solnet.Wallet.Bip39.Mnemonic;
using SolWordList = Solnet.Wallet.Bip39.WordList;
```

All three aliases are load-bearing:

- **`Account`** — `using Solnet.Wallet;` introduces `Solnet.Wallet.Account`, which collides with
  `Substrate.NetApi.Model.Types.Account` and makes the existing `SubstrateAccount` helper's return
  type ambiguous (CS0104). The alias pins bare `Account` to the Substrate one; the Solana type is
  written out in full.
- **`SolMnemonic` / `SolWordList`** — `Mnemonic` and `WordList` are ambiguous between
  `Substrate.NetApi` and `Solnet.Wallet.Bip39`.

Bare `Wallet` resolves to `Solnet.Wallet.Wallet` here: the file imports
`Substrate.NET.Wallet.Keyring`, not `Substrate.NET.Wallet`, so `Substrate.NET.Wallet.Wallet` is not
in scope.

```csharp
    private static Solnet.Wallet.Account SolanaAccount(byte entropyFill)
    {
        var mnemonic = string.Join(
            " ", MnemonicFromEntropy(Enumerable.Repeat(entropyFill, 16).ToArray(), BIP39Wordlist.English));

        return new Wallet(new SolMnemonic(mnemonic, SolWordList.English)).Account;
    }

    [Test]
    public void Solana_recognises_a_solana_address()
    {
        var scheme = new SolanaSignatureScheme();

        Assert.That(scheme.CanVerify(SolanaAccount(0x34).PublicKey.Key), Is.True);
    }

    /// <summary>
    /// The load-bearing guard. Solnet's PublicKey constructor does not validate length: handed an
    /// SS58 address it builds a 35-byte key and only throws inside Verify. Without the explicit
    /// 32-byte check an SS58 address would reach the ed25519 path and blow up with an
    /// ArgumentException instead of failing authentication cleanly.
    /// </summary>
    [Test]
    public void Solana_rejects_an_ss58_address_before_it_reaches_verify()
    {
        var scheme = new SolanaSignatureScheme();
        var ss58 = SubstrateAccount(0x35).Value;

        Assert.Multiple(() =>
        {
            Assert.That(scheme.CanVerify(ss58), Is.False);
            Assert.DoesNotThrow(() => scheme.Verify(Payload, new byte[64], ss58));
            Assert.That(scheme.Verify(Payload, new byte[64], ss58), Is.False);
        });
    }

    [TestCase("not-base58-0OIl!!", TestName = "Not base58")]
    [TestCase("abc", TestName = "Too few bytes")]
    [TestCase("", TestName = "Empty")]
    [TestCase(null, TestName = "Null")]
    public void Solana_rejects_malformed_addresses_without_throwing(string? address)
    {
        var scheme = new SolanaSignatureScheme();

        Assert.DoesNotThrow(() => scheme.CanVerify(address));
        Assert.That(scheme.CanVerify(address), Is.False);
    }

    [Test]
    public void Solana_verifies_a_signature_over_the_raw_utf8_payload()
    {
        var account = SolanaAccount(0x36);
        var signature = account.Sign(System.Text.Encoding.UTF8.GetBytes(Payload));

        Assert.That(
            new SolanaSignatureScheme().Verify(Payload, signature, account.PublicKey.Key),
            Is.True);
    }

    /// <summary>
    /// Solana signs the payload string itself, not its Blake2 digest, so a wallet shows the user
    /// readable text. A signature over the digest must not be accepted.
    /// </summary>
    [Test]
    public void Solana_rejects_a_signature_over_the_blake2_digest()
    {
        var account = SolanaAccount(0x37);
        var signature = account.Sign(CryptoHelper.Hash(Payload));

        Assert.That(
            new SolanaSignatureScheme().Verify(Payload, signature, account.PublicKey.Key),
            Is.False);
    }

    [Test]
    public void Solana_rejects_a_signature_over_a_different_payload()
    {
        var account = SolanaAccount(0x38);
        var signature = account.Sign(System.Text.Encoding.UTF8.GetBytes(Payload));

        Assert.That(
            new SolanaSignatureScheme().Verify(Payload + "tampered", signature, account.PublicKey.Key),
            Is.False);
    }

    /// <summary>Neither scheme may accept the other's signature.</summary>
    [Test]
    public async Task Schemes_reject_each_others_signatures()
    {
        var substrate = SubstrateAccount(0x39);
        var solana = SolanaAccount(0x3A);

        var substrateSignature = await CryptoHelper.SignAsync(Payload, substrate);
        var solanaSignature = solana.Sign(System.Text.Encoding.UTF8.GetBytes(Payload));

        Assert.Multiple(() =>
        {
            Assert.That(
                new Sr25519SignatureScheme().Verify(Payload, solanaSignature, substrate.Value),
                Is.False,
                "a Solana signature must not pass sr25519 verification");
            Assert.That(
                new SolanaSignatureScheme().Verify(Payload, substrateSignature, solana.PublicKey.Key),
                Is.False,
                "an sr25519 signature must not pass ed25519 verification");
        });
    }
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test tests/XcavateBuckets.Tests/XcavateBuckets.Tests.csproj --filter "FullyQualifiedName~SignatureSchemeTests"`
Expected: **compile error** — `SolanaSignatureScheme` does not exist.

- [ ] **Step 3: Write the implementation**

Create `src/XcavateProfileApiClient/Signing/SolanaSignatureScheme.cs`:

```csharp
using System.Text;
using Solnet.Wallet;

namespace XcavateProfileApiClient.Signing;

/// <summary>
/// Solana authentication: ed25519 over the <em>raw UTF-8 payload string</em>, keyed by a base58
/// 32-byte public key.
/// </summary>
/// <remarks>
/// The payload is signed unhashed on purpose. Wallets render the bytes handed to
/// <c>signMessage</c> as UTF-8 in the approval popup, so signing a 16-byte Blake2 digest would
/// show the user binary garbage — exactly the prompt users are trained to reject.
/// </remarks>
public sealed class SolanaSignatureScheme : ISignatureScheme
{
    private const int PublicKeyLength = 32;

    public string Name => "solana-ed25519";

    public bool CanVerify(string? address)
    {
        if (string.IsNullOrWhiteSpace(address))
        {
            return false;
        }

        try
        {
            // The length check is mandatory, not defensive tidiness: PublicKey's constructor
            // accepts any base58 string, so an SS58 address builds a 35-byte key here and only
            // throws later inside Verify. This is what keeps SS58 off the ed25519 path.
            return new PublicKey(address).KeyBytes.Length == PublicKeyLength;
        }
        catch (Exception)
        {
            // ArgumentException from invalid base58 characters.
            return false;
        }
    }

    public bool Verify(string payload, byte[] signature, string address)
    {
        if (!CanVerify(address))
        {
            return false;
        }

        try
        {
            return new PublicKey(address).Verify(Encoding.UTF8.GetBytes(payload), signature);
        }
        catch (Exception)
        {
            return false;
        }
    }
}
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test tests/XcavateBuckets.Tests/XcavateBuckets.Tests.csproj --filter "FullyQualifiedName~SignatureSchemeTests"`
Expected: PASS, 19 tests.

- [ ] **Step 5: Commit**

```bash
git add src/XcavateProfileApiClient/Signing/SolanaSignatureScheme.cs tests/XcavateBuckets.Tests/SignatureSchemeTests.cs
git commit -m "Add Solana ed25519 signature scheme"
```

---

### Task 4: Dispatch schemes from SignatureValidator

The integration point. After this task both the REST controllers and `/graphql` accept Solana signatures, because both already delegate here.

**Files:**
- Modify: `src/XcavateProfileApi/Middleware/SignatureValidator.cs:20-98`
- Test: `tests/XcavateBuckets.Tests/SignatureValidatorTests.cs`

**Interfaces:**
- Consumes: `SignatureEncoding.TryDecode` (Task 1), `ISignatureScheme`, `Sr25519SignatureScheme` (Task 2), `SolanaSignatureScheme` (Task 3).
- Produces: no signature change. `ISignatureValidator.ValidateAsync(address, signatureHex, timestamp, method, path, payloadBody)` and `SignatureValidationResult { IsValid, Error, Ss58Address }` stay exactly as they are, which is why no caller needs editing.

- [ ] **Step 1: Write the failing tests**

Create `tests/XcavateBuckets.Tests/SignatureValidatorTests.cs`:

```csharp
using Solnet.Wallet;
using Substrate.NET.Wallet.Keyring;
using Substrate.NetApi;
using Substrate.NetApi.Model.Types;
using XcavateProfile.Client;
using XcavateProfileApi.Middleware;
using XcavateProfileApiClient;
using XcavateProfileApiClient.Signing;
using static Substrate.NetApi.Mnemonic;
using Account = Substrate.NetApi.Model.Types.Account;
using SolMnemonic = Solnet.Wallet.Bip39.Mnemonic;
using SolWordList = Solnet.Wallet.Bip39.WordList;

namespace XcavateBuckets.Tests;

/// <summary>
/// The validator is the single place both the REST controllers and the GraphQL middleware go
/// through, so scheme dispatch and malformed-input handling are pinned here.
/// </summary>
[TestFixture]
public class SignatureValidatorTests
{
    private const string Method = "POST";
    private const string Path = "/api/profiles";

    private static SignatureValidator NewValidator(params string[] admins) =>
        new(admins.ToList(), new SignatureValidationOptions());

    private static Account SubstrateAccount(byte fill)
    {
        var mnemonic = string.Join(
            " ", MnemonicFromEntropy(Enumerable.Repeat(fill, 16).ToArray(), BIP39Wordlist.English));

        return new Keyring()
            .AddFromMnemonic(mnemonic, new Meta { Name = "ValidatorTests" }, KeyType.Sr25519)
            .Account;
    }

    private static Solnet.Wallet.Account SolanaAccount(byte fill)
    {
        var mnemonic = string.Join(
            " ", MnemonicFromEntropy(Enumerable.Repeat(fill, 16).ToArray(), BIP39Wordlist.English));

        return new Wallet(new SolMnemonic(mnemonic, SolWordList.English)).Account;
    }

    private static string Payload(DateTime timestamp) =>
        CryptoHelper.ConstructPayload(Method, Path, new EmptyPayloadBody(), timestamp);

    [Test]
    public async Task Substrate_signature_still_validates()
    {
        var account = SubstrateAccount(0x41);
        var timestamp = DateTime.UtcNow;
        var signature = await CryptoHelper.SignAsync(Payload(timestamp), account);

        var result = await NewValidator().ValidateAsync(
            account.Value, Utils.Bytes2HexString(signature), timestamp.ToString("o"),
            Method, Path, new EmptyPayloadBody());

        Assert.That(result.IsValid, Is.True, result.Error);
    }

    [Test]
    public async Task Solana_signature_validates_when_hex_encoded()
    {
        var account = SolanaAccount(0x42);
        var timestamp = DateTime.UtcNow;
        var signature = account.Sign(System.Text.Encoding.UTF8.GetBytes(Payload(timestamp)));

        var result = await NewValidator().ValidateAsync(
            account.PublicKey.Key, Utils.Bytes2HexString(signature), timestamp.ToString("o"),
            Method, Path, new EmptyPayloadBody());

        Assert.That(result.IsValid, Is.True, result.Error);
    }

    [Test]
    public async Task Solana_signature_validates_when_base58_encoded()
    {
        var account = SolanaAccount(0x43);
        var timestamp = DateTime.UtcNow;
        var signature = account.Sign(System.Text.Encoding.UTF8.GetBytes(Payload(timestamp)));

        var result = await NewValidator().ValidateAsync(
            account.PublicKey.Key,
            Solnet.Wallet.Utilities.Encoders.Base58.EncodeData(signature),
            timestamp.ToString("o"),
            Method, Path, new EmptyPayloadBody());

        Assert.That(result.IsValid, Is.True, result.Error);
    }

    [Test]
    public async Task Solana_signature_over_a_tampered_payload_is_rejected()
    {
        var account = SolanaAccount(0x44);
        var timestamp = DateTime.UtcNow;
        var signature = account.Sign(System.Text.Encoding.UTF8.GetBytes(Payload(timestamp)));

        var result = await NewValidator().ValidateAsync(
            account.PublicKey.Key, Utils.Bytes2HexString(signature), timestamp.ToString("o"),
            Method, "/api/profiles/other", new EmptyPayloadBody());

        Assert.That(result.IsValid, Is.False);
    }

    /// <summary>
    /// Previously HexToByteArray ran outside the try block, so a malformed signature surfaced as a
    /// 500 instead of a 401.
    /// </summary>
    [TestCase("0x123", TestName = "Odd hex digit count")]
    [TestCase("0xZZ", TestName = "Invalid hex digits")]
    [TestCase("not-base58-0OIl!!", TestName = "Not base58")]
    [TestCase("", TestName = "Empty")]
    public void Malformed_signature_fails_validation_instead_of_throwing(string signature)
    {
        var account = SubstrateAccount(0x45);

        SignatureValidationResult? result = null;
        Assert.DoesNotThrowAsync(async () => result = await NewValidator().ValidateAsync(
            account.Value, signature, DateTime.UtcNow.ToString("o"),
            Method, Path, new EmptyPayloadBody()));

        Assert.That(result!.IsValid, Is.False);
    }

    [Test]
    public async Task Unrecognised_address_format_is_rejected()
    {
        var result = await NewValidator().ValidateAsync(
            "not-an-address-at-all", "0x" + new string('a', 128), DateTime.UtcNow.ToString("o"),
            Method, Path, new EmptyPayloadBody());

        Assert.Multiple(() =>
        {
            Assert.That(result.IsValid, Is.False);
            Assert.That(result.Error, Does.Contain("address"));
        });
    }

    [Test]
    public void IsAdmin_matches_a_solana_address()
    {
        var solana = SolanaAccount(0x46).PublicKey.Key;

        Assert.That(NewValidator(solana).IsAdmin(solana), Is.True);
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test tests/XcavateBuckets.Tests/XcavateBuckets.Tests.csproj --filter "FullyQualifiedName~SignatureValidatorTests"`
Expected: FAIL. The Solana cases fail verification; the malformed-signature cases throw.

- [ ] **Step 3: Rewrite the validator's verification path**

In `src/XcavateProfileApi/Middleware/SignatureValidator.cs`, add to the `using` block:

```csharp
using XcavateProfileApiClient.Signing;
```

Add this field next to the existing `_adminAddresses` / `_options` fields:

```csharp
    /// <summary>
    /// Ordered by cost of recognition; the first scheme that claims the address format wins. The
    /// two formats do not overlap — SS58 decoding validates a checksum and yields 35 bytes, a
    /// Solana address is exactly 32 — so the order is for clarity rather than correctness.
    /// </summary>
    private static readonly IReadOnlyList<ISignatureScheme> Schemes =
    [
        new Sr25519SignatureScheme(),
        new SolanaSignatureScheme()
    ];
```

Replace everything from `// Construct the signed payload` (line 57) to the end of the `catch` block (line 97) with:

```csharp
        // Construct the signed payload. Identical for both schemes — they differ only in which
        // bytes of it get signed.
        var payload = CryptoHelper.ConstructPayload(method, path, payloadBody, ts);

        // Decoding happens inside the guarded path on purpose: this is unauthenticated input, and
        // a malformed signature must produce a 401, not an unhandled exception.
        if (!SignatureEncoding.TryDecode(signatureHex, out var signatureBytes))
        {
            return new SignatureValidationResult
            {
                IsValid = false,
                Error = $"Signature must decode to {SignatureEncoding.SignatureLength} bytes "
                    + "from 0x-prefixed hex or base58"
            };
        }

        var scheme = Schemes.FirstOrDefault(s => s.CanVerify(address));
        if (scheme is null)
        {
            return new SignatureValidationResult
            {
                IsValid = false,
                Error = "Unrecognised address format: expected an SS58 or Solana base58 address"
            };
        }

        try
        {
            var isValid = scheme.Verify(payload, signatureBytes, address);

            return new SignatureValidationResult
            {
                IsValid = isValid,
                Ss58Address = address,
                Error = isValid ? null : "Signature verification failed"
            };
        }
        catch (Exception ex)
        {
            return new SignatureValidationResult
            {
                IsValid = false,
                Error = $"Signature verification error: {ex.Message}"
            };
        }
```

Then delete the now-unused `using Substrate.NetApi;`-dependent line that read `var signatureBytes = Substrate.NetApi.Utils.HexToByteArray(signatureHex);` if any trace remains, and confirm the file no longer references `Sr25519v091` or builds the `<Bytes>` wrapper — that logic now lives in `Sr25519SignatureScheme`.

- [ ] **Step 4: Run the new tests to verify they pass**

Run: `dotnet test tests/XcavateBuckets.Tests/XcavateBuckets.Tests.csproj --filter "FullyQualifiedName~SignatureValidatorTests"`
Expected: PASS, 11 tests.

- [ ] **Step 5: Run the whole offline suite to prove the sr25519 path is untouched**

Run: `dotnet test tests/XcavateBuckets.Tests/XcavateBuckets.Tests.csproj`
Expected: PASS. `GraphQLIntegrationTests` signs with sr25519 through the real middleware — if any of those regress, the refactor changed behaviour and must be fixed, not the tests.

- [ ] **Step 6: Commit**

```bash
git add src/XcavateProfileApi/Middleware/SignatureValidator.cs tests/XcavateBuckets.Tests/SignatureValidatorTests.cs
git commit -m "Dispatch signature schemes by address format in SignatureValidator"
```

---

### Task 5: Client-side signers and the signing handler

**Files:**
- Create: `src/XcavateProfileApiClient/Signing/IRequestSigner.cs`
- Create: `src/XcavateProfileApiClient/Signing/SubstrateRequestSigner.cs`
- Create: `src/XcavateProfileApiClient/Signing/SolanaRequestSigner.cs`
- Modify: `src/XcavateProfileApiClient/SigningHttpMessageHandler.cs:16-45`
- Modify: `tests/XcavateBuckets.Tests/GraphQLHost.cs:45-53,108-143`
- Modify: `tests/XcavateBuckets.Tests/GraphQLIntegrationTests.cs` (append tests)

**Interfaces:**
- Consumes: `CryptoHelper` (existing), `Solnet.Wallet.Account`.
- Produces:
  - `IRequestSigner` — `string Address { get; }`, `Task<byte[]> SignAsync(string payload)`, `string EncodeSignature(byte[] signature)`.
  - `SubstrateRequestSigner(Substrate.NetApi.Model.Types.Account account)` — emits `0x` hex.
  - `SolanaRequestSigner(Solnet.Wallet.Account account)` — emits base58.
  - `SigningHttpMessageHandler(IRequestSigner signer)` plus the retained `SigningHttpMessageHandler(Account account)`.
  - `GraphQLHost.CreateSigningClient(IRequestSigner)` and `GraphQLHost.SignedAsync(string query, IRequestSigner signer, object? variables = null, DateTime? timestamp = null)`.

- [ ] **Step 1: Write the failing test**

Append to `tests/XcavateBuckets.Tests/GraphQLIntegrationTests.cs`. Add these `using`s at the top of the file:

```csharp
using Solnet.Wallet;
using Substrate.NetApi;
using XcavateProfile.Client;
using XcavateProfileApiClient.Signing;
using Account = Substrate.NetApi.Model.Types.Account;
using SolMnemonic = Solnet.Wallet.Bip39.Mnemonic;
using SolWordList = Solnet.Wallet.Bip39.WordList;
```

The `Account` alias is required, not cosmetic: `using Solnet.Wallet;` brings in
`Solnet.Wallet.Account`, which collides with the `Substrate.NetApi.Model.Types.Account` already
used by this file's `NewAccount`, `Alice` and `Bob` helpers (CS0104). `Substrate.NetApi` and
`XcavateProfile.Client` are needed for `Utils.Bytes2HexString` and `CryptoHelper` in the
tampered-body test below.

Add inside the class:

```csharp
    private static Solnet.Wallet.Account SolanaAccount(byte entropyFill)
    {
        var mnemonic = string.Join(
            " ", MnemonicFromEntropy(Enumerable.Repeat(entropyFill, 16).ToArray(), BIP39Wordlist.English));

        return new Wallet(new SolMnemonic(mnemonic, SolWordList.English)).Account;
    }

    /// <summary>
    /// End-to-end proof through the shipped handler and the shipped middleware: the Solana signer
    /// emits base58, so this also exercises the base58 branch of SignatureEncoding.
    /// </summary>
    [Test]
    public async Task Solana_signing_handler_is_accepted_by_the_server()
    {
        var solana = SolanaAccount(0x51);
        await using var host = await GraphQLHost.StartAsync();
        using var client = host.CreateSigningClient(new SolanaRequestSigner(solana));

        var body = JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["query"] = """mutation { createNamespace(metadata: { name: "via-solana" }) { id name creator } }"""
        });

        var response = await client.PostAsync(
            "/graphql", new StringContent(body, System.Text.Encoding.UTF8, "application/json"));
        var result = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        Assert.That(result.FirstErrorCode(), Is.Null, result.RootElement.ToString());
        Assert.That(result.Data("createNamespace").GetProperty("creator").GetString(),
            Is.EqualTo(solana.PublicKey.Key),
            "the Solana address must become the authenticated caller");
    }

    [Test]
    public async Task Solana_signed_mutation_over_a_tampered_body_is_rejected()
    {
        var solana = SolanaAccount(0x52);
        await using var host = await GraphQLHost.StartAsync();

        // Sign one document, send another.
        var signer = new SolanaRequestSigner(solana);
        var timestamp = DateTime.UtcNow;
        var signedBody = JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["query"] = """mutation { createNamespace(metadata: { name: "signed" }) { id } }"""
        });
        var sentBody = JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["query"] = """mutation { createNamespace(metadata: { name: "sent" }) { id } }"""
        });

        var bodyHash = Utils.Bytes2HexString(CryptoHelper.Hash(signedBody));
        var signature = await signer.SignAsync(
            $"POST:/graphql:{bodyHash}:{timestamp.ToUniversalTime():o}");

        var request = new HttpRequestMessage(HttpMethod.Post, "/graphql")
        {
            Content = new StringContent(sentBody, System.Text.Encoding.UTF8, "application/json")
        };
        request.Headers.Add("X-SS58-Address", signer.Address);
        request.Headers.Add("X-Signature", signer.EncodeSignature(signature));
        request.Headers.Add("X-Timestamp", timestamp.ToUniversalTime().ToString("o"));

        var response = await host.Client.SendAsync(request);
        var result = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        Assert.That(result.FirstErrorCode(), Is.EqualTo("INVALID_SIGNATURE"));
    }
```

- [ ] **Step 2: Run the test to verify it fails**

Run: `dotnet test tests/XcavateBuckets.Tests/XcavateBuckets.Tests.csproj --filter "FullyQualifiedName~GraphQLIntegrationTests"`
Expected: **compile error** — `SolanaRequestSigner` does not exist and `CreateSigningClient` has no `IRequestSigner` overload.

- [ ] **Step 3: Write the signer contract and implementations**

Create `src/XcavateProfileApiClient/Signing/IRequestSigner.cs`:

```csharp
namespace XcavateProfileApiClient.Signing;

/// <summary>
/// The client-side counterpart of <see cref="ISignatureScheme"/>: produces the credentials for one
/// address. Each implementation owns its wire conventions, so callers stay chain-agnostic.
/// </summary>
public interface IRequestSigner
{
    /// <summary>The value for the <c>X-SS58-Address</c> header.</summary>
    string Address { get; }

    /// <summary>Signs the payload string with whichever bytes this scheme signs.</summary>
    Task<byte[]> SignAsync(string payload);

    /// <summary>Encodes the signature for the <c>X-Signature</c> header.</summary>
    string EncodeSignature(byte[] signature);
}
```

Create `src/XcavateProfileApiClient/Signing/SubstrateRequestSigner.cs`:

```csharp
using Substrate.NetApi;
using Substrate.NetApi.Model.Types;
using XcavateProfile.Client;

namespace XcavateProfileApiClient.Signing;

/// <summary>Signs with sr25519 and emits hex, exactly as the client did before.</summary>
public sealed class SubstrateRequestSigner(Account account) : IRequestSigner
{
    public string Address => account.Value;

    public Task<byte[]> SignAsync(string payload) => CryptoHelper.SignAsync(payload, account);

    public string EncodeSignature(byte[] signature) => Utils.Bytes2HexString(signature);
}
```

Create `src/XcavateProfileApiClient/Signing/SolanaRequestSigner.cs`:

```csharp
using System.Text;
using Solnet.Wallet.Utilities;

namespace XcavateProfileApiClient.Signing;

/// <summary>
/// Signs with ed25519 over the raw payload string and emits base58 — the same bytes and the same
/// encoding a browser wallet produces via <c>signMessage</c> plus <c>bs58.encode</c>, so this path
/// and a real frontend are verified by the same server code.
/// </summary>
public sealed class SolanaRequestSigner(Solnet.Wallet.Account account) : IRequestSigner
{
    public string Address => account.PublicKey.Key;

    public Task<byte[]> SignAsync(string payload) =>
        Task.FromResult(account.Sign(Encoding.UTF8.GetBytes(payload)));

    public string EncodeSignature(byte[] signature) => Encoders.Base58.EncodeData(signature);
}
```

- [ ] **Step 4: Rewrite the signing handler**

Replace the body of `src/XcavateProfileApiClient/SigningHttpMessageHandler.cs` (keeping the file's existing `<summary>` doc comment, updated) with:

```csharp
using Substrate.NetApi;
using Substrate.NetApi.Model.Types;
using XcavateProfile.Client;
using XcavateProfileApiClient.Signing;

namespace XcavateProfileApiClient;

/// <summary>
/// Signs outgoing GraphQL requests, mirroring the server's <c>GraphQLSignatureMiddleware</c>. The
/// signature covers the exact bytes being sent, so this must be the outermost handler that touches
/// the body. The scheme — sr25519 or Solana — comes from the supplied signer.
/// </summary>
/// <remarks>
/// Reads are public, so an unsigned client works for queries; supply a signer only when the client
/// needs to send mutations.
/// </remarks>
public sealed class SigningHttpMessageHandler : DelegatingHandler
{
    private const string GraphQLPath = "/graphql";

    private readonly IRequestSigner _signer;

    public SigningHttpMessageHandler(IRequestSigner signer) => _signer = signer;

    /// <summary>Convenience overload for the sr25519 path, which every existing caller uses.</summary>
    public SigningHttpMessageHandler(Account account) : this(new SubstrateRequestSigner(account))
    {
    }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (request.Content is not null && request.Method == HttpMethod.Post)
        {
            // Buffer the body first: the signature is over these bytes, and the content must still
            // be readable afterwards for the actual send.
            var body = await request.Content.ReadAsStringAsync(cancellationToken);
            var timestamp = DateTime.UtcNow;

            var payload = CryptoHelper.ConstructPayload(
                "POST", GraphQLPath, new RawBody(body), timestamp);

            var signature = await _signer.SignAsync(payload);

            request.Headers.Remove("X-SS58-Address");
            request.Headers.Remove("X-Signature");
            request.Headers.Remove("X-Timestamp");

            request.Headers.Add("X-SS58-Address", _signer.Address);
            request.Headers.Add("X-Signature", _signer.EncodeSignature(signature));
            request.Headers.Add("X-Timestamp", timestamp.ToString("o"));
        }

        return await base.SendAsync(request, cancellationToken);
    }

    /// <summary>Hashes the serialized request body through the shared payload-hashing seam.</summary>
    private sealed class RawBody(string body) : IPayloadBody
    {
        public string Hash() => Utils.Bytes2HexString(CryptoHelper.Hash(body));
    }
}
```

- [ ] **Step 5: Add the test-host overloads**

In `tests/XcavateBuckets.Tests/GraphQLHost.cs`, add `using XcavateProfileApiClient.Signing;` and replace `CreateSigningClient` (lines 45-53) with:

```csharp
    public HttpClient CreateSigningClient(Account account) =>
        CreateSigningClient(new SubstrateRequestSigner(account));

    public HttpClient CreateSigningClient(IRequestSigner signer)
    {
        var handler = new SigningHttpMessageHandler(signer)
        {
            InnerHandler = _host.GetTestServer().CreateHandler()
        };

        return new HttpClient(handler) { BaseAddress = new Uri("http://localhost") };
    }
```

Then replace `SignedAsync` and `SendAsync` (lines 108-143) with:

```csharp
    /// <summary>
    /// Runs an operation signed the way the REST client signs: the payload is
    /// <c>POST:/graphql:&lt;blake2 of the exact request body&gt;:&lt;timestamp&gt;</c>.
    /// </summary>
    public Task<JsonDocument> SignedAsync(
        string query, Account signer, object? variables = null, DateTime? timestamp = null) =>
        SendAsync(query, variables, new SubstrateRequestSigner(signer), timestamp);

    /// <summary>The same, for any scheme.</summary>
    public Task<JsonDocument> SignedAsync(
        string query, IRequestSigner signer, object? variables = null, DateTime? timestamp = null) =>
        SendAsync(query, variables, signer, timestamp);

    private async Task<JsonDocument> SendAsync(
        string query, object? variables, IRequestSigner? signer, DateTime? timestamp = null)
    {
        var payload = variables is null
            ? new Dictionary<string, object> { ["query"] = query }
            : new Dictionary<string, object> { ["query"] = query, ["variables"] = variables };

        // Serialize once: the signature covers these exact bytes.
        var body = JsonSerializer.Serialize(payload);

        var request = new HttpRequestMessage(HttpMethod.Post, "/graphql")
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        };

        if (signer is not null)
        {
            var ts = timestamp ?? DateTime.UtcNow;
            var bodyHash = Utils.Bytes2HexString(CryptoHelper.Hash(body));
            var signed = $"POST:/graphql:{bodyHash}:{ts.ToUniversalTime():o}";
            var signature = await signer.SignAsync(signed);

            request.Headers.Add("X-SS58-Address", signer.Address);
            request.Headers.Add("X-Signature", signer.EncodeSignature(signature));
            request.Headers.Add("X-Timestamp", ts.ToUniversalTime().ToString("o"));
        }

        var response = await Client.SendAsync(request);
        var content = await response.Content.ReadAsStringAsync();

        return JsonDocument.Parse(content);
    }
```

Also update the existing `QueryAsync` call site on line 102 — it passes `signer: null`, which still compiles because the parameter type is now `IRequestSigner?`. Verify it reads `SendAsync(query, variables, signer: null)`.

- [ ] **Step 6: Run the integration tests to verify they pass**

Run: `dotnet test tests/XcavateBuckets.Tests/XcavateBuckets.Tests.csproj --filter "FullyQualifiedName~GraphQLIntegrationTests"`
Expected: PASS, including the pre-existing sr25519 tests — the `Account` overloads must keep them green unchanged.

- [ ] **Step 7: Run the full offline suite**

Run: `dotnet test tests/XcavateBuckets.Tests/XcavateBuckets.Tests.csproj`
Expected: PASS.

- [ ] **Step 8: Commit**

```bash
git add src/XcavateProfileApiClient/Signing/IRequestSigner.cs src/XcavateProfileApiClient/Signing/SubstrateRequestSigner.cs src/XcavateProfileApiClient/Signing/SolanaRequestSigner.cs src/XcavateProfileApiClient/SigningHttpMessageHandler.cs tests/XcavateBuckets.Tests/GraphQLHost.cs tests/XcavateBuckets.Tests/GraphQLIntegrationTests.cs
git commit -m "Sign GraphQL requests with any scheme via IRequestSigner"
```

---

### Task 6: REST client overloads

**Files:**
- Modify: `src/XcavateProfileApiClient/XcavateProfileApiClient.cs:75-210`

**Interfaces:**
- Consumes: `IRequestSigner`, `SubstrateRequestSigner` (Task 5).
- Produces: four new overloads, each mirroring the existing method with `IRequestSigner signer` in place of `Account account`:
  - `Task<Profile> CreateProfileAsync(Profile profile, IRequestSigner signer)`
  - `Task<Profile> UpdateProfileAsync(string address, Profile profile, IRequestSigner signer)`
  - `Task DeleteProfileAsync(string address, IRequestSigner signer)`
  - `Task<string> UploadImageAsync(string address, Stream imageStream, string filename, IRequestSigner signer)`

  The existing `Account`-based methods keep their exact signatures (including the `Account? account = null` defaults) and delegate to these.

- [ ] **Step 1: Add a header helper**

In `src/XcavateProfileApiClient/XcavateProfileApiClient.cs`, add `using XcavateProfileApiClient.Signing;` and add this private method to the class:

```csharp
    /// <summary>
    /// Signs the payload and installs the three auth headers. Kept in one place so every verb
    /// agrees on the header names and the timestamp format.
    /// </summary>
    private async Task SignRequestAsync(
        string method, string path, IPayloadBody body, IRequestSigner signer, DateTime timestamp)
    {
        var payload = CryptoHelper.ConstructPayload(method, path, body, timestamp);
        var signature = await signer.SignAsync(payload);

        _httpClient.DefaultRequestHeaders.Clear();
        _httpClient.DefaultRequestHeaders.Add("X-SS58-Address", signer.Address);
        _httpClient.DefaultRequestHeaders.Add("X-Signature", signer.EncodeSignature(signature));
        _httpClient.DefaultRequestHeaders.Add("X-Timestamp", timestamp.ToUniversalTime().ToString("o"));
    }
```

- [ ] **Step 2: Convert `CreateProfileAsync`**

Replace the existing method (lines 75-104) with:

```csharp
    /// <summary>
    /// Create a new profile, authenticated with the caller's signature
    /// </summary>
    public Task<Profile> CreateProfileAsync(Profile profile, Account account)
    {
        if (account == null)
            throw new InvalidOperationException("Account is required for profile creation");

        return CreateProfileAsync(profile, new SubstrateRequestSigner(account));
    }

    /// <summary>
    /// Create a new profile using any signature scheme
    /// </summary>
    public async Task<Profile> CreateProfileAsync(Profile profile, IRequestSigner signer)
    {
        ArgumentNullException.ThrowIfNull(signer);

        var bodyJson = JsonSerializer.Serialize(profile, _jsonOptions);

        await SignRequestAsync("POST", "/api/profiles", profile, signer, DateTime.UtcNow);

        var content = new StringContent(bodyJson, Encoding.UTF8, "application/json");

        var response = await _httpClient.PostAsync("api/profiles", content);
        response.EnsureSuccessStatusCode();

        var responseContent = await response.Content.ReadAsStringAsync();
        return JsonSerializer.Deserialize<Profile>(responseContent, _jsonOptions) ?? throw new InvalidOperationException("Failed to create profile");
    }
```

- [ ] **Step 3: Convert `UpdateProfileAsync`**

Replace the existing method (lines 109-138) with:

```csharp
    /// <summary>
    /// Update an existing profile, authenticated with the caller's signature
    /// </summary>
    public Task<Profile> UpdateProfileAsync(string ss58address, Profile profile, Account? account = null)
    {
        if (account == null)
            throw new InvalidOperationException("Account is required for profile update");

        return UpdateProfileAsync(ss58address, profile, new SubstrateRequestSigner(account));
    }

    /// <summary>
    /// Update an existing profile using any signature scheme
    /// </summary>
    public async Task<Profile> UpdateProfileAsync(string ss58address, Profile profile, IRequestSigner signer)
    {
        ArgumentNullException.ThrowIfNull(signer);

        var bodyJson = JsonSerializer.Serialize(profile, _jsonOptions);

        await SignRequestAsync(
            "PUT", $"/api/profiles/{ss58address}", profile, signer, DateTime.UtcNow);

        var content = new StringContent(bodyJson, Encoding.UTF8, "application/json");

        var response = await _httpClient.PutAsync($"api/profiles/{ss58address}", content);
        response.EnsureSuccessStatusCode();

        var responseContent = await response.Content.ReadAsStringAsync();
        return JsonSerializer.Deserialize<Profile>(responseContent, _jsonOptions) ?? throw new InvalidOperationException("Failed to update profile");
    }
```

- [ ] **Step 4: Convert `DeleteProfileAsync`**

Replace the existing method (lines 143-163) with:

```csharp
    /// <summary>
    /// Delete a profile, authenticated with the caller's signature
    /// </summary>
    public Task DeleteProfileAsync(string ss58address, Account? account = null)
    {
        if (account == null)
            throw new InvalidOperationException("Account is required for profile deletion");

        return DeleteProfileAsync(ss58address, new SubstrateRequestSigner(account));
    }

    /// <summary>
    /// Delete a profile using any signature scheme
    /// </summary>
    public async Task DeleteProfileAsync(string ss58address, IRequestSigner signer)
    {
        ArgumentNullException.ThrowIfNull(signer);

        await SignRequestAsync(
            "DELETE", $"/api/profiles/{ss58address}", new EmptyPayloadBody(), signer, DateTime.UtcNow);

        var response = await _httpClient.DeleteAsync($"api/profiles/{ss58address}");
        response.EnsureSuccessStatusCode();
    }
```

- [ ] **Step 5: Convert `UploadImageAsync`**

Replace the method header and its signing block (lines 168-192). The method keeps everything from `// Create the request content` onward; only the signature line, the null check, and the signing block change:

```csharp
    /// <summary>
    /// Upload a profile image, authenticated with the caller's signature
    /// </summary>
    public Task<string> UploadImageAsync(string ss58address, Stream imageStream, string filename, Account? account = null)
    {
        if (account == null)
            throw new InvalidOperationException("Account is required for image upload");

        return UploadImageAsync(ss58address, imageStream, filename, new SubstrateRequestSigner(account));
    }

    /// <summary>
    /// Upload a profile image using any signature scheme
    /// </summary>
    public async Task<string> UploadImageAsync(string ss58address, Stream imageStream, string filename, IRequestSigner signer)
    {
        ArgumentNullException.ThrowIfNull(signer);

        // Create the request content
        var content = new MultipartFormDataContent();
        var imageContent = new StreamContent(imageStream);
        imageContent.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(GetImageContentType(filename));
        content.Add(imageContent, "image", filename);

        // The server hashes an empty body for multipart uploads, so the client must too.
        await SignRequestAsync(
            "POST", $"/api/profiles/{ss58address}/image", new EmptyPayloadBody(), signer, DateTime.UtcNow);

        var uri = new Uri($"api/profiles/{ss58address}/image", UriKind.Relative);

        var response = await _httpClient.PostAsync(uri, content);
        response.EnsureSuccessStatusCode();
```

Leave the remainder of the original method body (from `var responseContent = await response.Content.ReadAsStringAsync();` to the closing brace) exactly as it is.

- [ ] **Step 6: Build and run the offline suite**

Run: `dotnet build XcavateProfile.sln && dotnet test tests/XcavateBuckets.Tests/XcavateBuckets.Tests.csproj`
Expected: build succeeds with no new warnings, tests PASS. If `Account` is reported as ambiguous anywhere, add `using Account = Substrate.NetApi.Model.Types.Account;` to that file.

- [ ] **Step 7: Commit**

```bash
git add src/XcavateProfileApiClient/XcavateProfileApiClient.cs
git commit -m "Add IRequestSigner overloads to the REST profile client"
```

---

### Task 7: End-to-end tests against the live stack

**Files:**
- Modify: `tests/XcavateProfile.ApiTests/XcavateProfile.ApiTests.csproj`
- Create: `tests/XcavateProfile.ApiTests/SolanaAccounts.cs`
- Create: `tests/XcavateProfile.ApiTests/SolanaProfileApiTests.cs`
- Modify: `.env.example`
- Modify: `.env` (gitignored — local only, do not commit)

**Interfaces:**
- Consumes: `XcavateProfileClient` `IRequestSigner` overloads (Task 6), `SolanaRequestSigner` (Task 5), `TestMnemonics` (existing).
- Produces: `SolanaAccounts.From(string mnemonic)`, plus `SolanaAccounts.Admin`, `.Base`, `.User1`, `.User2`.

**Deterministic addresses** derived from the existing mnemonics via `new Wallet(new SolMnemonic(phrase, WordList.English)).Account` (Solnet's default `SeedMode.Ed25519Bip32`, i.e. `m/44'/501'/0'/0'`, which is what Phantom uses):

| Mnemonic | Solana address |
|---|---|
| `BaseMnemonic` | `AK7AACuihtCk6abEywXtg7sPW2Qh9iYg5C6BA38h9ciE` |
| `AdminMnemonic` | `DQJZmAVJZmN919gkbxREzb5iqoLZWLYx65Ts5JDnSb1b` |
| `User1Mnemonic` | `EkkGCbQ73M3V8NGvLH3o9kYZQTjRKadqFCH95YP4cKJf` |
| `User2Mnemonic` | `Di2WEEU8vXxbzxe7qKbK23d4dvByPeQjDsrDpWjXd16e` |

- [ ] **Step 1: Add the package reference**

In `tests/XcavateProfile.ApiTests/XcavateProfile.ApiTests.csproj`, add to the `PackageReference` `ItemGroup`:

```xml
    <PackageReference Include="Solnet.Wallet" Version="6.1.0" />
```

- [ ] **Step 2: Add the Solana admin address to configuration**

In `.env.example`, replace the `ADMIN_ADDRESSES` block (lines 17-18) with:

```
# Admin Addresses (comma-separated; SS58 and/or Solana base58 are both accepted)
ADMIN_ADDRESSES=5GrwvaEF5zKbXCEe9qGjZL23Y641mot2Ff6hS3s8jF3g3k3W,DQJZmAVJZmN919gkbxREzb5iqoLZWLYx65Ts5JDnSb1b
```

Then in the local, gitignored `.env`, append the same Solana address to the existing value so the line reads:

```
ADMIN_ADDRESSES=5EFFpddToZ2yxhy91UJdRrPXtsCFyUCXnv1uidZQsxuaMCxF,DQJZmAVJZmN919gkbxREzb5iqoLZWLYx65Ts5JDnSb1b
```

The running API must be restarted for this to take effect — `Env.Load` runs once at startup, and `docker-compose.yml:38` passes `ADMIN_ADDRESSES` through to the container.

- [ ] **Step 3: Add the deterministic Solana accounts**

Create `tests/XcavateProfile.ApiTests/SolanaAccounts.cs`:

```csharp
using Solnet.Wallet;
using SolMnemonic = Solnet.Wallet.Bip39.Mnemonic;
using SolWordList = Solnet.Wallet.Bip39.WordList;

namespace XcavateProfile.ApiTests;

/// <summary>
/// Solana counterparts of the personas in <see cref="TestMnemonics"/>. The existing phrases are
/// already valid BIP39 with correct checksums, so Solnet accepts them unchanged and each persona
/// simply gains a second address alongside its SS58 one.
/// </summary>
/// <remarks>
/// Derivation is Solnet's default <c>SeedMode.Ed25519Bip32</c> (m/44'/501'/0'/0'), which is what
/// Phantom and Solflare use, so these addresses match what a wallet would show for the same phrase.
/// </remarks>
public static class SolanaAccounts
{
    public static Account From(string mnemonic) =>
        new Wallet(new SolMnemonic(mnemonic, SolWordList.English)).Account;

    /// <summary>DQJZmAVJZmN919gkbxREzb5iqoLZWLYx65Ts5JDnSb1b — must be in ADMIN_ADDRESSES.</summary>
    public static Account Admin => From(TestMnemonics.AdminMnemonic);

    /// <summary>AK7AACuihtCk6abEywXtg7sPW2Qh9iYg5C6BA38h9ciE</summary>
    public static Account Base => From(TestMnemonics.BaseMnemonic);

    /// <summary>EkkGCbQ73M3V8NGvLH3o9kYZQTjRKadqFCH95YP4cKJf</summary>
    public static Account User1 => From(TestMnemonics.User1Mnemonic);

    /// <summary>Di2WEEU8vXxbzxe7qKbK23d4dvByPeQjDsrDpWjXd16e</summary>
    public static Account User2 => From(TestMnemonics.User2Mnemonic);
}
```

- [ ] **Step 4: Write the E2E tests**

Create `tests/XcavateProfile.ApiTests/SolanaProfileApiTests.cs`:

Note the explicit `System.*` usings: unlike `XcavateBuckets.Tests`, this project does **not** enable
`ImplicitUsings`, so `Task`, `Stream`, `HttpRequestException` and `Convert` must be imported by hand
(the existing `ProfileApiTests.cs` does the same).

```csharp
using NUnit.Framework;
using System;
using System.IO;
using System.Net.Http;
using System.Threading.Tasks;
using XcavateProfileApiClient;
using XcavateProfileApiClient.Signing;

namespace XcavateProfile.ApiTests;

/// <summary>
/// The sr25519 profile lifecycle, re-run with a Solana identity against the live stack. Requires
/// the docker stack on http://localhost:5000 and SolanaAccounts.Admin in ADMIN_ADDRESSES.
/// </summary>
[TestFixture]
public class SolanaProfileApiTests
{
    private const string TestApiUrl = "http://localhost:5000";
    private const string X25519Key =
        "0x1234567890abcdef1234567890abcdef1234567890abcdef1234567890abcdef";

    private XcavateProfileClient? _client;

    [SetUp]
    public void Setup() =>
        _client = new XcavateProfileClient(new XcavateProfileClientOptions { ApiUrl = TestApiUrl });

    [TearDown]
    public void TearDown() => _client?.Dispose();

    private static IRequestSigner Signer(Solnet.Wallet.Account account) =>
        new SolanaRequestSigner(account);

    /// <summary>The database persists across runs, so clear the persona's profile first.</summary>
    private static async Task EnsureNoProfileAsync(XcavateProfileClient client, IRequestSigner signer)
    {
        try
        {
            await client.DeleteProfileAsync(signer.Address, signer);
        }
        catch (HttpRequestException)
        {
            // 404 — nothing to clean up.
        }
    }

    [Test]
    public async Task Create_profile_with_a_solana_signature()
    {
        var signer = Signer(SolanaAccounts.Base);
        await EnsureNoProfileAsync(_client!, signer);

        var profile = new Profile
        {
            Ss58Address = signer.Address,
            Nickname = "solana-testuser",
            Bio = "Signed with ed25519",
            X25519Key = X25519Key
        };

        var result = await _client!.CreateProfileAsync(profile, signer);

        Assert.Multiple(() =>
        {
            Assert.That(result.Ss58Address, Is.EqualTo(signer.Address));
            Assert.That(result.Nickname, Is.EqualTo("solana-testuser"));
        });
    }

    [Test]
    public async Task Update_profile_with_a_solana_signature()
    {
        var signer = Signer(SolanaAccounts.User1);
        await EnsureNoProfileAsync(_client!, signer);

        var profile = new Profile
        {
            Ss58Address = signer.Address,
            Nickname = "solana-user1",
            X25519Key = X25519Key
        };
        await _client!.CreateProfileAsync(profile, signer);

        profile.Bio = "Updated over ed25519";
        var result = await _client.UpdateProfileAsync(signer.Address, profile, signer);

        Assert.That(result.Bio, Is.EqualTo("Updated over ed25519"));
    }

    [Test]
    public async Task Delete_profile_with_a_solana_signature()
    {
        var signer = Signer(SolanaAccounts.User2);
        await EnsureNoProfileAsync(_client!, signer);

        await _client!.CreateProfileAsync(
            new Profile
            {
                Ss58Address = signer.Address,
                Nickname = "solana-user2",
                X25519Key = X25519Key
            },
            signer);

        await _client.DeleteProfileAsync(signer.Address, signer);

        Assert.That(await _client.GetProfileAsync(signer.Address), Is.Null);
    }

    [Test]
    public async Task Upload_image_with_a_solana_signature()
    {
        var signer = Signer(SolanaAccounts.Base);
        await EnsureNoProfileAsync(_client!, signer);

        await _client!.CreateProfileAsync(
            new Profile
            {
                Ss58Address = signer.Address,
                Nickname = "solana-imageuser",
                X25519Key = X25519Key
            },
            signer);

        // A 1x1 PNG.
        var png = Convert.FromBase64String(
            "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg==");

        using var stream = new MemoryStream(png);
        var url = await _client.UploadImageAsync(signer.Address, stream, "solana-test.png", signer);

        Assert.That(url, Does.Contain("solana-test.png"));
    }

    /// <summary>A Solana address in ADMIN_ADDRESSES gets the same privileges as an SS58 one.</summary>
    [Test]
    public async Task Solana_admin_can_update_another_users_profile()
    {
        var admin = Signer(SolanaAccounts.Admin);
        var user = Signer(SolanaAccounts.User1);
        await EnsureNoProfileAsync(_client!, user);

        var profile = new Profile
        {
            Ss58Address = user.Address,
            Nickname = "solana-victim",
            X25519Key = X25519Key
        };
        await _client!.CreateProfileAsync(profile, user);

        profile.Bio = "Edited by a Solana admin";
        var result = await _client.UpdateProfileAsync(user.Address, profile, admin);

        Assert.That(result.Bio, Is.EqualTo("Edited by a Solana admin"));
    }

    /// <summary>A Solana caller must not be able to write someone else's profile.</summary>
    [Test]
    public async Task Non_admin_solana_caller_cannot_update_another_users_profile()
    {
        var owner = Signer(SolanaAccounts.User1);
        var attacker = Signer(SolanaAccounts.User2);
        await EnsureNoProfileAsync(_client!, owner);

        var profile = new Profile
        {
            Ss58Address = owner.Address,
            Nickname = "solana-owner",
            X25519Key = X25519Key
        };
        await _client!.CreateProfileAsync(profile, owner);

        profile.Nickname = "hacked";

        Assert.ThrowsAsync<HttpRequestException>(
            async () => await _client.UpdateProfileAsync(owner.Address, profile, attacker));
    }
}
```

- [ ] **Step 5: Start the stack and run the E2E suite**

Run: `./run_e2e_tests.sh`

Expected: PASS, including both the pre-existing `ProfileApiTests` (sr25519 must not regress) and the new `SolanaProfileApiTests`.

If `Solana_admin_can_update_another_users_profile` fails with 403, the running API has a stale `ADMIN_ADDRESSES` — confirm `.env` contains `DQJZmAVJZmN919gkbxREzb5iqoLZWLYx65Ts5JDnSb1b` and restart the API.

- [ ] **Step 6: Commit**

Note `.env` is gitignored and must not be staged.

```bash
git add tests/XcavateProfile.ApiTests/XcavateProfile.ApiTests.csproj tests/XcavateProfile.ApiTests/SolanaAccounts.cs tests/XcavateProfile.ApiTests/SolanaProfileApiTests.cs .env.example
git commit -m "Add Solana end-to-end profile tests"
```

---

### Task 8: Documentation

**Files:**
- Modify: `README.md:55-65` (authentication section), `README.md:255-270` (ADMIN_ADDRESSES section)
- Modify: `ADMIN_AUTH.md:95-135` (headers section)

**Interfaces:**
- Consumes: everything above. Produces no code.

- [ ] **Step 1: Document the dual scheme in README.md**

In the authentication header section (around `README.md:59`), replace the `X-SS58-Address` bullet list with:

````markdown
### Authentication headers

| Header | Value |
|---|---|
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
|---|---|---|
| sr25519 | SS58 | `blake2b(utf8(payload), 128)` — a 16-byte digest |
| Solana ed25519 | base58, 32 bytes | `utf8(payload)` — the string itself, unhashed |

Solana signs the string unhashed so a wallet's approval popup shows readable text rather than
binary. Note that the body-hash *segment* is still Blake2b-128 in both cases — a JS caller needs
`blakejs`.

**Signing from a browser wallet:**

```javascript
import bs58 from 'bs58';
import { blake2b } from 'blakejs';

const hex = (bytes) => '0x' + Buffer.from(bytes).toString('hex');

const body = JSON.stringify({ query: '...' });
const bodyHash = hex(blake2b(new TextEncoder().encode(body), null, 16));
const timestamp = new Date().toISOString();
const payload = `POST:/graphql:${bodyHash}:${timestamp}`;

const { signature } = await window.solana.signMessage(
  new TextEncoder().encode(payload), 'utf8');

const headers = {
  'X-SS58-Address': window.solana.publicKey.toBase58(),
  'X-Signature': bs58.encode(signature),
  'X-Timestamp': timestamp,
};
```
````

- [ ] **Step 2: Document the admin config in README.md**

In the `ADMIN_ADDRESSES` section (around `README.md:263`), replace the format line with:

```markdown
**Format**: Comma-separated list of addresses. SS58 and Solana base58 addresses can be mixed freely.
```

- [ ] **Step 3: Update ADMIN_AUTH.md**

In the headers table (around `ADMIN_AUTH.md:100`), replace the `X-SS58-Address` row with:

```markdown
| `X-SS58-Address` | The signer's address — Substrate SS58 or Solana base58 | `5...` or `DQJZ...` |
| `X-Signature` | The 64-byte signature, `0x`-hex or base58 | `0xB8AA...` or `4h96nSR...` |
```

And in the `ADMIN_ADDRESSES` section (around `ADMIN_AUTH.md:175`), replace the format comment with:

```
# Format: comma-separated addresses (SS58 and/or Solana base58)
```

- [ ] **Step 4: Verify the examples**

Re-read both files and confirm every code sample matches the implemented behaviour: header names, the `0x` prefix rule, the 64-byte requirement, and the Blake2b-128 body hash.

- [ ] **Step 5: Commit**

```bash
git add README.md ADMIN_AUTH.md
git commit -m "Document dual-scheme signature authentication"
```

---

## Final Verification

- [ ] Run the full offline suite: `dotnet test tests/XcavateBuckets.Tests/XcavateBuckets.Tests.csproj` — all green.
- [ ] Run the E2E suite: `./run_e2e_tests.sh` — all green, including the untouched sr25519 tests.
- [ ] Confirm `git status` is clean and `.env` was never staged.
- [ ] Confirm `git diff master --stat` shows no change to `CryptoHelper.cs`, `GraphQLSignatureMiddleware.cs`, or `ProfilesController.cs` — if any of those moved, the scheme abstraction leaked past its intended boundary.
