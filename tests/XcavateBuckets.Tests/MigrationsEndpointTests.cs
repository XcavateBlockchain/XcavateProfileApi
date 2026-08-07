using Solnet.Wallet;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Substrate.NET.Wallet.Keyring;
using Substrate.NetApi;
using Substrate.NetApi.Model.Types;
using XcavateProfile.Client;
using XcavateProfileApiClient;
using XcavateProfileApiClient.Signing;
using static Substrate.NetApi.Mnemonic;
using Account = Substrate.NetApi.Model.Types.Account;
using SolMnemonic = Solnet.Wallet.Bip39.Mnemonic;
using SolWordList = Solnet.Wallet.Bip39.WordList;
using TextEncoding = System.Text.Encoding;

namespace XcavateBuckets.Tests;

/// <summary>
/// The wallet-migration endpoints driven through the shipped SDK client against the real controller,
/// validator and database — no docker, unlike <c>MigrationApiTests</c>.
/// </summary>
/// <remarks>
/// The registration path is the reason this fixture exists. The client signs
/// <c>POST:/api/migrations:&lt;hash of the JSON it sends&gt;:&lt;timestamp&gt;</c>; the server never sees
/// that hash, it re-serializes the <see cref="WalletMigration"/> its model binder produced and hashes
/// that instead. A 201 here is proof the two computations agree — the thing that would silently
/// break if <c>WalletMigration.Hash()</c> hashed anything other than the bytes actually posted.
/// </remarks>
[TestFixture]
public class MigrationsEndpointTests
{
    /// <summary>Restates the SDK's internal serializer options: these are the bytes on the wire.</summary>
    private static readonly JsonSerializerOptions WireOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

    private static Account SubstrateAccount(byte fill)
    {
        var mnemonic = string.Join(
            " ", MnemonicFromEntropy(Enumerable.Repeat(fill, 16).ToArray(), BIP39Wordlist.English));

        return new Keyring()
            .AddFromMnemonic(mnemonic, new Meta { Name = "MigrationTests" }, KeyType.Sr25519)
            .Account;
    }

    private static Solnet.Wallet.Account SolanaAccount(byte fill)
    {
        var mnemonic = string.Join(
            " ", MnemonicFromEntropy(Enumerable.Repeat(fill, 16).ToArray(), BIP39Wordlist.English));

        return new Wallet(new SolMnemonic(mnemonic, SolWordList.English)).Account;
    }

    private static string RandomSolanaAddress() => new Solnet.Wallet.Account().PublicKey.Key;

    private static WalletMigration MigrationFor(Account account, string? destination = null) =>
        new() { Ss58Address = account.Value, SolanaAddress = destination ?? RandomSolanaAddress() };

    private static string Json(WalletMigration migration) =>
        JsonSerializer.Serialize(migration, WireOptions);

    /// <summary>
    /// Builds the request the client would build, but lets the signed body and the posted body
    /// differ — which is how the tamper case below is expressed.
    /// </summary>
    private static async Task<HttpRequestMessage> SignedPostAsync(
        IRequestSigner signer,
        WalletMigration signedBody,
        string? postedJson = null,
        DateTime? timestamp = null)
    {
        var ts = timestamp ?? DateTime.UtcNow;
        var payload = CryptoHelper.ConstructPayload("POST", "/api/migrations", signedBody, ts);
        var signature = await signer.SignAsync(payload);

        var request = new HttpRequestMessage(HttpMethod.Post, "/api/migrations")
        {
            Content = new StringContent(
                postedJson ?? Json(signedBody), TextEncoding.UTF8, "application/json")
        };

        request.Headers.Add(SignedRequestHeaders.Address, signer.Address);
        request.Headers.Add(SignedRequestHeaders.Signature, signer.EncodeSignature(signature));
        request.Headers.Add(SignedRequestHeaders.Timestamp, ts.ToUniversalTime().ToString("o"));

        return request;
    }

