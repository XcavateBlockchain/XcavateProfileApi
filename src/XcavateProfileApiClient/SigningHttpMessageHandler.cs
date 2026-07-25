using Substrate.NetApi;
using Substrate.NetApi.Model.Types;
using XcavateProfile.Client;
using XcavateProfileApiClient.Signing;

namespace XcavateProfileApiClient;

/// <summary>
/// Signs outgoing GraphQL requests, mirroring the server's <c>GraphQLSignatureMiddleware</c>. The
/// signature covers the exact bytes being sent, so this must be the outermost handler that touches
/// the body. The scheme — sr25519 or Solana — comes from the supplied signer.
/// </summary>
/// <remarks>
/// Reads are public, so an unsigned client works for queries; supply a signer only when the client
/// needs to send mutations.
/// </remarks>
public sealed class SigningHttpMessageHandler : DelegatingHandler
{
    private const string GraphQLPath = "/graphql";

    private readonly IRequestSigner _signer;

    public SigningHttpMessageHandler(IRequestSigner signer) => _signer = signer;

    /// <summary>Convenience overload for the sr25519 path, which every existing caller uses.</summary>
    public SigningHttpMessageHandler(Account account) : this(new SubstrateRequestSigner(account))
    {
    }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (request.Content is not null && request.Method == HttpMethod.Post)
        {
            // Buffer the body first: the signature is over these bytes, and the content must still
            // be readable afterwards for the actual send.
            var body = await request.Content.ReadAsStringAsync(cancellationToken);
            var timestamp = DateTime.UtcNow;

            var payload = CryptoHelper.ConstructPayload(
                "POST", GraphQLPath, new RawBody(body), timestamp);

            var signature = await _signer.SignAsync(payload);

            request.Headers.Remove("X-SS58-Address");
            request.Headers.Remove("X-Signature");
            request.Headers.Remove("X-Timestamp");

            request.Headers.Add("X-SS58-Address", _signer.Address);
            request.Headers.Add("X-Signature", _signer.EncodeSignature(signature));
            request.Headers.Add("X-Timestamp", timestamp.ToString("o"));
        }

        return await base.SendAsync(request, cancellationToken);
    }

    /// <summary>Hashes the serialized request body through the shared payload-hashing seam.</summary>
    private sealed class RawBody(string body) : IPayloadBody
    {
        public string Hash() => Utils.Bytes2HexString(CryptoHelper.Hash(body));
    }
}
