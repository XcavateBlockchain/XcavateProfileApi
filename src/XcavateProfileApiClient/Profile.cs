using System.Text.Json;
using System.Text.Json.Serialization;
using XcavateProfileApiClient;

namespace XcavateProfile.Client;

/// <summary>
/// A user profile: the account that owns it, the contact and presentation details, the roles the
/// user declares, and the per-role clearance an admin has recorded for them.
/// </summary>
/// <remarks>
/// <see cref="Ss58Address"/> is the wallet address and the primary key, and <see cref="UserId"/> is
/// the same value under the name the rest of the platform uses — there is no separate user
/// identifier to look up.
/// <para>
/// Every property beyond the original five is omitted from the JSON when null. That is what keeps
/// the signed body hash stable for callers on an older SDK: a client that has never heard of
/// <c>email</c> sends exactly the bytes it used to, and the server — which re-serializes the body
/// it bound before hashing it — produces exactly the same bytes back. Dropping the
/// <see cref="JsonIgnoreAttribute"/> conditions would emit <c>"email":null</c> on the server side
/// only, and every write from a previously published client would start failing with a 401.
/// </para>
/// </remarks>
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
    /// The user's id, always equal to <see cref="Ss58Address"/>. Server-assigned: leave it null when
    /// writing, and the server fills it in; send it and it must match the wallet address or the
    /// write is refused.
    /// </summary>
    [JsonPropertyName("userId")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? UserId { get; set; }

    /// <summary>The user's full name.</summary>
    [JsonPropertyName("name")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Name { get; set; }

    /// <summary>Contact email. Validated for shape, never verified by a round-trip.</summary>
    [JsonPropertyName("email")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Email { get; set; }

    [JsonPropertyName("phone")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Phone { get; set; }

    /// <summary>Postal address, as one free-text block.</summary>
    [JsonPropertyName("address")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Address { get; set; }

    /// <summary>Job title.</summary>
    [JsonPropertyName("title")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Title { get; set; }

    /// <summary>Professional background — the longer form of <see cref="Title"/>.</summary>
    [JsonPropertyName("background")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Background { get; set; }

    /// <summary>
    /// The roles the user declares. Self-assigned and freely editable by the owner; duplicates are
    /// collapsed on save, so the stored list is a set.
    /// </summary>
    [JsonPropertyName("roles")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<UserRole>? Roles { get; set; }

    /// <summary>
    /// Per-role clearance. Admin-only to write: a non-admin write leaves whatever is stored in
    /// place, whatever this property says.
    /// </summary>
    [JsonPropertyName("permission")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public UserPermissions? Permission { get; set; }

    /// <summary>When the profile was first stored. Server-assigned; ignored on write.</summary>
    [JsonPropertyName("createdAt")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public DateTime? CreatedAt { get; set; }

    /// <summary>When the profile last changed. Server-assigned; ignored on write.</summary>
    [JsonPropertyName("updatedAt")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public DateTime? UpdatedAt { get; set; }

    /// <summary>
    /// The body hash for the signed payload. Every property carries an explicit
    /// <see cref="JsonPropertyNameAttribute"/>, so the serialized form — and therefore this hash —
    /// is fixed by declaration order and does not shift with the naming policy.
    /// </summary>
    public string Hash() => CryptoHelper.HashHex(JsonSerializer.Serialize(this, JsonDefaults.Options));
}
