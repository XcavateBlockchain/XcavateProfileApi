namespace XcavateProfileApiClient;

/// <summary>
/// The body of a request that sends none — DELETE, and multipart image upload, where the server
/// deliberately hashes an empty body rather than the file bytes.
/// </summary>
/// <remarks>
/// The hash is the empty string, not the hash of the empty string. That is what the server does,
/// so it is what the client must do.
/// </remarks>
public sealed class EmptyPayloadBody : IPayloadBody
{
    /// <summary>Shared instance; the type carries no state.</summary>
    public static readonly EmptyPayloadBody Instance = new();

    public string Hash() => "";
}
