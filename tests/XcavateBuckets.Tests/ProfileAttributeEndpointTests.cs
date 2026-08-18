using System.Net;
using XcavateProfile.Client;
using XcavateProfileApiClient.Signing;
using Account = Substrate.NetApi.Model.Types.Account;

namespace XcavateBuckets.Tests;

/// <summary>
/// The user attributes added to the profile — identity, contact, roles, clearance, timestamps —
/// driven through the shipped SDK against the real controller, validator and database.
/// </summary>
[TestFixture]
public class ProfileAttributeEndpointTests
{
    private const string X25519Key =
        "0x1234567890abcdef1234567890abcdef1234567890abcdef1234567890abcdef";

    private static Profile ProfileFor(Account account) =>
        new()
        {
            Ss58Address = account.Value,
            X25519Key = X25519Key,
            Nickname = null,
            Name = "Ada Lovelace",
            Email = "ada@example.com",
            Phone = "+44 20 7123 4567",
            Address = "1 Example Street, London",
            Title = "Analyst",
            Background = "Mathematics",
            Bio = "Writes notes",
            Roles = [UserRole.Investor, UserRole.Developer],
        };

    // ---- the happy path, which is the body-hash agreement proof for the new fields ---------------

    [Test]
    public async Task Create_round_trips_every_attribute()
    {
        await using var host = await RestHost.StartAsync();
        using var client = host.NewSdkClient();

        var account = TestWallets.Substrate(0x81);

        var created = await client.CreateProfileAsync(ProfileFor(account), account);
        var read = await client.GetProfileAsync(account.Value);

        Assert.Multiple(() =>
        {
            Assert.That(created.UserId, Is.EqualTo(account.Value), "userId is the wallet address");
            Assert.That(read?.Name, Is.EqualTo("Ada Lovelace"));
            Assert.That(read?.Email, Is.EqualTo("ada@example.com"));
            Assert.That(read?.Phone, Is.EqualTo("+44 20 7123 4567"));
            Assert.That(read?.Address, Is.EqualTo("1 Example Street, London"));
            Assert.That(read?.Title, Is.EqualTo("Analyst"));
            Assert.That(read?.Background, Is.EqualTo("Mathematics"));
            Assert.That(
                read?.Roles, Is.EquivalentTo(new[] { UserRole.Investor, UserRole.Developer }),
                "the roles survive the JSON column");
            Assert.That(read?.CreatedAt, Is.Not.Null);
            Assert.That(read?.UpdatedAt, Is.Not.Null);
        });
    }

    /// <summary>
    /// The PUT path creates the profile when there is none, and that branch builds the entity field
    /// by field — so a new attribute is easy to add to the model and forget here.
    /// </summary>
    [Test]
    public async Task Upsert_through_put_keeps_every_attribute()
    {
        await using var host = await RestHost.StartAsync();
        using var client = host.NewSdkClient();

        var account = TestWallets.Substrate(0x82);

        var created = await client.UpdateProfileAsync(account.Value, ProfileFor(account), account);
        var stored = await host.StoredProfileAsync(account.Value);

        Assert.Multiple(() =>
        {
            Assert.That(created.Name, Is.EqualTo("Ada Lovelace"));
            Assert.That(stored?.UserId, Is.EqualTo(account.Value));
            Assert.That(stored?.Email, Is.EqualTo("ada@example.com"));
            Assert.That(stored?.Title, Is.EqualTo("Analyst"));
            Assert.That(stored?.Background, Is.EqualTo("Mathematics"));
            Assert.That(stored?.Roles, Is.EquivalentTo(new[] { UserRole.Investor, UserRole.Developer }));
            Assert.That(stored?.CreatedAt, Is.Not.Null);
        });
    }

    [Test]
    public async Task Update_edits_the_attributes_and_moves_only_updated_at()
    {
        await using var host = await RestHost.StartAsync();
        using var client = host.NewSdkClient();

        var account = TestWallets.Substrate(0x83);
        var created = await client.CreateProfileAsync(ProfileFor(account), account);

        created.Title = "Principal Analyst";
        created.Roles = [UserRole.RegionalOperator];

        var updated = await client.UpdateProfileAsync(account.Value, created, account);

        Assert.Multiple(() =>
        {
            Assert.That(updated.Title, Is.EqualTo("Principal Analyst"));
            Assert.That(updated.Roles, Is.EquivalentTo(new[] { UserRole.RegionalOperator }));
            Assert.That(updated.CreatedAt, Is.EqualTo(created.CreatedAt));
            Assert.That(updated.UpdatedAt, Is.GreaterThanOrEqualTo(created.UpdatedAt!.Value));
        });
    }

    [Test]
    public async Task Duplicate_roles_are_stored_once()
    {
        await using var host = await RestHost.StartAsync();
        using var client = host.NewSdkClient();

        var account = TestWallets.Substrate(0x84);
        var profile = ProfileFor(account);
        profile.Roles = [UserRole.Investor, UserRole.Investor, UserRole.Spv];

        var created = await client.CreateProfileAsync(profile, account);

        Assert.That(created.Roles, Is.EquivalentTo(new[] { UserRole.Investor, UserRole.Spv }));
    }

