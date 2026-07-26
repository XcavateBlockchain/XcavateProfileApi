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

    /// <summary>
    /// Sr25519v091.Verify throws SignatureException rather than returning false when handed an
    /// ed25519 signature: Schnorrkel marks its own signatures with a bit that ed25519's math
    /// guarantees is always unset (the s-scalar is always &lt; 2^253), so this is deterministic, not
    /// a flaky edge case. That makes the try/catch around scheme.Verify in the validator
    /// load-bearing: it is what turns this throw into a clean IsValid = false instead of an
    /// unhandled exception reaching the caller as a 500.
    /// </summary>
    [Test]
    public void Solana_signature_presented_with_an_ss58_address_fails_without_throwing()
    {
        var solanaAccount = SolanaAccount(0x47);
        var ss58Account = SubstrateAccount(0x48);
        var timestamp = DateTime.UtcNow;
        var signature = solanaAccount.Sign(System.Text.Encoding.UTF8.GetBytes(Payload(timestamp)));

        SignatureValidationResult? result = null;
        Assert.DoesNotThrowAsync(async () => result = await NewValidator().ValidateAsync(
            ss58Account.Value, Utils.Bytes2HexString(signature), timestamp.ToString("o"),
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
