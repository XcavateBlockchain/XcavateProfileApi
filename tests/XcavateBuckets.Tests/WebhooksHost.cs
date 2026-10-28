using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using XcavateBuckets.Domain.Data;
using XcavateBuckets.Domain.Entities;
using XcavateProfileApi.Controllers;
using XcavateProfileApi.Data;
using XcavateProfileApi.GraphQL;
using XcavateProfileApi.Services.Indexer;
using XcavateProfileApi.Services.Notifications;

namespace XcavateBuckets.Tests;

/// <summary>
/// An in-process host running the real <see cref="WebhooksController"/> (and the notification
/// webhooks controller) over the real bucket domain services and SQLite databases (bucket +
/// profile stores) — the counterpart to <see cref="RestHost"/> and <see cref="GraphQLHost"/>. It
/// covers the property-asset webhook, including its idempotent redelivery, without the docker
/// stack the E2E suite needs. The bucket webhook carries no signature, so no signing client or
/// validator is wired here; the notification webhooks' shared secret is injected as
/// configuration when a test asks for it.
/// </summary>
public sealed class WebhooksHost : IAsyncDisposable
{
    private readonly IHost _host;
    private readonly SqliteConnection _connection;
    private readonly SqliteConnection _profileConnection;

    private WebhooksHost(
        IHost host,
        SqliteConnection connection,
        SqliteConnection profileConnection,
        HttpClient client,
        TestIndexerClient indexer,
        NotificationQueue? queue,
        StubS3Service s3)
    {
        _host = host;
        _connection = connection;
        _profileConnection = profileConnection;
        Client = client;
        Indexer = indexer;
        Queue = queue;
        S3 = s3;
    }

    /// <summary>A raw client — the webhook needs no credentials.</summary>
    public HttpClient Client { get; }

    public StubS3Service S3 { get; }

    /// <summary>
    /// The stub GraphQL client behind the voting webhooks' state resolution: set its properties to
    /// script what "the indexer says" before posting a webhook.
    /// </summary>
    public TestIndexerClient Indexer { get; }

    /// <summary>
    /// The push queue the notification webhooks enqueue into, when this host was started with
    /// push configured (StartAsync's pushConfigured) — drain it to assert the fanout. Null on a
    /// push-less host.
    /// </summary>
    public NotificationQueue? Queue { get; }

    public const string DefaultBucketAdminAddress = "BucketAdmin111";

    public static async Task<WebhooksHost> StartAsync(
        string? webhookSecret = null,
        bool pushConfigured = true,
        string? bucketAdminAddress = DefaultBucketAdminAddress)
    {
        var connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();
        // The profile store is a second context on its own in-memory database (the controller's
        // wallet -> viewer-key resolution reads it).
        var profileConnection = new SqliteConnection("DataSource=:memory:");
        profileConnection.Open();

        var indexer = new TestIndexerClient();
        var s3 = new StubS3Service();
        var queue = pushConfigured
            ? new NotificationQueue(NullLogger<NotificationQueue>.Instance)
            : null;

        var builder = Host.CreateDefaultBuilder();
        builder.ConfigureWebHost(web =>
        {
            web.UseTestServer();
            web.ConfigureAppConfiguration((_, config) =>
            {
                var values = new Dictionary<string, string?>();
                if (webhookSecret is not null)
                {
                    values[NotificationWebhooksController.WebhookSecretConfigKey] = webhookSecret;
                }
                if (bucketAdminAddress is not null)
                {
                    values["BUCKET_ADMIN_ADDRESS"] = bucketAdminAddress;
                }
                config.AddInMemoryCollection(values);
            });
            web.ConfigureServices(services =>
            {
                // SuppressAsyncSuffixInActionNames mirrors Program.cs, so action names keep the
                // Async suffix exactly as the controller declares them.
                services
                    .AddControllers(options => options.SuppressAsyncSuffixInActionNames = false)
                    .AddApplicationPart(typeof(WebhooksController).Assembly);

                services.AddDbContext<BucketDbContext>(o => o.UseSqlite(connection));
                services.AddDbContext<ProfileDbContext>(o => o.UseSqlite(profileConnection));

                // The voting webhooks resolve vote state through this stub instead of the live
                // indexer GraphQL.
                services.AddSingleton<IIndexerClient>(indexer);
                services.AddSingleton<XcavateProfileApi.Services.IS3Service>(s3);

                // Program.cs registers the queue only with NOTIFICATIONS_API_KEY; the
                // notification webhooks resolve it per request, so its absence is the
                // push-not-configured path under test.
                if (queue is not null)
                {
                    services.AddSingleton(queue);
                }

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
            await scope.ServiceProvider.GetRequiredService<ProfileDbContext>()
                .Database.EnsureCreatedAsync();
        }

        return new WebhooksHost(
            host, connection, profileConnection, host.GetTestClient(), indexer, queue, s3);
    }

    /// <summary>Every namespace straight out of the database, bypassing the API.</summary>
    public async Task<List<Namespace>> NamespacesAsync()
    {
        using var scope = _host.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<BucketDbContext>();

        return await context.Namespaces.ToListAsync();
    }

    /// <summary>Every bucket straight out of the database, bypassing the API.</summary>
    public async Task<List<Bucket>> BucketsAsync()
    {
        using var scope = _host.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<BucketDbContext>();

        return await context.Buckets.ToListAsync();
    }

    /// <summary>Runs a callback against a fresh DbContext scope — seeding and membership reads.</summary>
    public async Task<T> WithDbAsync<T>(Func<BucketDbContext, Task<T>> action)
    {
        using var scope = _host.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<BucketDbContext>();

        return await action(context);
    }

    /// <summary>Runs a callback against a fresh ProfileDbContext scope — seeding wallets' X25519 keys.</summary>
    public async Task<T> WithProfileDbAsync<T>(Func<ProfileDbContext, Task<T>> action)
    {
        using var scope = _host.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<ProfileDbContext>();

        return await action(context);
    }

    public async Task SeedViewerKeyAsync(string wallet, string key)
    {
        await WithProfileDbAsync(async db =>
        {
            var profile = await db.Profiles.FindAsync(wallet);
            if (profile is null)
            {
                db.Profiles.Add(new XcavateProfile.Client.Profile
                {
                    Ss58Address = wallet,
                    X25519Key = key
                });
            }
            else
            {
                profile.X25519Key = key;
            }
            await db.SaveChangesAsync();
            return true;
        });
    }

    public async Task SeedTermsDocumentAsync(string wallet, byte[]? pdf = null)
    {
        pdf ??= "%PDF-1.4 lawyer terms"u8.ToArray();
        using var content = new MemoryStream(pdf, writable: false);
        var key = $"terms-documents/{wallet}.pdf";
        var url = await S3.UploadImageAsync("xcavate-profile", key, content, "application/pdf");
        await WithProfileDbAsync(async db =>
        {
            db.LawyerTermsDocuments.Add(new XcavateProfileApi.Models.LawyerTermsDocument
            {
                Ss58Address = wallet,
                Url = url,
                FileName = "terms.pdf",
                ContentType = "application/pdf",
                UpdatedAt = DateTimeOffset.UtcNow
            });
            await db.SaveChangesAsync();
            return true;
        });
    }

    public async ValueTask DisposeAsync()
    {
        Client.Dispose();
        await _host.StopAsync();
        _host.Dispose();
        _connection.Dispose();
        _profileConnection.Dispose();
    }
}
