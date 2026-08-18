using NUnit.Framework;
using Substrate.NetApi.Model.Types;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
using XcavateProfile.Client;
using XcavateProfileApiClient;

namespace XcavateProfile.ApiTests;

/// <summary>
/// The company endpoints against a running API and a real PostgreSQL database. The in-process suite
/// covers the controller's rules in detail; what only this suite can show is that the schema the
/// migration created stores and returns them — the JSON permission column and the timestamps in
/// particular, which SQLite would accept in shapes Npgsql will not.
/// </summary>
[TestFixture]
public class CompanyApiTests
{
    private const string TestApiUrl = "http://localhost:5000";

    private XcavateProfileClient? _client;

    private static Account Owner => MnemonicsModel.GetAccountFromMnemonics(
        TestMnemonics.CompanyOwnerMnemonic);

    private static Account Stranger => MnemonicsModel.GetAccountFromMnemonics(
        TestMnemonics.CompanyStrangerMnemonic);

    [SetUp]
    public async Task SetUpAsync()
    {
        _client = new XcavateProfileClient(new XcavateProfileClientOptions { ApiUrl = TestApiUrl });

        // The API uses a persistent database, so companies registered by a previous test (or a
        // previous run) survive. Clear this account's before every test.
        await RemoveOwnedAsync(Owner);
        await RemoveOwnedAsync(Stranger);
    }

    [TearDown]
    public void TearDown() => _client?.Dispose();

    private async Task RemoveOwnedAsync(Account account)
    {
        foreach (var company in await _client!.GetCompaniesByUserAsync(account.Value))
        {
            await _client.DeleteCompanyAsync(company.CompanyId!, account);
        }
    }

    private static Company CompanyFor(Account account, string name = "Xcavate Developments") =>
        new()
        {
            UserId = account.Value,
            CompanyWalletAddress = account.Value,
            Name = name,
            Email = "hello@xcavate.io",
            Website = "https://xcavate.io",
            Summary = "Builds things",
            Address = "1 Example Street, London",
        };

    [Test]
    public async Task Create_Company_SuccessAsync()
    {
        var created = await _client!.CreateCompanyAsync(CompanyFor(Owner), Owner);

        Assert.Multiple(() =>
        {
            Assert.That(created.CompanyId, Does.StartWith("company_"));
            Assert.That(created.UserId, Is.EqualTo(Owner.Value));
            Assert.That(created.CompanyWalletAddress, Is.EqualTo(Owner.Value));
            Assert.That(created.Name, Is.EqualTo("Xcavate Developments"));
            Assert.That(created.CreatedAt, Is.Not.Null);
            Assert.That(created.UpdatedAt, Is.Not.Null);
        });
    }

    [Test]
    public async Task Get_Company_By_Id_And_By_User_SuccessAsync()
    {
        var created = await _client!.CreateCompanyAsync(CompanyFor(Owner), Owner);

        var byId = await _client.GetCompanyAsync(created.CompanyId!);
        var byUser = await _client.GetCompaniesByUserAsync(Owner.Value);
        var all = await _client.GetCompaniesAsync();

        Assert.Multiple(() =>
        {
            Assert.That(byId?.Name, Is.EqualTo("Xcavate Developments"));
            Assert.That(byId?.Website, Is.EqualTo("https://xcavate.io"));
            Assert.That(byUser.Select(c => c.CompanyId), Does.Contain(created.CompanyId));
            Assert.That(all.Select(c => c.CompanyId), Does.Contain(created.CompanyId));
        });
    }

    [Test]
    public async Task Get_Unknown_Company_Returns_NullAsync()
    {
        Assert.That(await _client!.GetCompanyAsync("company_nosuchthing"), Is.Null);
    }

