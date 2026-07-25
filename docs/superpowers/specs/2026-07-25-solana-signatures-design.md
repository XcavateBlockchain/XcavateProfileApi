# Solana Signature Support — Design

**Date:** 2026-07-25
**Status:** Approved

## Goal

Let a caller authenticate against XcavateProfileApi with a **Solana** keypair (ed25519, base58
address) in addition to the **sr25519 / SS58** scheme the API implements today. Both the REST
profile endpoints and the `/graphql` endpoint accept either scheme.

Two consumers are in scope:

- **Browser wallets** (Phantom, Solflare) calling `signMessage` from a frontend.
- **C# callers** using `XcavateProfileApiClient` with a `Solnet.Wallet.Account`.

Verification and test-side signing both use **Solnet** (`Solnet.Wallet` 6.1.0).

## The one thing that stays the same

The signed **payload string** is identical for both schemes, built by the existing
`CryptoHelper.ConstructPayload`:

```
{METHOD}:{path}:{blake2b_128_hex_of_body}:{timestamp:o}
```

Only the *bytes handed to the signature function* differ:

| | sr25519 (unchanged) | Solana (new) |
|---|---|---|
| Address format | SS58 — base58 of `prefix ‖ 32-byte pubkey ‖ 2-byte blake2 checksum` | base58 of exactly 32 bytes |
| Signed bytes | `Blake2b(utf8(payload), 128)` → 16 bytes | `utf8(payload)` → raw |
| Wrapped fallback | retry against `<Bytes>` ‖ hash ‖ `</Bytes>` | none |
| Verify | `Sr25519v091.Verify` | `Solnet.Wallet.PublicKey.Verify` (ed25519) |
| Signature length | 64 bytes | 64 bytes |

Solana signs the **raw payload string** rather than its hash because wallets render the bytes
passed to `signMessage` as UTF-8 in the approval popup. Signing a 16-byte Blake2 digest would show
the user binary garbage, which is exactly the prompt users are trained not to approve.

The sr25519 path — including the `<Bytes>…</Bytes>` retry that accommodates the polkadot-js
extension — is byte-for-byte unchanged. No existing client re-signs anything.

### Frontend consequence: Blake2b is still required

Because the payload format does not fork, a JS caller on the Solana path still computes the
body-hash segment with Blake2b-128 (`blakejs`: `blake2b(bytes, null, 16)`). Substituting SHA-256
for Solana callers would mean two payload formats, two server-side reconstructions, and a
permanent "which hash did this client use?" ambiguity. One format is worth the extra frontend
dependency. This must be called out in the docs (§7) so it is not discovered at integration time.

## Scheme dispatch — inferred from the address, not declared

No `X-Signature-Scheme` header. The two address formats are provably distinguishable, verified
empirically against `Substrate.NET.API` 0.9.24-rc6 and `Solnet.Wallet` 6.1.0:

| Input | `Utils.GetPublicKeyFrom` | `new PublicKey(s)` |
|---|---|---|
| SS58 (48 chars) | 32-byte pubkey | **35 bytes**, `IsOnCurve = false` |
| Solana (43–44 chars) | throws `NotSupportedException: Unsupported address size.` | 32 bytes, `IsOnCurve = true` |
| SS58 with a corrupted char | throws `NotSupportedException: Address checksum is wrong.` | — |

Dispatch is an ordered try-list; first scheme that recognises the address wins:

```
Sr25519SignatureScheme.CanVerify(addr) → SS58 decode succeeds (checksum validated)
SolanaSignatureScheme.CanVerify(addr)  → base58 decode yields exactly 32 bytes
neither                                → "Unrecognised address format"
```

**Mandatory guard.** Solnet's `PublicKey(string)` constructor does *not* validate length — handed
an SS58 address it constructs a 35-byte key and only throws `ArgumentException: Public key size
must be 32` later, inside `Verify`. `SolanaSignatureScheme` must therefore check
`KeyBytes.Length == 32` in `CanVerify`, *before* any verification is attempted. This length check
is the sole thing preventing an SS58 address from reaching the ed25519 path, so it gets a
dedicated test.

## Signature encoding on the wire

Headers are unchanged: `X-SS58-Address`, `X-Signature`, `X-Timestamp`.

- `X-SS58-Address` carries an SS58 *or* a Solana base58 address. The name is now a slight
  misnomer; renaming it is not worth breaking every existing caller and doc (see Non-goals).
- `X-Signature` accepts **either** encoding, for both schemes:
  - starts with `0x` (case-insensitive) → hex, what `Utils.Bytes2HexString` already emits
  - otherwise → base58, what `bs58.encode()` gives a JS dev straight from `signMessage`
- The decoded signature must be **exactly 64 bytes**; anything else is a validation failure.

Ambiguity between the two encodings is not reachable in practice: an unprefixed hex string could
in principle decode as base58, but only a 64-byte result passes, and every existing client emits
the `0x` prefix.

### Bug fixed en route

`SignatureValidator.cs:60` currently calls `Utils.HexToByteArray(signatureHex)` **outside** the
`try` block, so a malformed `X-Signature` throws an unhandled exception and the caller gets a 500
instead of a 401. Signature decoding moves inside the guarded path and returns a
`SignatureValidationResult` with `IsValid = false`. This is a precondition of the work — the new
base58 branch adds more ways for decoding to fail.

## Code layout

New folder `src/XcavateProfileApiClient/Signing/`, shared by client and server exactly as
`CryptoHelper` is today:

