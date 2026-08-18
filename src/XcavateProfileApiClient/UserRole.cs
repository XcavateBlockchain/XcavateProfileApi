using System.Text.Json.Serialization;

namespace XcavateProfile.Client;

/// <summary>
/// A role a user declares on their profile. A profile may hold several at once — an account can be
/// both an investor and a developer.
/// </summary>
/// <remarks>
/// Roles are self-declared: holding one says what the user intends to do, not that they are cleared
/// to do it. Clearance is <see cref="UserPermissions"/>, which only an admin can set.
/// <para>
/// The wire values are pinned per member with <see cref="JsonStringEnumMemberNameAttribute"/>
/// rather than left to a naming policy. They are part of the signed body hash, so the serialized
/// spelling has to be identical on the client, in the server's model binder, and in the database
/// column — three places with three different <see cref="System.Text.Json.JsonSerializerOptions"/>.
/// Renaming a member without its attribute would silently invalidate signatures.
/// </para>
/// </remarks>
[JsonConverter(typeof(JsonStringEnumConverter<UserRole>))]
public enum UserRole
{
    [JsonStringEnumMemberName("investor")]
    Investor,

    [JsonStringEnumMemberName("developer")]
    Developer,

    [JsonStringEnumMemberName("lawyer")]
    Lawyer,

    [JsonStringEnumMemberName("agent")]
    Agent,

    [JsonStringEnumMemberName("spv")]
    Spv,

    [JsonStringEnumMemberName("regionalOperator")]
    RegionalOperator,
}
