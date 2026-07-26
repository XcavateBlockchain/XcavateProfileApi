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
            // Utils.HexToByteArray only strips a lowercase "0x" prefix internally: given "0X..." it
            // fails to strip it and misparses the whole string (confirmed empirically). We detect
            // the prefix case-insensitively but always hand the helper the bare hex body ourselves.
            var decoded = signature.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
                ? Utils.HexToByteArray(signature[2..])
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
