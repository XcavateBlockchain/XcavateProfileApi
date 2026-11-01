using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.WebUtilities;
using XcavateBuckets.Domain;
using Org.BouncyCastle.Crypto.Agreement.Kdf;
using Org.BouncyCastle.Crypto.Digests;
using Org.BouncyCastle.Crypto.Engines;
using Org.BouncyCastle.Crypto.Modes;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Math.EC.Rfc7748;

namespace XcavateProfileApi.Services;

public sealed record LawyerTermsKeySharing(
    string PublicKey,
    JsonObject SecretJwk,
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
        var kid = $"{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}-{RandomNumberGenerator.GetInt32(0, 1_000_000)}";
        var recipientArray = new JsonArray();
        foreach (var recipient in participants
                     .Select(participant => X25519KeyCodec.Normalize(participant.Value, "recipient"))
                     .Distinct(StringComparer.Ordinal))
        {
            recipientArray.Add(recipient);
        }

        var secret = new JsonObject
        {
            ["kty"] = "OKP",
            ["crv"] = "X25519",
            ["x"] = WebEncoders.Base64UrlEncode(publicKey),
            ["d"] = WebEncoders.Base64UrlEncode(privateKey.GetEncoded()),
            ["use"] = "enc",
            ["kid"] = kid,
            ["y"] = ""
        };
        var plaintext = new JsonObject
        {
            ["id"] = id,
            ["from"] = admin,
            ["to"] = recipientArray,
            ["keys"] = new JsonArray(secret)
        }.ToJsonString();
        var publicKeys = participants
            .Select(participant => X25519KeyCodec.Normalize(participant.Value, "recipient"))
            .Distinct(StringComparer.Ordinal)
            .Select(key => X25519KeyCodec.Decode32(key, "recipient"))
            .ToList();
        var body = EncryptForRecipients(plaintext, publicKeys);

        return new LawyerTermsKeySharing(
            WebEncoders.Base64UrlEncode(publicKey),
            secret,
            $"key-sharing:{id}",
            body,
            ContentHash(body));
    }

    public static string ContentHash(string content) => ContentHash(Encoding.UTF8.GetBytes(content));

    public static string ContentHash(byte[] content) =>
        "0x" + Convert.ToHexString(SHA256.HashData(content));

    public static string EncryptMessage(
        string plaintext,
        JsonObject bucketSecretJwk,
        IReadOnlyDictionary<string, string>? extraProtectedHeader = null) =>
        EncryptMessage(
            Encoding.UTF8.GetBytes(plaintext),
            bucketSecretJwk,
            extraProtectedHeader);

    public static string EncryptMessage(
        byte[] plaintext,
        JsonObject bucketSecretJwk,
        IReadOnlyDictionary<string, string>? extraProtectedHeader = null)
    {
        var recipientPublicKey = WebEncoders.Base64UrlDecode(bucketSecretJwk["x"]!.GetValue<string>());
        var ephemeralPrivate = new X25519PrivateKeyParameters(RandomNumberGenerator.GetBytes(32), 0);
        var ephemeralPublic = ephemeralPrivate.GeneratePublicKey().GetEncoded();
        var protectedHeader = new JsonObject
        {
            ["alg"] = "ECDH-ES+A256KW",
            ["enc"] = "A256GCM",
            ["typ"] = "didcomm/encrypted-message-v1",
            ["epk"] = new JsonObject
            {
                ["kty"] = "OKP",
                ["crv"] = "X25519",
                ["x"] = WebEncoders.Base64UrlEncode(ephemeralPublic)
            }
        };
        if (bucketSecretJwk["kid"] is not null)
        {
            protectedHeader["kid"] = bucketSecretJwk["kid"]!.GetValue<string>();
        }
        if (extraProtectedHeader is not null)
        {
            foreach (var entry in extraProtectedHeader)
            {
                protectedHeader[entry.Key] = entry.Value;
            }
        }
        var protectedValue = WebEncoders.Base64UrlEncode(
            Encoding.UTF8.GetBytes(protectedHeader.ToJsonString()));
        var aad = Encoding.ASCII.GetBytes(protectedValue);

        var shared = new byte[32];
        X25519.ScalarMult(ephemeralPrivate.GetEncoded(), 0, recipientPublicKey, 0, shared, 0);
        var cek = RandomNumberGenerator.GetBytes(32);
        var wrap = new Rfc3394WrapEngine(new AesEngine());
        wrap.Init(true, new KeyParameter(DeriveKek(shared)));
        var encryptedKey = wrap.Wrap(cek, 0, cek.Length);
        var iv = RandomNumberGenerator.GetBytes(12);
        var ciphertext = new byte[plaintext.Length];
        var tag = new byte[16];
        using (var aes = new AesGcm(cek, tag.Length))
        {
            aes.Encrypt(iv, plaintext, ciphertext, tag, aad);
        }
        CryptographicOperations.ZeroMemory(shared);
        CryptographicOperations.ZeroMemory(cek);

        return string.Join(
            '.',
            protectedValue,
            WebEncoders.Base64UrlEncode(encryptedKey),
            WebEncoders.Base64UrlEncode(iv),
            WebEncoders.Base64UrlEncode(ciphertext),
            WebEncoders.Base64UrlEncode(tag));
    }

    public static byte[] DecryptMessage(string compactJwe, JsonObject bucketSecretJwk)
    {
        var parts = compactJwe.Split('.');
        if (parts.Length != 5)
        {
            throw new CryptographicException("The message is not a compact JWE.");
        }
        var protectedValue = parts[0];
        using var protectedHeader = JsonDocument.Parse(WebEncoders.Base64UrlDecode(protectedValue));
        var ephemeralPublic = WebEncoders.Base64UrlDecode(
            protectedHeader.RootElement.GetProperty("epk").GetProperty("x").GetString()!);
        var recipientPrivateKey = WebEncoders.Base64UrlDecode(bucketSecretJwk["d"]!.GetValue<string>());
        var shared = new byte[32];
        X25519.ScalarMult(recipientPrivateKey, 0, ephemeralPublic, 0, shared, 0);
        var wrap = new Rfc3394WrapEngine(new AesEngine());
        wrap.Init(false, new KeyParameter(DeriveKek(shared)));
        var encryptedKey = WebEncoders.Base64UrlDecode(parts[1]);
        var cek = wrap.Unwrap(encryptedKey, 0, encryptedKey.Length);
        var iv = WebEncoders.Base64UrlDecode(parts[2]);
        var ciphertext = WebEncoders.Base64UrlDecode(parts[3]);
        var tag = WebEncoders.Base64UrlDecode(parts[4]);
        using var aes = new AesGcm(cek, 16);
        var plaintext = new byte[ciphertext.Length];
        aes.Decrypt(iv, ciphertext, tag, plaintext, Encoding.ASCII.GetBytes(protectedValue));
        return plaintext;
    }

    public static JsonObject DecryptKeySharing(string jweJson, JsonObject recipientPrivateJwk)
    {
        using var document = JsonDocument.Parse(jweJson);
        var root = document.RootElement;
        var protectedValue = root.GetProperty("protected").GetString()!;
        var aad = Encoding.ASCII.GetBytes(protectedValue);
        var iv = WebEncoders.Base64UrlDecode(root.GetProperty("iv").GetString()!);
        var ciphertext = WebEncoders.Base64UrlDecode(root.GetProperty("ciphertext").GetString()!);
        var tag = WebEncoders.Base64UrlDecode(root.GetProperty("tag").GetString()!);
        var recipientPrivateKey = WebEncoders.Base64UrlDecode(
            recipientPrivateJwk["d"]!.GetValue<string>());

        foreach (var recipient in root.GetProperty("recipients").EnumerateArray())
        {
            var ephemeralPublic = WebEncoders.Base64UrlDecode(
                recipient.GetProperty("header").GetProperty("epk").GetProperty("x").GetString()!);
            var shared = new byte[32];
            X25519.ScalarMult(recipientPrivateKey, 0, ephemeralPublic, 0, shared, 0);
            var wrap = new Rfc3394WrapEngine(new AesEngine());
            wrap.Init(false, new KeyParameter(DeriveKek(shared)));
            try
            {
                var encryptedKey = WebEncoders.Base64UrlDecode(
                    recipient.GetProperty("encrypted_key").GetString()!);
                var cek = wrap.Unwrap(encryptedKey, 0, encryptedKey.Length);
                using var aes = new AesGcm(cek, 16);
                var plaintext = new byte[ciphertext.Length];
                aes.Decrypt(iv, ciphertext, tag, plaintext, aad);
                using var payload = JsonDocument.Parse(plaintext);
                return JsonNode.Parse(payload.RootElement.GetProperty("keys")[0].GetRawText())!.AsObject();
            }
            catch (Org.BouncyCastle.Crypto.InvalidCipherTextException)
            {
            }
        }

        throw new CryptographicException("The admin cannot decrypt the bucket key-sharing message.");
    }

    public static string EncryptForRecipients(string plaintext, IReadOnlyCollection<byte[]> publicKeys)
    {
        var protectedValue = WebEncoders.Base64UrlEncode(Encoding.UTF8.GetBytes(
            """{"enc":"A256GCM","typ":"didcomm/key-sharing-v1"}"""));
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
                    ["kid"] = WebEncoders.Base64UrlEncode(publicKey),
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
