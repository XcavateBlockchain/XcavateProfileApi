using NUnit.Framework;
using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Threading.Tasks;
using XcavateProfile.Client;
using XcavateProfileApiClient;
using XcavateProfileApiClient.Signing;
using static Substrate.NetApi.Mnemonic;
using SubstrateAccount = Substrate.NetApi.Model.Types.Account;

namespace XcavateProfile.ApiTests;

/// <summary>
/// The Polkadot → Solana wallet migration endpoints, run against the live stack on
/// http://localhost:5000 (same docker setup as <see cref="ProfileApiTests"/>).
/// </summary>
/// <remarks>
/// A migration cannot be deleted through the API, and the database persists across runs — so every
/// test that registers one derives a fresh account from random entropy instead of reusing the
/// fixed <see cref="TestMnemonics"/> personas. Reruns then always start from an unregistered
/// address, at the cost of one extra row in the test database per run.
/// </remarks>
[TestFixture]
public class MigrationApiTests
{
    private const string TestApiUrl = "http://localhost:5000";

    private XcavateProfileClient? _client;

    [SetUp]
    public void Setup() =>
        _client = new XcavateProfileClient(new XcavateProfileClientOptions { ApiUrl = TestApiUrl });

    [TearDown]
    public void TearDown() => _client?.Dispose();

    /// <summary>A valid BIP39 phrase from fresh random entropy — a never-before-seen account.</summary>
    private static string RandomMnemonic()
    {
        var entropy = new byte[16];
        RandomNumberGenerator.Fill(entropy);
        return string.Join(" ", MnemonicFromEntropy(entropy, BIP39Wordlist.English));
    }

    private static SubstrateAccount RandomPolkadotAccount() =>
        MnemonicsModel.GetAccountFromMnemonics(RandomMnemonic());

    /// <summary>A valid, random Solana destination address (base58 of a fresh ed25519 key).</summary>
    private static string RandomSolanaAddress() => new Solnet.Wallet.Account().PublicKey.Key;

    [Test]
    public async Task Register_WalletMigration_SuccessAsync()
    {
        var account = RandomPolkadotAccount();
        var migration = new WalletMigration
        {
            Ss58Address = account.Value,
            SolanaAddress = RandomSolanaAddress()
        };

        var result = await _client!.RegisterWalletMigrationAsync(migration, account);

        Assert.Multiple(() =>
        {
            Assert.That(result.Ss58Address, Is.EqualTo(migration.Ss58Address));
            Assert.That(result.SolanaAddress, Is.EqualTo(migration.SolanaAddress));
        });
    }

    [Test]
    public async Task Registered_Migration_Appears_In_The_ListAsync()
    {
        var account = RandomPolkadotAccount();
        var migration = new WalletMigration
        {
            Ss58Address = account.Value,
            SolanaAddress = RandomSolanaAddress()
        };
        await _client!.RegisterWalletMigrationAsync(migration, account);

        var all = await _client.GetWalletMigrationsAsync();

        var stored = all.SingleOrDefault(m => m.Ss58Address == migration.Ss58Address);
        Assert.That(stored, Is.Not.Null);
        Assert.That(stored!.SolanaAddress, Is.EqualTo(migration.SolanaAddress));
    }

    [Test]
    public async Task Get_WalletMigration_By_AddressAsync()
    {
        var account = RandomPolkadotAccount();
        var migration = new WalletMigration
        {
            Ss58Address = account.Value,
            SolanaAddress = RandomSolanaAddress()
        };
        await _client!.RegisterWalletMigrationAsync(migration, account);

        var stored = await _client.GetWalletMigrationAsync(account.Value);

        Assert.That(stored, Is.Not.Null);
        Assert.That(stored!.SolanaAddress, Is.EqualTo(migration.SolanaAddress));
    }

    [Test]
    public async Task Get_Unknown_WalletMigration_Returns_NullAsync()
    {
        // A fresh account that never registered anything
        var unknown = RandomPolkadotAccount().Value;

        Assert.That(await _client!.GetWalletMigrationAsync(unknown), Is.Null);
    }

    [Test]
    public async Task Register_Duplicate_Migration_FailsAsync()
    {
        var account = RandomPolkadotAccount();
        var migration = new WalletMigration
        {
            Ss58Address = account.Value,
            SolanaAddress = RandomSolanaAddress()
        };
        await _client!.RegisterWalletMigrationAsync(migration, account);

        // A second registration must be rejected even with a different destination —
        // one Polkadot account migrates exactly once
        var second = new WalletMigration
        {
            Ss58Address = account.Value,
            SolanaAddress = RandomSolanaAddress()
        };

        var ex = Assert.ThrowsAsync<HttpRequestException>(
            () => _client.RegisterWalletMigrationAsync(second, account));
        Assert.That(ex?.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
    }

    [Test]
    public async Task Register_For_Another_Account_FailsAsync()
    {
        // The signature is valid sr25519 — but from a different wallet than the one
        // being migrated, so the server must refuse to store the pair
        var victim = RandomPolkadotAccount();
        var attacker = RandomPolkadotAccount();

        var migration = new WalletMigration
        {
            Ss58Address = victim.Value,
            SolanaAddress = RandomSolanaAddress()
        };

        var ex = Assert.ThrowsAsync<HttpRequestException>(
            () => _client!.RegisterWalletMigrationAsync(migration, attacker));
        Assert.That(ex?.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized));

        // Nothing must have been stored for the victim
        Assert.That(await _client!.GetWalletMigrationAsync(victim.Value), Is.Null);
    }

    [Test]
    public async Task Register_With_Solana_Signature_FailsAsync()
    {
        // A valid ed25519 signature over the correct payload, but from a Solana wallet:
        // the header address is base58, never equal to the SS58 address in the body
        var polkadotAccount = RandomPolkadotAccount();
        var solanaSigner = new SolanaRequestSigner(SolanaAccounts.Base);

        var migration = new WalletMigration
        {
            Ss58Address = polkadotAccount.Value,
            SolanaAddress = RandomSolanaAddress()
        };

        var ex = Assert.ThrowsAsync<HttpRequestException>(
            () => _client!.RegisterWalletMigrationAsync(migration, solanaSigner));
        Assert.That(ex?.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized));
    }

    [Test]
    public void Register_Solana_Account_As_Migration_Source_Fails()
    {
        // A Solana wallet signing for itself: header address equals the body's ss58address and
        // the ed25519 signature is genuine, so only the SS58 format check stands between this
        // request and the table. It must come back 400, proving the source of a migration can
        // only ever be a Polkadot account.
        var solanaSigner = new SolanaRequestSigner(SolanaAccounts.User1);

        var migration = new WalletMigration
        {
            Ss58Address = solanaSigner.Address,
            SolanaAddress = RandomSolanaAddress()
        };

        var ex = Assert.ThrowsAsync<HttpRequestException>(
            () => _client!.RegisterWalletMigrationAsync(migration, solanaSigner));
        Assert.That(ex?.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
    }

    [Test]
    public void Register_With_Invalid_Solana_Destination_Fails()
    {
        // An SS58 string is base58 too, but decodes to 35 bytes — not a Solana public key
        var account = RandomPolkadotAccount();
        var migration = new WalletMigration
        {
            Ss58Address = account.Value,
            SolanaAddress = account.Value
        };

        var ex = Assert.ThrowsAsync<HttpRequestException>(
            () => _client!.RegisterWalletMigrationAsync(migration, account));
        Assert.That(ex?.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
    }
}
