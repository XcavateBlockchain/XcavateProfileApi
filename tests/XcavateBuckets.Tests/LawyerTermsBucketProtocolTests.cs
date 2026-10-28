using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.WebUtilities;
using Org.BouncyCastle.Crypto.Agreement.Kdf;
using Org.BouncyCastle.Crypto.Digests;
using Org.BouncyCastle.Crypto.Engines;
using Org.BouncyCastle.Crypto.Modes;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Math.EC.Rfc7748;
using XcavateProfileApi.Services;

namespace XcavateBuckets.Tests;

[TestFixture]
public class LawyerTermsBucketProtocolTests
{
    private sealed record TestKeyPair(byte[] PrivateKey, byte[] PublicKey, string PublicKeyHex);

    [Test]
    public void CreateKeySharing_encrypts_the_private_jwk_for_each_participant()
    {
        var holder = NewKeyPair();
        var lawyer = NewKeyPair();
        var result = LawyerTermsBucketProtocol.CreateKeySharing(
            "BucketAdmin111",
            [
                new KeyValuePair<string, string>("HolderA", holder.PublicKeyHex),
                new KeyValuePair<string, string>("LawyerB", lawyer.PublicKeyHex),
                new KeyValuePair<string, string>("HolderA", holder.PublicKeyHex)
            ]);

        var publicKey = Convert.FromHexString(result.PublicKeyHex.AsSpan(2));
        using var jwe = JsonDocument.Parse(result.Body);
        var root = jwe.RootElement;
        var plaintext = DecryptForRecipient(result.Body, holder.PrivateKey);
        using var document = JsonDocument.Parse(plaintext);
        var payload = document.RootElement;
        var id = payload.GetProperty("id").GetString();
        var recipients = payload.GetProperty("to").EnumerateArray()
            .Select(wallet => wallet.GetString()).ToArray();
        var key = payload.GetProperty("keys").EnumerateArray().Single();
        var privateKey = WebEncoders.Base64UrlDecode(key.GetProperty("d").GetString()!);
        var documentPublicKey = WebEncoders.Base64UrlDecode(key.GetProperty("x").GetString()!);
        var derivedPublicKey = new X25519PrivateKeyParameters(privateKey, 0)
            .GeneratePublicKey().GetEncoded();

        Assert.Multiple(() =>
        {
            Assert.That(result.PublicKeyHex, Does.StartWith("0x"));
            Assert.That(publicKey, Has.Length.EqualTo(32));
            Assert.That(publicKey.Any(value => value != 0), Is.True);
            Assert.That(id, Is.Not.Null.And.Not.Empty);
            Assert.That(result.Reference, Is.EqualTo($"key-sharing:{id}"));
            Assert.That(root.GetProperty("recipients").GetArrayLength(), Is.EqualTo(2));
            Assert.That(root.GetProperty("ciphertext").GetString(), Is.Not.Null.And.Not.Empty);
            Assert.That(result.Body, Does.Not.Contain("\"keys\""));
            Assert.That(result.Body, Does.Not.Contain(key.GetProperty("d").GetString()));
            Assert.That(payload.GetProperty("from").GetString(), Is.EqualTo("BucketAdmin111"));
            Assert.That(recipients, Is.EquivalentTo(new[] { "BucketAdmin111", "HolderA", "LawyerB" }));
            Assert.That(key.GetProperty("kty").GetString(), Is.EqualTo("OKP"));
            Assert.That(key.GetProperty("crv").GetString(), Is.EqualTo("X25519"));
            Assert.That(privateKey, Has.Length.EqualTo(32));
            Assert.That(documentPublicKey, Is.EqualTo(publicKey));
            Assert.That(derivedPublicKey, Is.EqualTo(publicKey));
            Assert.That(result.ContentHash, Is.EqualTo(
                "0x" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(result.Body)))));
        });
    }

    [Test]
    public void CreateKeySharing_cannot_be_unwrapped_by_a_nonrecipient()
    {
        var holder = NewKeyPair();
        var other = NewKeyPair();
        var result = LawyerTermsBucketProtocol.CreateKeySharing(
            "BucketAdmin111", [new KeyValuePair<string, string>("HolderA", holder.PublicKeyHex)]);

        Assert.Throws<CryptographicException>(() => DecryptForRecipient(result.Body, other.PrivateKey));
    }

    [Test]
    public void CreateKeySharing_generates_a_fresh_bucket_key_each_time()
    {
        var lawyer = NewKeyPair();
        var recipients = new[] { new KeyValuePair<string, string>("LawyerB", lawyer.PublicKeyHex) };
        var first = LawyerTermsBucketProtocol.CreateKeySharing("BucketAdmin111", recipients);
        var second = LawyerTermsBucketProtocol.CreateKeySharing("BucketAdmin111", recipients);

        Assert.That(second.PublicKeyHex, Is.Not.EqualTo(first.PublicKeyHex));
        Assert.That(second.Body, Is.Not.EqualTo(first.Body));
    }

    [Test]
    public void ContentHash_hashes_the_exact_utf8_and_binary_content()
    {
        const string expected = "0xBA7816BF8F01CFEA414140DE5DAE2223B00361A396177A9CB410FF61F20015AD";

        Assert.Multiple(() =>
        {
            Assert.That(LawyerTermsBucketProtocol.ContentHash("abc"), Is.EqualTo(expected));
            Assert.That(LawyerTermsBucketProtocol.ContentHash(new byte[] { 0x61, 0x62, 0x63 }),
                Is.EqualTo(expected));
        });
    }

    [Test]
    public void Protocol_uses_the_requested_prompt_and_message_tags()
    {
        Assert.Multiple(() =>
        {
            Assert.That(LawyerTermsBucketProtocol.VotePrompt, Is.EqualTo(
                "All property token holders should now vote on whether to accept the Lawyer's terms."));
            Assert.That(LawyerTermsBucketProtocol.KeySharingTag, Is.EqualTo("didcomm/key-sharing-v1"));
            Assert.That(LawyerTermsBucketProtocol.VotePromptTag, Is.EqualTo("lawyer/terms-vote"));
            Assert.That(LawyerTermsBucketProtocol.DocumentTag, Is.EqualTo("lawyer/terms-document"));
        });
    }

    private static TestKeyPair NewKeyPair()
    {
        var privateKey = new X25519PrivateKeyParameters(RandomNumberGenerator.GetBytes(32), 0);
        var publicKey = privateKey.GeneratePublicKey().GetEncoded();
        return new TestKeyPair(
            privateKey.GetEncoded(), publicKey, "0x" + Convert.ToHexString(publicKey));
    }

    private static string DecryptForRecipient(string jweJson, byte[] recipientPrivateKey)
    {
        using var document = JsonDocument.Parse(jweJson);
        var root = document.RootElement;
        var protectedValue = root.GetProperty("protected").GetString()!;
        var protectedHeader = Encoding.UTF8.GetString(WebEncoders.Base64UrlDecode(protectedValue));
        using (var header = JsonDocument.Parse(protectedHeader))
        {
            Assert.Multiple(() =>
            {
                Assert.That(header.RootElement.GetProperty("alg").GetString(),
                    Is.EqualTo("ECDH-ES+A256KW"));
                Assert.That(header.RootElement.GetProperty("enc").GetString(), Is.EqualTo("A256GCM"));
            });
        }

        var iv = WebEncoders.Base64UrlDecode(root.GetProperty("iv").GetString()!);
        var ciphertext = WebEncoders.Base64UrlDecode(root.GetProperty("ciphertext").GetString()!);
        var tag = WebEncoders.Base64UrlDecode(root.GetProperty("tag").GetString()!);
        var aad = Encoding.ASCII.GetBytes(protectedValue);
        foreach (var recipient in root.GetProperty("recipients").EnumerateArray())
        {
            var header = recipient.GetProperty("header");
            Assert.That(header.GetProperty("alg").GetString(), Is.EqualTo("ECDH-ES+A256KW"));
            var epk = header.GetProperty("epk");
            Assert.Multiple(() =>
            {
                Assert.That(epk.GetProperty("kty").GetString(), Is.EqualTo("OKP"));
                Assert.That(epk.GetProperty("crv").GetString(), Is.EqualTo("X25519"));
            });
            var ephemeralPublic = WebEncoders.Base64UrlDecode(epk.GetProperty("x").GetString()!);
            var shared = new byte[32];
            X25519.ScalarMult(recipientPrivateKey, 0, ephemeralPublic, 0, shared, 0);
            var kek = DeriveKek(shared);
            var wrap = new Rfc3394WrapEngine(new AesEngine());
            wrap.Init(false, new KeyParameter(kek));
            byte[] cek;
            try
            {
                var encryptedKey = WebEncoders.Base64UrlDecode(
                    recipient.GetProperty("encrypted_key").GetString()!);
                cek = wrap.Unwrap(encryptedKey, 0, encryptedKey.Length);
            }
            catch (Org.BouncyCastle.Crypto.InvalidCipherTextException)
            {
                continue;
            }

            using var aes = new AesGcm(cek, 16);
            var plaintext = new byte[ciphertext.Length];
            aes.Decrypt(iv, ciphertext, tag, plaintext, aad);
            return Encoding.UTF8.GetString(plaintext);
        }

        throw new CryptographicException("No recipient entry can be unwrapped by this key.");
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
