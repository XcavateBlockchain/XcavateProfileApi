using Amazon.S3;
using Amazon.S3.Model;

namespace XcavateProfileApi.Services;

public class S3Config
{
    public string Endpoint { get; set; } = string.Empty;
    public string Region { get; set; } = string.Empty;
    public string AccessKey { get; set; } = string.Empty;
    public string SecretKey { get; set; } = string.Empty;
}

public interface IS3Service
{
    Task<string> UploadImageAsync(string bucketName, string key, Stream content, string contentType);
    Task DeleteObjectAsync(string bucketName, string key);
}

public class S3Service : IS3Service
{
    private readonly AmazonS3Client _client;
    private readonly Uri _endpoint;

    public S3Service(S3Config config)
    {
        if (string.IsNullOrWhiteSpace(config.Endpoint))
        {
            throw new InvalidOperationException("S3_ENDPOINT must be configured for Hetzner Object Storage.");
        }

        _endpoint = NormalizeEndpoint(config.Endpoint);

        var s3Config = new AmazonS3Config
        {
            ServiceURL = _endpoint.ToString().TrimEnd('/'),
            AuthenticationRegion = config.Region,
            ForcePathStyle = true
        };

        _client = new AmazonS3Client(config.AccessKey, config.SecretKey, s3Config);
    }

    private static Uri NormalizeEndpoint(string endpoint)
    {
        var normalizedEndpoint = endpoint.Trim();
        if (!normalizedEndpoint.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
            !normalizedEndpoint.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            normalizedEndpoint = $"https://{normalizedEndpoint}";
        }

        if (!Uri.TryCreate(normalizedEndpoint, UriKind.Absolute, out var uri))
        {
            throw new InvalidOperationException("S3_ENDPOINT must be a valid Hetzner Object Storage endpoint.");
        }

        return uri;
    }

    public async Task<string> UploadImageAsync(string bucketName, string key, Stream content, string contentType)
    {
        if (content.CanSeek)
        {
            content.Position = 0;
        }

        var request = new PutObjectRequest
        {
            BucketName = bucketName,
            Key = key,
            InputStream = content,
            ContentType = contentType,
            CannedACL = S3CannedACL.PublicRead
        };

        await _client.PutObjectAsync(request);

        return BuildPublicObjectUrl(bucketName, key);
    }

    public async Task DeleteObjectAsync(string bucketName, string key)
    {
        await _client.DeleteObjectAsync(new DeleteObjectRequest
        {
            BucketName = bucketName,
            Key = key
        });
    }

    private string BuildPublicObjectUrl(string bucketName, string key)
    {
        var escapedKey = string.Join('/', key.Split('/').Select(Uri.EscapeDataString));
        var builder = new UriBuilder(_endpoint)
        {
            Host = $"{bucketName}.{_endpoint.Host}",
            Path = escapedKey
        };

        return builder.Uri.ToString();
    }
}
