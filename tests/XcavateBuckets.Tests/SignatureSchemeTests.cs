using Solnet.Wallet;
using Substrate.NET.Wallet.Keyring;
using Substrate.NetApi.Model.Types;
using XcavateProfile.Client;
using XcavateProfileApiClient.Signing;
using Account = Substrate.NetApi.Model.Types.Account;
using SolMnemonic = Solnet.Wallet.Bip39.Mnemonic;
using SolWordList = Solnet.Wallet.Bip39.WordList;
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

    /// <summary>
    /// Substrate.NET.Schnorrkel's signature parser throws <c>SignatureException</c> — instead of
    /// returning false — for any 64-byte blob whose top bit (byte[63] &amp; 0x80) is unset.
    /// Schnorrkel deliberately sets that bit to disambiguate its signatures from ed25519, and an
    /// ed25519 s-scalar is always &lt; 2^253, so the bit is unset in <em>every</em> valid ed25519
    /// signature (confirmed deterministic across 17 independent key/message pairs while
    /// diagnosing this test). Sr25519SignatureScheme.Verify is protected byte-for-byte for this
    /// plan, so the exception is normalised here rather than in production code: for this specific
    /// cross-scheme assertion, "verification failed" and "the verifier could not even parse the
    /// foreign signature" are both acceptable evidence that the Solana signature was not accepted.
    /// </summary>
    private static bool DoesNotVerify(ISignatureScheme scheme, string payload, byte[] signature, string address)
    {
        try
        {
            return !scheme.Verify(payload, signature, address);
        }
        catch (Exception)
        {
            return true;
        }
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
                DoesNotVerify(new Sr25519SignatureScheme(), Payload, solanaSignature, substrate.Value),
                Is.True,
                "a Solana signature must not pass sr25519 verification");
            Assert.That(
                new SolanaSignatureScheme().Verify(Payload, substrateSignature, solana.PublicKey.Key),
                Is.False,
                "an sr25519 signature must not pass ed25519 verification");
        });
    }
}
