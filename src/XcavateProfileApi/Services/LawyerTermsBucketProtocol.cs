using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.WebUtilities;
using Org.BouncyCastle.Crypto.Agreement.Kdf;
using Org.BouncyCastle.Crypto.Digests;
using Org.BouncyCastle.Crypto.Engines;
using Org.BouncyCastle.Crypto.Modes;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Math.EC.Rfc7748;

namespace XcavateProfileApi.Services;

public sealed record LawyerTermsKeySharing(
    string PublicKeyHex,
    string Reference,
    string Body,
    string ContentHash);

public static class LawyerTermsBucketProtocol
{
    public const string KeySharingTag = "didcomm/key-sharing-v1";
    public const string KeySharingContentType = "application/didcomm-encrypted+json";
    public const string VotePromptTag = "lawyer/terms-vote";
    public const string DocumentTag = "lawyer/terms-document";
    public const string VotePrompt =
        "All property token holders should now vote on whether to accept the Lawyer's terms.";

    public static LawyerTermsKeySharing CreateKeySharing(
        string admin, IEnumerable<KeyValuePair<string, string>> recipients)
    {
        var participants = recipients.ToList();
        var privateKey = new X25519PrivateKeyParameters(RandomNumberGenerator.GetBytes(32), 0);
        var publicKey = privateKey.GeneratePublicKey().GetEncoded();
        var id = Guid.NewGuid().ToString();
        var recipientArray = new JsonArray();
        foreach (var recipient in new[] { admin }.Concat(participants.Select(participant => participant.Key))
                     .Distinct(StringComparer.Ordinal))
        {
            recipientArray.Add(recipient);
        }

        var secret = new JsonObject
        {
            ["kty"] = "OKP",
            ["crv"] = "X25519",
            ["x"] = WebEncoders.Base64UrlEncode(publicKey),
            ["d"] = WebEncoders.Base64UrlEncode(privateKey.GetEncoded())
        };
        var plaintext = new JsonObject
        {
            ["id"] = id,
            ["from"] = admin,
            ["to"] = recipientArray,
            ["keys"] = new JsonArray(secret)
        }.ToJsonString();
        var publicKeys = participants
            .Select(participant => Convert.ToHexString(DecodeHex32(participant.Value)))
            .Distinct(StringComparer.Ordinal)
            .Select(Convert.FromHexString)
            .ToList();
        var body = EncryptForRecipients(plaintext, publicKeys);

        return new LawyerTermsKeySharing(
            "0x" + Convert.ToHexString(publicKey),
            $"key-sharing:{id}",
            body,
            ContentHash(body));
    }

    public static string ContentHash(string content) => ContentHash(Encoding.UTF8.GetBytes(content));

    public static string ContentHash(byte[] content) =>
        "0x" + Convert.ToHexString(SHA256.HashData(content));

    private static string EncryptForRecipients(string plaintext, IReadOnlyCollection<byte[]> publicKeys)
    {
        var protectedValue = WebEncoders.Base64UrlEncode(Encoding.UTF8.GetBytes(
            """{"enc":"A256GCM","alg":"ECDH-ES+A256KW"}"""));
        var aad = Encoding.ASCII.GetBytes(protectedValue);
        var cek = RandomNumberGenerator.GetBytes(32);
        var iv = RandomNumberGenerator.GetBytes(12);
        var plaintextBytes = Encoding.UTF8.GetBytes(plaintext);
        var ciphertext = new byte[plaintextBytes.Length];
        var tag = new byte[16];
        using (var aes = new AesGcm(cek, tag.Length))
        {
            aes.Encrypt(iv, plaintextBytes, ciphertext, tag, aad);
        }

        var recipientArray = new JsonArray();
        foreach (var publicKey in publicKeys)
        {
            var ephemeralPrivate = new X25519PrivateKeyParameters(RandomNumberGenerator.GetBytes(32), 0);
            var ephemeralPublic = ephemeralPrivate.GeneratePublicKey().GetEncoded();
            var shared = new byte[32];
            X25519.ScalarMult(ephemeralPrivate.GetEncoded(), 0, publicKey, 0, shared, 0);
            var wrap = new Rfc3394WrapEngine(new AesEngine());
            wrap.Init(true, new KeyParameter(DeriveKek(shared)));
            var encryptedKey = wrap.Wrap(cek, 0, cek.Length);
            recipientArray.Add(new JsonObject
            {
                ["header"] = new JsonObject
                {
                    ["alg"] = "ECDH-ES+A256KW",
                    ["epk"] = new JsonObject
                    {
                        ["kty"] = "OKP",
                        ["crv"] = "X25519",
                        ["x"] = WebEncoders.Base64UrlEncode(ephemeralPublic)
                    }
                },
                ["encrypted_key"] = WebEncoders.Base64UrlEncode(encryptedKey)
            });
            CryptographicOperations.ZeroMemory(shared);
        }
        CryptographicOperations.ZeroMemory(cek);

        return new JsonObject
        {
            ["protected"] = protectedValue,
            ["iv"] = WebEncoders.Base64UrlEncode(iv),
            ["ciphertext"] = WebEncoders.Base64UrlEncode(ciphertext),
            ["tag"] = WebEncoders.Base64UrlEncode(tag),
            ["recipients"] = recipientArray
        }.ToJsonString();
    }

    private static byte[] DecodeHex32(string value)
    {
        var bytes = Convert.FromHexString(value.StartsWith("0x", StringComparison.Ordinal)
            ? value.AsSpan(2)
            : value.AsSpan());
        if (bytes.Length != 32)
        {
            throw new ArgumentException("An X25519 public key must contain 32 bytes.", nameof(value));
        }
        return bytes;
    }

    private static byte[] DeriveKek(byte[] shared)
    {
        var kdf = new ConcatenationKdfGenerator(new Sha256Digest());
        kdf.Init(new KdfParameters(shared, BuildOtherInfo()));
        var kek = new byte[32];
        kdf.GenerateBytes(kek, 0, kek.Length);
        return kek;
    }

    private static byte[] BuildOtherInfo()
    {
        static byte[] LenPref(byte[]? data)
        {
            data ??= [];
            var result = new byte[4 + data.Length];
            BitConverter.GetBytes(System.Net.IPAddress.HostToNetworkOrder(data.Length))
                .CopyTo(result, 0);
            data.CopyTo(result, 4);
            return result;
        }

        var algorithm = LenPref(Encoding.UTF8.GetBytes("ECDH-ES+A256KW"));
        var partyU = LenPref(null);
        var partyV = LenPref(null);
        var keyLength = BitConverter.GetBytes(System.Net.IPAddress.HostToNetworkOrder(256));
        return algorithm.Concat(partyU).Concat(partyV).Concat(keyLength).ToArray();
    }
}
