using Substrate.NET.Schnorrkel;
using Substrate.NetApi;
using Substrate.NetApi.Model.Types;
using System.Text;
using XcavateProfileApiClient;

namespace XcavateProfile.Client;

/// <summary>
/// Helper class for cryptographic operations such as hashing, signing, and verifying signatures
/// </summary>
public static class CryptoHelper
{
    /// <summary>
    /// Compute Blake2b hash of a string
    /// </summary>
    public static byte[] Hash(string input)
    {
        var encodedInput = Encoding.UTF8.GetBytes(input);

        var hash = HashExtension.Blake2(encodedInput, 128);

        return hash;
    }

    /// <summary>
    /// Sign a payload string using the Sr25519 signature scheme with the provided Account's private key
    /// </summary>
    /// <param name="input">The input string to sign</param>
    /// <param name="account">The Account instance containing the keypair</param>
    /// <returns>The signature as a byte array</returns>
    public static async Task<byte[]> SignAsync(string input, IAccount account)
    {
        var hash = Hash(input);

        var signature = await account.SignAsync(hash);

        return signature;
    }

    /// <summary>
    /// Verify a signature using Sr25519 signature scheme
    /// </summary>
    /// <param name="input">The original message</param>
    /// <param name="signature">The signature as a byte array</param>
    /// <param name="address">The address associated with the public key</param>
    /// <returns>True if the signature is valid</returns>
    public static bool VerifySignature(string input, byte[] signature, string address)
    {
        var hash = Hash(input);

        return VerifySignature(hash, signature, address);
    }

    public static bool VerifySignature(byte[] input, byte[] signature, string address)
    {
        var publicKey = Substrate.NetApi.Utils.GetPublicKeyFrom(address);

        var verification = Sr25519v091.Verify(signature, publicKey, input);

        return verification;
    }

    /// <summary>
    /// Construct the signed payload string for authentication
    /// Format: method:path:body_hash:timestamp
    /// </summary>
    public static string ConstructPayload(string method, string path, IPayloadBody body, DateTime timestamp)
    {
        return $"{method}:{path}:{body.Hash()}:{timestamp.ToUniversalTime():o}";
    }
}
