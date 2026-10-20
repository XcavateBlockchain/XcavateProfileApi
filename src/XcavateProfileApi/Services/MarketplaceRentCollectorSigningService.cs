using System.Text.Json;
using Solnet.Wallet;
using Solnet.Wallet.Utilities;

namespace XcavateProfileApi.Services;

/// <summary>
/// Validates a compiled Solana message and signs it with the rent collector key. The service is
/// stateless after construction: it holds the decoded keypair bytes, the rent collector public
/// key, the marketplace and property program ids, and the per-program allowed instruction
/// discriminators.
/// </summary>
public class MarketplaceRentCollectorSigningService
{
    // Anchor discriminators: sha256("global:<instruction_name>")[0..8].
    // Marketplace program (dj9Q3CpHvDHwexCbkgJ5APDx4JsTxPssNebkvP15g1T).
    private static readonly byte[] BuyDiscriminator = [4, 160, 53, 28, 202, 98, 234, 11];
    private static readonly byte[] ClaimSharesDiscriminator = [130, 131, 29, 237, 134, 20, 110, 245];
    private static readonly byte[] ClaimSpvCaseDiscriminator = [175, 185, 102, 63, 166, 39, 168, 56];
    private static readonly byte[] BuyRelistedSharesDiscriminator = [205, 204, 84, 12, 55, 204, 167, 234];
    private static readonly byte[] ReserveDiscriminator = [137, 47, 218, 50, 106, 149, 133, 110];
    private static readonly byte[] SendPropertySharesDiscriminator = [214, 10, 132, 137, 120, 190, 89, 240];
    private static readonly byte[] RelistSharesDiscriminator = [233, 196, 4, 160, 236, 180, 219, 12];
    private static readonly byte[] DelistSharesDiscriminator = [75, 11, 92, 6, 15, 186, 201, 253];
    // Property program (deCp9srk9C6P4BXJaFpjR5H6Jsm6DCq8AL2kk338dVq).
    private static readonly byte[] ClaimIncomeDiscriminator = [6, 24, 199, 191, 118, 141, 95, 123];
    private static readonly byte[] CloseIncomeCheckpointDiscriminator = [178, 97, 236, 81, 222, 159, 104, 227];
    private static readonly byte[] VoteOnAgentDiscriminator = [203, 127, 40, 102, 254, 243, 11, 244];
    private static readonly byte[] VoteOnProposalDiscriminator = [188, 239, 13, 88, 119, 199, 251, 119];
    private static readonly byte[] VoteOnChallengeDiscriminator = [19, 36, 128, 75, 123, 135, 79, 64];

    private readonly byte[] _keypair;
    private readonly byte[] _rentPubkey;
    private readonly byte[] _programId;
    private readonly byte[] _propertyProgramId;

    /// <summary>Outcome: not configured (key missing), error (validation failed), or signed.</summary>
    public enum Outcome { NotConfigured, Error, Signed }

