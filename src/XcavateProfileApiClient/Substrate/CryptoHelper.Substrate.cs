using Substrate.NET.Schnorrkel;
using Substrate.NetApi;
using Substrate.NetApi.Model.Types;

namespace XcavateProfile.Client;

/// <summary>
/// The sr25519 half of <see cref="CryptoHelper"/>. Everything in the <c>Substrate/</c> folder is
/// excluded from the Solana-only package, which is what keeps Substrate.NET.API — and the
/// StreamJsonRpc, Serilog, Newtonsoft.Json and MessagePack chain behind it — out of that package's
/// dependency graph.
/// </summary>
public static partial class CryptoHelper
{
    /// <summary>
    /// Sign a payload string using the sr25519 signature scheme with the provided account's
    /// private key. The signature is over the Blake2b-128 digest, not the payload text.
    /// </summary>
    /// <param name="input">The input string to sign</param>
    /// <param name="account">The account instance containing the keypair</param>
    /// <returns>The signature as a byte array</returns>
    public static Task<byte[]> SignAsync(string input, IAccount account) => account.SignAsync(Hash(input));

    /// <summary>
    /// Verify an sr25519 signature over the digest of <paramref name="input"/>.
    /// </summary>
    /// <param name="input">The original message</param>
    /// <param name="signature">The signature as a byte array</param>
    /// <param name="address">The address associated with the public key</param>
    /// <returns>True if the signature is valid</returns>
    public static bool VerifySignature(string input, byte[] signature, string address) =>
        VerifySignature(Hash(input), signature, address);

    /// <summary>Verify an sr25519 signature over exactly <paramref name="input"/>.</summary>
    public static bool VerifySignature(byte[] input, byte[] signature, string address) =>
        Sr25519v091.Verify(signature, Utils.GetPublicKeyFrom(address), input);
}
