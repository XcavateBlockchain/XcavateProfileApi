using System.Net.Mail;
using XcavateProfileApiClient.Signing;

namespace XcavateProfileApi.Controllers;

/// <summary>
/// The body checks the profile and company endpoints share: is this a wallet address, is this an
/// email, does this string fit its column.
/// </summary>
/// <remarks>
/// Length checks live here rather than being left to the database because a column overflow surfaces
/// as a 500 from <c>SaveChanges</c>, which tells the caller nothing about which field was too long.
/// The limits must stay in step with the <c>HasMaxLength</c> calls in
/// <see cref="Data.ProfileDbContext"/>.
/// </remarks>
internal static class FieldValidation
{
    /// <summary>
    /// Used purely as address-format validators (<see cref="ISignatureScheme.CanVerify"/>), not for
    /// verification — that stays in <see cref="Middleware.ISignatureValidator"/>. Reusing the schemes
    /// is what keeps "what is a wallet address" defined in one place for both paths.
    /// </summary>
    private static readonly Sr25519SignatureScheme Sr25519Format = new();
    private static readonly SolanaSignatureScheme SolanaFormat = new();

    /// <summary>True for a checksummed SS58 address or a base58 Solana public key.</summary>
    public static bool IsWalletAddress(string? value) =>
        !string.IsNullOrWhiteSpace(value)
        && (Sr25519Format.CanVerify(value) || SolanaFormat.CanVerify(value));

    /// <summary>
    /// A shape check, not a deliverability one. <see cref="MailAddress"/> alone would accept
    /// <c>Some One &lt;a@b.com&gt;</c>; requiring the parse to round-trip keeps it to a bare address.
    /// </summary>
    public static bool IsEmail(string value) =>
        MailAddress.TryCreate(value, out var parsed)
        && string.Equals(parsed.Address, value, StringComparison.Ordinal)
        && parsed.Host.Contains('.', StringComparison.Ordinal);

    /// <summary>The refusal message for a too-long field, or null when it fits.</summary>
    public static string? TooLong(string field, string? value, int maxLength) =>
        value is not null && value.Length > maxLength
            ? $"{field} must be at most {maxLength} characters"
            : null;

    /// <summary>The first failure among <paramref name="checks"/>, or null when they all pass.</summary>
    public static string? FirstFailure(params string?[] checks) =>
        checks.FirstOrDefault(c => c is not null);
}
