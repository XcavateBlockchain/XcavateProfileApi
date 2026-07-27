using Substrate.NetApi.Model.Types;
using XcavateProfileApiClient.Signing;

namespace XcavateProfileApiClient;

public sealed partial class SigningHttpMessageHandler
{
    /// <summary>Convenience overload for the sr25519 path, which every existing caller uses.</summary>
    public SigningHttpMessageHandler(Account account) : this(new SubstrateRequestSigner(account))
    {
    }
}
