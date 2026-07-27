using System.Text.Json;

namespace XcavateProfileApiClient;

/// <summary>
/// The single serializer configuration the SDK uses.
/// </summary>
/// <remarks>
/// Sharing one instance is not just about avoiding the per-call metadata cache that a fresh
/// <see cref="JsonSerializerOptions"/> forces. <c>Profile.Hash()</c> and the bytes actually POSTed
/// must serialize identically or the server's body hash will not match the signed one, and two
/// separately-maintained option sets are exactly how that drifts apart.
/// </remarks>
internal static class JsonDefaults
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };
}
