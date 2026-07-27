using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using XcavateProfileApiClient;
using XcavateProfileApiClient.Signing;

namespace XcavateProfile.Client;

/// <summary>
/// REST client for the profile API. Reads are public; every write takes an
/// <see cref="IRequestSigner"/>, which is what selects the signature scheme.
/// </summary>
/// <remarks>
/// Instances are safe to use from concurrent calls: the signature headers are attached to each
/// <see cref="HttpRequestMessage"/> rather than to the client's default headers, which two
/// in-flight writes would otherwise overwrite for each other.
/// </remarks>
public partial class XcavateProfileClient : IDisposable
{
    private readonly HttpClient _httpClient;
    private readonly Uri _baseAddress;
    private readonly bool _ownsHttpClient;
    private bool _disposed;

    public XcavateProfileClient(XcavateProfileClientOptions options)
        : this(options, new HttpClient(), ownsHttpClient: true)
    {
    }

    /// <summary>
    /// Uses a caller-supplied <see cref="HttpClient"/> — an <c>IHttpClientFactory</c> one, or a test
    /// double. The caller keeps ownership: <see cref="Dispose"/> will not dispose it.
    /// </summary>
    public XcavateProfileClient(XcavateProfileClientOptions options, HttpClient httpClient)
        : this(options, httpClient, ownsHttpClient: false)
    {
    }

    private XcavateProfileClient(
        XcavateProfileClientOptions options, HttpClient httpClient, bool ownsHttpClient)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(httpClient);

        _httpClient = httpClient;
        _ownsHttpClient = ownsHttpClient;

