using System.Text.Json.Serialization;

namespace XcavateProfile.Client;

/// <summary>
/// Whether an account is cleared to act in a role.
/// </summary>
/// <remarks>
/// Absent (no entry for the role) is a third state and the default: never assessed. It is not the
/// same as <see cref="Revoked"/>, which records that clearance was granted and then withdrawn.
/// </remarks>
[JsonConverter(typeof(JsonStringEnumConverter<PermissionStatus>))]
public enum PermissionStatus
{
    [JsonStringEnumMemberName("compliant")]
    Compliant,

    [JsonStringEnumMemberName("revoked")]
    Revoked,
}

/// <summary>
/// Per-role clearance for a user profile. One entry per role in <see cref="UserRole"/>; null means
/// the role has never been assessed.
/// </summary>
/// <remarks>
/// Only an address in <c>ADMIN_ADDRESSES</c> may write this map. A profile's own signature is proof
/// of who the caller is, never of their compliance — letting callers set their own clearance would
/// make the field mean nothing. Non-admin writes leave the stored map untouched.
/// <para>
/// A record rather than a class: the database column is JSON, and EF change tracking needs value
/// equality to notice an edit inside the map.
/// </para>
/// </remarks>
public record UserPermissions
{
    [JsonPropertyName("regionalOperator")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public PermissionStatus? RegionalOperator { get; set; }

    [JsonPropertyName("investor")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public PermissionStatus? Investor { get; set; }

    [JsonPropertyName("developer")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public PermissionStatus? Developer { get; set; }

    [JsonPropertyName("lawyer")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public PermissionStatus? Lawyer { get; set; }

    [JsonPropertyName("agent")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public PermissionStatus? Agent { get; set; }

    [JsonPropertyName("spv")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public PermissionStatus? Spv { get; set; }
}

/// <summary>
/// Per-role clearance for a company. The same shape as <see cref="UserPermissions"/> minus the
/// roles only a natural person holds: a company is never an <c>investor</c> or an <c>spv</c>.
/// </summary>
/// <remarks>Admin-only to write, for the same reason as <see cref="UserPermissions"/>.</remarks>
public record CompanyPermissions
{
    [JsonPropertyName("regionalOperator")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public PermissionStatus? RegionalOperator { get; set; }

    [JsonPropertyName("developer")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public PermissionStatus? Developer { get; set; }

    [JsonPropertyName("lawyer")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public PermissionStatus? Lawyer { get; set; }

    [JsonPropertyName("agent")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public PermissionStatus? Agent { get; set; }
}
