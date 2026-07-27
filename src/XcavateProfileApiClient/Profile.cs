using System.Text.Json;
using System.Text.Json.Serialization;
using XcavateProfileApiClient;

namespace XcavateProfile.Client;

/// <summary>
/// Represents a user profile with properties for address, nickname, bio, profile picture, and encryption key
/// </summary>
public class Profile : IPayloadBody
{
    /// <summary>The account that owns the profile: an SS58 or a Solana base58 address.</summary>
    [JsonPropertyName("ss58address")]
    public required string Ss58Address { get; set; }

    [JsonPropertyName("nickname")]
    public string? Nickname { get; set; }

    [JsonPropertyName("bio")]
    public string? Bio { get; set; }

    /// <summary>URL of the uploaded picture; set by the image endpoint, not by the caller.</summary>
    [JsonPropertyName("profilePicture")]
    public string? ProfilePicture { get; set; }

    [JsonPropertyName("x25519Key")]
    public required string X25519Key { get; set; }

    /// <summary>
    /// The body hash for the signed payload. Every property carries an explicit
    /// <see cref="JsonPropertyNameAttribute"/>, so the serialized form — and therefore this hash —
    /// is fixed by declaration order and does not shift with the naming policy.
    /// </summary>
    public string Hash() => CryptoHelper.HashHex(JsonSerializer.Serialize(this, JsonDefaults.Options));
}
