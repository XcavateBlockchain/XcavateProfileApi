using Substrate.NetApi;
using System.Text;
using System.Text.Json;
using XcavateProfile.Client;
using XcavateProfileApiClient;

namespace XcavateBuckets.Tests;

/// <summary>
/// <see cref="WalletMigration.Hash"/> hands a JSON string to <c>CryptoHelper.HashHex</c>, which
/// reads as though it wanted hex digits in. It does not: <c>Hex</c> names the encoding of the
/// <em>result</em>, and the input is arbitrary UTF-8 text that gets Blake2b-128'd as bytes. These
/// tests pin that distinction directly, so a future rename or a "fix" that hex-decodes the input
/// fails here instead of silently invalidating every migration signature in production.
/// </summary>
/// <remarks>
/// The migration body hash matters more than most: the server does not hash the bytes it received,
/// it re-serializes the model it bound and hashes that. Client and server therefore only agree while
/// serialize → deserialize → serialize is the identity, which is what the round-trip tests below
/// assert. <see cref="MigrationsEndpointTests"/> proves the same thing through the real controller.
/// </remarks>
[TestFixture]
public class WalletMigrationHashTests
{
    private const string Ss58 = "5GrwvaEF5zXb26Fz9rcQpDWS57CtERHpNehXCPcNoHGKutQY";
    private const string SolanaAddress = "AK7AACuihtCk6abEywXtg7sPW2Qh9iYg5C6BA38h9ciE";

    /// <summary>
    /// The SDK's own options are internal, so they are restated here. A drift between this and
    /// <c>JsonDefaults</c> shows up as a failure in every hash assertion below, which is the point.
    /// </summary>
    private static readonly JsonSerializerOptions WireOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

    /// <summary>What ASP.NET Core's model binder uses when it deserializes the posted body.</summary>
    private static readonly JsonSerializerOptions MvcOptions = new(JsonSerializerDefaults.Web);

    private static WalletMigration NewMigration(
        string ss58 = Ss58, string solana = SolanaAddress) =>
        new() { Ss58Address = ss58, SolanaAddress = solana };

    /// <summary>The body hash computed the long way round, through Substrate.NET.API's helpers.</summary>
    private static string SubstrateHashHex(string input) =>
        Utils.Bytes2HexString(HashExtension.Blake2(Encoding.UTF8.GetBytes(input), 128));

    // ---- the "input is not hex" question -----------------------------------------------------

    /// <summary>
    /// The direct disproof. If <c>HashHex</c> decoded its argument as hex, "0x41" would hash the
    /// single byte 0x41 and collide with hashing "A". It hashes the four characters instead.
    /// </summary>
    [Test]
    public void HashHex_hashes_its_input_as_text_not_as_hex_digits()
    {
        var asText = CryptoHelper.HashHex("0x41");
        var asDecodedByte = Utils.Bytes2HexString(HashExtension.Blake2([0x41], 128));

        Assert.Multiple(() =>
        {
            Assert.That(
                asText, Is.EqualTo(SubstrateHashHex("0x41")),
                "the four characters 0, x, 4, 1 are what gets hashed");
            Assert.That(
                asText, Is.Not.EqualTo(asDecodedByte),
                "a hex-decoding HashHex would produce this instead");
        });
    }

    /// <summary>
    /// Text that could never be hex — braces, quotes, non-ASCII — has to hash without complaint,
    /// because a JSON body is exactly that.
    /// </summary>
    [TestCase("{\"ss58address\":\"abc\"}", TestName = "JSON object")]
    [TestCase("query { buckets { id } }", TestName = "GraphQL document")]
    [TestCase("ünïcödé ✓ 日本語", TestName = "Non-ASCII")]
    [TestCase("", TestName = "Empty")]
    [TestCase("zz", TestName = "Hex-shaped but invalid digits")]
    public void HashHex_accepts_input_that_is_not_hex(string input)
    {
        string? hash = null;
        Assert.DoesNotThrow(() => hash = CryptoHelper.HashHex(input));

        Assert.That(hash, Is.EqualTo(SubstrateHashHex(input)));
    }

