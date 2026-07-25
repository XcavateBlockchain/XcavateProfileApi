namespace XcavateProfileApiClient.Signing;

/// <summary>
/// One way of proving control of an address. Implementations share the payload *string* built by
/// <c>CryptoHelper.ConstructPayload</c> and differ only in which bytes of it get signed.
/// </summary>
public interface ISignatureScheme
{
    /// <summary>Stable identifier, used in log and error text.</summary>
    string Name { get; }

    /// <summary>
    /// Whether this scheme owns the given address format. Must never throw — it runs against
    /// unvalidated header input, and the address decoders in both ecosystems throw freely.
    /// </summary>
    bool CanVerify(string? address);

    /// <summary>
    /// Verifies the signature over the payload. Only called when <see cref="CanVerify"/> is true.
    /// </summary>
    bool Verify(string payload, byte[] signature, string address);
}
