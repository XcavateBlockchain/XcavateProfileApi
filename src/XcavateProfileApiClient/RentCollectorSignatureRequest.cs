using System.Text.Json;
using System.Text.Json.Serialization;
using XcavateProfileApiClient;

namespace XcavateProfile.Client;

/// <summary>
/// A request for the rent collector's signature on a marketplace buy/claim transaction. The
/// investor posts the base64 of the compiled Solana MESSAGE bytes; the server validates the
/// message (fee payer is the rent collector, every instruction is a marketplace buy/claim call)
/// and signs those exact bytes with the rent collector key. The investor signs the same bytes
/// with their own key and submits the two-signature transaction.
/// </summary>
public class RentCollectorSignatureRequest : IPayloadBody
{
    /// <summary>
    /// Base64 of the compiled Solana message bytes (header + account keys + recent blockhash +
    /// instructions) with NO signatures prefix. Must be the exact bytes the investor also signs.
    /// </summary>
    [JsonPropertyName("message")]
    public required string Message { get; set; }

    /// <summary>
    /// The body hash for the signed payload. The property carries an explicit
    /// <see cref="JsonPropertyNameAttribute"/>, so the serialized form — and therefore this hash —
    /// is fixed and does not shift with the naming policy.
    /// </summary>
    public string Hash() => CryptoHelper.HashHex(JsonSerializer.Serialize(this, JsonDefaults.Options));
}
