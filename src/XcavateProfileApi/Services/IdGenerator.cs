using System.Buffers.Text;
using System.Security.Cryptography;

namespace XcavateProfileApi.Services;

/// <summary>
/// Generates the prefixed random ids the platform uses for records that are not keyed by a wallet
/// address — currently companies.
/// </summary>
/// <remarks>
/// The shape is <c>{prefix}_{22 url-safe characters}</c>, e.g. <c>company_3kQ8ZrW…</c>. The prefix
/// makes an id self-describing in a log line or a URL; the random part is 128 bits from
/// <see cref="RandomNumberGenerator"/>, so ids are unguessable and need no coordination to stay
/// unique. Sequential ids would leak how many companies exist and let anyone enumerate them.
/// </remarks>
public static class IdGenerator
{
    /// <summary>Bytes of randomness per id. 16 bytes is 128 bits — collision-free in practice.</summary>
    private const int EntropyBytes = 16;

    public static string Generate(string prefix)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(prefix);

        // Base64Url rather than plain Base64: the id travels in a route segment, where '+' and '/'
        // would need escaping and '=' padding is noise. 16 bytes encode to 22 characters.
        var random = Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(EntropyBytes));

        return $"{prefix}_{random}";
    }
}