    [Test]
    public async Task Update_Company_SuccessAsync()
    {
        var created = await _client!.CreateCompanyAsync(CompanyFor(Owner), Owner);

        created.Name = "Xcavate Holdings";
        created.Summary = "Holds things instead";

        var updated = await _client.UpdateCompanyAsync(created.CompanyId!, created, Owner);
        var read = await _client.GetCompanyAsync(created.CompanyId!);

        Assert.Multiple(() =>
        {
            Assert.That(updated.Name, Is.EqualTo("Xcavate Holdings"));
            Assert.That(read?.Summary, Is.EqualTo("Holds things instead"));
            Assert.That(read?.CreatedAt, Is.EqualTo(created.CreatedAt));
        });
    }

    [Test]
    public async Task Update_Company_By_Stranger_ForbiddenAsync()
    {
        var created = await _client!.CreateCompanyAsync(CompanyFor(Owner), Owner);
        created.Name = "Renamed by a stranger";

        var error = Assert.ThrowsAsync<HttpRequestException>(
            () => _client.UpdateCompanyAsync(created.CompanyId!, created, Stranger));

        Assert.That(error!.StatusCode, Is.EqualTo(HttpStatusCode.Forbidden));
    }

    [Test]
    public async Task Delete_Company_SuccessAsync()
    {
        var created = await _client!.CreateCompanyAsync(CompanyFor(Owner), Owner);

        await _client.DeleteCompanyAsync(created.CompanyId!, Owner);

        Assert.That(await _client.GetCompanyAsync(created.CompanyId!), Is.Null);
    }

    [Test]
    public async Task Delete_Company_By_Stranger_ForbiddenAsync()
    {
        var created = await _client!.CreateCompanyAsync(CompanyFor(Owner), Owner);

        var error = Assert.ThrowsAsync<HttpRequestException>(
            () => _client.DeleteCompanyAsync(created.CompanyId!, Stranger));

        Assert.Multiple(async () =>
        {
            Assert.That(error!.StatusCode, Is.EqualTo(HttpStatusCode.Forbidden));
            Assert.That(await _client.GetCompanyAsync(created.CompanyId!), Is.Not.Null);
        });
    }

    /// <summary>
    /// Registering for someone else's wallet is refused, so a company's owner is always a wallet
    /// that consented to owning it.
    /// </summary>
    [Test]
    public async Task Create_Company_For_Another_Wallet_UnauthorizedAsync()
    {
        var company = CompanyFor(Owner);
        company.UserId = Stranger.Value;

        var error = Assert.ThrowsAsync<HttpRequestException>(
            () => _client!.CreateCompanyAsync(company, Owner));

        Assert.Multiple(async () =>
        {
            Assert.That(error!.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized));
            Assert.That(await _client!.GetCompaniesByUserAsync(Stranger.Value), Is.Empty);
        });
    }

    /// <summary>
    /// Clearance is the admin's record about a company. This suite's signer is an ordinary wallet, so
    /// what it must not be able to do is grant itself one.
    /// </summary>
    [Test]
    public async Task Company_Cannot_Set_Its_Own_PermissionAsync()
    {
        var company = CompanyFor(Owner);
        company.Permission = new CompanyPermissions { Developer = PermissionStatus.Compliant };

        var created = await _client!.CreateCompanyAsync(company, Owner);
        var read = await _client.GetCompanyAsync(created.CompanyId!);

        Assert.That(read?.Permission, Is.Null);
    }

    [Test]
    public async Task Create_Company_With_Invalid_Email_BadRequestAsync()
    {
        var company = CompanyFor(Owner);
        company.Email = "not-an-email";

        var error = Assert.ThrowsAsync<HttpRequestException>(
            () => _client!.CreateCompanyAsync(company, Owner));

        Assert.That(error!.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
    }

    [Test]
    public async Task One_Wallet_Can_Own_Several_CompaniesAsync()
    {
        await _client!.CreateCompanyAsync(CompanyFor(Owner, "First"), Owner);
        await _client.CreateCompanyAsync(CompanyFor(Owner, "Second"), Owner);

        var owned = await _client.GetCompaniesByUserAsync(Owner.Value);

        Assert.That(owned.Select(c => c.Name), Is.EquivalentTo(new List<string> { "First", "Second" }));
    }
}
