using System.Text;
using System.Text.Json;
using XcavateProfile.Client;
using XcavateProfileApiClient;
using XcavateProfileApiClient.Signing;

namespace XcavateBuckets.Tests;

/// <summary>
/// Builds signed REST requests by hand, for the cases the SDK cannot express: a body edited after
/// signing, or a body from an older SDK version that no longer exists in the source tree.
/// </summary>
internal static class SignedRequests
{
    /// <summary>Restates the SDK's internal serializer options: these are the bytes on the wire.</summary>
    private static readonly JsonSerializerOptions WireOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

    public static string Json<T>(T body) => JsonSerializer.Serialize(body, WireOptions);

    public static Task<HttpRequestMessage> PostAsync(
        string path,
        IPayloadBody signedBody,
        IRequestSigner signer,
        string? postedJson = null,
        DateTime? timestamp = null) =>
        SendAsync(HttpMethod.Post, path, signedBody, signer, postedJson, timestamp);

    /// <summary>
    /// Signs <paramref name="signedBody"/> but posts <paramref name="postedJson"/>, which is how the
    /// tamper cases are expressed. They are the same bytes unless a test says otherwise.
    /// </summary>
    public static async Task<HttpRequestMessage> SendAsync(
        HttpMethod method,
        string path,
        IPayloadBody signedBody,
        IRequestSigner signer,
        string? postedJson = null,
        DateTime? timestamp = null)
    {
        var request = new HttpRequestMessage(method, path)
        {
            Content = new StringContent(
                postedJson ?? Json(signedBody), Encoding.UTF8, "application/json")
        };

        var utc = (timestamp ?? DateTime.UtcNow).ToUniversalTime();
        var payload = CryptoHelper.ConstructPayload(method.Method, path, signedBody, utc);
        var signature = await signer.SignAsync(payload);

        request.Headers.Add(SignedRequestHeaders.Address, signer.Address);
        request.Headers.Add(SignedRequestHeaders.Signature, signer.EncodeSignature(signature));
        request.Headers.Add(SignedRequestHeaders.Timestamp, utc.ToString("o"));

        return request;
    }

    /// <summary>
    /// A body that is already JSON. Lets a test sign bytes it wrote itself — the only way to send
    /// what a previously published SDK would have sent.
    /// </summary>
    public sealed record RawJson(string Json) : IPayloadBody
    {
        public string Hash() => CryptoHelper.HashHex(Json);
    }
}
