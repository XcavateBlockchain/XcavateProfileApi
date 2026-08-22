using System.Net;
using Microsoft.EntityFrameworkCore;
using XcavateProfile.Client;

namespace XcavateBuckets.Tests;

/// <summary>
/// Nicknames are one name in every case they can be written in: "tester" and "Tester" cannot both be
/// registered, and either spelling finds the profile holding it. Driven through the shipped SDK
/// against the real controller and database, so what is pinned here is the behaviour a consumer sees.
/// </summary>
[TestFixture]
public class NicknameCaseTests
{
    private const string X25519Key =
        "0x1234567890abcdef1234567890abcdef1234567890abcdef1234567890abcdef";

    private static Profile ProfileFor(Substrate.NetApi.Model.Types.Account account, string? nickname) =>
        new() { Ss58Address = account.Value, X25519Key = X25519Key, Nickname = nickname };

    // ---- lookup -----------------------------------------------------------------------------------

    [Test]
    public async Task Lookup_finds_a_nickname_written_in_any_case()
    {
        await using var host = await RestHost.StartAsync();
        using var client = host.NewSdkClient();

        var account = TestWallets.Substrate(0x90);
        await client.CreateProfileAsync(ProfileFor(account, "Tester"), account);

        var lowered = await client.GetProfileByNicknameAsync("tester");
        var shouted = await client.GetProfileByNicknameAsync("TESTER");
        var asTyped = await client.GetProfileByNicknameAsync("Tester");

        Assert.Multiple(() =>
        {
            Assert.That(lowered?.Ss58Address, Is.EqualTo(account.Value));
            Assert.That(shouted?.Ss58Address, Is.EqualTo(account.Value));
            Assert.That(asTyped?.Ss58Address, Is.EqualTo(account.Value));
            Assert.That(
                lowered?.Nickname, Is.EqualTo("Tester"),
                "the case the user typed is what is stored and returned");
        });
    }

    [Test]
    public async Task Lookup_still_misses_a_nickname_nobody_holds()
    {
        await using var host = await RestHost.StartAsync();
        using var client = host.NewSdkClient();

        var account = TestWallets.Substrate(0x91);
        await client.CreateProfileAsync(ProfileFor(account, "Tester"), account);

        Assert.That(await client.GetProfileByNicknameAsync("testers"), Is.Null);
    }

    // ---- uniqueness -------------------------------------------------------------------------------

