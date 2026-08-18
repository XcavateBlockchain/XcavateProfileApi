using NUnit.Framework;
using Substrate.NetApi.Model.Types;
using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
using XcavateProfile.Client;
using XcavateProfileApiClient;

namespace XcavateProfile.ApiTests;

/// <summary>
/// The user attributes on a profile — identity, contact, roles, clearance, timestamps — against a
/// running API and a real PostgreSQL database.
/// </summary>
/// <remarks>
/// The in-process suite covers the rules; this is where the storage is proven. <c>roles</c> and
/// <c>permission</c> are JSON text columns and the timestamps are <c>timestamptz</c>, none of which
/// SQLite is strict about, so a round trip through Npgsql is the only thing that shows the mapping
/// holds.
/// </remarks>
[TestFixture]
public class ProfileAttributeApiTests
{
    private const string TestApiUrl = "http://localhost:5000";

    private const string X25519Key =
        "0x1234567890abcdef1234567890abcdef1234567890abcdef1234567890abcdef";

    private XcavateProfileClient? _client;

    private static Account Owner => MnemonicsModel.GetAccountFromMnemonics(
        TestMnemonics.ProfileAttributesMnemonic);

    [SetUp]
    public async Task SetUpAsync()
    {
        _client = new XcavateProfileClient(new XcavateProfileClientOptions { ApiUrl = TestApiUrl });

        // The database persists between runs, so start from no profile for this account.
        try
        {
            await _client.DeleteProfileAsync(Owner.Value, Owner);
        }
        catch (HttpRequestException)
        {
            // 404 — nothing to clean up
        }
    }

    [TearDown]
    public void TearDown() => _client?.Dispose();

    private static Profile ProfileFor(Account account) =>
        new()
        {
            Ss58Address = account.Value,
            X25519Key = X25519Key,
            Name = "Ada Lovelace",
            Email = "ada@example.com",
            Phone = "+44 20 7123 4567",
            Address = "1 Example Street, London",
            Title = "Analyst",
            Background = "Mathematics",
            Bio = "Writes notes",
            Roles = new List<UserRole> { UserRole.Investor, UserRole.Developer },
        };

    [Test]
    public async Task Create_Profile_Stores_Every_AttributeAsync()
    {
        var created = await _client!.CreateProfileAsync(ProfileFor(Owner), Owner);
        var read = await _client.GetProfileAsync(Owner.Value);

        Assert.Multiple(() =>
        {
            Assert.That(created.UserId, Is.EqualTo(Owner.Value), "userId is the wallet address");
            Assert.That(read?.Name, Is.EqualTo("Ada Lovelace"));
            Assert.That(read?.Email, Is.EqualTo("ada@example.com"));
            Assert.That(read?.Phone, Is.EqualTo("+44 20 7123 4567"));
            Assert.That(read?.Address, Is.EqualTo("1 Example Street, London"));
            Assert.That(read?.Title, Is.EqualTo("Analyst"));
            Assert.That(read?.Background, Is.EqualTo("Mathematics"));
            Assert.That(
                read?.Roles,
                Is.EquivalentTo(new List<UserRole> { UserRole.Investor, UserRole.Developer }),
                "the roles survive the JSON column");
            Assert.That(read?.CreatedAt, Is.Not.Null);
            Assert.That(read?.UpdatedAt, Is.Not.Null);
        });
    }

    [Test]
    public async Task Update_Profile_Changes_Attributes_And_Keeps_Created_AtAsync()
    {
        var created = await _client!.CreateProfileAsync(ProfileFor(Owner), Owner);

        created.Title = "Principal Analyst";
        created.Roles = new List<UserRole> { UserRole.RegionalOperator, UserRole.Spv };

        var updated = await _client.UpdateProfileAsync(Owner.Value, created, Owner);
        var read = await _client.GetProfileAsync(Owner.Value);

        Assert.Multiple(() =>
        {
            Assert.That(updated.Title, Is.EqualTo("Principal Analyst"));
            Assert.That(
                read?.Roles,
                Is.EquivalentTo(new List<UserRole> { UserRole.RegionalOperator, UserRole.Spv }));
            Assert.That(read?.CreatedAt, Is.EqualTo(created.CreatedAt), "createdAt never moves");
            Assert.That(read?.UpdatedAt, Is.GreaterThanOrEqualTo(created.UpdatedAt!.Value));
        });
    }

    [Test]
    public async Task User_Cannot_Set_Their_Own_PermissionAsync()
    {
        var profile = ProfileFor(Owner);
        profile.Permission = new UserPermissions { Investor = PermissionStatus.Compliant };

        await _client!.CreateProfileAsync(profile, Owner);
        var read = await _client.GetProfileAsync(Owner.Value);

        Assert.That(read?.Permission, Is.Null);
    }

    [Test]
    public async Task Create_Profile_With_Invalid_Email_BadRequestAsync()
    {
        var profile = ProfileFor(Owner);
        profile.Email = "ada@@example";

        var error = Assert.ThrowsAsync<HttpRequestException>(
            () => _client!.CreateProfileAsync(profile, Owner));

        Assert.That(error!.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
    }

    [Test]
    public async Task Create_Profile_With_Contradicting_UserId_BadRequestAsync()
    {
        var profile = ProfileFor(Owner);
        profile.UserId = "5GrwvaEF5zKbXCEe9qGjZL23Y641mot2Ff6hS3s8jF3g3k3W";

        var error = Assert.ThrowsAsync<HttpRequestException>(
            () => _client!.CreateProfileAsync(profile, Owner));

        Assert.That(error!.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
    }
}
