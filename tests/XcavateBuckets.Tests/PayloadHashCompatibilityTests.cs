using Substrate.NetApi;
using System.Text;
using System.Text.Json;
using XcavateProfile.Client;
using XcavateProfileApiClient;

namespace XcavateBuckets.Tests;

/// <summary>
/// Pins the body hash against Substrate.NET.API's helpers.
/// </summary>
/// <remarks>
/// <c>CryptoHelper</c> used to reach Blake2 and hex encoding through <c>HashExtension.Blake2</c> and
/// <c>Utils.Bytes2HexString</c>. It now calls Blake2Core directly and uses
/// <c>Convert.ToHexString</c>, which is the only reason XcavateProfileApiSolanaClient can ship
/// without Substrate.NET.API at all. That swap is invisible if — and only if — the bytes are
/// identical: the hash goes into the signed payload, so a difference of one hex digit's case would
/// reject every signature the deployed server has ever accepted, and would do it silently.
///
/// These tests are the guard. They live here rather than beside the Solana client because the code
/// under test is shared source, compiled into both packages, and this is the suite that already has
/// Substrate.NET.API available to compare against.
/// </remarks>
[TestFixture]
public class PayloadHashCompatibilityTests
{
    private static readonly string[] Inputs =
    [
        "",
        "a",
        "POST:/api/profiles",
        "DELETE:/api/profiles/5GrwvaEF5zXb26Fz9rcQpDWS57CtERHpNehXCPcNoHGKutQY::2026-07-27T10:00:00.0000000Z",
        "{\"ss58address\":\"AK7AACuihtCk6abEywXtg7sPW2Qh9iYg5C6BA38h9ciE\",\"x25519Key\":\"0x01\"}",
        "unicode: ünïcödé ✓ 日本語",
    ];

    [Test]
    public void Hash_matches_substrates_blake2b_128([ValueSource(nameof(Inputs))] string input)
    {
        var expected = HashExtension.Blake2(Encoding.UTF8.GetBytes(input), 128);

        Assert.That(CryptoHelper.Hash(input), Is.EqualTo(expected));
    }

    [Test]
    public void HashHex_matches_substrates_hex_encoding([ValueSource(nameof(Inputs))] string input)
    {
        var expected = Utils.Bytes2HexString(HashExtension.Blake2(Encoding.UTF8.GetBytes(input), 128));

        // Uppercase digits behind a lowercase "0x" prefix — Bytes2HexString goes through
        // BitConverter.ToString, and that casing is part of the wire format.
        Assert.That(CryptoHelper.HashHex(input), Is.EqualTo(expected));
    }

    /// <summary>
    /// The profile's own hash, end to end: JSON shape, digest and encoding together. This is the
    /// value that actually travels in the payload for every create and update.
    /// </summary>
    [Test]
    public void Profile_hash_matches_the_substrate_computation()
    {
        var profile = new Profile
        {
            Ss58Address = "5GrwvaEF5zXb26Fz9rcQpDWS57CtERHpNehXCPcNoHGKutQY",
            Nickname = "myprofile",
            Bio = "A bio with \"quotes\", a slash / and ünïcödé",
            ProfilePicture = null,
            X25519Key = "0x1234567890abcdef1234567890abcdef1234567890abcdef1234567890abcdef"
        };

        var json = JsonSerializer.Serialize(
            profile,
            new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                PropertyNameCaseInsensitive = true
            });

        var expected = Utils.Bytes2HexString(HashExtension.Blake2(Encoding.UTF8.GetBytes(json), 128));

        Assert.That(profile.Hash(), Is.EqualTo(expected));
    }

    /// <summary>
    /// The payload is what both sides sign over, so its assembly — separators, the empty-body
    /// convention, the round-trip through UTC — has to be pinned too, not just the hash inside it.
    /// </summary>
    [Test]
    public void ConstructPayload_lays_out_method_path_hash_and_timestamp()
    {
        var timestamp = new DateTime(2026, 7, 27, 10, 0, 0, DateTimeKind.Utc);

        Assert.That(
            CryptoHelper.ConstructPayload("DELETE", "/api/profiles/abc", EmptyPayloadBody.Instance, timestamp),
            Is.EqualTo("DELETE:/api/profiles/abc::2026-07-27T10:00:00.0000000Z"));
    }

    /// <summary>
    /// A local time in must still produce a UTC payload, or a client outside UTC signs a timestamp
    /// the server reads as hours of skew.
    /// </summary>
    [Test]
    public void ConstructPayload_normalises_the_timestamp_to_utc()
    {
        var utc = new DateTime(2026, 7, 27, 10, 0, 0, DateTimeKind.Utc);

        Assert.That(
            CryptoHelper.ConstructPayload("GET", "/x", EmptyPayloadBody.Instance, utc.ToLocalTime()),
            Is.EqualTo(CryptoHelper.ConstructPayload("GET", "/x", EmptyPayloadBody.Instance, utc)));
    }
}