    /// <summary>The result is what is hex-encoded: a 16-byte digest, 0x-prefixed and uppercase.</summary>
    [Test]
    public void Hash_returns_prefixed_uppercase_hex_of_the_128_bit_digest()
    {
        Assert.That(NewMigration().Hash(), Does.Match("^0x[0-9A-F]{32}$"));
    }

    // ---- the migration body --------------------------------------------------------------------

    /// <summary>
    /// The serialized shape is the wire format: property names come from the attributes rather than
    /// the naming policy, and the order is declaration order. Both feed the hash, so both are pinned.
    /// </summary>
    [Test]
    public void WalletMigration_serializes_to_the_pinned_wire_json()
    {
        Assert.That(
            JsonSerializer.Serialize(NewMigration(), WireOptions),
            Is.EqualTo($"{{\"ss58address\":\"{Ss58}\",\"solanaAddress\":\"{SolanaAddress}\"}}"));
    }

    /// <summary>JSON shape, digest and hex encoding together — the value that travels in the payload.</summary>
    [Test]
    public void WalletMigration_hash_matches_the_substrate_computation()
    {
        var migration = NewMigration();

        Assert.That(
            migration.Hash(),
            Is.EqualTo(SubstrateHashHex(JsonSerializer.Serialize(migration, WireOptions))));
    }

    /// <summary>Both properties have to reach the hash, or one of them is not covered by the signature.</summary>
    [Test]
    public void Hash_covers_both_addresses()
    {
        var baseline = NewMigration().Hash();

        Assert.Multiple(() =>
        {
            Assert.That(
                NewMigration(ss58: "5FHneW46xGXgs5mUiveU4sbTyGBzmstUspZC92UhjJM694ty").Hash(),
                Is.Not.EqualTo(baseline),
                "the migrated account must be signed over");
            Assert.That(
                NewMigration(solana: "9WzDXwBbmkg8ZTbNMqUxvQRAyrZzDsGYdLVL9zYtAWWM").Hash(),
                Is.Not.EqualTo(baseline),
                "the destination must be signed over — otherwise it could be swapped in flight");
        });
    }

    /// <summary>The properties are plain settable state; the hash reads them back as written.</summary>
    [Test]
    public void Addresses_round_trip_through_the_properties()
    {
        var migration = NewMigration();
        migration.Ss58Address = "5FHneW46xGXgs5mUiveU4sbTyGBzmstUspZC92UhjJM694ty";
        migration.SolanaAddress = "9WzDXwBbmkg8ZTbNMqUxvQRAyrZzDsGYdLVL9zYtAWWM";

        Assert.Multiple(() =>
        {
            Assert.That(
                migration.Ss58Address,
                Is.EqualTo("5FHneW46xGXgs5mUiveU4sbTyGBzmstUspZC92UhjJM694ty"));
            Assert.That(
                migration.SolanaAddress, Is.EqualTo("9WzDXwBbmkg8ZTbNMqUxvQRAyrZzDsGYdLVL9zYtAWWM"));
            Assert.That(migration.Hash(), Is.Not.EqualTo(NewMigration().Hash()));
        });
    }

    // ---- client/server agreement ---------------------------------------------------------------

    /// <summary>
    /// The server hashes the model it bound, not the bytes it received. That is only equivalent
    /// while the round trip is lossless — so this is the assertion that actually stands between a
    /// migration registration and a 401.
    /// </summary>
    [Test]
    public void Hash_survives_the_servers_deserialize_and_reserialize_round_trip()
    {
        var sent = NewMigration();
        var postedJson = JsonSerializer.Serialize(sent, WireOptions);

        var bound = JsonSerializer.Deserialize<WalletMigration>(postedJson, MvcOptions)!;

        Assert.Multiple(() =>
        {
            Assert.That(bound.Ss58Address, Is.EqualTo(Ss58));
            Assert.That(bound.SolanaAddress, Is.EqualTo(SolanaAddress));
            Assert.That(
                bound.Hash(), Is.EqualTo(sent.Hash()),
                "server-side re-serialization must reproduce the client's bytes");
            Assert.That(
                CryptoHelper.HashHex(postedJson), Is.EqualTo(sent.Hash()),
                "the bytes POSTed must be the bytes that were hashed");
        });
    }