    // ---- clearance is the admin's to record ------------------------------------------------------

    [Test]
    public async Task A_user_cannot_declare_themselves_compliant()
    {
        await using var host = await RestHost.StartAsync();
        using var client = host.NewSdkClient();

        var account = TestWallets.Substrate(0x85);
        var profile = ProfileFor(account);
        profile.Permission = new UserPermissions { Investor = PermissionStatus.Compliant };

        var created = await client.CreateProfileAsync(profile, account);

        Assert.That(created.Permission, Is.Null, "permission is admin-only on create");

        created.Permission = new UserPermissions { Investor = PermissionStatus.Compliant };
        var updated = await client.UpdateProfileAsync(account.Value, created, account);

        Assert.That(updated.Permission, Is.Null, "permission is admin-only on update too");
    }

    [Test]
    public async Task An_admin_records_clearance_and_a_later_self_edit_keeps_it()
    {
        var admin = TestWallets.Substrate(0x86);
        await using var host = await RestHost.StartAsync(admin.Value);
        using var client = host.NewSdkClient();

        var account = TestWallets.Substrate(0x87);
        var created = await client.CreateProfileAsync(ProfileFor(account), account);

        created.Permission = new UserPermissions
        {
            Investor = PermissionStatus.Compliant,
            Developer = PermissionStatus.Revoked,
        };

        var cleared = await client.UpdateProfileAsync(account.Value, created, admin);

        Assert.Multiple(() =>
        {
            Assert.That(cleared.Permission?.Investor, Is.EqualTo(PermissionStatus.Compliant));
            Assert.That(cleared.Permission?.Developer, Is.EqualTo(PermissionStatus.Revoked));
            Assert.That(cleared.Permission?.Spv, Is.Null, "never assessed stays absent");
        });

        var selfEdit = await client.GetProfileAsync(account.Value);
        selfEdit!.Permission = null;
        selfEdit.Bio = "Edited by the user";

        var afterSelfEdit = await client.UpdateProfileAsync(account.Value, selfEdit, account);

        Assert.Multiple(() =>
        {
            Assert.That(afterSelfEdit.Bio, Is.EqualTo("Edited by the user"));
            Assert.That(
                afterSelfEdit.Permission?.Investor, Is.EqualTo(PermissionStatus.Compliant),
                "a non-admin update must not clear the admin's record");
        });
    }

    // ---- body validation --------------------------------------------------------------------------

