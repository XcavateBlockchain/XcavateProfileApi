using System.Net;
using System.Net.Http.Json;
using XcavateProfile.Client;
using XcavateProfileApiClient.Signing;
using Account = Substrate.NetApi.Model.Types.Account;

namespace XcavateBuckets.Tests;

/// <summary>
/// The company endpoints driven through the shipped SDK client against the real controller,
/// validator and database — no docker, unlike the E2E suite.
/// </summary>
/// <remarks>
/// The create path is the reason this fixture exists. The client signs
/// <c>POST:/api/companies:&lt;hash of the JSON it sends&gt;:&lt;timestamp&gt;</c>; the server never sees
/// that hash, it re-serializes the <see cref="Company"/> its model binder produced and hashes that
/// instead. A 201 here is proof the two computations agree — the thing that would silently break if
/// <see cref="Company.Hash"/> hashed anything other than the bytes actually posted.
/// </remarks>
[TestFixture]
public class CompaniesEndpointTests
{
    private static Company CompanyFor(Account account, string? name = null) =>
        new()
        {
            UserId = account.Value,
            CompanyWalletAddress = account.Value,
            Name = name ?? "Xcavate Developments",
            Email = "hello@xcavate.io",
            Website = "https://xcavate.io",
            Summary = "Builds things",
            Address = "1 Example Street, London",
        };

    // ---- the happy path, which is the body-hash agreement proof ---------------------------------

    [Test]
    public async Task Create_accepts_a_signature_over_the_bytes_the_client_posted()
    {
        await using var host = await RestHost.StartAsync();
        using var client = host.NewSdkClient();

        var account = TestWallets.Substrate(0x61);

        var created = await client.CreateCompanyAsync(CompanyFor(account), account);
        var stored = await host.StoredCompanyAsync(created.CompanyId!);

        Assert.Multiple(() =>
        {
            Assert.That(created.CompanyId, Does.StartWith("company_"), "the server assigns the id");
            Assert.That(created.UserId, Is.EqualTo(account.Value));
            Assert.That(created.CompanyWalletAddress, Is.EqualTo(account.Value));
            Assert.That(created.CreatedAt, Is.Not.Null);
            Assert.That(created.UpdatedAt, Is.Not.Null);
            Assert.That(stored?.Name, Is.EqualTo("Xcavate Developments"),
                "the company must reach the database");
        });
    }

    [Test]
    public async Task Create_ignores_a_caller_supplied_id()
    {
        await using var host = await RestHost.StartAsync();
        using var client = host.NewSdkClient();

        var account = TestWallets.Substrate(0x62);
        var company = CompanyFor(account);
        company.CompanyId = "company_ichoosethis";

        var created = await client.CreateCompanyAsync(company, account);
        var underTheChosenId = await host.StoredCompanyAsync("company_ichoosethis");

        Assert.Multiple(() =>
        {
            Assert.That(created.CompanyId, Is.Not.EqualTo("company_ichoosethis"));
            Assert.That(underTheChosenId, Is.Null);
        });
    }

    [Test]
    public async Task One_wallet_can_own_several_companies()
    {
        await using var host = await RestHost.StartAsync();
        using var client = host.NewSdkClient();

        var account = TestWallets.Substrate(0x63);

        await client.CreateCompanyAsync(CompanyFor(account, "First"), account);
        await client.CreateCompanyAsync(CompanyFor(account, "Second"), account);

        var owned = await client.GetCompaniesByUserAsync(account.Value);

        Assert.That(owned.Select(c => c.Name), Is.EquivalentTo(new[] { "First", "Second" }));
    }

    [Test]
    public async Task Reads_are_public_and_absent_records_are_not_errors()
    {
        await using var host = await RestHost.StartAsync();
        using var client = host.NewSdkClient();

        var account = TestWallets.Substrate(0x64);
        var created = await client.CreateCompanyAsync(CompanyFor(account), account);

        var byId = await client.GetCompanyAsync(created.CompanyId!);
        var unknown = await client.GetCompanyAsync("company_nosuchthing");
        var ownedByAStranger = await client.GetCompaniesByUserAsync(TestWallets.Substrate(0x65).Value);
        var all = await client.GetCompaniesAsync();

        Assert.Multiple(() =>
        {
            Assert.That(byId?.Name, Is.EqualTo(created.Name));
            Assert.That(unknown, Is.Null);
            Assert.That(
                ownedByAStranger, Is.Empty, "a wallet owning nothing is an empty list, not a 404");
            Assert.That(all, Is.Not.Empty);
        });
    }

    // ---- ownership --------------------------------------------------------------------------------