    /// <summary>The same round trip for the sibling body, which shares the pattern and adds nulls.</summary>
    [Test]
    public void Profile_hash_survives_the_same_round_trip()
    {
        var sent = new Profile
        {
            Ss58Address = Ss58,
            Nickname = "a name with \"quotes\", a slash / and ünïcödé",
            Bio = null,
            ProfilePicture = null,
            X25519Key = "0x1234567890abcdef1234567890abcdef1234567890abcdef1234567890abcdef"
        };
        var postedJson = JsonSerializer.Serialize(sent, WireOptions);

        Assert.Multiple(() =>
        {
            Assert.That(
                JsonSerializer.Deserialize<Profile>(postedJson, MvcOptions)!.Hash(),
                Is.EqualTo(sent.Hash()));
            Assert.That(CryptoHelper.HashHex(postedJson), Is.EqualTo(sent.Hash()));
        });
    }

    // ---- the other bodies that go through the same seam -----------------------------------------

    /// <summary>
    /// The empty body is the one case where the hash is not a hash: both sides put an empty string
    /// in the payload, so a DELETE or an image upload signs "METHOD:/path::timestamp". Turning this
    /// into <c>HashHex("")</c> on either side alone would break every signature of that shape.
    /// </summary>
    [Test]
    public void Empty_body_contributes_an_empty_string_rather_than_a_digest()
    {
        var timestamp = new DateTime(2026, 7, 27, 10, 0, 0, DateTimeKind.Utc);

        Assert.Multiple(() =>
        {
            Assert.That(EmptyPayloadBody.Instance.Hash(), Is.Empty);
            Assert.That(new EmptyPayloadBody().Hash(), Is.Empty, "the server constructs its own");
            Assert.That(
                CryptoHelper.ConstructPayload(
                    "DELETE", "/api/profiles/abc", EmptyPayloadBody.Instance, timestamp),
                Is.EqualTo("DELETE:/api/profiles/abc::2026-07-27T10:00:00.0000000Z"));
            Assert.That(
                CryptoHelper.HashHex(""), Is.Not.Empty,
                "an empty string does have a digest — the convention is deliberately not it");
        });
    }

    /// <summary>
    /// The GraphQL path hashes the raw document on both sides, but reaches hex through different
    /// helpers: <c>CryptoHelper.HashHex</c> in the client's handler, <c>Utils.Bytes2HexString</c> in
    /// the server's middleware. They have to be the same string, prefix and casing included.
    /// </summary>
    [TestCase("{\"query\":\"query { buckets { id } }\"}", TestName = "Query document")]
    [TestCase("{\"query\":\"mutation { createBucket(name: \\\"ünï\\\") { id } }\"}",
        TestName = "Mutation with non-ASCII")]
    [TestCase("", TestName = "Empty body")]
    public void Raw_body_hashing_agrees_between_the_client_handler_and_the_server_middleware(
        string body)
    {
        Assert.That(
            CryptoHelper.HashHex(body),
            Is.EqualTo(Utils.Bytes2HexString(CryptoHelper.Hash(body))));
    }

    /// <summary>
    /// The full payload for a registration, assembled the way both sides assemble it. The path is
    /// the one <c>MigrationsController</c> signs over.
    /// </summary>
    [Test]
    public void ConstructPayload_embeds_the_migration_hash_between_path_and_timestamp()
    {
        var migration = NewMigration();
        var timestamp = new DateTime(2026, 8, 1, 12, 0, 0, DateTimeKind.Utc);

        Assert.That(
            CryptoHelper.ConstructPayload("POST", "/api/migrations", migration, timestamp),
            Is.EqualTo(
                $"POST:/api/migrations:{migration.Hash()}:2026-08-01T12:00:00.0000000Z"));
    }
}
