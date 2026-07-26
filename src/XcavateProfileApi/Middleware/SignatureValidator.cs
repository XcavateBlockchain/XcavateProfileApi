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
    /// The first scheme that claims the address format wins. The list order is not a cost
    /// ordering — if anything it runs backwards, since sr25519 recognition
    /// (<c>Utils.GetPublicKeyFrom</c>) does a base58 decode plus a blake2b checksum verify,
    /// while Solana recognition (Solnet's <c>PublicKey</c>) only checks the decoded length.
    /// Order does not affect correctness because the two formats never overlap: a valid,
    /// checksummed SS58 address decodes to exactly 32 bytes via
    /// <c>Utils.GetPublicKeyFrom</c>, while Solnet's <c>PublicKey</c> — which performs no
    /// checksum check — yields 32 bytes only for a genuine Solana address, and 35 bytes
    /// (undecoded prefix + key + checksum) if handed an SS58 string instead.
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

        // TryDecode cannot throw: it wraps its own work in an internal try/catch and returns
        // false for anything malformed, which is what turns a bad signature into a 401 instead
        // of an unhandled exception. That safety comes from TryDecode itself — this call runs
        // before the try block below (which guards scheme.Verify), not inside it.
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
