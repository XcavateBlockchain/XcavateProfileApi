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
    private const string HexBody =
        "B8AA78CE847A5A127B5E97F747BFFB90B97AAB5A54811531985FED4ACE25BA54"
        + "AA3D488D27F132B155326DB97B6034EE9CA9D499AF6D25977D94EC1B062C9E0E";

    private const string Hex = "0x" + HexBody;

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

    // Hex now decodes through Convert.FromHexString, which reports invalid digits and odd lengths
    // rather than quietly returning a short array the way Substrate's Utils.HexToByteArray did.
    // Both the status and the 64-byte length check have to reject these.
    [TestCase("0xZZ", TestName = "Invalid hex digits")]
    [TestCase("0x1234", TestName = "Hex too short")]
    [TestCase("0x123", TestName = "Odd hex digit count")]
    [TestCase("0x" + HexBody + HexBody, TestName = "Hex too long")]
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
