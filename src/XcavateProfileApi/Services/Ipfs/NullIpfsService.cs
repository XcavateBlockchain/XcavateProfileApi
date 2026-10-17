namespace XcavateProfileApi.Services.Ipfs;

/// <summary>
/// Stands in for <see cref="PinataIpfsService"/> when PINATA_JWT is unset: pinning is refused,
/// but gateway URLs can still be built for documents pinned before the key went away.
/// </summary>
public sealed class NullIpfsService : IIpfsService
{
    public Task<string> PinFileAsync(
        Stream content, string fileName, string contentType, CancellationToken ct) =>
        throw new IpfsNotConfiguredException(
            "IPFS pinning is not configured on this server (PINATA_JWT is unset)");

    public string BuildGatewayUrl(string cid) =>
        PinataIpfsService.DefaultGatewayUrl.TrimEnd('/') + "/" + cid;
}
