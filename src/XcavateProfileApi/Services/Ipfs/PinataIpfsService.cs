using System.Net.Http.Headers;
using System.Text.Json;

namespace XcavateProfileApi.Services.Ipfs;

/// <summary>
/// Pins files through Pinata's pinFileToIPFS endpoint, authenticated with the account JWT.
/// </summary>
public sealed class PinataIpfsService : IIpfsService
{
    public const string HttpClientName = "pinata-ipfs";

    /// <summary>Where the pinned files are read back from when IPFS_GATEWAY_URL is unset.</summary>
    public const string DefaultGatewayUrl = "https://gateway.pinata.cloud/ipfs/";

    private const string PinFileUrl = "https://api.pinata.cloud/pinning/pinFileToIPFS";

    private readonly HttpClient _http;
    private readonly string _jwt;
    private readonly string _gatewayBase;

    public PinataIpfsService(HttpClient http, string jwt, string gatewayBase)
    {
        _http = http;
        _jwt = jwt;
        _gatewayBase = gatewayBase.Trim().TrimEnd('/') + "/";
    }

    public async Task<string> PinFileAsync(
        Stream content, string fileName, string contentType, CancellationToken ct)
    {
        var fileContent = new StreamContent(content);
        fileContent.Headers.ContentType = new MediaTypeHeaderValue(contentType);

        using var request = new HttpRequestMessage(HttpMethod.Post, PinFileUrl);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _jwt);
        request.Content = new MultipartFormDataContent { { fileContent, "file", fileName } };

        using var response = await _http.SendAsync(request, ct);
        var body = await response.Content.ReadAsStringAsync(ct);

        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException(
                $"Pinata pinFileToIPFS failed: {(int)response.StatusCode} {response.ReasonPhrase}: {body}",
                inner: null,
                response.StatusCode);
        }

        using var json = JsonDocument.Parse(body);

        return json.RootElement.GetProperty("IpfsHash").GetString()
            ?? throw new HttpRequestException("Pinata pinFileToIPFS returned no IpfsHash");
    }

    public string BuildGatewayUrl(string cid) => _gatewayBase + cid;
}
