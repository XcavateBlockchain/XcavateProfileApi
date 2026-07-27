using XcavateProfile.Client;

namespace XcavateProfileApiClient.Signing;

/// <summary>The three headers that carry a signature to the server.</summary>
public static class SignedRequestHeaders
{
    /// <summary>The signing address. Named for SS58 for compatibility; Solana addresses use it too.</summary>
    public const string Address = "X-SS58-Address";

    public const string Signature = "X-Signature";
    public const string Timestamp = "X-Timestamp";
}

/// <summary>
/// Builds and attaches the signature headers. Both the REST client and
/// <see cref="SigningHttpMessageHandler"/> go through here so the header names, the timestamp
/// format and the payload the timestamp is signed into can never drift apart between them.
/// </summary>
internal static class RequestSigning
{
    /// <summary>
    /// Signs <paramref name="body"/> for <paramref name="method"/> and <paramref name="path"/> and
    /// writes the headers onto <paramref name="request"/>.
    /// </summary>
    /// <remarks>
    /// <paramref name="path"/> must be the decoded path, matching the route values the server binds
    /// — not the percent-encoded form in <see cref="HttpRequestMessage.RequestUri"/>.
    /// </remarks>
    public static async Task ApplyAsync(
        HttpRequestMessage request,
        IRequestSigner signer,
        string method,
        string path,
        IPayloadBody body,
        DateTime timestamp)
    {
        var utc = timestamp.ToUniversalTime();

        var payload = CryptoHelper.ConstructPayload(method, path, body, utc);
        var signature = await signer.SignAsync(payload);

        // Remove first: a DelegatingHandler may be re-signing a request that already carries
        // headers from an earlier attempt, and Add would append rather than replace.
        request.Headers.Remove(SignedRequestHeaders.Address);
        request.Headers.Remove(SignedRequestHeaders.Signature);
        request.Headers.Remove(SignedRequestHeaders.Timestamp);

        request.Headers.Add(SignedRequestHeaders.Address, signer.Address);
        request.Headers.Add(SignedRequestHeaders.Signature, signer.EncodeSignature(signature));
        request.Headers.Add(SignedRequestHeaders.Timestamp, utc.ToString("o"));
    }
}
