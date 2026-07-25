namespace XcavateProfileApiClient.Signing;

/// <summary>
/// The client-side counterpart of <see cref="ISignatureScheme"/>: produces the credentials for one
/// address. Each implementation owns its wire conventions, so callers stay chain-agnostic.
/// </summary>
public interface IRequestSigner
{
    /// <summary>The value for the <c>X-SS58-Address</c> header.</summary>
    string Address { get; }

    /// <summary>Signs the payload string with whichever bytes this scheme signs.</summary>
    Task<byte[]> SignAsync(string payload);

    /// <summary>Encodes the signature for the <c>X-Signature</c> header.</summary>
    string EncodeSignature(byte[] signature);
}
