using NUnit.Framework;
using System;
using System.IO;
using System.Net.Http;
using System.Threading.Tasks;
using XcavateProfile.Client;
using XcavateProfileApiClient;
using XcavateProfileApiClient.Signing;

namespace XcavateProfile.ApiTests;

/// <summary>
/// The sr25519 profile lifecycle, re-run with a Solana identity against the live stack. Requires
/// the docker stack on http://localhost:5000 and SolanaAccounts.Admin in ADMIN_ADDRESSES.
/// </summary>
[TestFixture]
public class SolanaProfileApiTests
{
    private const string TestApiUrl = "http://localhost:5000";
    private const string X25519Key =
        "0x1234567890abcdef1234567890abcdef1234567890abcdef1234567890abcdef";

    private XcavateProfileClient? _client;

    [SetUp]
    public void Setup() =>
        _client = new XcavateProfileClient(new XcavateProfileClientOptions { ApiUrl = TestApiUrl });

    [TearDown]
    public void TearDown() => _client?.Dispose();

    private static IRequestSigner Signer(Solnet.Wallet.Account account) =>
        new SolanaRequestSigner(account);

    /// <summary>The database persists across runs, so clear the persona's profile first.</summary>
    private static async Task EnsureNoProfileAsync(XcavateProfileClient client, IRequestSigner signer)
    {
        try
        {
            await client.DeleteProfileAsync(signer.Address, signer);
        }
        catch (HttpRequestException)
        {
            // 404 — nothing to clean up.
        }
    }

    [Test]
    public async Task Create_profile_with_a_solana_signatureAsync()
    {
        var signer = Signer(SolanaAccounts.Base);
        await EnsureNoProfileAsync(_client!, signer);

        var profile = new Profile
        {
            Ss58Address = signer.Address,
            Nickname = "solana-testuser",
            Bio = "Signed with ed25519",
            X25519Key = X25519Key
        };

        var result = await _client!.CreateProfileAsync(profile, signer);

        Assert.Multiple(() =>
        {
            Assert.That(result.Ss58Address, Is.EqualTo(signer.Address));
            Assert.That(result.Nickname, Is.EqualTo("solana-testuser"));
        });
    }

    [Test]
    public async Task Update_profile_with_a_solana_signatureAsync()
    {
        var signer = Signer(SolanaAccounts.User1);
        await EnsureNoProfileAsync(_client!, signer);

        var profile = new Profile
        {
            Ss58Address = signer.Address,
            Nickname = "solana-user1",
            X25519Key = X25519Key
        };
        await _client!.CreateProfileAsync(profile, signer);

        profile.Bio = "Updated over ed25519";
        var result = await _client.UpdateProfileAsync(signer.Address, profile, signer);

        Assert.That(result.Bio, Is.EqualTo("Updated over ed25519"));
    }

    [Test]
    public async Task Delete_profile_with_a_solana_signatureAsync()
    {
        var signer = Signer(SolanaAccounts.User2);
        await EnsureNoProfileAsync(_client!, signer);

        await _client!.CreateProfileAsync(
            new Profile
            {
                Ss58Address = signer.Address,
                Nickname = "solana-user2",
                X25519Key = X25519Key
            },
            signer);

        await _client.DeleteProfileAsync(signer.Address, signer);

        Assert.That(await _client.GetProfileAsync(signer.Address), Is.Null);
    }

    [Test]
    public async Task Upload_image_with_a_solana_signatureAsync()
    {
        var signer = Signer(SolanaAccounts.Base);
        await EnsureNoProfileAsync(_client!, signer);

        await _client!.CreateProfileAsync(
            new Profile
            {
                Ss58Address = signer.Address,
                Nickname = "solana-imageuser",
                X25519Key = X25519Key
            },
            signer);

        // A 1x1 PNG.
        var png = Convert.FromBase64String(
            "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg==");

        using var stream = new MemoryStream(png);
        var url = await _client.UploadImageAsync(signer.Address, stream, "solana-test.png", signer);

        Assert.That(url, Does.Contain("solana-test.png"));
    }

    /// <summary>A Solana address in ADMIN_ADDRESSES gets the same privileges as an SS58 one.</summary>
    [Test]
    public async Task Solana_admin_can_update_another_users_profileAsync()
    {
        var admin = Signer(SolanaAccounts.Admin);
        var user = Signer(SolanaAccounts.User1);
        await EnsureNoProfileAsync(_client!, user);

        var profile = new Profile
        {
            Ss58Address = user.Address,
            Nickname = "solana-victim",
            X25519Key = X25519Key
        };
        await _client!.CreateProfileAsync(profile, user);

        profile.Bio = "Edited by a Solana admin";
        var result = await _client.UpdateProfileAsync(user.Address, profile, admin);

        Assert.That(result.Bio, Is.EqualTo("Edited by a Solana admin"));
    }

    /// <summary>A Solana caller must not be able to write someone else's profile.</summary>
    [Test]
    public async Task Non_admin_solana_caller_cannot_update_another_users_profileAsync()
    {
        var owner = Signer(SolanaAccounts.User1);
        var attacker = Signer(SolanaAccounts.User2);
        await EnsureNoProfileAsync(_client!, owner);

        var profile = new Profile
        {
            Ss58Address = owner.Address,
            Nickname = "solana-owner",
            X25519Key = X25519Key
        };
        await _client!.CreateProfileAsync(profile, owner);

        profile.Nickname = "hacked";

        Assert.ThrowsAsync<HttpRequestException>(
            async () => await _client.UpdateProfileAsync(owner.Address, profile, attacker));
    }
}
