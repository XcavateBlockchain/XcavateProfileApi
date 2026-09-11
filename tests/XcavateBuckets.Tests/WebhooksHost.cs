using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using XcavateBuckets.Domain.Data;
using XcavateBuckets.Domain.Entities;
using XcavateProfileApi.Controllers;
using XcavateProfileApi.GraphQL;

namespace XcavateBuckets.Tests;

/// <summary>
/// An in-process host running the real <see cref="WebhooksController"/> over the real bucket domain
/// services and a SQLite database — the counterpart to <see cref="RestHost"/> and
/// <see cref="GraphQLHost"/>. It covers the property-asset webhook, including its idempotent
/// redelivery, without the docker stack the E2E suite needs. The webhook carries no signature, so
/// no signing client or validator is wired here.
/// </summary>
public sealed class WebhooksHost : IAsyncDisposable
{
    private readonly IHost _host;
    private readonly SqliteConnection _connection;

    private WebhooksHost(IHost host, SqliteConnection connection, HttpClient client)
    {
        _host = host;
        _connection = connection;
        Client = client;
    }

    /// <summary>A raw client — the webhook needs no credentials.</summary>
    public HttpClient Client { get; }

    public static async Task<WebhooksHost> StartAsync()
    {
        var connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();

        var builder = Host.CreateDefaultBuilder();
        builder.ConfigureWebHost(web =>
        {
            web.UseTestServer();
            web.ConfigureServices(services =>
            {
                // SuppressAsyncSuffixInActionNames mirrors Program.cs, so action names keep the
                // Async suffix exactly as the controller declares them.
                services
                    .AddControllers(options => options.SuppressAsyncSuffixInActionNames = false)
                    .AddApplicationPart(typeof(WebhooksController).Assembly);

                services.AddDbContext<BucketDbContext>(o => o.UseSqlite(connection));

                // The domain wiring shared with the application host: BucketDbContext is the only
                // thing AddBucketDomain expects the caller to supply (see its remarks).
                services.AddBucketDomain();
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
            await scope.ServiceProvider.GetRequiredService<BucketDbContext>()
                .Database.EnsureCreatedAsync();
        }

        return new WebhooksHost(host, connection, host.GetTestClient());
    }

    /// <summary>Every namespace straight out of the database, bypassing the API.</summary>
    public async Task<List<Namespace>> NamespacesAsync()
    {
        using var scope = _host.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<BucketDbContext>();

        return await context.Namespaces.ToListAsync();
    }

    public async ValueTask DisposeAsync()
    {
        Client.Dispose();
        await _host.StopAsync();
        _host.Dispose();
        _connection.Dispose();
    }
}
