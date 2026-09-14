using System.Text.Json;
using Solnet.Wallet;
using Solnet.Wallet.Utilities;

namespace XcavateProfileApi.Services;

/// <summary>
/// Validates a compiled Solana message and signs it with the rent collector key. The service is
/// stateless after construction: it holds the decoded keypair bytes, the rent collector public
/// key, the marketplace program id, and the allowed instruction discriminators.
/// </summary>
public class MarketplaceRentCollectorSigningService
{
    private static readonly byte[] BuyDiscriminator = [4, 160, 53, 28, 202, 98, 234, 11];
    private static readonly byte[] ClaimSharesDiscriminator = [130, 131, 29, 237, 134, 20, 110, 245];
    private static readonly byte[] ClaimSpvCaseDiscriminator = [175, 185, 102, 63, 166, 39, 168, 56];
    private static readonly byte[] BuyRelistedSharesDiscriminator = [205, 204, 84, 12, 55, 204, 167, 234];

    private readonly byte[] _keypair;
    private readonly byte[] _rentPubkey;
    private readonly byte[] _programId;
    private readonly byte[][] _allowedDiscriminators;

    /// <summary>Outcome: not configured (key missing), error (validation failed), or signed.</summary>
    public enum Outcome { NotConfigured, Error, Signed }

    public (Outcome outcome, string? error, string? signature) ValidateAndSign(
        byte[] message, string investorAddress)
    {
        if (_keypair.Length == 0)
        {
            return (Outcome.NotConfigured, "Rent collector key not configured", null);
        }

        // Parse the compiled message manually (wire format).
        // Layout:
        //   3 bytes: header (numRequiredSignatures, numReadOnlySigned, numReadOnlyUnsigned)
        //   1 byte:  accountCount
        //   accountCount * 32 bytes: account keys (index 0 = fee payer)
        //   32 bytes: recent blockhash
        //   then per instruction:
        //     1 byte: programIdIndex
        //     1 byte: accountKeyCount
        //     accountKeyCount * 1 byte: account key indices
        //     2 bytes (LE u16): dataLength
        //     dataLength bytes: data
        try
        {
            int offset = 0;
            byte numRequiredSignatures = message[offset];
            offset += 3; // skip header (3 bytes)

            byte accountCount = message[offset++];
            byte[][] accountKeys = new byte[accountCount][];
            for (int i = 0; i < accountCount; i++)
            {
                accountKeys[i] = message.AsSpan(offset, 32).ToArray();
                offset += 32;
            }
            offset += 32; // skip blockhash

            // Fee payer (account 0) must be the rent collector.
            if (!accountKeys[0].AsSpan().SequenceEqual(_rentPubkey))
            {
                return (Outcome.Error, "Fee payer is not the rent collector", null);
            }

            // Program id must be present among the account keys.
            bool programPresent = false;
            foreach (var key in accountKeys)
            {
                if (key.AsSpan().SequenceEqual(_programId))
                {
                    programPresent = true;
                    break;
                }
            }
            if (!programPresent)
            {
                return (Outcome.Error, "Marketplace program id not found in account keys", null);
            }

            // Investor must be among the first numRequiredSignatures keys, at index != 0.
            byte[] investorBytes = Encoders.Base58.DecodeData(investorAddress);
            if (investorBytes.Length != 32)
            {
                return (Outcome.Error, "Invalid investor address", null);
            }

            bool investorIsSigner = false;
            for (int i = 1; i < numRequiredSignatures && i < accountCount; i++)
            {
                if (accountKeys[i].AsSpan().SequenceEqual(investorBytes))
                {
                    investorIsSigner = true;
                    break;
                }
            }
            if (!investorIsSigner)
            {
                return (Outcome.Error, "Investor is not a required signer", null);
            }

            // Validate every instruction: programIdIndex must resolve to the program id,
            // and data[0..8] must be an allowed discriminator.
            int ixCount = 0;
            while (offset < message.Length)
            {
                byte programIdIndex = message[offset++];
                if (programIdIndex >= accountCount)
                {
                    return (Outcome.Error, "Instruction programIdIndex out of range", null);
                }
                if (!accountKeys[programIdIndex].AsSpan().SequenceEqual(_programId))
                {
                    return (Outcome.Error, "Instruction does not target the marketplace program", null);
                }

                byte keyCount = message[offset++];
                offset += keyCount; // skip key indices

                ushort dataLength = (ushort)(message[offset] | (message[offset + 1] << 8));
                offset += 2;

                if (dataLength < 8)
                {
                    return (Outcome.Error, "Instruction data too short", null);
                }

                byte[] disc = message.AsSpan(offset, 8).ToArray();
                if (!IsAllowedDiscriminator(disc))
                {
                    return (Outcome.Error, "Instruction discriminator not allowed", null);
                }

                offset += dataLength;
                ixCount++;
            }

            if (ixCount == 0)
            {
                return (Outcome.Error, "No instructions in message", null);
            }

            // Sign the message with the rent collector key.
            var signer = new PrivateKey(_keypair);
            byte[] sig = signer.Sign(message);

            // Self-verify as a layout canary.
            var pub = new PublicKey(_rentPubkey);
            if (!pub.Verify(message, sig))
            {
                return (Outcome.Error, "Signature verification failed (key layout error)", null);
            }

            string base58 = Encoders.Base58.EncodeData(sig);
            return (Outcome.Signed, null, base58);
        }
        catch (Exception ex)
        {
            return (Outcome.Error, $"Failed to parse message: {ex.Message}", null);
        }
    }

