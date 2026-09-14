using System.Net;
using System.Text;
using System.Text.Json;
using Solnet.Wallet;
using Solnet.Wallet.Utilities;
using XcavateProfile.Client;
using XcavateProfileApiClient;
using XcavateProfileApiClient.Signing;

namespace XcavateBuckets.Tests;

/// <summary>
/// The rent collector signing endpoint: auth via the investor's signed-request headers, then
/// wire-format validation of the compiled Solana message before the server signs it.
/// </summary>
public class MarketplaceEndpointTests
{
    private const string Endpoint = "/api/marketplace/rent-collector-signature";
    private const string ProgramIdBase58 = "dj9Q3CpHvDHwexCbkgJ5APDx4JsTxPssNebkvP15g1T";

    private static readonly byte[] Buy = [4, 160, 53, 28, 202, 98, 234, 11];
    private static readonly byte[] ClaimShares = [130, 131, 29, 237, 134, 20, 110, 245];
    private static readonly byte[] Reserve = [137, 47, 218, 50, 106, 149, 133, 110];

    private static byte[] ProgramId => Encoders.Base58.DecodeData(ProgramIdBase58);

    /// <summary>
    /// A minimal compiled Solana message: 3-byte header, account keys, blockhash,
    /// one-instruction count, then the instruction. Matches the legacy wire layout
    /// the server parser expects.
    /// </summary>
    private static byte[] BuildWire(
        int numRequiredSignatures,
        IReadOnlyList<byte[]> keys,
        int programIdIndex,
        IReadOnlyList<int> ixKeyIndices,
        byte[] discriminator) =>
        BuildRawWire(numRequiredSignatures, keys, programIdIndex, ixKeyIndices,
            discriminator.Concat(Enumerable.Repeat((byte)0x42, 8)).ToArray());

    private static byte[] BuildRawWire(
        int numRequiredSignatures,
        IReadOnlyList<byte[]> keys,
        int programIdIndex,
        IReadOnlyList<int> ixKeyIndices,
        byte[] data)
    {

        using var ms = new MemoryStream();
        ms.WriteByte((byte)numRequiredSignatures);
        ms.WriteByte(0);
        ms.WriteByte(0);
        ms.WriteByte((byte)keys.Count);
        for (var i = 0; i < keys.Count; i++)
        {
            if (keys[i] is not { Length: 32 })
            {
                throw new InvalidOperationException($"key[{i}] has length {keys[i]?.Length ?? -1}");
            }
            ms.Write(keys[i], 0, 32);
        }
        ms.Write(new byte[32]); // recent blockhash
        ms.WriteByte(1); // instruction count (compact-u16, single byte)
        ms.WriteByte((byte)programIdIndex);
        ms.WriteByte((byte)ixKeyIndices.Count);
        foreach (var index in ixKeyIndices)
        {
            ms.WriteByte((byte)index);
        }
        WriteCompactU16(ms, data.Length);
        ms.Write(data, 0, data.Length);
        return ms.ToArray();
    }

    // Solana encodes instruction data length as compact-u16 (shortvec), not a fixed u16.
    private static void WriteCompactU16(Stream s, int value)
    {
        while (true)
        {
            if (value < 0x80)
            {
                s.WriteByte((byte)value);
                return;
            }
            s.WriteByte((byte)(0x80 | (value & 0x7F)));
            value >>= 7;
        }
    }

    private static RentCollectorSignatureRequest Body(byte[] wire) =>
        new() { Message = Convert.ToBase64String(wire) };

