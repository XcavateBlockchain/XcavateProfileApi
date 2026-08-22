using Microsoft.EntityFrameworkCore;
using XcavateProfile.Client;

namespace XcavateProfileApi.Data;

/// <summary>
/// Nicknames are case-insensitive: <c>tester</c> and <c>Tester</c> are one and the same name, so one
/// of them can be taken and either spelling finds the profile that owns it.
/// </summary>
/// <remarks>
/// The case the user typed is what is stored and returned — only the comparison ignores it. The
/// comparison key is a lower-cased copy kept in a shadow column, <see cref="NormalizedProperty"/>,
/// which <see cref="ProfileDbContext"/> fills in on every save and a unique index covers. Folding
/// case in .NET rather than in SQL is what keeps the answer identical on PostgreSQL and on the
/// SQLite the tests run against, whose <c>lower()</c> is ASCII-only.
/// <para>
/// The flip side of computing the key in .NET is that it is the application, not the database, that
/// derives it: a row inserted by hand in psql without a key would sit outside the unique index. Every
/// write here goes through a save, so the index still does its real job — catching two requests that
/// claim the same name at once, which the check in the controller cannot.
/// </para>
/// </remarks>
public static class Nicknames
{
    /// <summary>
    /// The shadow property holding the comparison key. Shadow rather than a property on
    /// <see cref="Profile"/> because the profile is the published SDK's model: the key is the
    /// server's bookkeeping and has no business on the wire.
    /// </summary>
    public const string NormalizedProperty = "NicknameNormalized";

    /// <summary>
    /// The comparison key for a nickname, or null when there is no nickname to compare. Blank is
    /// null so that the unique index does not treat two profiles without a nickname as a clash —
    /// SQL leaves NULLs out of a unique index, but not empty strings.
    /// </summary>
    public static string? Normalize(string? nickname) =>
        string.IsNullOrWhiteSpace(nickname) ? null : nickname.ToLowerInvariant();

    /// <summary>Whether two nicknames are the same name, ignoring case.</summary>
    public static bool AreSame(string? left, string? right) => Normalize(left) == Normalize(right);

    /// <summary>
    /// The profiles whose nickname is <paramref name="nickname"/>, whatever case either is written
    /// in. Matches nothing for a blank nickname: not having one is not a way of sharing one.
    /// </summary>
    public static IQueryable<Profile> WithNickname(this IQueryable<Profile> profiles, string? nickname)
    {
        var normalized = Normalize(nickname);

        return normalized is null
            ? profiles.Where(_ => false)
            : profiles.Where(p => EF.Property<string>(p, NormalizedProperty) == normalized);
    }
}
