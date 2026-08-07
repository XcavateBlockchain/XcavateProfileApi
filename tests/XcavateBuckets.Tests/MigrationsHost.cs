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
using XcavateProfileApiClient;

namespace XcavateBuckets.Tests;

/// <summary>
/// An in-process host running the real <see cref="MigrationsController"/> over the real
/// <see cref="SignatureValidator"/> and a SQLite database — the REST counterpart to
/// <see cref="GraphQLHost"/>. It exists so the client/server body-hash agreement for
/// <c>/api/migrations</c> is covered without the docker stack the E2E suite needs.
/// </summary>
public sealed class MigrationsHost : IAsyncDisposable
{
    private readonly IHost _host;
    private readonly SqliteConnection _connection;

    private MigrationsHost(IHost host, SqliteConnection connection, HttpClient client)
    {
        _host = host;
        _connection = connection;
        Client = client;
    }

    /// <summary>A raw client, for requests that deliberately carry no or broken credentials.</summary>
    public HttpClient Client { get; }

    /// <summary>The shipped SDK client, wired to the test server — the same code a consumer runs.</summary>
    public XcavateProfileClient NewSdkClient() =>
        new(
            new XcavateProfileClientOptions { ApiUrl = "http://localhost" },
            new HttpClient(_host.GetTestServer().CreateHandler()));

    public static async Task<MigrationsHost> StartAsync(params string[] adminAddresses)
    {
        var connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();

        var builder = Host.CreateDefaultBuilder();
        builder.ConfigureWebHost(web =>
        {
            web.UseTestServer();
            web.ConfigureServices(services =>
            {
                // SuppressAsyncSuffixInActionNames mirrors Program.cs. Without it the Async suffix
                // is stripped from action names and the CreatedAtAction(nameof(GetWalletMigrationAsync))
                // in the controller cannot resolve its route, turning a 201 into a 500.
                services
                    .AddControllers(options => options.SuppressAsyncSuffixInActionNames = false)
                    .AddApplicationPart(typeof(MigrationsController).Assembly);

                services.AddDbContext<ProfileDbContext>(o => o.UseSqlite(connection));

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

        return new MigrationsHost(host, connection, host.GetTestClient());
    }

    /// <summary>Reads a migration straight out of the database, bypassing the API.</summary>
    public async Task<WalletMigration?> StoredAsync(string ss58Address)
    {
        using var scope = _host.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<ProfileDbContext>();

        return await context.WalletMigrations.FindAsync(ss58Address);
    }

    public async ValueTask DisposeAsync()
    {
        Client.Dispose();
        await _host.StopAsync();
        _host.Dispose();
        _connection.Dispose();
    }
}
