using System.Text.Json;
using System.Text.Json.Serialization;
using XcavateProfileApiClient;

namespace XcavateProfile.Client;

/// <summary>
/// A company registered by a user: who owns it now, which wallet created it, and the per-role
/// clearance an admin has recorded for it.
/// </summary>
/// <remarks>
/// <see cref="UserId"/> and <see cref="CompanyWalletAddress"/> are both wallet addresses and are
/// equal at creation, but they answer different questions afterwards. <see cref="UserId"/> is the
/// current owner and may be reassigned — that is how a company changes hands.
/// <see cref="CompanyWalletAddress"/> is fixed for the lifetime of the record and always names the
/// wallet that created it.
/// <para>
/// One user may own any number of companies; <c>GET /api/companies/user/{userId}</c> lists them.
/// </para>
/// </remarks>
public class Company : IPayloadBody
{
    /// <summary>
    /// The company's id, of the form <c>company_…</c>. Server-assigned on create: a value supplied
    /// in a create body is ignored, so leave it null.
    /// </summary>
    [JsonPropertyName("companyId")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? CompanyId { get; set; }

    /// <summary>
    /// The wallet address of the user who owns the company. Equal to that user's
    /// <see cref="Profile.UserId"/>, though no profile has to exist for the address.
    /// </summary>
    [JsonPropertyName("userId")]
    public required string UserId { get; set; }

    /// <summary>
    /// The wallet address that created the company. Immutable, and unlike <see cref="UserId"/> it
    /// still names the creator after the company changes owner.
    /// </summary>
    [JsonPropertyName("companyWalletAddress")]
    public required string CompanyWalletAddress { get; set; }

    /// <summary>The registered company name.</summary>
    [JsonPropertyName("name")]
    public required string Name { get; set; }

    /// <summary>Contact email. Validated for shape, never verified by a round-trip.</summary>
    [JsonPropertyName("email")]
    public required string Email { get; set; }

    /// <summary>URL of the uploaded logo; set by the logo endpoint, not by the caller.</summary>
    [JsonPropertyName("logo")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Logo { get; set; }

    [JsonPropertyName("website")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Website { get; set; }

    /// <summary>What the company does, as one free-text block.</summary>
    [JsonPropertyName("summary")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Summary { get; set; }

    /// <summary>Registered address, as one free-text block.</summary>
    [JsonPropertyName("address")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Address { get; set; }

    /// <summary>
    /// Per-role clearance for the company. Admin-only to write: a non-admin write leaves whatever is
    /// stored in place, whatever this property says.
    /// </summary>
    [JsonPropertyName("permission")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public CompanyPermissions? Permission { get; set; }

    /// <summary>When the company was first stored. Server-assigned; ignored on write.</summary>
    [JsonPropertyName("createdAt")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public DateTime? CreatedAt { get; set; }

    /// <summary>When the company last changed. Server-assigned; ignored on write.</summary>
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
