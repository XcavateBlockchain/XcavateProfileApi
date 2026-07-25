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
