using System.Text;
using Solnet.Wallet.Utilities;

namespace XcavateProfileApiClient.Signing;

/// <summary>
/// Signs with ed25519 over the raw payload string and emits base58 — the same bytes and the same
/// encoding a browser wallet produces via <c>signMessage</c> plus <c>bs58.encode</c>, so this path
/// and a real frontend are verified by the same server code.
/// </summary>
public sealed class SolanaRequestSigner(Solnet.Wallet.Account account) : IRequestSigner
{
    public string Address => account.PublicKey.Key;

    public Task<byte[]> SignAsync(string payload) =>
        Task.FromResult(account.Sign(Encoding.UTF8.GetBytes(payload)));

    public string EncodeSignature(byte[] signature) => Encoders.Base58.EncodeData(signature);
}
