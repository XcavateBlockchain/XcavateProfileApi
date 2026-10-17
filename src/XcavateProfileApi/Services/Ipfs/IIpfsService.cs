namespace XcavateProfileApi.Services.Ipfs;

/// <summary>
/// Pins files to IPFS and builds the public gateway URLs they are served from.
/// </summary>
public interface IIpfsService
{
    /// <summary>
    /// Pins the file and returns its CID. Throws <see cref="IpfsNotConfiguredException"/> when no
    /// IPFS backend is configured, and <see cref="HttpRequestException"/> when the pin fails.
    /// </summary>
    Task<string> PinFileAsync(Stream content, string fileName, string contentType, CancellationToken ct);

    /// <summary>The public gateway URL a CID is served from.</summary>
    string BuildGatewayUrl(string cid);
}

/// <summary>
/// No IPFS backend is configured (PINATA_JWT unset) — the feature is off, like the other
/// optional integrations whose env keys are absent.
/// </summary>
public sealed class IpfsNotConfiguredException : Exception
{
    public IpfsNotConfiguredException(string message)
        : base(message)
    {
    }
}
