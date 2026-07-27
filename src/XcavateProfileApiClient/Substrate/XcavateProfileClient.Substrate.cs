using Substrate.NetApi.Model.Types;
using XcavateProfileApiClient.Signing;

namespace XcavateProfile.Client;

/// <summary>
/// The sr25519 convenience overloads: they wrap the account in a <see cref="SubstrateRequestSigner"/>
/// and defer to the <see cref="IRequestSigner"/> methods, which hold all the actual behaviour.
/// </summary>
/// <remarks>
/// Excluded from the Solana-only package along with the rest of <c>Substrate/</c>. There, the
/// <see cref="IRequestSigner"/> overloads are the whole API.
/// </remarks>
public partial class XcavateProfileClient
{
    /// <summary>
    /// Create a new profile, authenticated with the account's sr25519 signature
    /// </summary>
    public Task<Profile> CreateProfileAsync(
        Profile profile, Account account, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(account);

        return CreateProfileAsync(profile, new SubstrateRequestSigner(account), cancellationToken);
    }

    /// <summary>
    /// Update an existing profile, authenticated with the account's sr25519 signature
    /// </summary>
    public Task<Profile> UpdateProfileAsync(
        string address, Profile profile, Account account, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(account);

        return UpdateProfileAsync(
            address, profile, new SubstrateRequestSigner(account), cancellationToken);
    }

    /// <summary>
    /// Delete a profile, authenticated with the account's sr25519 signature
    /// </summary>
    public Task DeleteProfileAsync(
        string address, Account account, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(account);

        return DeleteProfileAsync(address, new SubstrateRequestSigner(account), cancellationToken);
    }

    /// <summary>
    /// Upload a profile image, authenticated with the account's sr25519 signature
    /// </summary>
    public Task<string> UploadImageAsync(
        string address,
        Stream imageStream,
        string filename,
        Account account,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(account);

        return UploadImageAsync(
            address, imageStream, filename, new SubstrateRequestSigner(account), cancellationToken);
    }
}
