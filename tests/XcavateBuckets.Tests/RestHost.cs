using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using XcavateProfile.Client;
using XcavateProfileApi.Controllers;
using XcavateProfileApi.Data;
using XcavateProfileApi.Middleware;
using XcavateProfileApi.Services;
using XcavateProfileApiClient;

namespace XcavateBuckets.Tests;

/// <summary>
/// An in-process host running the real <see cref="ProfilesController"/> and
/// <see cref="CompaniesController"/> over the real <see cref="SignatureValidator"/>, a SQLite
/// database and a stub object store — the profile/company counterpart to
/// <see cref="MigrationsHost"/>. It exists so the client/server body-hash agreement for those
/// endpoints is covered without the docker stack the E2E suite needs.
/// </summary>
public sealed class RestHost : IAsyncDisposable
{
    private readonly IHost _host;
    private readonly SqliteConnection _connection;

    private RestHost(IHost host, SqliteConnection connection, HttpClient client, StubS3Service s3)
    {
        _host = host;
        _connection = connection;
        Client = client;
        S3 = s3;
    }

    /// <summary>A raw client, for requests that deliberately carry no or broken credentials.</summary>
    public HttpClient Client { get; }

    /// <summary>The object store the endpoints uploaded to — the same instance they resolved.</summary>
    public StubS3Service S3 { get; }

    /// <summary>The shipped SDK client, wired to the test server — the same code a consumer runs.</summary>
    public XcavateProfileClient NewSdkClient() =>
        new(
            new XcavateProfileClientOptions { ApiUrl = "http://localhost" },
            new HttpClient(_host.GetTestServer().CreateHandler()));

    public static async Task<RestHost> StartAsync(params string[] adminAddresses)
    {
        var connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();

        var s3 = new StubS3Service();

        var builder = Host.CreateDefaultBuilder();
        builder.ConfigureWebHost(web =>
        {
            web.UseTestServer();
            web.ConfigureServices(services =>
            {
                // SuppressAsyncSuffixInActionNames mirrors Program.cs. Without it the Async suffix
                // is stripped from action names and the CreatedAtAction(nameof(GetCompanyAsync)) in
                // the controller cannot resolve its route, turning a 201 into a 500.
                services
                    .AddControllers(options => options.SuppressAsyncSuffixInActionNames = false)
                    .AddApplicationPart(typeof(CompaniesController).Assembly);

                services.AddDbContext<ProfileDbContext>(o => o.UseSqlite(connection));

                services.AddSingleton<IS3Service>(s3);
                services.AddSingleton(adminAddresses.ToList());
                services.AddScoped(_ => new SignatureValidationOptions());
                services.AddScoped<ISignatureValidator, SignatureValidator>();
            });
            web.Configure(app =>
            {
                app.UseRouting();
                app.UseEndpoints(e => e.MapControllers());
            });
        });

        var host = await builder.StartAsync();

        using (var scope = host.Services.CreateScope())
        {
            await scope.ServiceProvider.GetRequiredService<ProfileDbContext>()
                .Database.EnsureCreatedAsync();
        }

        return new RestHost(host, connection, host.GetTestClient(), s3);
    }

    /// <summary>Reads a company straight out of the database, bypassing the API.</summary>
    public async Task<Company?> StoredCompanyAsync(string companyId)
    {
        using var scope = _host.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<ProfileDbContext>();

        return await context.Companies.FindAsync(companyId);
    }

    /// <summary>
    /// Writes a profile straight into the database, bypassing the API — the only way to ask what the
    /// schema itself refuses, rather than what the controller refuses on its behalf.
    /// </summary>
    public async Task SeedProfileAsync(Profile profile)
    {
        using var scope = _host.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<ProfileDbContext>();

        context.Profiles.Add(profile);
        await context.SaveChangesAsync();
    }

    /// <summary>Reads a profile straight out of the database, bypassing the API.</summary>
    public async Task<Profile?> StoredProfileAsync(string address)
    {
        using var scope = _host.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<ProfileDbContext>();

        return await context.Profiles.FindAsync(address);
    }

    public async ValueTask DisposeAsync()
    {
        Client.Dispose();
        await _host.StopAsync();
        _host.Dispose();
        _connection.Dispose();
    }
}

/// <summary>
/// Stands in for Hetzner Object Storage: records what it was handed and returns a URL shaped like
/// the real one, so what the tests observe is the endpoints' own behaviour — the allow-list, the key,
/// the field the URL is written to.
/// </summary>
public sealed class StubS3Service : IS3Service
{
    private readonly List<Upload> _uploads = [];

    public IReadOnlyList<Upload> Uploads => _uploads;

    public async Task<string> UploadImageAsync(
        string bucketName, string key, Stream content, string contentType)
    {
        using var buffer = new MemoryStream();
        await content.CopyToAsync(buffer);

        _uploads.Add(new Upload(bucketName, key, contentType, buffer.Length));

        return $"https://objects.test/{bucketName}/{key}";
    }

    public readonly record struct Upload(string Bucket, string Key, string ContentType, long Length);
}