    // ---- the happy path, which is the body-hash agreement proof ---------------------------------

    [Test]
    public async Task Register_accepts_a_signature_over_the_bytes_the_client_posted()
    {
        await using var host = await MigrationsHost.StartAsync();
        using var client = host.NewSdkClient();

        var account = SubstrateAccount(0x51);
        var migration = MigrationFor(account);

        var created = await client.RegisterWalletMigrationAsync(migration, account);
        var stored = await host.StoredAsync(account.Value);

        Assert.Multiple(() =>
        {
            Assert.That(created.Ss58Address, Is.EqualTo(migration.Ss58Address));
            Assert.That(created.SolanaAddress, Is.EqualTo(migration.SolanaAddress));
            Assert.That(
                stored?.SolanaAddress, Is.EqualTo(migration.SolanaAddress),
                "the pair must reach the database");
        });
    }

    /// <summary>
    /// The same registration signed by the <see cref="IRequestSigner"/> overload rather than the
    /// sr25519 convenience one, so both entry points are covered.
    /// </summary>
    [Test]
    public async Task Register_accepts_the_signer_overload()
    {
        await using var host = await MigrationsHost.StartAsync();
        using var client = host.NewSdkClient();

        var account = SubstrateAccount(0x52);
        var migration = MigrationFor(account);

        var created = await client.RegisterWalletMigrationAsync(
            migration, new SubstrateRequestSigner(account));

        Assert.That(created.SolanaAddress, Is.EqualTo(migration.SolanaAddress));
    }

