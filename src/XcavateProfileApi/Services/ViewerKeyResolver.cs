using Microsoft.EntityFrameworkCore;
using XcavateBuckets.Domain;
using XcavateProfileApi.Data;

namespace XcavateProfileApi.Services;

/// <summary>
/// Resolves the bucket viewer id (a 32-byte X25519 key) for a wallet, or null when the wallet has
/// no usable one: the profile keyed directly by the wallet first, then the
/// <c>WalletMigrations</c> fallback for a profile still keyed by the legacy SS58 address of a
/// migrated account. Base64url is preserved, legacy hex is normalized to base64url, and malformed
/// keys resolve to null rather than causing a permanent <c>InvalidInput</c> retry loop.
/// </summary>
public class ViewerKeyResolver(ProfileDbContext profiles)
{
    public async Task<string?> ResolveAsync(string wallet, CancellationToken ct)
    {
        var direct = await profiles.Profiles
            .Where(p => p.Ss58Address == wallet)
            .Select(p => p.X25519Key)
            .FirstOrDefaultAsync(ct);
        var key = direct ?? await profiles.WalletMigrations
            .Where(m => m.SolanaAddress == wallet)
            .Join(
                profiles.Profiles,
                m => m.Ss58Address,
                p => p.Ss58Address,
                (m, p) => p.X25519Key)
            .FirstOrDefaultAsync(ct);

        return X25519KeyCodec.TryNormalize(key, out var canonical) ? canonical : null;
    }

    /// <summary>Legacy hex shape accepted for backward-compatible inputs.</summary>
    public static bool IsHex32Key(string? key)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            return false;
        }
        var hex = key.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? key[2..] : key;
        return hex.Length == 64 && hex.All(Uri.IsHexDigit);
    }
}