    [Test]
    public async Task Create_refuses_a_userId_that_contradicts_the_wallet_address()
    {
        await using var host = await RestHost.StartAsync();
        using var client = host.NewSdkClient();

        var account = TestWallets.Substrate(0x88);
        var profile = ProfileFor(account);
        profile.UserId = TestWallets.Substrate(0x89).Value;

        var error = Assert.ThrowsAsync<HttpRequestException>(
            () => client.CreateProfileAsync(profile, account));

        Assert.Multiple(() =>
        {
            Assert.That(error!.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
            Assert.That(error.Message, Does.Contain("userId"));
        });
    }

    [Test]
    public async Task Create_refuses_a_malformed_email()
    {
        await using var host = await RestHost.StartAsync();
        using var client = host.NewSdkClient();

        var account = TestWallets.Substrate(0x8A);
        var profile = ProfileFor(account);
        profile.Email = "ada@@example";

        var error = Assert.ThrowsAsync<HttpRequestException>(
            () => client.CreateProfileAsync(profile, account));

        Assert.That(error!.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
    }

    [Test]
    public async Task Create_refuses_a_title_longer_than_the_column()
    {
        await using var host = await RestHost.StartAsync();
        using var client = host.NewSdkClient();

        var account = TestWallets.Substrate(0x8B);
        var profile = ProfileFor(account);
        profile.Title = new string('x', 129);

        var error = Assert.ThrowsAsync<HttpRequestException>(
            () => client.CreateProfileAsync(profile, account));

        Assert.Multiple(() =>
        {
            Assert.That(error!.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
            Assert.That(error.Message, Does.Contain("title"));
        });
    }

    /// <summary>
    /// An unknown role is a client bug, not something to store as an empty set.
    /// </summary>
    [Test]
    public async Task Create_refuses_an_unknown_role()
    {
        await using var host = await RestHost.StartAsync();

        var account = TestWallets.Substrate(0x8C);
        var json = $$"""
            {"ss58address":"{{account.Value}}","x25519Key":"{{X25519Key}}","roles":["landlord"]}
            """;

        using var request = await SignedRequests.PostAsync(
            "/api/profiles",
            new SignedRequests.RawJson(json),
            new SubstrateRequestSigner(account),
            postedJson: json);
        using var response = await host.Client.SendAsync(request);

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
    }

    // ---- the picture endpoint --------------------------------------------------------------------

    /// <summary>
    /// The allow-list the profile-picture endpoint uses now lives in <c>ImageUploads</c>, shared with
    /// the company-logo endpoint. This is what pins that the extension still decides the stored
    /// content type — the bucket is public, so a client-supplied one would be stored XSS.
    /// </summary>
    [Test]
    public async Task Image_upload_stores_the_picture_and_records_its_url()
    {
        await using var host = await RestHost.StartAsync();
        using var client = host.NewSdkClient();

        var account = TestWallets.Substrate(0x8E);
        var created = await client.CreateProfileAsync(ProfileFor(account), account);

        using var image = new MemoryStream([0x89, 0x50, 0x4E, 0x47]);
        var url = await client.UploadImageAsync(account.Value, image, "avatar.png", account);

        var stored = await host.StoredProfileAsync(account.Value);

        Assert.Multiple(() =>
        {
            Assert.That(stored?.ProfilePicture, Is.EqualTo(url));
            Assert.That(host.S3.Uploads, Has.Count.EqualTo(1));
            Assert.That(
                host.S3.Uploads[0].Key, Is.EqualTo($"profiles/{account.Value}/avatar.png"));
            Assert.That(host.S3.Uploads[0].ContentType, Is.EqualTo("image/png"));
            Assert.That(
                stored?.UpdatedAt, Is.GreaterThanOrEqualTo(created.UpdatedAt!.Value),
                "uploading a picture is a change to the profile");
        });
    }

    [Test]
    public async Task Image_upload_refuses_an_svg()
    {
        await using var host = await RestHost.StartAsync();
        using var client = host.NewSdkClient();

        var account = TestWallets.Substrate(0x8F);
        await client.CreateProfileAsync(ProfileFor(account), account);

        using var image = new MemoryStream("<svg/>"u8.ToArray());

        var error = Assert.ThrowsAsync<HttpRequestException>(
            () => client.UploadImageAsync(account.Value, image, "avatar.svg", account));

        Assert.Multiple(() =>
        {
            Assert.That(error!.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
            Assert.That(host.S3.Uploads, Is.Empty, "the file must never reach the store");
        });
    }

    // ---- the compatibility guarantee --------------------------------------------------------------

    /// <summary>
    /// The reason every new property is omitted from the JSON when null.
    /// </summary>
    /// <remarks>
    /// This is the exact body a published SDK build — one that has never heard of <c>name</c>,
    /// <c>roles</c> or <c>userId</c> — serializes and signs. The server re-serializes what its model
    /// binder produced and hashes that, so if the new properties emitted <c>"name":null</c> on the
    /// server side the two hashes would differ and every write from every deployed consumer would
    /// start failing with a 401. A green test here is that promise; a red one means the wire format
    /// broke, not that the test is stale.
    /// </remarks>
    [Test]
    public async Task A_body_from_an_older_sdk_is_still_accepted()
    {
        await using var host = await RestHost.StartAsync();

        var account = TestWallets.Substrate(0x8D);

        // Property order is the old Profile's declaration order, and nulls are written, because that
        // is what JsonSerializer produced for a class with no ignore conditions.
        var legacyJson = $$"""
            {"ss58address":"{{account.Value}}","nickname":"legacy","bio":"from an older sdk","profilePicture":null,"x25519Key":"{{X25519Key}}"}
            """.Trim();

        using var request = await SignedRequests.PostAsync(
            "/api/profiles",
            new SignedRequests.RawJson(legacyJson),
            new SubstrateRequestSigner(account),
            postedJson: legacyJson);
        using var response = await host.Client.SendAsync(request);

        var stored = await host.StoredProfileAsync(account.Value);
        var body = await response.Content.ReadAsStringAsync();

        Assert.Multiple(() =>
        {
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Created), body);
            Assert.That(stored?.Nickname, Is.EqualTo("legacy"));
            Assert.That(stored?.Name, Is.Null, "the caller sent none");
            Assert.That(stored?.Roles, Is.Null);
            Assert.That(
                stored?.UserId, Is.EqualTo(account.Value),
                "the server fills in userId even for a caller that does not know the field");
        });
    }

    /// <summary>
    /// The same guarantee from the other direction: what the current SDK serializes for a profile
    /// with no new attributes set must be byte-identical to the old shape.
    /// </summary>
    [Test]
    public void An_untouched_profile_serializes_to_the_old_shape()
    {
        var profile = new Profile
        {
            Ss58Address = "5GrwvaEF5zKbXCEe9qGjZL23Y641mot2Ff6hS3s8jF3g3k3W",
            Nickname = "legacy",
            Bio = "from an older sdk",
            X25519Key = X25519Key,
        };

        Assert.That(
            SignedRequests.Json(profile),
            Is.EqualTo(
                """
                {"ss58address":"5GrwvaEF5zKbXCEe9qGjZL23Y641mot2Ff6hS3s8jF3g3k3W","nickname":"legacy","bio":"from an older sdk","profilePicture":null,"x25519Key":"0x1234567890abcdef1234567890abcdef1234567890abcdef1234567890abcdef"}
                """));
    }
}