    /// <summary>
    /// The destination is inside the signed hash, so swapping it after signing must be rejected.
    /// If the server hashed the request bytes without re-deriving them — or if the hash covered only
    /// the SS58 address — this would be a silent account hijack.
    /// </summary>
    [Test]
    public async Task Register_rejects_a_destination_swapped_after_signing()
    {
        await using var host = await MigrationsHost.StartAsync();

        var account = SubstrateAccount(0x53);
        var signed = MigrationFor(account);
        var tampered = MigrationFor(account, RandomSolanaAddress());

        using var request = await SignedPostAsync(
            new SubstrateRequestSigner(account), signed, postedJson: Json(tampered));
        using var response = await host.Client.SendAsync(request);
        var stored = await host.StoredAsync(account.Value);

        Assert.Multiple(() =>
        {
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized));
            Assert.That(stored, Is.Null);
        });
    }

    // ---- the controller's refusals --------------------------------------------------------------

    [Test]
    public async Task Register_without_credentials_is_unauthorized()
    {
        await using var host = await MigrationsHost.StartAsync();

        using var response = await host.Client.PostAsync(
            "/api/migrations",
            new StringContent(
                Json(MigrationFor(SubstrateAccount(0x54))), TextEncoding.UTF8, "application/json"));

        var body = await response.Content.ReadAsStringAsync();

        Assert.Multiple(() =>
        {
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized));
            Assert.That(body, Does.Contain("Missing authentication"));
        });
    }

    /// <summary>
    /// A genuine ed25519 signature from a Solana wallet registering itself: the header address
    /// equals the body's, so only the SS58 format check stands between this and the table.
    /// </summary>
    [Test]
    public async Task Register_rejects_a_non_ss58_source()
    {
        await using var host = await MigrationsHost.StartAsync();
        using var client = host.NewSdkClient();

        var signer = new SolanaRequestSigner(SolanaAccount(0x55));
        var migration = new WalletMigration
        {
            Ss58Address = signer.Address,
            SolanaAddress = RandomSolanaAddress()
        };

        var ex = Assert.ThrowsAsync<HttpRequestException>(
            () => client.RegisterWalletMigrationAsync(migration, signer));

        Assert.Multiple(() =>
        {
            Assert.That(ex!.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
            Assert.That(ex.Message, Does.Contain("ss58address"));
        });
    }

    /// <summary>An SS58 address is base58 too, but decodes to 35 bytes — not an ed25519 key.</summary>
    [Test]
    public async Task Register_rejects_a_destination_that_is_not_a_solana_key()
    {
        await using var host = await MigrationsHost.StartAsync();
        using var client = host.NewSdkClient();

        var account = SubstrateAccount(0x56);
        var migration = MigrationFor(account, destination: account.Value);

        var ex = Assert.ThrowsAsync<HttpRequestException>(
            () => client.RegisterWalletMigrationAsync(migration, account));

        Assert.Multiple(() =>
        {
            Assert.That(ex!.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
            Assert.That(ex.Message, Does.Contain("solanaAddress"));
        });
    }

    /// <summary>A valid sr25519 signature, but from a wallet other than the one being migrated.</summary>
    [Test]
    public async Task Register_for_another_account_is_unauthorized()
    {
        await using var host = await MigrationsHost.StartAsync();
        using var client = host.NewSdkClient();

        var victim = SubstrateAccount(0x57);
        var attacker = SubstrateAccount(0x58);

        var ex = Assert.ThrowsAsync<HttpRequestException>(
            () => client.RegisterWalletMigrationAsync(MigrationFor(victim), attacker));
        var stored = await host.StoredAsync(victim.Value);

        Assert.Multiple(() =>
        {
            Assert.That(ex!.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized));
            Assert.That(stored, Is.Null);
        });
    }

    /// <summary>A signature over a different payload has to fail verification, not bind anyway.</summary>
    [Test]
    public async Task Register_with_a_signature_over_a_different_payload_is_unauthorized()
    {
        await using var host = await MigrationsHost.StartAsync();

        var account = SubstrateAccount(0x59);
        var migration = MigrationFor(account);
        var signer = new SubstrateRequestSigner(account);

        // Signed for the wrong path; everything else about the request is well formed.
        var signature = await signer.SignAsync(
            CryptoHelper.ConstructPayload(
                "POST", "/api/profiles", migration, DateTime.UtcNow));

        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/migrations")
        {
            Content = new StringContent(Json(migration), TextEncoding.UTF8, "application/json")
        };
        request.Headers.Add(SignedRequestHeaders.Address, signer.Address);
        request.Headers.Add(SignedRequestHeaders.Signature, signer.EncodeSignature(signature));
        request.Headers.Add(SignedRequestHeaders.Timestamp, DateTime.UtcNow.ToString("o"));

        using var response = await host.Client.SendAsync(request);

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized));
    }

    /// <summary>A signature outside the skew window is refused however valid it is.</summary>
    [Test]
    public async Task Register_with_a_stale_timestamp_is_unauthorized()
    {
        await using var host = await MigrationsHost.StartAsync();

        var account = SubstrateAccount(0x5A);

        using var request = await SignedPostAsync(
            new SubstrateRequestSigner(account),
            MigrationFor(account),
            timestamp: DateTime.UtcNow.AddHours(-1));
        using var response = await host.Client.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();

        Assert.Multiple(() =>
        {
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized));
            Assert.That(body, Does.Contain("Timestamp"));
        });
    }

    [Test]
    public async Task Register_twice_for_the_same_account_is_refused()
    {
        await using var host = await MigrationsHost.StartAsync();
        using var client = host.NewSdkClient();

        var account = SubstrateAccount(0x5B);
        var first = MigrationFor(account);
        await client.RegisterWalletMigrationAsync(first, account);

        var ex = Assert.ThrowsAsync<HttpRequestException>(
            () => client.RegisterWalletMigrationAsync(MigrationFor(account), account));
        var stored = await host.StoredAsync(account.Value);

        Assert.Multiple(() =>
        {
            Assert.That(ex!.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
            Assert.That(ex.Message, Does.Contain("already registered"));
            Assert.That(
                stored?.SolanaAddress, Is.EqualTo(first.SolanaAddress),
                "the original destination must survive the rejected overwrite");
        });
    }

    /// <summary>
    /// Both properties are <c>required</c>, so a body missing one never reaches the controller —
    /// the model binder refuses it first.
    /// </summary>
    [Test]
    public async Task Register_without_a_destination_is_a_bad_request()
    {
        await using var host = await MigrationsHost.StartAsync();

        using var response = await host.Client.PostAsync(
            "/api/migrations",
            new StringContent(
                $"{{\"ss58address\":\"{SubstrateAccount(0x5C).Value}\"}}",
                TextEncoding.UTF8,
                "application/json"));

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
    }

    // ---- the public reads -----------------------------------------------------------------------

    [Test]
    public async Task Registered_migrations_are_readable_by_address_and_in_the_list()
    {
        await using var host = await MigrationsHost.StartAsync();
        using var client = host.NewSdkClient();

        var first = SubstrateAccount(0x5D);
        var second = SubstrateAccount(0x5E);
        var firstMigration = MigrationFor(first);
        var secondMigration = MigrationFor(second);

        await client.RegisterWalletMigrationAsync(firstMigration, first);
        await client.RegisterWalletMigrationAsync(secondMigration, second);

        var byAddress = await client.GetWalletMigrationAsync(first.Value);
        var all = await client.GetWalletMigrationsAsync();

        Assert.Multiple(() =>
        {
            Assert.That(byAddress, Is.Not.Null);
            Assert.That(byAddress!.SolanaAddress, Is.EqualTo(firstMigration.SolanaAddress));
            Assert.That(
                all.Select(m => m.Ss58Address),
                Is.EquivalentTo(new[] { first.Value, second.Value }));
        });
    }

    [Test]
    public async Task An_unregistered_address_reads_back_as_null()
    {
        await using var host = await MigrationsHost.StartAsync();
        using var client = host.NewSdkClient();

        Assert.That(
            await client.GetWalletMigrationAsync(SubstrateAccount(0x5F).Value), Is.Null);
    }

    [Test]
    public async Task The_list_is_empty_before_anything_is_registered()
    {
        await using var host = await MigrationsHost.StartAsync();
        using var client = host.NewSdkClient();

        Assert.That(await client.GetWalletMigrationsAsync(), Is.Empty);
    }

    /// <summary>The created resource is addressable at the location the 201 advertises.</summary>
    [Test]
    public async Task Register_returns_a_location_pointing_at_the_registration()
    {
        await using var host = await MigrationsHost.StartAsync();

        var account = SubstrateAccount(0x60);
        var migration = MigrationFor(account);

        using var request = await SignedPostAsync(new SubstrateRequestSigner(account), migration);
        using var response = await host.Client.SendAsync(request);

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Created));

        var location = response.Headers.Location!;
        using var followed = await host.Client.GetAsync(location);
        var fetched = await followed.Content.ReadFromJsonAsync<WalletMigration>();

        Assert.Multiple(() =>
        {
            Assert.That(location.ToString(), Does.Contain(account.Value));
            Assert.That(followed.StatusCode, Is.EqualTo(HttpStatusCode.OK));
            Assert.That(fetched?.SolanaAddress, Is.EqualTo(migration.SolanaAddress));
        });
    }

    // ---- client argument guards ------------------------------------------------------------------

    [Test]
    public async Task The_client_refuses_null_arguments()
    {
        await using var host = await MigrationsHost.StartAsync();
        using var client = host.NewSdkClient();

        var account = SubstrateAccount(0x61);

        Assert.Multiple(() =>
        {
            Assert.ThrowsAsync<ArgumentNullException>(
                () => client.RegisterWalletMigrationAsync(null!, account));
            Assert.ThrowsAsync<ArgumentNullException>(
                () => client.RegisterWalletMigrationAsync(
                    MigrationFor(account), (Account)null!));
            Assert.ThrowsAsync<ArgumentNullException>(
                () => client.RegisterWalletMigrationAsync(
                    MigrationFor(account), (IRequestSigner)null!));
        });
    }
}