        // Kept here rather than assigned to httpClient.BaseAddress. That property cannot be set
        // once the client has sent a request, so writing to it would throw for exactly the
        // supplied-HttpClient case the second constructor exists to support — and it would be a
        // mutation of an object the caller owns either way.
        //
        // The trailing slash is load-bearing: without it, resolving a relative URI against a base
        // address that has a path ("https://host/profile-api") discards the last segment.
        _baseAddress = new Uri(options.ApiUrl.EndsWith('/') ? options.ApiUrl : options.ApiUrl + "/");
    }

    /// <summary>
    /// Get all profiles
    /// </summary>
    public async Task<List<Profile>> GetProfilesAsync(CancellationToken cancellationToken = default)
    {
        using var response = await _httpClient.GetAsync(
            Resolve(ApiPath.Of("api", "profiles")), cancellationToken);

        await EnsureSuccessAsync(response, cancellationToken);

        return await response.Content.ReadFromJsonAsync<List<Profile>>(
            JsonDefaults.Options, cancellationToken) ?? [];
    }

    /// <summary>
    /// Get a profile by SS58 or Solana address. Null when there is none.
    /// </summary>
    public Task<Profile?> GetProfileAsync(
        string address, CancellationToken cancellationToken = default) =>
        GetProfileOrNullAsync(ApiPath.Of("api", "profiles", address), cancellationToken);

    /// <summary>
    /// Get a profile by nickname. Null when there is none.
    /// </summary>
    public Task<Profile?> GetProfileByNicknameAsync(
        string nickname, CancellationToken cancellationToken = default) =>
        GetProfileOrNullAsync(ApiPath.Of("api", "profiles", "nickname", nickname), cancellationToken);

    private async Task<Profile?> GetProfileOrNullAsync(
        ApiPath path, CancellationToken cancellationToken)
    {
        using var response = await _httpClient.GetAsync(Resolve(path), cancellationToken);

        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }

        await EnsureSuccessAsync(response, cancellationToken);

        return await response.Content.ReadFromJsonAsync<Profile>(
            JsonDefaults.Options, cancellationToken);
    }

    /// <summary>
    /// Create a new profile using any signature scheme
    /// </summary>
    public async Task<Profile> CreateProfileAsync(
        Profile profile, IRequestSigner signer, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(signer);

        // Not disposed here: SendSignedAsync attaches it to the request, which owns it.
        var content = JsonBody(profile);
        using var response = await SendSignedAsync(
            HttpMethod.Post,
            ApiPath.Of("api", "profiles"),
            content,
            profile,
            signer,
            cancellationToken);

        return await ReadProfileAsync(response, cancellationToken);
    }

    /// <summary>
    /// Update an existing profile using any signature scheme
    /// </summary>
    public async Task<Profile> UpdateProfileAsync(
        string address,
        Profile profile,
        IRequestSigner signer,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(signer);

        // Not disposed here: SendSignedAsync attaches it to the request, which owns it.
        var content = JsonBody(profile);
        using var response = await SendSignedAsync(
            HttpMethod.Put,
            ApiPath.Of("api", "profiles", address),
            content,
            profile,
            signer,
            cancellationToken);

        return await ReadProfileAsync(response, cancellationToken);
    }

    /// <summary>
    /// Delete a profile using any signature scheme
    /// </summary>
    public async Task DeleteProfileAsync(
        string address, IRequestSigner signer, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(signer);

        using var response = await SendSignedAsync(
            HttpMethod.Delete,
            ApiPath.Of("api", "profiles", address),
            content: null,
            EmptyPayloadBody.Instance,
            signer,
            cancellationToken);

        await EnsureSuccessAsync(response, cancellationToken);
    }

    /// <summary>
    /// Upload a profile image using any signature scheme. Returns the stored image's URL.
    /// </summary>
    public async Task<string> UploadImageAsync(
        string address,
        Stream imageStream,
        string filename,
        IRequestSigner signer,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(imageStream);
        ArgumentNullException.ThrowIfNull(signer);

        var imageContent = new StreamContent(imageStream);
        imageContent.Headers.ContentType = new MediaTypeHeaderValue(GetImageContentType(filename));

        var content = new MultipartFormDataContent { { imageContent, "image", filename } };

        // The server hashes an empty body for multipart uploads, so the client must too.
        using var response = await SendSignedAsync(
            HttpMethod.Post,
            ApiPath.Of("api", "profiles", address, "image"),
            content,
            EmptyPayloadBody.Instance,
            signer,
            cancellationToken);

        await EnsureSuccessAsync(response, cancellationToken);

        var responseContent = await response.Content.ReadAsStringAsync(cancellationToken);

        // ASP.NET Core serves bare strings as text/plain; only parse JSON when the
        // server actually sent JSON
        return response.Content.Headers.ContentType?.MediaType == "application/json"
            ? JsonSerializer.Deserialize<string>(responseContent, JsonDefaults.Options) ?? ""
            : responseContent;
    }

    /// <summary>
    /// Signs the request and sends it. The signature covers the path as the server binds it — the
    /// decoded form — while the URI carries the percent-encoded one.
    /// </summary>
    private async Task<HttpResponseMessage> SendSignedAsync(
        HttpMethod method,
        ApiPath path,
        HttpContent? content,
        IPayloadBody body,
        IRequestSigner signer,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(method, Resolve(path)) { Content = content };

        await RequestSigning.ApplyAsync(
            request, signer, method.Method, path.Signed, body, DateTime.UtcNow);

        return await _httpClient.SendAsync(request, cancellationToken);
    }

    private Uri Resolve(ApiPath path) => new(_baseAddress, path.Relative);

    /// <summary>
    /// Serializes through the same options <see cref="Profile.Hash"/> uses, so the bytes sent are
    /// the bytes that were hashed into the signature.
    /// </summary>
    private static StringContent JsonBody(Profile profile) =>
        new(JsonSerializer.Serialize(profile, JsonDefaults.Options), Encoding.UTF8, "application/json");

    private static async Task<Profile> ReadProfileAsync(
        HttpResponseMessage response, CancellationToken cancellationToken)
    {
        await EnsureSuccessAsync(response, cancellationToken);

        return await response.Content.ReadFromJsonAsync<Profile>(JsonDefaults.Options, cancellationToken)
            ?? throw new InvalidOperationException(
                "The server reported success but returned no profile for "
                    + $"{response.RequestMessage?.RequestUri}.");
    }

    /// <summary>
    /// Like <c>EnsureSuccessStatusCode</c>, but keeps the server's error text. The API explains
    /// every 401 and 403 in the body, and the built-in version discards that explanation.
    /// </summary>
    private static async Task EnsureSuccessAsync(
        HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        var body = await response.Content.ReadAsStringAsync(cancellationToken);

        throw new HttpRequestException(
            $"{(int)response.StatusCode} {response.ReasonPhrase} from "
                + $"{response.RequestMessage?.Method} {response.RequestMessage?.RequestUri}"
                + (string.IsNullOrWhiteSpace(body) ? "" : $": {body}"),
            inner: null,
            response.StatusCode);
    }

    private static string GetImageContentType(string filename) =>
        Path.GetExtension(filename).ToLowerInvariant() switch
        {
            ".jpg" or ".jpeg" => "image/jpeg",
            ".png" => "image/png",
            ".gif" => "image/gif",
            ".webp" => "image/webp",
            ".bmp" => "image/bmp",
            ".svg" => "image/svg+xml",
            _ => "application/octet-stream"
        };

    /// <summary>
    /// A path in both the forms a signed request needs: <see cref="Signed"/> goes into the payload,
    /// <see cref="Relative"/> goes into the URI.
    /// </summary>
    /// <remarks>
    /// They differ whenever a segment contains URI-special characters. Addresses are base58 so never
    /// do, but a nickname is free text — and the server signs over the decoded route value, so
    /// escaping the payload path too would break any lookup containing a space or a slash.
    /// </remarks>
    private readonly record struct ApiPath(string Signed, Uri Relative)
    {
        public static ApiPath Of(params string[] segments) =>
            new(
                "/" + string.Join('/', segments),
                new Uri(string.Join('/', segments.Select(Uri.EscapeDataString)), UriKind.Relative));
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        if (_ownsHttpClient)
        {
            _httpClient.Dispose();
        }

        GC.SuppressFinalize(this);
    }
}
