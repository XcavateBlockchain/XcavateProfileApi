namespace XcavateProfileApiClient;

/// <summary>
/// The request body as it appears in the signed payload. Implementations return the
/// <c>0x</c>-prefixed Blake2b-128 hex of the exact bytes being sent — see
/// <c>CryptoHelper.HashHex</c>.
/// </summary>
public interface IPayloadBody
{
    string Hash();
}
