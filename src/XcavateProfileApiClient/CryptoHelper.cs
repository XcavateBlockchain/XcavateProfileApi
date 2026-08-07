using Blake2Core;
using System.Text;
using XcavateProfileApiClient;

namespace XcavateProfile.Client;

/// <summary>
/// The hashing and payload-construction seam shared by the client, the server and both signature
/// schemes. Chain-agnostic on purpose: the sr25519 signing and verification helpers live in the
/// partial under <c>Substrate/</c>, which the Solana-only package does not compile.
/// </summary>
public static partial class CryptoHelper
{
    /// <summary>
    /// Blake2b-128. Matches <c>Substrate.NetApi.HashExtension.Blake2(bytes, 128)</c>, which is the
    /// same Blake2Core implementation reached directly rather than through Substrate.NET.API — that
    /// indirection is the only thing the Solana package would otherwise need the whole Substrate
    /// stack for.
    /// </summary>
    private const int HashSizeInBits = 128;

    /// <summary>Compute the Blake2b-128 hash of a string.</summary>
    public static byte[] Hash(string input) =>
        Blake2B.ComputeHash(
            Encoding.UTF8.GetBytes(input),
            new Blake2BConfig { OutputSizeInBits = HashSizeInBits });

    /// <summary>
    /// The hash as the <c>0x</c>-prefixed uppercase hex the payload format expects. Use this when
    /// implementing <see cref="IPayloadBody"/> — hand-rolling the encoding is how a body hash ends
    /// up subtly different from the server's.
    /// </summary>
    /// <remarks>
    /// <c>Hex</c> names the encoding of the <em>result</em>, not a requirement on
    /// <paramref name="input"/>. The input is arbitrary text — a serialized JSON body, a GraphQL
    /// document — hashed as its UTF-8 bytes, never hex-decoded first. Making it decode hex would
    /// invalidate every signature the deployed server has accepted.
    /// </remarks>
    public static string HashHex(string input) => Hex.ToPrefixedString(Hash(input));

    /// <summary>
    /// Construct the signed payload string for authentication.
    /// Format: <c>method:path:body_hash:timestamp</c>.
    /// </summary>
    /// <remarks>
    /// <paramref name="path"/> must be the decoded path, matching the route value the server binds
    /// — not the percent-encoded form that goes into the request URI.
    /// </remarks>
    public static string ConstructPayload(string method, string path, IPayloadBody body, DateTime timestamp) =>
        $"{method}:{path}:{body.Hash()}:{timestamp.ToUniversalTime():o}";
}
