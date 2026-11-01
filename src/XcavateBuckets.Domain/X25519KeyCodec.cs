namespace XcavateBuckets.Domain;

public static class X25519KeyCodec
{
    public static string Normalize(string? value, string field)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw BucketException.InvalidInput($"'{field}' is required.");
        }
        if (value.Length > 66)
        {
            throw BucketException.InvalidInput($"'{field}' has an invalid encoded length.");
        }

        byte[] bytes;
        try
        {
            bytes = Decode32(value, field);
        }
        catch (FormatException)
        {
            throw BucketException.InvalidInput(
                $"'{field}' must be a 32-byte X25519 key in base64url or legacy hex.");
        }

        return Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    public static bool TryNormalize(string? value, out string? canonical)
    {
        canonical = null;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        try
        {
            canonical = Normalize(value, "value");
            return true;
        }
        catch (BucketException)
        {
            return false;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    public static byte[] Decode32(string? value, string field)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw BucketException.InvalidInput($"'{field}' is required.");
        }

        var hex = value.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? value[2..] : value;
        byte[] bytes;
        if (hex.Length == 64 && hex.All(Uri.IsHexDigit))
        {
            bytes = Convert.FromHexString(hex);
        }
        else
        {
            var base64 = value.Replace('-', '+').Replace('_', '/');
            var padding = (4 - base64.Length % 4) % 4;
            if (padding == 3)
            {
                throw BucketException.InvalidInput(
                    $"'{field}' must be a 32-byte X25519 key in base64url or legacy hex.");
            }
            bytes = Convert.FromBase64String(base64.PadRight(base64.Length + padding, '='));
        }

        if (bytes.Length != 32)
        {
            throw BucketException.InvalidInput(
                $"'{field}' must be a 32-byte X25519 key in base64url or legacy hex.");
        }

        return bytes;
    }

    public static bool KeysEqual(string? left, string? right) =>
        TryNormalize(left, out var leftCanonical)
        && TryNormalize(right, out var rightCanonical)
        && string.Equals(leftCanonical, rightCanonical, StringComparison.Ordinal);
}
