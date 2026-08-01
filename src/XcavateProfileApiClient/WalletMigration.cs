using System.Text.Json;
using System.Text.Json.Serialization;
using XcavateProfileApiClient;

namespace XcavateProfile.Client;

/// <summary>
/// A registered Polkadot → Solana wallet migration: the owner of <see cref="Ss58Address"/> has
/// declared, with an sr25519 signature, that their account moves to <see cref="SolanaAddress"/>.
/// </summary>
/// <remarks>
/// Registration is only accepted when the request is signed by the Polkadot wallet being migrated,
/// so a pair in this table is proof of intent by the SS58 side. The Solana side is a declared
/// destination — it does not co-sign.
/// </remarks>
public class WalletMigration : IPayloadBody
{
    /// <summary>The Polkadot account being migrated. SS58 format; owns the registration.</summary>
    [JsonPropertyName("ss58address")]
    public required string Ss58Address { get; set; }

    /// <summary>The Solana wallet the account migrates to. Base58, 32-byte ed25519 key.</summary>
    [JsonPropertyName("solanaAddress")]
    public required string SolanaAddress { get; set; }

    /// <summary>
    /// The body hash for the signed payload. Every property carries an explicit
    /// <see cref="JsonPropertyNameAttribute"/>, so the serialized form — and therefore this hash —
    /// is fixed by declaration order and does not shift with the naming policy.
    /// </summary>
    public string Hash() => CryptoHelper.HashHex(JsonSerializer.Serialize(this, JsonDefaults.Options));
}
