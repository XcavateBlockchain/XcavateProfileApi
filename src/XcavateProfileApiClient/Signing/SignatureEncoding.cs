using Solnet.Wallet.Utilities;

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

        // The prefix is matched case-insensitively but stripped before decoding, so "0X…" is
        // accepted rather than misparsed as hex digits.
        if (signature.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            return Hex.TryDecodeExact(signature.AsSpan(2), SignatureLength, out bytes);
        }

        try
        {
            var decoded = Encoders.Base58.DecodeData(signature);

            if (decoded.Length != SignatureLength)
            {
                return false;
            }

            bytes = decoded;
            return true;
        }
        catch (Exception)
        {
            // FormatException from invalid base58 characters.
            return false;
        }
    }
}
