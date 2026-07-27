using System.Buffers;

namespace XcavateProfileApiClient;

/// <summary>
/// Hex encoding for the values that go on the wire.
/// </summary>
/// <remarks>
/// The output format — <c>0x</c> prefix, uppercase digits — is not a style choice. It reproduces
/// <c>Substrate.NetApi.Utils.Bytes2HexString</c> byte for byte, which is what produced every body
/// hash the server has ever verified. Changing the casing or dropping the prefix would silently
/// invalidate every signature, so <c>HexMatchesSubstrateEncoding</c> in the Solana client's tests
/// pins this against the real Substrate helper.
/// </remarks>
internal static class Hex
{
    /// <summary>Encodes as <c>0x</c> + uppercase hex.</summary>
    public static string ToPrefixedString(ReadOnlySpan<byte> bytes) => "0x" + Convert.ToHexString(bytes);

    /// <summary>
    /// Decodes hex of an exactly known byte length, without throwing on malformed input. The
    /// caller passes the expected length because every hex value this SDK decodes is fixed-size,
    /// and a length mismatch is the check that rejects garbage.
    /// </summary>
    public static bool TryDecodeExact(ReadOnlySpan<char> hex, int byteLength, out byte[] bytes)
    {
        bytes = [];

        var buffer = new byte[byteLength];

        // Anything malformed lands on a non-Done status: InvalidData for non-hex digits,
        // NeedMoreData for an odd digit count, DestinationTooSmall for an over-long input.
        if (Convert.FromHexString(hex, buffer, out _, out var written) != OperationStatus.Done
            || written != byteLength)
        {
            return false;
        }

        bytes = buffer;
        return true;
    }
}