    [Test]
    public async Task Create_refuses_a_company_for_someone_elses_wallet()
    {
        await using var host = await RestHost.StartAsync();
        using var client = host.NewSdkClient();

        var signer = TestWallets.Substrate(0x66);
        var someoneElse = TestWallets.Substrate(0x67);

        var company = CompanyFor(signer);
        company.UserId = someoneElse.Value;

        var error = Assert.ThrowsAsync<HttpRequestException>(
            () => client.CreateCompanyAsync(company, signer));
        var stored = await client.GetCompaniesAsync();

        Assert.Multiple(() =>
        {
            Assert.That(error!.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized));
            Assert.That(stored, Is.Empty);
        });
    }

    [Test]
    public async Task Update_by_a_stranger_is_forbidden()
    {
        await using var host = await RestHost.StartAsync();
        using var client = host.NewSdkClient();

        var owner = TestWallets.Substrate(0x68);
        var stranger = TestWallets.Substrate(0x69);

        var created = await client.CreateCompanyAsync(CompanyFor(owner), owner);
        created.Name = "Renamed by a stranger";

        var error = Assert.ThrowsAsync<HttpRequestException>(
            () => client.UpdateCompanyAsync(created.CompanyId!, created, stranger));
        var stored = await host.StoredCompanyAsync(created.CompanyId!);

        Assert.Multiple(() =>
        {
            Assert.That(error!.StatusCode, Is.EqualTo(HttpStatusCode.Forbidden));
            Assert.That(stored?.Name, Is.EqualTo("Xcavate Developments"));
        });
    }

    [Test]
    public async Task Delete_by_a_stranger_is_forbidden_and_by_the_owner_succeeds()
    {
        await using var host = await RestHost.StartAsync();
        using var client = host.NewSdkClient();

        var owner = TestWallets.Substrate(0x6A);
        var stranger = TestWallets.Substrate(0x6B);

        var created = await client.CreateCompanyAsync(CompanyFor(owner), owner);

        var error = Assert.ThrowsAsync<HttpRequestException>(
            () => client.DeleteCompanyAsync(created.CompanyId!, stranger));
        Assert.That(error!.StatusCode, Is.EqualTo(HttpStatusCode.Forbidden));

        await client.DeleteCompanyAsync(created.CompanyId!, owner);

        Assert.That(await host.StoredCompanyAsync(created.CompanyId!), Is.Null);
    }

    /// <summary>
    /// Reassigning <c>userId</c> hands the company over, and the wallet that gave it away loses its
    /// own access. That is the intended meaning of the field being writable, so it is worth pinning:
    /// a future change that treated userId as immutable would break it here rather than in production.
    /// </summary>
    [Test]
    public async Task Update_can_transfer_ownership_away_from_the_caller()
    {
        await using var host = await RestHost.StartAsync();
        using var client = host.NewSdkClient();

        var owner = TestWallets.Substrate(0x6C);
        var newOwner = TestWallets.Substrate(0x6D);

        var created = await client.CreateCompanyAsync(CompanyFor(owner), owner);
        created.UserId = newOwner.Value;

        var transferred = await client.UpdateCompanyAsync(created.CompanyId!, created, owner);

        Assert.Multiple(() =>
        {
            Assert.That(transferred.UserId, Is.EqualTo(newOwner.Value));
            Assert.That(
                transferred.CompanyWalletAddress, Is.EqualTo(owner.Value),
                "companyWalletAddress still names the creator after a transfer");
        });

        transferred.Name = "Renamed by the former owner";
        var error = Assert.ThrowsAsync<HttpRequestException>(
            () => client.UpdateCompanyAsync(created.CompanyId!, transferred, owner));

        Assert.That(error!.StatusCode, Is.EqualTo(HttpStatusCode.Forbidden));
    }

    [Test]
    public async Task Update_refuses_to_change_the_creating_wallet()
    {
        await using var host = await RestHost.StartAsync();
        using var client = host.NewSdkClient();

        var owner = TestWallets.Substrate(0x6E);
        var created = await client.CreateCompanyAsync(CompanyFor(owner), owner);

        created.CompanyWalletAddress = TestWallets.Substrate(0x6F).Value;

        var error = Assert.ThrowsAsync<HttpRequestException>(
            () => client.UpdateCompanyAsync(created.CompanyId!, created, owner));

        Assert.That(error!.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
    }

    [Test]
    public async Task Update_of_an_unknown_company_is_not_found()
    {
        await using var host = await RestHost.StartAsync();
        using var client = host.NewSdkClient();

        var account = TestWallets.Substrate(0x70);

        var error = Assert.ThrowsAsync<HttpRequestException>(
            () => client.UpdateCompanyAsync("company_nosuchthing", CompanyFor(account), account));

        Assert.That(error!.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
    }

    [Test]
    public async Task Update_by_the_owner_changes_the_editable_fields()
    {
        await using var host = await RestHost.StartAsync();
        using var client = host.NewSdkClient();

        var owner = TestWallets.Substrate(0x71);
        var created = await client.CreateCompanyAsync(CompanyFor(owner), owner);

        created.Name = "Xcavate Holdings";
        created.Email = "legal@xcavate.io";
        created.Summary = "Holds things instead";

        var updated = await client.UpdateCompanyAsync(created.CompanyId!, created, owner);

        Assert.Multiple(() =>
        {
            Assert.That(updated.Name, Is.EqualTo("Xcavate Holdings"));
            Assert.That(updated.Email, Is.EqualTo("legal@xcavate.io"));
            Assert.That(updated.Summary, Is.EqualTo("Holds things instead"));
            Assert.That(updated.CreatedAt, Is.EqualTo(created.CreatedAt), "createdAt never moves");
            Assert.That(updated.UpdatedAt, Is.GreaterThanOrEqualTo(created.UpdatedAt!.Value));
        });
    }

    // ---- clearance is the admin's to record ------------------------------------------------------

    [Test]
    public async Task A_company_cannot_declare_itself_compliant()
    {
        await using var host = await RestHost.StartAsync();
        using var client = host.NewSdkClient();

        var owner = TestWallets.Substrate(0x72);
        var company = CompanyFor(owner);
        company.Permission = new CompanyPermissions { Developer = PermissionStatus.Compliant };

        var created = await client.CreateCompanyAsync(company, owner);

        Assert.That(created.Permission, Is.Null, "permission is admin-only on create");

        created.Permission = new CompanyPermissions { Developer = PermissionStatus.Compliant };
        var updated = await client.UpdateCompanyAsync(created.CompanyId!, created, owner);

        Assert.That(updated.Permission, Is.Null, "permission is admin-only on update too");
    }

    [Test]
    public async Task An_admin_records_clearance_and_a_later_owner_edit_keeps_it()
    {
        var admin = TestWallets.Substrate(0x73);
        await using var host = await RestHost.StartAsync(admin.Value);
        using var client = host.NewSdkClient();

        var owner = TestWallets.Substrate(0x74);
        var created = await client.CreateCompanyAsync(CompanyFor(owner), owner);

        created.Permission = new CompanyPermissions
        {
            Developer = PermissionStatus.Compliant,
            Lawyer = PermissionStatus.Revoked,
        };

        var cleared = await client.UpdateCompanyAsync(created.CompanyId!, created, admin);

        Assert.Multiple(() =>
        {
            Assert.That(cleared.Permission?.Developer, Is.EqualTo(PermissionStatus.Compliant));
            Assert.That(cleared.Permission?.Lawyer, Is.EqualTo(PermissionStatus.Revoked));
            Assert.That(cleared.Permission?.Agent, Is.Null, "never assessed stays absent");
        });

        // The owner edits something unrelated. Their body carries no permission at all, and the
        // stored clearance has to survive that.
        var ownerEdit = await client.GetCompanyAsync(created.CompanyId!);
        ownerEdit!.Permission = null;
        ownerEdit.Name = "Xcavate Developments Ltd";

        var afterOwnerEdit = await client.UpdateCompanyAsync(created.CompanyId!, ownerEdit, owner);

        Assert.Multiple(() =>
        {
            Assert.That(afterOwnerEdit.Name, Is.EqualTo("Xcavate Developments Ltd"));
            Assert.That(
                afterOwnerEdit.Permission?.Developer, Is.EqualTo(PermissionStatus.Compliant),
                "a non-admin update must not clear the admin's record");
        });
    }

    // ---- body validation --------------------------------------------------------------------------

    [TestCase("not-an-email", TestName = "Create_refuses_a_malformed_email")]
    [TestCase("Someone <someone@example.com>", TestName = "Create_refuses_a_display_name_email")]
    public async Task Create_refuses_a_bad_email(string email)
    {
        await using var host = await RestHost.StartAsync();
        using var client = host.NewSdkClient();

        var account = TestWallets.Substrate(0x75);
        var company = CompanyFor(account);
        company.Email = email;

        var error = Assert.ThrowsAsync<HttpRequestException>(
            () => client.CreateCompanyAsync(company, account));

        Assert.That(error!.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
    }

    [Test]
    public async Task Create_refuses_a_name_longer_than_the_column()
    {
        await using var host = await RestHost.StartAsync();
        using var client = host.NewSdkClient();

        var account = TestWallets.Substrate(0x76);
        var company = CompanyFor(account);
        company.Name = new string('x', 129);

        var error = Assert.ThrowsAsync<HttpRequestException>(
            () => client.CreateCompanyAsync(company, account));

        Assert.Multiple(() =>
        {
            Assert.That(error!.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
            Assert.That(error.Message, Does.Contain("name"),
                "the refusal has to name the field, which a database overflow would not");
        });
    }

    /// <summary>
    /// An admin may register a company for another wallet, so the address checks cannot be what
    /// rejects a malformed one — the format check has to stand on its own.
    /// </summary>
    [Test]
    public async Task Create_refuses_a_userId_that_is_not_a_wallet_address()
    {
        var admin = TestWallets.Substrate(0x77);
        await using var host = await RestHost.StartAsync(admin.Value);
        using var client = host.NewSdkClient();

        var company = CompanyFor(admin);
        company.UserId = "not-a-wallet";

        var error = Assert.ThrowsAsync<HttpRequestException>(
            () => client.CreateCompanyAsync(company, admin));

        Assert.That(error!.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
    }

    [Test]
    public async Task Create_without_credentials_is_unauthorized()
    {
        await using var host = await RestHost.StartAsync();

        var account = TestWallets.Substrate(0x78);

        using var response = await host.Client.PostAsJsonAsync("/api/companies", CompanyFor(account));

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized));
    }

    /// <summary>
    /// The whole body is inside the signed hash, so editing it after signing must be rejected. The
    /// SDK cannot express this — it signs what it sends — so the request is assembled by hand.
    /// </summary>
    [Test]
    public async Task Create_rejects_a_body_swapped_after_signing()
    {
        await using var host = await RestHost.StartAsync();

        var account = TestWallets.Substrate(0x79);
        var signed = CompanyFor(account, "The signed name");
        var tampered = CompanyFor(account, "The posted name");

        using var request = await SignedRequests.PostAsync(
            "/api/companies", signed, new SubstrateRequestSigner(account), SignedRequests.Json(tampered));
        using var response = await host.Client.SendAsync(request);

        using var client = host.NewSdkClient();
        var stored = await client.GetCompaniesAsync();

        Assert.Multiple(() =>
        {
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized));
            Assert.That(stored, Is.Empty, "nothing may be stored");
        });
    }

    // ---- the logo endpoint ------------------------------------------------------------------------

    [Test]
    public async Task Logo_upload_stores_the_image_and_records_its_url()
    {
        await using var host = await RestHost.StartAsync();
        using var client = host.NewSdkClient();

        var owner = TestWallets.Substrate(0x7A);
        var created = await client.CreateCompanyAsync(CompanyFor(owner), owner);

        using var image = new MemoryStream([0x89, 0x50, 0x4E, 0x47]);
        var url = await client.UploadCompanyLogoAsync(created.CompanyId!, image, "logo.png", owner);

        var stored = await host.StoredCompanyAsync(created.CompanyId!);

        Assert.Multiple(() =>
        {
            Assert.That(stored?.Logo, Is.EqualTo(url));
            Assert.That(host.S3.Uploads, Has.Count.EqualTo(1));
            Assert.That(host.S3.Uploads[0].Key, Is.EqualTo($"companies/{created.CompanyId}/logo.png"));
            Assert.That(host.S3.Uploads[0].ContentType, Is.EqualTo("image/png"));
        });
    }

    [Test]
    public async Task Logo_upload_refuses_an_svg()
    {
        await using var host = await RestHost.StartAsync();
        using var client = host.NewSdkClient();

        var owner = TestWallets.Substrate(0x7B);
        var created = await client.CreateCompanyAsync(CompanyFor(owner), owner);

        using var image = new MemoryStream("<svg/>"u8.ToArray());

        var error = Assert.ThrowsAsync<HttpRequestException>(
            () => client.UploadCompanyLogoAsync(created.CompanyId!, image, "logo.svg", owner));

        Assert.Multiple(() =>
        {
            Assert.That(error!.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
            Assert.That(host.S3.Uploads, Is.Empty, "the file must never reach the store");
        });
    }

    [Test]
    public async Task Logo_upload_by_a_stranger_is_forbidden()
    {
        await using var host = await RestHost.StartAsync();
        using var client = host.NewSdkClient();

        var owner = TestWallets.Substrate(0x7C);
        var stranger = TestWallets.Substrate(0x7D);
        var created = await client.CreateCompanyAsync(CompanyFor(owner), owner);

        using var image = new MemoryStream([0x89, 0x50, 0x4E, 0x47]);

        var error = Assert.ThrowsAsync<HttpRequestException>(
            () => client.UploadCompanyLogoAsync(created.CompanyId!, image, "logo.png", stranger));

        Assert.Multiple(() =>
        {
            Assert.That(error!.StatusCode, Is.EqualTo(HttpStatusCode.Forbidden));
            Assert.That(host.S3.Uploads, Is.Empty);
        });
    }
}
