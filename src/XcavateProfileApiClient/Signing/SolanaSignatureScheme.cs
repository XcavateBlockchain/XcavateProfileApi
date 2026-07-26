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