    private static bool IsAllowedDiscriminator(byte[] data)
    {
        return data.AsSpan(0, 8).SequenceEqual(BuyDiscriminator)
            || data.AsSpan(0, 8).SequenceEqual(ClaimSharesDiscriminator)
            || data.AsSpan(0, 8).SequenceEqual(ClaimSpvCaseDiscriminator)
            || data.AsSpan(0, 8).SequenceEqual(BuyRelistedSharesDiscriminator);
    }

    public MarketplaceRentCollectorSigningService(IConfiguration config)
    {
        string? keyEnv = config["RENT_COLLECTOR_PRIVATE_KEY"];
        if (string.IsNullOrWhiteSpace(keyEnv))
        {
            _keypair = [];
            _rentPubkey = [];
            _programId = [];
            _allowedDiscriminators = [];
            return;
        }

        // Accept hex (0x-prefixed or bare), base58, or the solana-keygen JSON byte array,
        // each decoding to 64 bytes (canonical solana-keygen layout).
        byte[]? keypair = DecodeKeyPair(keyEnv);
        if (keypair == null || keypair.Length != 64)
        {
            _keypair = [];
            _rentPubkey = [];
            _programId = [];
            _allowedDiscriminators = [];
            return;
        }

        _keypair = keypair;
        _rentPubkey = keypair.AsSpan(32, 32).ToArray();

        string? programIdEnv = config["RENT_COLLECTOR_MARKETPLACE_PROGRAM_ID"];
        // Empty (e.g. the deployment .env line exists but the secret is unset) must behave
        // like missing: fall back to the devnet marketplace program id.
        string programIdStr = string.IsNullOrWhiteSpace(programIdEnv)
            ? "dj9Q3CpHvDHwexCbkgJ5APDx4JsTxPssNebkvP15g1T"
            : programIdEnv;
        _programId = Encoders.Base58.DecodeData(programIdStr);
        _allowedDiscriminators = [BuyDiscriminator, ClaimSharesDiscriminator, ClaimSpvCaseDiscriminator, BuyRelistedSharesDiscriminator];
    }

    private static byte[]? DecodeKeyPair(string value)
    {
        string trimmed = value.Trim();
        // solana-keygen JSON output: [109, 11, 135, ...]
        if (trimmed.StartsWith('[') && trimmed.EndsWith(']'))
        {
            byte[]? arrayBytes = DecodeJsonByteArray(trimmed);
            return arrayBytes;
        }
        if (trimmed.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            trimmed = trimmed[2..];
        }
        if (trimmed.Length % 2 == 0 && IsHex(trimmed))
        {
            return Convert.FromHexString(trimmed);
        }
        try
        {
            return Encoders.Base58.DecodeData(trimmed);
        }
        catch
        {
            return null;
        }
    }

    private static byte[]? DecodeJsonByteArray(string value)
    {
        try
        {
            int[]? parts = JsonSerializer.Deserialize<int[]>(value);
            if (parts is null) return null;
            var bytes = new byte[parts.Length];
            for (int i = 0; i < parts.Length; i++)
            {
                if (parts[i] is < 0 or > 255) return null;
                bytes[i] = (byte)parts[i];
            }
            return bytes;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static bool IsHex(string s)
    {
        foreach (char c in s)
        {
            bool isDigit = c >= '0' && c <= '9';
            bool isLower = c >= 'a' && c <= 'f';
            bool isUpper = c >= 'A' && c <= 'F';
            if (!isDigit && !isLower && !isUpper) return false;
        }
        return true;
    }
}