| Type | Responsibility |
|---|---|
| `ISignatureScheme` | `bool CanVerify(string address)`, `bool Verify(string payload, byte[] signature, string address)` |
| `Sr25519SignatureScheme` | Delegates to the existing `CryptoHelper` calls, including the `<Bytes>` retry. No behaviour change. |
| `SolanaSignatureScheme` | 32-byte guard, then `PublicKey.Verify(utf8(payload), signature)` |
| `SignatureEncoding` | `byte[] Decode(string)` — `0x`-hex or base58, 64-byte length check |
| `IRequestSigner` | `string Address { get; }`, `Task<byte[]> SignAsync(string payload)` |
| `SubstrateRequestSigner` | Wraps `Substrate.NetApi.Model.Types.Account`; hashes then signs |
| `SolanaRequestSigner` | Wraps `Solnet.Wallet.Account`; signs UTF-8 bytes |

`XcavateProfileApiClient.csproj` gains `Solnet.Wallet` 6.1.0 (targets net6.0, so net10.0-compatible;
deps `Chaos.NaCl.Standard` — already in the graph via Substrate.NET.API — and
`Portable.BouncyCastle`). `XcavateProfileApi` picks it up transitively through the existing
`ProjectReference`.

`CryptoHelper` keeps its current public surface; `Sr25519SignatureScheme` calls into it.

## Server changes

`SignatureValidator.ValidateAsync` becomes:

1. Parse and range-check the timestamp *(unchanged)*
2. `CryptoHelper.ConstructPayload(...)` *(unchanged)*
3. `SignatureEncoding.Decode(signatureHex)` — inside the guarded path
4. Pick the first `ISignatureScheme` whose `CanVerify(address)` is true
5. `scheme.Verify(payload, signature, address)`

`ISignatureValidator` keeps its current signature, so:

- **`GraphQLSignatureMiddleware` needs no change** — it already delegates to the validator, and
  `CallerRejection` mapping still works off the error text.
- **All four REST actions in `ProfilesController` need no change** — same reason.
- **`IsAdmin` needs no change** — `ADMIN_ADDRESSES` is a plain string list and compares equally
  well against a Solana address.

Both authenticated surfaces therefore gain Solana support from one change to the validator.

## Client changes

- `SigningHttpMessageHandler` takes an `IRequestSigner`. An `Account`-accepting constructor
  overload is kept so existing wiring compiles unchanged.
- `XcavateProfileApiClient`'s four signing methods — `CreateProfileAsync`, `UpdateProfileAsync`,
  `DeleteProfileAsync`, `UploadImageAsync` — gain `IRequestSigner` overloads alongside their
  existing `Account` parameters. Nothing existing breaks.

## Tests

### `tests/XcavateBuckets.Tests` — offline, no docker

- Scheme detection: SS58 → sr25519 scheme; Solana → Solana scheme; garbage → neither.
- **Cross-scheme rejection**: a Solana signature presented with an SS58 address fails, and an
  sr25519 signature presented with a Solana address fails.
- The 35-byte `PublicKey` guard: an SS58 address never reaches `PublicKey.Verify`.
- Round-trip verify for both schemes; tampered payload fails.
- Signature decoding: `0x`-hex accepted, base58 accepted, wrong length rejected, malformed input
  yields a clean `IsValid = false` rather than a thrown exception.
- A Solana-signed GraphQL mutation against the in-process `GraphQLHost`, plus a tampered-body
  variant that must be rejected.

### `tests/XcavateProfile.ApiTests` — live docker stack

Solana identity through the REST profile lifecycle: create, update, delete, image upload, and the
admin path (Solana admin updating another user's profile), mirroring the existing sr25519 tests.

### Test key material

`TestMnemonics` gains deterministic Solana accounts by feeding the **existing** mnemonic phrases
(`AdminMnemonic`, `User1Mnemonic`, …) through `Solnet.Wallet.Wallet`. Those phrases are already
valid BIP39 with correct checksums, so Solnet accepts them unchanged; each persona simply gains a
second, Solana-side address alongside its SS58 one, stable across runs. No new entropy constants.

**Known collision:** `Mnemonic` is ambiguous between `Substrate.NetApi.Mnemonic` and
`Solnet.Wallet.Bip39.Mnemonic`; any file referencing both needs `using` aliases
(e.g. `using SubMnemonic = Substrate.NetApi.Mnemonic;`).

## Config and docs

- `.env.example` and the local `.env`: append the deterministic Solana admin address to
  `ADMIN_ADDRESSES`. It is already a comma-separated string list, so no format change — but the
  live docker stack's `.env` must be updated or the admin E2E tests fail.
- `README.md` and `ADMIN_AUTH.md`: a dual-scheme authentication section covering which bytes each
  scheme signs, hex-vs-base58 signature encoding, the Blake2b-128 body-hash requirement, and a
  Phantom `signMessage` example.

## Non-goals

- **Renaming `Profile.Ss58Address`** (column and primary key). It is a string; a Solana address
  stores fine. Renaming means an EF migration plus a breaking change to the REST route
  `/api/profiles/{ss58address}`, the JSON contract, and every doc — for no functional gain.
- Per-scheme columns, or persisting a "chain type" alongside an address.
- Solana transactions or RPC. Only `Solnet.Wallet` is referenced, never `Solnet.Rpc`.
- Sign-In-With-Solana (SIWS) structured messages.
- Replay protection beyond the existing 5-minute `TimestampSkew` window.
- Address-format validation on profile *creation*. Today any string is accepted as a profile key;
  that stays true, and authentication is what actually gates writes.