    private static async Task<(HttpStatusCode status, string body)> PostUnsignedAsync(
        HttpClient client, string json)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, Endpoint)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };
        using var response = await client.SendAsync(request);
        return (response.StatusCode, await response.Content.ReadAsStringAsync());
    }

    [Test]
    public async Task Buy_message_is_signed_and_the_signature_verifies()
    {
        await using var host = await MarketplaceHost.StartAsync();
        var investor = new Solnet.Wallet.Account();
        var wire = BuildWire(2, [host.RentPubkey, investor.PublicKey.KeyBytes, ProgramId], 2, [0, 1], Buy);
        var signer = new SolanaRequestSigner(investor);

        using var request = await SignedRequests.PostAsync(Endpoint, Body(wire), signer);
        using var response = await host.Client.SendAsync(request);

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        var json = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(json);
        var signatureBase58 = doc.RootElement.GetProperty("signature").GetString()!;

        var verified = new PublicKey(host.RentPubkey).Verify(wire, Encoders.Base58.DecodeData(signatureBase58));
        Assert.That(verified, Is.True, "the returned signature must verify over the exact posted bytes");
    }

    [Test]
    public async Task Rent_key_configured_as_solana_keygen_json_array_is_accepted()
    {
        var rent = new Solnet.Wallet.Account();
        await using var host = await MarketplaceHost.StartAsync(
            rent.PrivateKey.KeyBytes, MarketplaceHost.RentKeyFormat.JsonArray);
        var investor = new Solnet.Wallet.Account();
        var wire = BuildWire(2, [host.RentPubkey, investor.PublicKey.KeyBytes, ProgramId], 2, [0, 1], Buy);
        var signer = new SolanaRequestSigner(investor);

        using var request = await SignedRequests.PostAsync(Endpoint, Body(wire), signer);
        using var response = await host.Client.SendAsync(request);

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        var json = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(json);
        var signatureBase58 = doc.RootElement.GetProperty("signature").GetString()!;

        Assert.That(new PublicKey(host.RentPubkey).Verify(wire, Encoders.Base58.DecodeData(signatureBase58)), Is.True);
    }

    [Test]
    public async Task Empty_program_id_env_falls_back_to_the_devnet_program()
    {
        // The deploy .env always contains the RENT_COLLECTOR_MARKETPLACE_PROGRAM_ID line;
        // when the secret is unset the value is empty and must behave like a missing key.
        var rent = new Solnet.Wallet.Account();
        await using var host = await MarketplaceHost.StartAsync(rent.PrivateKey.KeyBytes, rentProgramId: string.Empty);
        var investor = new Solnet.Wallet.Account();
        var wire = BuildWire(2, [host.RentPubkey, investor.PublicKey.KeyBytes, ProgramId], 2, [0, 1], Buy);
        var signer = new SolanaRequestSigner(investor);

        using var request = await SignedRequests.PostAsync(Endpoint, Body(wire), signer);
        using var response = await host.Client.SendAsync(request);

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        var json = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(json);
        var signatureBase58 = doc.RootElement.GetProperty("signature").GetString()!;

        Assert.That(new PublicKey(host.RentPubkey).Verify(wire, Encoders.Base58.DecodeData(signatureBase58)), Is.True);
    }

    [Test]
    public async Task Claim_shares_message_is_signed()
    {
        await using var host = await MarketplaceHost.StartAsync();
        var investor = new Solnet.Wallet.Account();
        var wire = BuildWire(2, [host.RentPubkey, investor.PublicKey.KeyBytes, ProgramId], 2, [0, 1], ClaimShares);
        var signer = new SolanaRequestSigner(investor);

        using var request = await SignedRequests.PostAsync(Endpoint, Body(wire), signer);
        using var response = await host.Client.SendAsync(request);

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        var json = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(json);
        var signatureBase58 = doc.RootElement.GetProperty("signature").GetString()!;

        var verified = new PublicKey(host.RentPubkey).Verify(wire, Encoders.Base58.DecodeData(signatureBase58));
        Assert.That(verified, Is.True);
    }

    [Test]
    public async Task Fee_payer_that_is_not_the_rent_collector_is_rejected()
    {
        await using var host = await MarketplaceHost.StartAsync();
        var investor = new Solnet.Wallet.Account();
        // Investor at index 0: they, not the rent collector, would pay the fee.
        var wire = BuildWire(2, [investor.PublicKey.KeyBytes, host.RentPubkey, ProgramId], 2, [0, 1], Buy);
        var signer = new SolanaRequestSigner(investor);

        using var request = await SignedRequests.PostAsync(Endpoint, Body(wire), signer);
        using var response = await host.Client.SendAsync(request);

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
        var json = await response.Content.ReadAsStringAsync();
        Assert.That(json, Does.Contain("Fee payer"));
    }

    [Test]
    public async Task Instruction_targeting_a_non_marketplace_program_is_rejected()
    {
        await using var host = await MarketplaceHost.StartAsync();
        var investor = new Solnet.Wallet.Account();
        var other = new Solnet.Wallet.Account().PublicKey.KeyBytes;
        var keys = new List<byte[]> { host.RentPubkey, investor.PublicKey.KeyBytes, ProgramId, other };
        var wire = BuildWire(2, keys, 3, [0, 1], Buy);

        using var request = await SignedRequests.PostAsync(Endpoint, Body(wire), new SolanaRequestSigner(investor));
        using var response = await host.Client.SendAsync(request);

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
        var json = await response.Content.ReadAsStringAsync();
        Assert.That(json, Does.Contain("marketplace program"));
    }

    [Test]
    public async Task Reserve_message_is_signed_and_the_signature_verifies()
    {
        await using var host = await MarketplaceHost.StartAsync();
        var investor = new Solnet.Wallet.Account();
        var wire = BuildWire(2, [host.RentPubkey, investor.PublicKey.KeyBytes, ProgramId], 2, [0, 1], Reserve);
        var signer = new SolanaRequestSigner(investor);

        using var request = await SignedRequests.PostAsync(Endpoint, Body(wire), signer);
        using var response = await host.Client.SendAsync(request);

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        var json = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(json);
        var signatureBase58 = doc.RootElement.GetProperty("signature").GetString()!;

        var verified = new PublicKey(host.RentPubkey).Verify(wire, Encoders.Base58.DecodeData(signatureBase58));
        Assert.That(verified, Is.True, "the returned signature must verify over the exact posted bytes");
    }

    [Test]
    public async Task Real_13_key_reserve_layout_message_is_signed()
    {
        // The actual reserve_shares message shape: rent fee payer, investor, 9 PDA
        // non-signers, system program, marketplace program last (index 12), with 24
        // bytes of instruction data (discriminator + 16).
        await using var host = await MarketplaceHost.StartAsync();
        var investor = new Solnet.Wallet.Account();
        var pdas = Enumerable.Range(0, 9).Select(_ => new Solnet.Wallet.Account().PublicKey.KeyBytes).ToList();
        var systemProgram = Encoders.Base58.DecodeData("11111111111111111111111111111111");
        var keys = new List<byte[]> { host.RentPubkey, investor.PublicKey.KeyBytes };
        keys.AddRange(pdas);
        keys.Add(systemProgram);
        keys.Add(ProgramId);

        var data = Reserve.Concat(Enumerable.Repeat((byte)0x42, 16)).ToArray();
        var wire = BuildRawWire(
            2, keys, 12, [1, 0, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11], data);
        var signer = new SolanaRequestSigner(investor);

        using var request = await SignedRequests.PostAsync(Endpoint, Body(wire), signer);
        using var response = await host.Client.SendAsync(request);

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        var json = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(json);
        var signatureBase58 = doc.RootElement.GetProperty("signature").GetString()!;

        Assert.That(new PublicKey(host.RentPubkey).Verify(wire, Encoders.Base58.DecodeData(signatureBase58)), Is.True);
    }

    [Test]
    public async Task Versioned_v0_message_is_rejected()
    {
        await using var host = await MarketplaceHost.StartAsync();
        var investor = new Solnet.Wallet.Account();
        var wire = BuildWire(2, [host.RentPubkey, investor.PublicKey.KeyBytes, ProgramId], 2, [0, 1], Buy);
        // Prepend the versioned-transaction marker.
        var versioned = new byte[wire.Length + 1];
        versioned[0] = 0x80;
        Array.Copy(wire, 0, versioned, 1, wire.Length);

        using var request = await SignedRequests.PostAsync(Endpoint, Body(versioned), new SolanaRequestSigner(investor));
        using var response = await host.Client.SendAsync(request);

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
        var json = await response.Content.ReadAsStringAsync();
        Assert.That(json, Does.Contain("not supported"));
    }

    [Test]
    public async Task Investor_present_but_not_a_required_signer_is_rejected()
    {
        await using var host = await MarketplaceHost.StartAsync();
        var investor = new Solnet.Wallet.Account();
        // Investor at index 2 while the header says only the first 2 accounts sign: the
        // investor would be a read-only participant, not a message signer.
        var wire = BuildWire(2, [host.RentPubkey, ProgramId, investor.PublicKey.KeyBytes], 1, [0, 2], Buy);

        using var request = await SignedRequests.PostAsync(Endpoint, Body(wire), new SolanaRequestSigner(investor));
        using var response = await host.Client.SendAsync(request);

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
        var json = await response.Content.ReadAsStringAsync();
        Assert.That(json, Does.Contain("required signer"));
    }

    [Test]
    public async Task Post_without_authentication_headers_is_unauthorized()
    {
        await using var host = await MarketplaceHost.StartAsync();
        var investor = new Solnet.Wallet.Account();
        var wire = BuildWire(2, [host.RentPubkey, investor.PublicKey.KeyBytes, ProgramId], 2, [0, 1], Buy);

        var (status, body) = await PostUnsignedAsync(host.Client, SignedRequests.Json(Body(wire)));

        Assert.That(status, Is.EqualTo(HttpStatusCode.Unauthorized));
        Assert.That(body, Does.Contain("Missing authentication"));
    }

    [Test]
    public async Task Signature_over_a_different_path_is_unauthorized()
    {
        await using var host = await MarketplaceHost.StartAsync();
        var investor = new Solnet.Wallet.Account();
        var wire = BuildWire(2, [host.RentPubkey, investor.PublicKey.KeyBytes, ProgramId], 2, [0, 1], Buy);
        var body = Body(wire);
        var signer = new SolanaRequestSigner(investor);
        var utc = DateTime.UtcNow;

        // Signed for the wrong path; everything else about the request is well formed.
        var signature = await signer.SignAsync(
            CryptoHelper.ConstructPayload("POST", "/api/migrations", body, utc));

        using var request = new HttpRequestMessage(HttpMethod.Post, Endpoint)
        {
            Content = new StringContent(SignedRequests.Json(body), Encoding.UTF8, "application/json")
        };
        request.Headers.Add(SignedRequestHeaders.Address, signer.Address);
        request.Headers.Add(SignedRequestHeaders.Signature, signer.EncodeSignature(signature));
        request.Headers.Add(SignedRequestHeaders.Timestamp, utc.ToString("o"));

        using var response = await host.Client.SendAsync(request);

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized));
    }

    [Test]
    public async Task Unconfigured_rent_collector_key_returns_service_unavailable()
    {
        await using var host = await MarketplaceHost.StartAsync(null);
        var investor = new Solnet.Wallet.Account();
        var rentKey = new Solnet.Wallet.Account().PublicKey.KeyBytes;
        var wire = BuildWire(2, [rentKey, investor.PublicKey.KeyBytes, ProgramId], 2, [0, 1], Buy);

        using var request = await SignedRequests.PostAsync(Endpoint, Body(wire), new SolanaRequestSigner(investor));
        using var response = await host.Client.SendAsync(request);

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.ServiceUnavailable));
    }
}
