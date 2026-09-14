using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using XcavateProfileApi.Controllers;
using XcavateProfileApi.Middleware;
using XcavateProfileApi.Services;

namespace XcavateBuckets.Tests;

/// <summary>
/// An in-process host running the real <see cref="MarketplaceController"/> over the real
/// <see cref="SignatureValidator"/> and <see cref="MarketplaceRentCollectorSigningService"/> —
/// the stateless counterpart to <see cref="MigrationsHost"/>. The rent collector keypair comes
/// from an in-memory <c>RENT_COLLECTOR_PRIVATE_KEY</c> entry, mirroring the deployment .env.
/// </summary>
public sealed class MarketplaceHost : IAsyncDisposable
{
    private readonly IHost _host;

    private MarketplaceHost(IHost host, HttpClient client, string? rentAddress, byte[]? rentPubkey)
    {
        _host = host;
        Client = client;
        RentAddress = rentAddress!;
        RentPubkey = rentPubkey!;
    }

    /// <summary>A raw client, for requests that deliberately carry no or broken credentials.</summary>
    public HttpClient Client { get; }

    /// <summary>The rent collector's base58 address (null when the key is not configured).</summary>
    public string? RentAddress { get; }

    /// <summary>The rent collector's raw 32-byte public key (empty when not configured).</summary>
    public byte[] RentPubkey { get; }

    /// <summary>Starts the host with a freshly generated, configured rent collector keypair.</summary>
    public static Task<MarketplaceHost> StartAsync() =>
        StartAsync(rentKeyPair: new Solnet.Wallet.Account().PrivateKey.KeyBytes);

    /// <summary>
    /// Starts the host. Pass <paramref name="rentKeyPair"/> (64-byte seed||pubkey) to configure
    /// the rent collector, or null for the not-configured 503 case.
    /// </summary>
    public static async Task<MarketplaceHost> StartAsync(byte[]? rentKeyPair)
    {
        var keypair = rentKeyPair ?? new Solnet.Wallet.Account().PrivateKey.KeyBytes;
        var rentPubkey = keypair.AsSpan(32, 32).ToArray();

        var configuration = new Dictionary<string, string?>
        {
            ["RENT_COLLECTOR_PRIVATE_KEY"] = rentKeyPair == null ? null : Solnet.Wallet.Utilities.Encoders.Base58.EncodeData(keypair),
            ["RENT_COLLECTOR_MARKETPLACE_PROGRAM_ID"] = "dj9Q3CpHvDHwexCbkgJ5APDx4JsTxPssNebkvP15g1T"
        };

        var builder = Host.CreateDefaultBuilder();
        builder.ConfigureAppConfiguration((_, cfg) => cfg.AddInMemoryCollection(configuration));
        builder.ConfigureWebHost(web =>
        {
            web.UseTestServer();
            web.ConfigureServices(services =>
            {
                services
                    .AddControllers(options => options.SuppressAsyncSuffixInActionNames = false)
                    .AddApplicationPart(typeof(MarketplaceController).Assembly);

                services.AddSingleton<List<string>>([]);
                services.AddScoped(_ => new SignatureValidationOptions());
                services.AddScoped<ISignatureValidator, SignatureValidator>();
                services.AddSingleton(sp => new MarketplaceRentCollectorSigningService(
                    sp.GetRequiredService<IConfiguration>()));
            });
            web.Configure(app =>
            {
                app.UseRouting();
                app.UseEndpoints(e => e.MapControllers());
            });
        });

        var host = await builder.StartAsync();
        var client = host.GetTestClient();

        return new MarketplaceHost(
            host,
            client,
            rentKeyPair == null ? null : Solnet.Wallet.Utilities.Encoders.Base58.EncodeData(rentPubkey),
            rentKeyPair == null ? [] : rentPubkey);
    }

    public async ValueTask DisposeAsync()
    {
        Client.Dispose();
        await _host.StopAsync();
        _host.Dispose();
    }
}