    public (Outcome outcome, string? error, string? signature) ValidateAndSign(
        byte[] message, string investorAddress)
    {
        if (_keypair.Length == 0)
        {
            return (Outcome.NotConfigured, "Rent collector key not configured", null);
        }

        // Parse the compiled legacy (v0-header-less) message manually (wire format).
        // Layout:
        //   1 byte:  0x80 marks a versioned transaction and is not supported
        //   3 bytes: header (numRequiredSignatures, numReadOnlySigned, numReadOnlyUnsigned)
        //   compact-u16: accountCount
        //   accountCount * 32 bytes: account keys (index 0 = fee payer)
        //   32 bytes: recent blockhash
        //   compact-u16: instructionCount
        //   then per instruction:
        //     1 byte: programIdIndex
        //     1 byte: accountKeyCount
        //     accountKeyCount * 1 byte: account key indices
        //     compact-u16: dataLength
        //     dataLength bytes: data
        try
        {
            if (message.Length < 4)
            {
                return (Outcome.Error, "Malformed message: too short", null);
            }
            if (message[0] == 0x80)
            {
                return (Outcome.Error, "Versioned (v0) transactions are not supported", null);
            }

            byte numRequiredSignatures = message[0];
            int offset = 3; // skip header (3 bytes)

            int accountCount;
            try
            {
                accountCount = ReadCompactU16(message, ref offset);
            }
            catch (Exception)
            {
                return (Outcome.Error, "Malformed message: truncated account keys", null);
            }

            byte[][] accountKeys = new byte[accountCount][];
            for (int i = 0; i < accountCount; i++)
            {
                if (offset + 32 > message.Length)
                {
                    return (Outcome.Error, "Malformed message: truncated account keys", null);
                }
                accountKeys[i] = message.AsSpan(offset, 32).ToArray();
                offset += 32;
            }

            if (offset + 32 > message.Length)
            {
                return (Outcome.Error, "Malformed message: truncated blockhash", null);
            }
            offset += 32; // skip blockhash

            // Fee payer (account 0) must be the rent collector.
            if (!accountKeys[0].AsSpan().SequenceEqual(_rentPubkey))
            {
                return (Outcome.Error, "Fee payer is not the rent collector", null);
            }

            // An allowlisted program id (marketplace or property) must be present among the
            // account keys.
            bool programPresent = false;
            foreach (var key in accountKeys)
            {
                if (key.AsSpan().SequenceEqual(_programId)
                    || key.AsSpan().SequenceEqual(_propertyProgramId))
                {
                    programPresent = true;
                    break;
                }
            }
            if (!programPresent)
            {
                return (Outcome.Error, "Marketplace or property program id not found in account keys", null);
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

            // Validate every instruction: programIdIndex must resolve to an allowlisted
            // program id, and data[0..8] must be a discriminator allowed for that program.
            int ixCount;
            try
            {
                ixCount = ReadCompactU16(message, ref offset);
            }
            catch (Exception)
            {
                return (Outcome.Error, "Malformed message: truncated instruction count", null);
            }
            for (int ix = 0; ix < ixCount; ix++)
            {
                if (offset >= message.Length)
                {
                    return (Outcome.Error, "Malformed message: truncated instruction", null);
                }
                byte programIdIndex = message[offset++];
                if (programIdIndex >= accountCount)
                {
                    return (Outcome.Error, "Instruction programIdIndex out of range", null);
                }
                bool isMarketplace = accountKeys[programIdIndex].AsSpan().SequenceEqual(_programId);
                bool isProperty = accountKeys[programIdIndex].AsSpan().SequenceEqual(_propertyProgramId);
                if (!isMarketplace && !isProperty)
                {
                    return (Outcome.Error,
                        "Instruction does not target the marketplace program or the property program", null);
                }

                if (offset >= message.Length)
                {
                    return (Outcome.Error, "Malformed message: truncated instruction", null);
                }
                byte keyCount = message[offset++];
                if (offset + keyCount > message.Length)
                {
                    return (Outcome.Error, "Malformed message: truncated instruction", null);
                }
                // The instruction's first account key index - the holder/voter/sender position
                // the newer allowlisted instructions pin to the requesting wallet.
                int firstKeyIndex = keyCount > 0 ? message[offset] : -1;
                offset += keyCount; // skip key indices

                int dataLength;
                try
                {
                    dataLength = ReadCompactU16(message, ref offset);
                }
                catch (Exception)
                {
                    return (Outcome.Error, "Malformed message: truncated instruction", null);
                }
                if (offset + dataLength > message.Length)
                {
                    return (Outcome.Error, "Malformed message: truncated instruction", null);
                }

                if (dataLength < 8)
                {
                    return (Outcome.Error, "Instruction data too short", null);
                }

                byte[] disc = message.AsSpan(offset, 8).ToArray();
                bool allowed = isMarketplace
                    ? IsAllowedMarketplaceDiscriminator(disc)
                    : IsAllowedPropertyDiscriminator(disc);
                if (!allowed)
                {
                    return (Outcome.Error, "Instruction discriminator not allowed", null);
                }

                // For the instructions added with the property program (plus the marketplace
                // send_property_shares / relist_shares / delist_shares), account [0] is the
                // holder/voter/sender/seller whose assets move or lock - pin it to the wallet
                // that requested the signature.
                if (RequiresRequestingWalletAsFirstAccount(disc)
                    && (firstKeyIndex < 0
                        || firstKeyIndex >= accountCount
                        || !accountKeys[firstKeyIndex].AsSpan().SequenceEqual(investorBytes)))
                {
                    return (Outcome.Error,
                        "Instruction's first account is not the requesting wallet", null);
                }

                offset += dataLength;
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

    private static bool IsAllowedMarketplaceDiscriminator(byte[] data)
    {
        return data.AsSpan(0, 8).SequenceEqual(BuyDiscriminator)
            || data.AsSpan(0, 8).SequenceEqual(ClaimSharesDiscriminator)
            || data.AsSpan(0, 8).SequenceEqual(ClaimSpvCaseDiscriminator)
            || data.AsSpan(0, 8).SequenceEqual(BuyRelistedSharesDiscriminator)
            || data.AsSpan(0, 8).SequenceEqual(ReserveDiscriminator)
            || data.AsSpan(0, 8).SequenceEqual(SendPropertySharesDiscriminator)
            || data.AsSpan(0, 8).SequenceEqual(RelistSharesDiscriminator)
            || data.AsSpan(0, 8).SequenceEqual(DelistSharesDiscriminator);
    }

    private static bool IsAllowedPropertyDiscriminator(byte[] data)
    {
        return data.AsSpan(0, 8).SequenceEqual(ClaimIncomeDiscriminator)
            || data.AsSpan(0, 8).SequenceEqual(CloseIncomeCheckpointDiscriminator)
            || data.AsSpan(0, 8).SequenceEqual(VoteOnAgentDiscriminator)
            || data.AsSpan(0, 8).SequenceEqual(VoteOnProposalDiscriminator)
            || data.AsSpan(0, 8).SequenceEqual(VoteOnChallengeDiscriminator);
    }

    /// <summary>
    /// The instructions whose on-chain account [0] (sender / holder / voter / seller) must be the
    /// wallet that requested the rent collector's signature.
    /// </summary>
    private static bool RequiresRequestingWalletAsFirstAccount(byte[] data)
    {
        return data.AsSpan(0, 8).SequenceEqual(SendPropertySharesDiscriminator)
            || data.AsSpan(0, 8).SequenceEqual(RelistSharesDiscriminator)
            || data.AsSpan(0, 8).SequenceEqual(DelistSharesDiscriminator)
            || data.AsSpan(0, 8).SequenceEqual(ClaimIncomeDiscriminator)
            || data.AsSpan(0, 8).SequenceEqual(CloseIncomeCheckpointDiscriminator)
            || data.AsSpan(0, 8).SequenceEqual(VoteOnAgentDiscriminator)
            || data.AsSpan(0, 8).SequenceEqual(VoteOnProposalDiscriminator)
            || data.AsSpan(0, 8).SequenceEqual(VoteOnChallengeDiscriminator);
    }

    /// <summary>Reads a Solana compact-u16 (variable-length LEB128-like) from <paramref name="data"/>.</summary>
    private static int ReadCompactU16(byte[] data, ref int offset)
    {
        int value = 0, shift = 0;
        while (true)
        {
            if (offset >= data.Length) throw new IndexOutOfRangeException();
            byte b = data[offset++];
            value |= (b & 0x7F) << shift;
            if ((b & 0x80) == 0) return value;
            shift += 7;
            if (shift >= 14) throw new ArgumentOutOfRangeException();
        }
    }

    public MarketplaceRentCollectorSigningService(IConfiguration config)
    {
        string? keyEnv = config["RENT_COLLECTOR_PRIVATE_KEY"];
        if (string.IsNullOrWhiteSpace(keyEnv))
        {
            _keypair = [];
            _rentPubkey = [];
            _programId = [];
            _propertyProgramId = [];
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
            _propertyProgramId = [];
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

        string? propertyProgramIdEnv = config["RENT_COLLECTOR_PROPERTY_PROGRAM_ID"];
        // Same empty-is-missing fallback, to the devnet property program id.
        string propertyProgramIdStr = string.IsNullOrWhiteSpace(propertyProgramIdEnv)
            ? "deCp9srk9C6P4BXJaFpjR5H6Jsm6DCq8AL2kk338dVq"
            : propertyProgramIdEnv;
        _propertyProgramId = Encoders.Base58.DecodeData(propertyProgramIdStr);
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
