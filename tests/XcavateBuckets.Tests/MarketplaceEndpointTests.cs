using System.Net;
using System.Text;
using System.Text.Json;
using Solnet.Wallet;
using Solnet.Wallet.Utilities;
using XcavateProfile.Client;
using XcavateProfileApiClient.Signing;

namespace XcavateBuckets.Tests;

/// <summary>
/// The rent collector signing endpoint: no request authentication beyond the investor's
/// address header - the protection is the wire-format validation of the compiled Solana
/// message, which only ever signs a message that also requires the investor's own on-chain
/// signature.
/// </summary>
public class MarketplaceEndpointTests
{
    private const string Endpoint = "/api/marketplace/rent-collector-signature";
    private const string ProgramIdBase58 = "dj9Q3CpHvDHwexCbkgJ5APDx4JsTxPssNebkvP15g1T";
    private const string PropertyProgramIdBase58 = "deCp9srk9C6P4BXJaFpjR5H6Jsm6DCq8AL2kk338dVq";

    private static readonly byte[] Buy = [4, 160, 53, 28, 202, 98, 234, 11];
    private static readonly byte[] ClaimShares = [130, 131, 29, 237, 134, 20, 110, 245];
    private static readonly byte[] ClaimSpvCase = [175, 185, 102, 63, 166, 39, 168, 56];
    private static readonly byte[] RegisterLawyer = [165, 135, 154, 240, 244, 159, 91, 255];
    private static readonly byte[] BuyRelistedShares = [205, 204, 84, 12, 55, 204, 167, 234];
    private static readonly byte[] Reserve = [137, 47, 218, 50, 106, 149, 133, 110];
    private static readonly byte[] SendPropertyShares = [214, 10, 132, 137, 120, 190, 89, 240];
    private static readonly byte[] RelistShares = [233, 196, 4, 160, 236, 180, 219, 12];
    private static readonly byte[] DelistShares = [75, 11, 92, 6, 15, 186, 201, 253];
    private static readonly byte[] ClaimIncome = [6, 24, 199, 191, 118, 141, 95, 123];
    private static readonly byte[] CloseIncomeCheckpoint = [178, 97, 236, 81, 222, 159, 104, 227];
    private static readonly byte[] VoteOnAgent = [203, 127, 40, 102, 254, 243, 11, 244];
    private static readonly byte[] VoteOnProposal = [188, 239, 13, 88, 119, 199, 251, 119];
    private static readonly byte[] VoteOnChallenge = [19, 36, 128, 75, 123, 135, 79, 64];

