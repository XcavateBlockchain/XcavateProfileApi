using Substrate.NetApi;
using Substrate.NetApi.Model.Types;
using XcavateProfile.Client;

namespace XcavateProfileApiClient.Signing;

/// <summary>
/// Signs with sr25519 and emits hex, exactly as the client did before.
/// </summary>
/// <remarks>
/// Lives under <c>Substrate/</c> rather than <c>Signing/</c> because that folder is what
/// XcavateProfileApiSolanaClient excludes; the namespace is unchanged.
/// </remarks>
public sealed class SubstrateRequestSigner(Account account) : IRequestSigner
{
    public string Address => account.Value;

    public Task<byte[]> SignAsync(string payload) => CryptoHelper.SignAsync(payload, account);

    public string EncodeSignature(byte[] signature) => Utils.Bytes2HexString(signature);
}
