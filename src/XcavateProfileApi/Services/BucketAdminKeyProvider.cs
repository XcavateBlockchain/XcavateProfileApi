using System.Text.Json;
using Microsoft.AspNetCore.WebUtilities;

namespace XcavateProfileApi.Services;

public static class BucketAdminKeyProvider
{
    public const string ConfigKey = "BUCKET_ADMIN_X25519_KEY";

    public static string GetPublicKey(IConfiguration config)
    {
        var configured = config[ConfigKey];
        if (string.IsNullOrWhiteSpace(configured))
        {
            throw new InvalidOperationException($"The bucket admin X25519 key ({ConfigKey}) is not configured.");
        }

        try
        {
            using var document = JsonDocument.Parse(configured);
            var publicJwk = document.RootElement.GetProperty("publicJwk");
            if (publicJwk.GetProperty("crv").GetString() != "X25519"
                || publicJwk.GetProperty("kty").GetString() != "OKP")
            {
                throw new InvalidOperationException(
                    $"{ConfigKey} must contain an X25519 public JWK.");
            }

            var encoded = publicJwk.GetProperty("x").GetString();
            if (string.IsNullOrWhiteSpace(encoded))
            {
                throw new InvalidOperationException(
                    $"{ConfigKey} must contain an X25519 public JWK.");
            }
            var bytes = WebEncoders.Base64UrlDecode(encoded);
            if (bytes.Length != 32)
            {
                throw new InvalidOperationException(
                    $"{ConfigKey} must contain a 32-byte X25519 public JWK.");
            }
            return WebEncoders.Base64UrlEncode(bytes);
        }
        catch (Exception ex) when (ex is JsonException or FormatException
            or System.Collections.Generic.KeyNotFoundException)
        {
            throw new InvalidOperationException(
                $"{ConfigKey} must contain a valid public X25519 JWK.", ex);
        }
    }
}
