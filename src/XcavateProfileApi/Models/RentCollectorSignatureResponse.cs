using System.Text.Json.Serialization;

namespace XcavateProfileApi.Models;

/// <summary>
/// The server's response: the rent collector's base58-encoded 64-byte ed25519 signature over
/// the compiled Solana message bytes the investor submitted.
/// </summary>
public class RentCollectorSignatureResponse
{
    /// <summary>Base58-encoded 64-byte ed25519 signature produced by the rent collector key.</summary>
    [JsonPropertyName("signature")]
    public string Signature { get; set; } = "";
}
