using System.Globalization;
using XcavateProfile.Client;
using XcavateProfileApiClient;
using XcavateProfileApiClient.Signing;

namespace XcavateProfileApi.Middleware;

public class SignatureValidator : ISignatureValidator
{
    private readonly List<string> _adminAddresses;
    private readonly SignatureValidationOptions _options;

    /// <summary>
    /// Ordered by cost of recognition; the first scheme that claims the address format wins. The
    /// two formats do not overlap — SS58 decoding validates a checksum and yields 35 bytes, a
    /// Solana address is exactly 32 — so the order is for clarity rather than correctness.
    /// </summary>
    private static readonly IReadOnlyList<ISignatureScheme> Schemes =
    [
        new Sr25519SignatureScheme(),
        new SolanaSignatureScheme()
    ];

    public SignatureValidator(
        List<string> adminAddresses,
        SignatureValidationOptions options)
    {
        _adminAddresses = adminAddresses;
        _options = options;
    }

    public async Task<SignatureValidationResult> ValidateAsync(
        string address,
        string signatureHex,
        string timestamp,
        string method,
        string path,
        IPayloadBody payloadBody)
    {
        // Parse timestamp and validate freshness.
        // AdjustToUniversal keeps the comparison below against DateTime.UtcNow honest: a plain
        // TryParse of an ISO-8601 "...Z" string yields a Local DateTime, so on any host that is not
        // itself on UTC the skew came out wrong by the machine's offset and every valid signature
        // was rejected. AssumeUniversal covers clients that omit the zone designator.
        if (!DateTime.TryParse(
                timestamp,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal,
                out var ts))
        {
            return new SignatureValidationResult
            {
                IsValid = false,
                Error = "Invalid timestamp format"
            };
        }

        var now = DateTime.UtcNow;
        var skew = Math.Abs((now - ts).TotalSeconds);
        if (skew > _options.TimestampSkew.TotalSeconds)
        {
            return new SignatureValidationResult
            {
                IsValid = false,
                Error = $"Timestamp too old or too far in the future (skew: {skew}s, max: {_options.TimestampSkew.TotalSeconds}s)"
            };
        }

        // Construct the signed payload. Identical for both schemes — they differ only in which
        // bytes of it get signed.
        var payload = CryptoHelper.ConstructPayload(method, path, payloadBody, ts);

        // Decoding happens inside the guarded path on purpose: this is unauthenticated input, and
        // a malformed signature must produce a 401, not an unhandled exception.
        if (!SignatureEncoding.TryDecode(signatureHex, out var signatureBytes))
        {
            return new SignatureValidationResult
            {
                IsValid = false,
                Error = $"Signature must decode to {SignatureEncoding.SignatureLength} bytes "
                    + "from 0x-prefixed hex or base58"
            };
        }

        var scheme = Schemes.FirstOrDefault(s => s.CanVerify(address));
        if (scheme is null)
        {
            return new SignatureValidationResult
            {
                IsValid = false,
                Error = "Unrecognised address format: expected an SS58 or Solana base58 address"
            };
        }

        try
        {
            var isValid = scheme.Verify(payload, signatureBytes, address);

            return new SignatureValidationResult
            {
                IsValid = isValid,
                Ss58Address = address,
                Error = isValid ? null : "Signature verification failed"
            };
        }
        catch (Exception ex)
        {
            return new SignatureValidationResult
            {
                IsValid = false,
                Error = $"Signature verification error: {ex.Message}"
            };
        }
    }

    public bool IsAdmin(string address)
    {
        return _adminAddresses.Contains(address);
    }
}
