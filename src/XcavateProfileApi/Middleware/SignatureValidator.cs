using System.Globalization;
using XcavateProfile.Client;
using XcavateProfileApiClient;

namespace XcavateProfileApi.Middleware;

public class SignatureValidator : ISignatureValidator
{
    private readonly List<string> _adminAddresses;
    private readonly SignatureValidationOptions _options;

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

        // Construct the signed payload
        var payload = CryptoHelper.ConstructPayload(method, path, payloadBody, ts);

        var signatureBytes = Substrate.NetApi.Utils.HexToByteArray(signatureHex);

        try
        {
            var isValid = CryptoHelper.VerifySignature(
                payload,
                signatureBytes,
                address);

            if (!isValid)
            {
                var wrappedPayloadHash = "<Bytes>"u8
                    .ToArray()
                    .Concat(CryptoHelper.Hash(payload))
                    .Concat("</Bytes>"u8.ToArray())
                    .ToArray();

                isValid = CryptoHelper.VerifySignature(
                    input: wrappedPayloadHash,
                    signature: signatureBytes,
                    address: address);
            }

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
