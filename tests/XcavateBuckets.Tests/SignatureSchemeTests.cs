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
    public async Task Sr25519_verifies_a_wrapped_signature_from_a_browser_extension()
    {
        var account = SubstrateAccount(0x34);

        // The polkadot-js extension never signs the raw payload: it wraps the payload's hash in
        // <Bytes>...</Bytes> and signs that byte array as-is. account.SignAsync is the raw signing
        // primitive (no internal hashing) — CryptoHelper.SignAsync would hash the wrapped bytes
        // again, producing a signature Verify's fallback branch could never match.
        var wrapped = "<Bytes>"u8
            .ToArray()
            .Concat(CryptoHelper.Hash(Payload))
            .Concat("</Bytes>"u8.ToArray())
            .ToArray();
        var signature = await account.SignAsync(wrapped);

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
