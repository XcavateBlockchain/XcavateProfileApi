using Substrate.NetApi.Model.Types;
using System.Text;
using System.Text.Json;
using XcavateProfileApiClient;
using XcavateProfileApiClient.Signing;

namespace XcavateProfile.Client;

public class XcavateProfileClient : IDisposable
{
    private readonly HttpClient _httpClient;
    private readonly XcavateProfileClientOptions _options;
    private readonly JsonSerializerOptions _jsonOptions;

    public XcavateProfileClient(XcavateProfileClientOptions options)
    {
        _options = options;
        _httpClient = new HttpClient();
        _httpClient.BaseAddress = new Uri(_options.ApiUrl);

        _jsonOptions = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = true
        };
    }

    /// <summary>
    /// Get all profiles
    /// </summary>
    public async Task<List<Profile>> GetProfilesAsync()
    {
        var response = await _httpClient.GetAsync("api/profiles");
        response.EnsureSuccessStatusCode();

        var content = await response.Content.ReadAsStringAsync();
        return JsonSerializer.Deserialize<List<Profile>>(content, _jsonOptions) ?? new List<Profile>();
    }

    /// <summary>
    /// Get a profile by SS58 address
    /// </summary>
    public async Task<Profile?> GetProfileAsync(string ss58address)
    {
        var response = await _httpClient.GetAsync($"api/profiles/{ss58address}");

        if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
            return null;

        response.EnsureSuccessStatusCode();

        var content = await response.Content.ReadAsStringAsync();
        return JsonSerializer.Deserialize<Profile>(content, _jsonOptions);
    }

    /// <summary>
    /// Get a profile by nickname
    /// </summary>
    public async Task<Profile?> GetProfileByNicknameAsync(string nickname)
    {
        var response = await _httpClient.GetAsync($"api/profiles/nickname/{nickname}");

        if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
            return null;

        response.EnsureSuccessStatusCode();

        var content = await response.Content.ReadAsStringAsync();
        return JsonSerializer.Deserialize<Profile>(content, _jsonOptions);
    }

    /// <summary>
    /// Signs the payload and installs the three auth headers. Kept in one place so every verb
    /// agrees on the header names and the timestamp format.
    /// </summary>
    private async Task SignRequestAsync(
        string method, string path, IPayloadBody body, IRequestSigner signer, DateTime timestamp)
    {
        var payload = CryptoHelper.ConstructPayload(method, path, body, timestamp);
        var signature = await signer.SignAsync(payload);

        _httpClient.DefaultRequestHeaders.Clear();
        _httpClient.DefaultRequestHeaders.Add("X-SS58-Address", signer.Address);
        _httpClient.DefaultRequestHeaders.Add("X-Signature", signer.EncodeSignature(signature));
        _httpClient.DefaultRequestHeaders.Add("X-Timestamp", timestamp.ToUniversalTime().ToString("o"));
    }

    /// <summary>
    /// Create a new profile, authenticated with the caller's signature
    /// </summary>
    public Task<Profile> CreateProfileAsync(Profile profile, Account account)
    {
        if (account == null)
            throw new InvalidOperationException("Account is required for profile creation");

        return CreateProfileAsync(profile, new SubstrateRequestSigner(account));
    }

    /// <summary>
    /// Create a new profile using any signature scheme
    /// </summary>
    public async Task<Profile> CreateProfileAsync(Profile profile, IRequestSigner signer)
    {
        ArgumentNullException.ThrowIfNull(signer);

        var bodyJson = JsonSerializer.Serialize(profile, _jsonOptions);

        await SignRequestAsync("POST", "/api/profiles", profile, signer, DateTime.UtcNow);

        var content = new StringContent(bodyJson, Encoding.UTF8, "application/json");

        var response = await _httpClient.PostAsync("api/profiles", content);
        response.EnsureSuccessStatusCode();

        var responseContent = await response.Content.ReadAsStringAsync();
        return JsonSerializer.Deserialize<Profile>(responseContent, _jsonOptions) ?? throw new InvalidOperationException("Failed to create profile");
    }

    /// <summary>
    /// Update an existing profile, authenticated with the caller's signature
    /// </summary>
    public Task<Profile> UpdateProfileAsync(string ss58address, Profile profile, Account? account = null)
    {
        if (account == null)
            throw new InvalidOperationException("Account is required for profile update");

        return UpdateProfileAsync(ss58address, profile, new SubstrateRequestSigner(account));
    }

    /// <summary>
    /// Update an existing profile using any signature scheme
    /// </summary>
    public async Task<Profile> UpdateProfileAsync(string ss58address, Profile profile, IRequestSigner signer)
    {
        ArgumentNullException.ThrowIfNull(signer);

        var bodyJson = JsonSerializer.Serialize(profile, _jsonOptions);

        await SignRequestAsync(
            "PUT", $"/api/profiles/{ss58address}", profile, signer, DateTime.UtcNow);

        var content = new StringContent(bodyJson, Encoding.UTF8, "application/json");

        var response = await _httpClient.PutAsync($"api/profiles/{ss58address}", content);
        response.EnsureSuccessStatusCode();

        var responseContent = await response.Content.ReadAsStringAsync();
        return JsonSerializer.Deserialize<Profile>(responseContent, _jsonOptions) ?? throw new InvalidOperationException("Failed to update profile");
    }

    /// <summary>
    /// Delete a profile, authenticated with the caller's signature
    /// </summary>
    public Task DeleteProfileAsync(string ss58address, Account? account = null)
    {
        if (account == null)
            throw new InvalidOperationException("Account is required for profile deletion");

        return DeleteProfileAsync(ss58address, new SubstrateRequestSigner(account));
    }

    /// <summary>
    /// Delete a profile using any signature scheme
    /// </summary>
    public async Task DeleteProfileAsync(string ss58address, IRequestSigner signer)
    {
        ArgumentNullException.ThrowIfNull(signer);

        await SignRequestAsync(
            "DELETE", $"/api/profiles/{ss58address}", new EmptyPayloadBody(), signer, DateTime.UtcNow);

        var response = await _httpClient.DeleteAsync($"api/profiles/{ss58address}");
        response.EnsureSuccessStatusCode();
    }

    /// <summary>
    /// Upload a profile image, authenticated with the caller's signature
    /// </summary>
    public Task<string> UploadImageAsync(string ss58address, Stream imageStream, string filename, Account? account = null)
    {
        if (account == null)
            throw new InvalidOperationException("Account is required for image upload");

        return UploadImageAsync(ss58address, imageStream, filename, new SubstrateRequestSigner(account));
    }

    /// <summary>
    /// Upload a profile image using any signature scheme
    /// </summary>
    public async Task<string> UploadImageAsync(string ss58address, Stream imageStream, string filename, IRequestSigner signer)
    {
        ArgumentNullException.ThrowIfNull(signer);

        // Create the request content
        var content = new MultipartFormDataContent();
        var imageContent = new StreamContent(imageStream);
        imageContent.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(GetImageContentType(filename));
        content.Add(imageContent, "image", filename);

        // The server hashes an empty body for multipart uploads, so the client must too.
        await SignRequestAsync(
            "POST", $"/api/profiles/{ss58address}/image", new EmptyPayloadBody(), signer, DateTime.UtcNow);

        var uri = new Uri($"api/profiles/{ss58address}/image", UriKind.Relative);

        var response = await _httpClient.PostAsync(uri, content);
        response.EnsureSuccessStatusCode();

        var responseContent = await response.Content.ReadAsStringAsync();

        // ASP.NET Core serves bare strings as text/plain; only parse JSON when the
        // server actually sent JSON
        if (response.Content.Headers.ContentType?.MediaType == "application/json")
        {
            return JsonSerializer.Deserialize<string>(responseContent, _jsonOptions) ?? "";
        }

        return responseContent;
    }

    private static string GetImageContentType(string filename)
    {
        return Path.GetExtension(filename).ToLowerInvariant() switch
        {
            ".jpg" or ".jpeg" => "image/jpeg",
            ".png" => "image/png",
            ".gif" => "image/gif",
            ".webp" => "image/webp",
            ".bmp" => "image/bmp",
            ".svg" => "image/svg+xml",
            _ => "application/octet-stream"
        };
    }

    public void Dispose()
    {
        _httpClient?.Dispose();
    }
}