    [Test]
    public async Task Create_refuses_a_nickname_already_held_in_another_case()
    {
        await using var host = await RestHost.StartAsync();
        using var client = host.NewSdkClient();

        var first = TestWallets.Substrate(0x92);
        var second = TestWallets.Substrate(0x93);

        await client.CreateProfileAsync(ProfileFor(first, "tester"), first);

        var error = Assert.ThrowsAsync<HttpRequestException>(
            () => client.CreateProfileAsync(ProfileFor(second, "Tester"), second));

        Assert.Multiple(() =>
        {
            Assert.That(error!.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
            Assert.That(error.Message, Does.Contain("Nickname already exists"));
        });
    }

    [Test]
    public async Task Update_refuses_a_nickname_already_held_in_another_case()
    {
        await using var host = await RestHost.StartAsync();
        using var client = host.NewSdkClient();

        var owner = TestWallets.Substrate(0x94);
        var other = TestWallets.Substrate(0x95);

        await client.CreateProfileAsync(ProfileFor(owner, "tester"), owner);
        var otherProfile = await client.CreateProfileAsync(ProfileFor(other, "someone-else"), other);

        otherProfile.Nickname = "TESTER";

        var error = Assert.ThrowsAsync<HttpRequestException>(
            () => client.UpdateProfileAsync(other.Value, otherProfile, other));

        var stored = await host.StoredProfileAsync(other.Value);

        Assert.Multiple(() =>
        {
            Assert.That(error!.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
            Assert.That(
                stored?.Nickname, Is.EqualTo("someone-else"), "the refused update changed nothing");
        });
    }

    /// <summary>
    /// The upsert branch of PUT creates the profile, and it must be held to the same rule as POST.
    /// </summary>
    [Test]
    public async Task Upsert_refuses_a_nickname_already_held_in_another_case()
    {
        await using var host = await RestHost.StartAsync();
        using var client = host.NewSdkClient();

        var first = TestWallets.Substrate(0x96);
        var second = TestWallets.Substrate(0x97);

        await client.CreateProfileAsync(ProfileFor(first, "tester"), first);

        var error = Assert.ThrowsAsync<HttpRequestException>(
            () => client.UpdateProfileAsync(second.Value, ProfileFor(second, "TeStEr"), second));

        var stored = await host.StoredProfileAsync(second.Value);

        Assert.Multiple(() =>
        {
            Assert.That(error!.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
            Assert.That(stored, Is.Null, "nothing was created");
        });
    }

    /// <summary>
    /// Recasing is not claiming: the name is already the owner's, so only its spelling changes.
    /// </summary>
    [Test]
    public async Task An_owner_may_recase_their_own_nickname()
    {
        await using var host = await RestHost.StartAsync();
        using var client = host.NewSdkClient();

        var account = TestWallets.Substrate(0x98);
        var profile = await client.CreateProfileAsync(ProfileFor(account, "tester"), account);

        profile.Nickname = "TesTer";
        var updated = await client.UpdateProfileAsync(account.Value, profile, account);

        var found = await client.GetProfileByNicknameAsync("tester");

        Assert.Multiple(() =>
        {
            Assert.That(updated.Nickname, Is.EqualTo("TesTer"));
            Assert.That(
                found?.Ss58Address, Is.EqualTo(account.Value),
                "the old spelling still finds the profile, because it is the same name");
        });
    }

    /// <summary>
    /// The nickname a profile gives up is free for the next caller, in any case they write it.
    /// </summary>
    [Test]
    public async Task A_released_nickname_can_be_taken_in_another_case()
    {
        await using var host = await RestHost.StartAsync();
        using var client = host.NewSdkClient();

        var first = TestWallets.Substrate(0x99);
        var second = TestWallets.Substrate(0x9A);

        var firstProfile = await client.CreateProfileAsync(ProfileFor(first, "tester"), first);
        firstProfile.Nickname = "moved-on";
        await client.UpdateProfileAsync(first.Value, firstProfile, first);

        var taken = await client.CreateProfileAsync(ProfileFor(second, "TESTER"), second);

        var found = await client.GetProfileByNicknameAsync("tester");

        Assert.Multiple(() =>
        {
            Assert.That(taken.Nickname, Is.EqualTo("TESTER"));
            Assert.That(found?.Ss58Address, Is.EqualTo(second.Value));
        });
    }

    /// <summary>
    /// Having no nickname is not a way of sharing one — including the empty string, which the unique
    /// index would otherwise treat as a value two profiles cannot both have.
    /// </summary>
    [Test]
    public async Task Profiles_without_a_nickname_do_not_clash()
    {
        await using var host = await RestHost.StartAsync();
        using var client = host.NewSdkClient();

        var first = TestWallets.Substrate(0x9B);
        var second = TestWallets.Substrate(0x9C);
        var third = TestWallets.Substrate(0x9D);

        await client.CreateProfileAsync(ProfileFor(first, null), first);
        await client.CreateProfileAsync(ProfileFor(second, ""), second);
        await client.CreateProfileAsync(ProfileFor(third, "   "), third);

        var storedSecond = await host.StoredProfileAsync(second.Value);
        var storedThird = await host.StoredProfileAsync(third.Value);
        var blankLookup = await client.GetProfileByNicknameAsync("");

        Assert.Multiple(() =>
        {
            Assert.That(storedSecond, Is.Not.Null);
            Assert.That(storedThird, Is.Not.Null);
            Assert.That(blankLookup, Is.Null, "a blank nickname matches nobody");
        });
    }

    // ---- the schema, not the controller -----------------------------------------------------------

    /// <summary>
    /// The rule is in the database as well as in the endpoint, so a write that never passes through
    /// ProfilesController cannot slip a second spelling in.
    /// </summary>
    [Test]
    public async Task The_database_refuses_a_case_variant_written_behind_the_api()
    {
        await using var host = await RestHost.StartAsync();

        await host.SeedProfileAsync(ProfileFor(TestWallets.Substrate(0x9E), "tester"));

        Assert.ThrowsAsync<DbUpdateException>(
            () => host.SeedProfileAsync(ProfileFor(TestWallets.Substrate(0x9F), "TESTER")));
    }
}