    private static byte[] ProgramId => Encoders.Base58.DecodeData(ProgramIdBase58);
    private static byte[] PropertyProgramId => Encoders.Base58.DecodeData(PropertyProgramIdBase58);

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
        byte[] data) =>
        BuildRawWire(numRequiredSignatures, keys,
            [new WireInstruction(programIdIndex, ixKeyIndices, data)]);

    /// <summary>One instruction of a compiled message: program, account key indices, data.</summary>
    private sealed record WireInstruction(int ProgramIdIndex, IReadOnlyList<int> KeyIndices, byte[] Data);

    private static byte[] BuildRawWire(
        int numRequiredSignatures,
        IReadOnlyList<byte[]> keys,
        IReadOnlyList<WireInstruction> instructions)
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
        WriteCompactU16(ms, instructions.Count);
        foreach (var ix in instructions)
        {
            ms.WriteByte((byte)ix.ProgramIdIndex);
            ms.WriteByte((byte)ix.KeyIndices.Count);
            foreach (var index in ix.KeyIndices)
            {
                ms.WriteByte((byte)index);
            }
            WriteCompactU16(ms, ix.Data.Length);
            ms.Write(ix.Data, 0, ix.Data.Length);
        }
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

    /// <summary>
    /// An unsigned post carrying only the investor's address header - all the endpoint
    /// asks for. No X-Signature, no X-Timestamp.
    /// </summary>
    private static HttpRequestMessage Post(RentCollectorSignatureRequest body, string investorAddress)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, Endpoint)
        {
            Content = new StringContent(SignedRequests.Json(body), Encoding.UTF8, "application/json")
        };
        request.Headers.Add(SignedRequestHeaders.Address, investorAddress);
        return request;
    }

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

        using var request = Post(Body(wire), investor.PublicKey.Key);
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

        using var request = Post(Body(wire), investor.PublicKey.Key);
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

        using var request = Post(Body(wire), investor.PublicKey.Key);
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

        using var request = Post(Body(wire), investor.PublicKey.Key);
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

        using var request = Post(Body(wire), investor.PublicKey.Key);
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

        using var request = Post(Body(wire), investor.PublicKey.Key);
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

        using var request = Post(Body(wire), investor.PublicKey.Key);
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

        using var request = Post(Body(wire), investor.PublicKey.Key);
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

        using var request = Post(Body(versioned), investor.PublicKey.Key);
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

        using var request = Post(Body(wire), investor.PublicKey.Key);
        using var response = await host.Client.SendAsync(request);

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
        var json = await response.Content.ReadAsStringAsync();
        Assert.That(json, Does.Contain("required signer"));
    }

    [Test]
    public async Task Post_without_an_address_header_is_rejected()
    {
        await using var host = await MarketplaceHost.StartAsync();
        var investor = new Solnet.Wallet.Account();
        var wire = BuildWire(2, [host.RentPubkey, investor.PublicKey.KeyBytes, ProgramId], 2, [0, 1], Buy);

        var (status, body) = await PostUnsignedAsync(host.Client, SignedRequests.Json(Body(wire)));

        Assert.That(status, Is.EqualTo(HttpStatusCode.BadRequest));
        Assert.That(body, Does.Contain("X-SS58-Address"));
    }

    [Test]
    public async Task Unconfigured_rent_collector_key_returns_service_unavailable()
    {
        await using var host = await MarketplaceHost.StartAsync(null);
        var investor = new Solnet.Wallet.Account();
        var rentKey = new Solnet.Wallet.Account().PublicKey.KeyBytes;
        var wire = BuildWire(2, [rentKey, investor.PublicKey.KeyBytes, ProgramId], 2, [0, 1], Buy);

        using var request = Post(Body(wire), investor.PublicKey.Key);
        using var response = await host.Client.SendAsync(request);

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.ServiceUnavailable));
    }

    [Test]
    public async Task Claim_spv_case_message_is_signed()
    {
        await using var host = await MarketplaceHost.StartAsync();
        var investor = new Solnet.Wallet.Account();
        var wire = BuildWire(2, [host.RentPubkey, investor.PublicKey.KeyBytes, ProgramId], 2, [0, 1], ClaimSpvCase);

        using var request = Post(Body(wire), investor.PublicKey.Key);
        using var response = await host.Client.SendAsync(request);

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        var json = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(json);
        var signatureBase58 = doc.RootElement.GetProperty("signature").GetString()!;

        Assert.That(new PublicKey(host.RentPubkey).Verify(wire, Encoders.Base58.DecodeData(signatureBase58)), Is.True);
    }

    [Test]
    public async Task Register_lawyer_message_is_signed_and_the_signature_verifies()
    {
        await using var host = await MarketplaceHost.StartAsync();
        var lawyer = new Solnet.Wallet.Account();
        var wire = BuildWire(2, [host.RentPubkey, lawyer.PublicKey.KeyBytes, ProgramId], 2, [1, 0], RegisterLawyer);

        using var request = Post(Body(wire), lawyer.PublicKey.Key);
        using var response = await host.Client.SendAsync(request);

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        var json = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(json);
        var signatureBase58 = doc.RootElement.GetProperty("signature").GetString()!;

        Assert.That(new PublicKey(host.RentPubkey).Verify(wire, Encoders.Base58.DecodeData(signatureBase58)), Is.True);
    }

    [Test]
    public async Task Buy_relisted_shares_message_is_signed()
    {
        await using var host = await MarketplaceHost.StartAsync();
        var investor = new Solnet.Wallet.Account();
        var wire = BuildWire(2, [host.RentPubkey, investor.PublicKey.KeyBytes, ProgramId], 2, [0, 1], BuyRelistedShares);

        using var request = Post(Body(wire), investor.PublicKey.Key);
        using var response = await host.Client.SendAsync(request);

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        var json = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(json);
        var signatureBase58 = doc.RootElement.GetProperty("signature").GetString()!;

        Assert.That(new PublicKey(host.RentPubkey).Verify(wire, Encoders.Base58.DecodeData(signatureBase58)), Is.True);
    }

    [Test]
    public async Task Send_property_shares_message_is_signed_and_the_signature_verifies()
    {
        // The sender (instruction account [0]) is the requesting wallet; the rent collector
        // fronts the receiver's holding account creation as payer.
        await using var host = await MarketplaceHost.StartAsync();
        var investor = new Solnet.Wallet.Account();
        var wire = BuildWire(2, [host.RentPubkey, investor.PublicKey.KeyBytes, ProgramId], 2, [1, 0], SendPropertyShares);

        using var request = Post(Body(wire), investor.PublicKey.Key);
        using var response = await host.Client.SendAsync(request);

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        var json = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(json);
        var signatureBase58 = doc.RootElement.GetProperty("signature").GetString()!;

        var verified = new PublicKey(host.RentPubkey).Verify(wire, Encoders.Base58.DecodeData(signatureBase58));
        Assert.That(verified, Is.True, "the returned signature must verify over the exact posted bytes");
    }

    [Test]
    public async Task Claim_income_on_the_property_program_is_signed_and_the_signature_verifies()
    {
        await using var host = await MarketplaceHost.StartAsync();
        var investor = new Solnet.Wallet.Account();
        var wire = BuildWire(
            2, [host.RentPubkey, investor.PublicKey.KeyBytes, PropertyProgramId], 2, [1, 0], ClaimIncome);

        using var request = Post(Body(wire), investor.PublicKey.Key);
        using var response = await host.Client.SendAsync(request);

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        var json = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(json);
        var signatureBase58 = doc.RootElement.GetProperty("signature").GetString()!;

        var verified = new PublicKey(host.RentPubkey).Verify(wire, Encoders.Base58.DecodeData(signatureBase58));
        Assert.That(verified, Is.True, "the returned signature must verify over the exact posted bytes");
    }

    [Test]
    public async Task Close_income_checkpoint_on_the_property_program_is_signed()
    {
        await using var host = await MarketplaceHost.StartAsync();
        var investor = new Solnet.Wallet.Account();
        var wire = BuildWire(
            2, [host.RentPubkey, investor.PublicKey.KeyBytes, PropertyProgramId], 2, [1, 0], CloseIncomeCheckpoint);

        using var request = Post(Body(wire), investor.PublicKey.Key);
        using var response = await host.Client.SendAsync(request);

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        var json = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(json);
        var signatureBase58 = doc.RootElement.GetProperty("signature").GetString()!;

        Assert.That(new PublicKey(host.RentPubkey).Verify(wire, Encoders.Base58.DecodeData(signatureBase58)), Is.True);
    }

    [Test]
    public async Task Vote_on_agent_on_the_property_program_is_signed()
    {
        await using var host = await MarketplaceHost.StartAsync();
        var investor = new Solnet.Wallet.Account();
        var wire = BuildWire(
            2, [host.RentPubkey, investor.PublicKey.KeyBytes, PropertyProgramId], 2, [1, 0], VoteOnAgent);

        using var request = Post(Body(wire), investor.PublicKey.Key);
        using var response = await host.Client.SendAsync(request);

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        var json = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(json);
        var signatureBase58 = doc.RootElement.GetProperty("signature").GetString()!;

        Assert.That(new PublicKey(host.RentPubkey).Verify(wire, Encoders.Base58.DecodeData(signatureBase58)), Is.True);
    }

    [Test]
    public async Task Vote_on_proposal_on_the_property_program_is_signed()
    {
        await using var host = await MarketplaceHost.StartAsync();
        var investor = new Solnet.Wallet.Account();
        var wire = BuildWire(
            2, [host.RentPubkey, investor.PublicKey.KeyBytes, PropertyProgramId], 2, [1, 0], VoteOnProposal);

        using var request = Post(Body(wire), investor.PublicKey.Key);
        using var response = await host.Client.SendAsync(request);

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        var json = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(json);
        var signatureBase58 = doc.RootElement.GetProperty("signature").GetString()!;

        Assert.That(new PublicKey(host.RentPubkey).Verify(wire, Encoders.Base58.DecodeData(signatureBase58)), Is.True);
    }

    [Test]
    public async Task Vote_on_challenge_on_the_property_program_is_signed()
    {
        await using var host = await MarketplaceHost.StartAsync();
        var investor = new Solnet.Wallet.Account();
        var wire = BuildWire(
            2, [host.RentPubkey, investor.PublicKey.KeyBytes, PropertyProgramId], 2, [1, 0], VoteOnChallenge);

        using var request = Post(Body(wire), investor.PublicKey.Key);
        using var response = await host.Client.SendAsync(request);

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        var json = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(json);
        var signatureBase58 = doc.RootElement.GetProperty("signature").GetString()!;

        Assert.That(new PublicKey(host.RentPubkey).Verify(wire, Encoders.Base58.DecodeData(signatureBase58)), Is.True);
    }

    [Test]
    public async Task Claim_bundle_with_close_income_checkpoint_is_signed()
    {
        // The real claim flow: one claim_income per claimable mint, then one
        // close_income_checkpoint, all on the property program in a single message.
        await using var host = await MarketplaceHost.StartAsync();
        var investor = new Solnet.Wallet.Account();
        var keys = new List<byte[]> { host.RentPubkey, investor.PublicKey.KeyBytes, PropertyProgramId };
        byte[] Filler(byte[] disc) => disc.Concat(Enumerable.Repeat((byte)0x42, 8)).ToArray();
        var wire = BuildRawWire(2, keys,
        [
            new WireInstruction(2, [1, 0], Filler(ClaimIncome)),
            new WireInstruction(2, [1, 0], Filler(ClaimIncome)),
            new WireInstruction(2, [1, 0], Filler(CloseIncomeCheckpoint)),
        ]);

        using var request = Post(Body(wire), investor.PublicKey.Key);
        using var response = await host.Client.SendAsync(request);

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        var json = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(json);
        var signatureBase58 = doc.RootElement.GetProperty("signature").GetString()!;

        Assert.That(new PublicKey(host.RentPubkey).Verify(wire, Encoders.Base58.DecodeData(signatureBase58)), Is.True);
    }

    [Test]
    public async Task Empty_property_program_id_env_falls_back_to_the_devnet_program()
    {
        // Same "secret exists but is unset" shape as the marketplace program id fallback.
        var rent = new Solnet.Wallet.Account();
        await using var host = await MarketplaceHost.StartAsync(
            rent.PrivateKey.KeyBytes, rentPropertyProgramId: string.Empty);
        var investor = new Solnet.Wallet.Account();
        var wire = BuildWire(
            2, [host.RentPubkey, investor.PublicKey.KeyBytes, PropertyProgramId], 2, [1, 0], ClaimIncome);

        using var request = Post(Body(wire), investor.PublicKey.Key);
        using var response = await host.Client.SendAsync(request);

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        var json = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(json);
        var signatureBase58 = doc.RootElement.GetProperty("signature").GetString()!;

        Assert.That(new PublicKey(host.RentPubkey).Verify(wire, Encoders.Base58.DecodeData(signatureBase58)), Is.True);
    }

    [Test]
    public async Task Property_discriminator_targeting_the_marketplace_program_is_rejected()
    {
        // claim_income is only allowlisted on the property program.
        await using var host = await MarketplaceHost.StartAsync();
        var investor = new Solnet.Wallet.Account();
        var wire = BuildWire(2, [host.RentPubkey, investor.PublicKey.KeyBytes, ProgramId], 2, [1, 0], ClaimIncome);

        using var request = Post(Body(wire), investor.PublicKey.Key);
        using var response = await host.Client.SendAsync(request);

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
        var json = await response.Content.ReadAsStringAsync();
        Assert.That(json, Does.Contain("discriminator not allowed"));
    }

    [Test]
    public async Task Marketplace_discriminator_targeting_the_property_program_is_rejected()
    {
        // buy_property_shares is only allowlisted on the marketplace program.
        await using var host = await MarketplaceHost.StartAsync();
        var investor = new Solnet.Wallet.Account();
        var wire = BuildWire(
            2, [host.RentPubkey, investor.PublicKey.KeyBytes, PropertyProgramId], 2, [1, 0], Buy);

        using var request = Post(Body(wire), investor.PublicKey.Key);
        using var response = await host.Client.SendAsync(request);

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
        var json = await response.Content.ReadAsStringAsync();
        Assert.That(json, Does.Contain("discriminator not allowed"));
    }

    [Test]
    public async Task Property_program_with_a_non_allowlisted_discriminator_is_rejected()
    {
        await using var host = await MarketplaceHost.StartAsync();
        var investor = new Solnet.Wallet.Account();
        var unknown = new byte[] { 9, 9, 9, 9, 9, 9, 9, 9 };
        var wire = BuildWire(
            2, [host.RentPubkey, investor.PublicKey.KeyBytes, PropertyProgramId], 2, [1, 0], unknown);

        using var request = Post(Body(wire), investor.PublicKey.Key);
        using var response = await host.Client.SendAsync(request);

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
        var json = await response.Content.ReadAsStringAsync();
        Assert.That(json, Does.Contain("discriminator not allowed"));
    }

    [Test]
    public async Task Claim_income_whose_holder_is_not_the_requesting_wallet_is_rejected()
    {
        // The investor signs the message (index 1), but the instruction's holder slot
        // points at someone else's key.
        await using var host = await MarketplaceHost.StartAsync();
        var investor = new Solnet.Wallet.Account();
        var other = new Solnet.Wallet.Account().PublicKey.KeyBytes;
        var keys = new List<byte[]> { host.RentPubkey, investor.PublicKey.KeyBytes, other, PropertyProgramId };
        var wire = BuildWire(2, keys, 3, [2, 0], ClaimIncome);

        using var request = Post(Body(wire), investor.PublicKey.Key);
        using var response = await host.Client.SendAsync(request);

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
        var json = await response.Content.ReadAsStringAsync();
        Assert.That(json, Does.Contain("first account"));
    }

    [Test]
    public async Task Send_property_shares_whose_sender_is_not_the_requesting_wallet_is_rejected()
    {
        await using var host = await MarketplaceHost.StartAsync();
        var investor = new Solnet.Wallet.Account();
        var other = new Solnet.Wallet.Account().PublicKey.KeyBytes;
        var keys = new List<byte[]> { host.RentPubkey, investor.PublicKey.KeyBytes, other, ProgramId };
        var wire = BuildWire(2, keys, 3, [2, 0], SendPropertyShares);

        using var request = Post(Body(wire), investor.PublicKey.Key);
        using var response = await host.Client.SendAsync(request);

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
        var json = await response.Content.ReadAsStringAsync();
        Assert.That(json, Does.Contain("first account"));
    }

    [Test]
    public async Task Vote_on_proposal_whose_voter_is_not_the_requesting_wallet_is_rejected()
    {
        await using var host = await MarketplaceHost.StartAsync();
        var investor = new Solnet.Wallet.Account();
        var other = new Solnet.Wallet.Account().PublicKey.KeyBytes;
        var keys = new List<byte[]> { host.RentPubkey, investor.PublicKey.KeyBytes, other, PropertyProgramId };
        var wire = BuildWire(2, keys, 3, [2, 0], VoteOnProposal);

        using var request = Post(Body(wire), investor.PublicKey.Key);
        using var response = await host.Client.SendAsync(request);

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
        var json = await response.Content.ReadAsStringAsync();
        Assert.That(json, Does.Contain("first account"));
    }

    [Test]
    public async Task Relist_shares_message_is_signed_and_the_signature_verifies()
    {
        // The seller (instruction account [0]) is the requesting wallet; the rent collector
        // fronts the new ShareListing account's rent as payer (account [1]).
        await using var host = await MarketplaceHost.StartAsync();
        var investor = new Solnet.Wallet.Account();
        var wire = BuildWire(2, [host.RentPubkey, investor.PublicKey.KeyBytes, ProgramId], 2, [1, 0], RelistShares);

        using var request = Post(Body(wire), investor.PublicKey.Key);
        using var response = await host.Client.SendAsync(request);

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        var json = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(json);
        var signatureBase58 = doc.RootElement.GetProperty("signature").GetString()!;

        var verified = new PublicKey(host.RentPubkey).Verify(wire, Encoders.Base58.DecodeData(signatureBase58));
        Assert.That(verified, Is.True, "the returned signature must verify over the exact posted bytes");
    }

    [Test]
    public async Task Delist_and_relist_bundle_is_signed()
    {
        // The reprice flow: delist_shares + relist_shares in ONE message, so the listing
        // never sits unlisted between the two. Both pin the seller (account [0]) to the
        // requesting wallet.
        await using var host = await MarketplaceHost.StartAsync();
        var investor = new Solnet.Wallet.Account();
        var keys = new List<byte[]> { host.RentPubkey, investor.PublicKey.KeyBytes, ProgramId };
        byte[] Filler(byte[] disc) => disc.Concat(Enumerable.Repeat((byte)0x42, 8)).ToArray();
        var wire = BuildRawWire(2, keys,
        [
            new WireInstruction(2, [1, 0], Filler(DelistShares)),
            new WireInstruction(2, [1, 0], Filler(RelistShares)),
        ]);

        using var request = Post(Body(wire), investor.PublicKey.Key);
        using var response = await host.Client.SendAsync(request);

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        var json = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(json);
        var signatureBase58 = doc.RootElement.GetProperty("signature").GetString()!;

        Assert.That(new PublicKey(host.RentPubkey).Verify(wire, Encoders.Base58.DecodeData(signatureBase58)), Is.True);
    }

    [Test]
    public async Task Relist_shares_whose_seller_is_not_the_requesting_wallet_is_rejected()
    {
        await using var host = await MarketplaceHost.StartAsync();
        var investor = new Solnet.Wallet.Account();
        var other = new Solnet.Wallet.Account().PublicKey.KeyBytes;
        var keys = new List<byte[]> { host.RentPubkey, investor.PublicKey.KeyBytes, other, ProgramId };
        var wire = BuildWire(2, keys, 3, [2, 0], RelistShares);

        using var request = Post(Body(wire), investor.PublicKey.Key);
        using var response = await host.Client.SendAsync(request);

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
        var json = await response.Content.ReadAsStringAsync();
        Assert.That(json, Does.Contain("first account"));
    }

    [Test]
    public async Task Delist_shares_whose_seller_is_not_the_requesting_wallet_is_rejected()
    {
        await using var host = await MarketplaceHost.StartAsync();
        var investor = new Solnet.Wallet.Account();
        var other = new Solnet.Wallet.Account().PublicKey.KeyBytes;
        var keys = new List<byte[]> { host.RentPubkey, investor.PublicKey.KeyBytes, other, ProgramId };
        var wire = BuildWire(2, keys, 3, [2, 0], DelistShares);

        using var request = Post(Body(wire), investor.PublicKey.Key);
        using var response = await host.Client.SendAsync(request);

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
        var json = await response.Content.ReadAsStringAsync();
        Assert.That(json, Does.Contain("first account"));
    }
}
