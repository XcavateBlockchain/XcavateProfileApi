using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Substrate.NetApi;
using Substrate.NetApi.Model.Types;
using XcavateBuckets.Domain.Data;
using XcavateProfile.Client;
using XcavateProfileApi.GraphQL;
using XcavateProfileApi.GraphQL.Auth;
using XcavateProfileApi.Middleware;
using XcavateProfileApiClient;
using XcavateProfileApiClient.Signing;

namespace XcavateBuckets.Tests;

/// <summary>
/// An in-process host running the real request pipeline — the actual
/// <see cref="GraphQLSignatureMiddleware"/>, the shipped schema registration and a SQLite database.
/// It verifies the parts unit tests cannot: body-hash signing, field authorization and error mapping.
/// </summary>
public sealed class GraphQLHost : IAsyncDisposable
{
    private readonly IHost _host;
    private readonly SqliteConnection _connection;

    private GraphQLHost(IHost host, SqliteConnection connection, HttpClient client)
    {
        _host = host;
        _connection = connection;
        Client = client;
    }

    public HttpClient Client { get; }

    /// <summary>
    /// An HttpClient that signs through the shipped <see cref="SigningHttpMessageHandler"/> rather
    /// than this fixture's own signing code, so client and server are verified against each other.
    /// </summary>
    public HttpClient CreateSigningClient(Account account) =>
        CreateSigningClient(new SubstrateRequestSigner(account));

    public HttpClient CreateSigningClient(IRequestSigner signer)
    {
        var handler = new SigningHttpMessageHandler(signer)
        {
            InnerHandler = _host.GetTestServer().CreateHandler()
        };

        return new HttpClient(handler) { BaseAddress = new Uri("http://localhost") };
    }

    /// <summary>A raw handler onto the test server, for wrapping in the client's own pipeline.</summary>
    public HttpMessageHandler CreateTestMessageHandler() =>
        _host.GetTestServer().CreateHandler();

    public static async Task<GraphQLHost> StartAsync(params string[] adminAddresses)
    {
        var connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();

        var builder = Host.CreateDefaultBuilder();
        builder.ConfigureWebHost(web =>
        {
            web.UseTestServer();
            web.ConfigureServices(services =>
            {
                services.AddRouting();
                services.AddDbContext<BucketDbContext>(o => o.UseSqlite(connection));

                // The same signature validator the REST controllers use.
                services.AddSingleton(adminAddresses.ToList());
                services.AddScoped(_ => new SignatureValidationOptions());
                services.AddScoped<ISignatureValidator, SignatureValidator>();

                services.AddBucketDomain();
                services.AddBucketGraphQL();
            });
            web.Configure(app =>
            {
                app.UseMiddleware<GraphQLSignatureMiddleware>();
                app.UseRouting();
                app.UseEndpoints(e => e.MapGraphQL());
            });
        });

        var host = await builder.StartAsync();

        using (var scope = host.Services.CreateScope())
        {
            await scope.ServiceProvider.GetRequiredService<BucketDbContext>()
                .Database.EnsureCreatedAsync();
        }

        return new GraphQLHost(host, connection, host.GetTestClient());
    }

    /// <summary>Runs a query with no credentials.</summary>
    public Task<JsonDocument> QueryAsync(string query, object? variables = null) =>
        SendAsync(query, variables, signer: null);

    /// <summary>
    /// Runs an operation signed the way the REST client signs: the payload is
    /// <c>POST:/graphql:&lt;blake2 of the exact request body&gt;:&lt;timestamp&gt;</c>.
    /// </summary>
    public Task<JsonDocument> SignedAsync(
        string query, Account signer, object? variables = null, DateTime? timestamp = null) =>
        SendAsync(query, variables, new SubstrateRequestSigner(signer), timestamp);

    /// <summary>The same, for any scheme.</summary>
    public Task<JsonDocument> SignedAsync(
        string query, IRequestSigner signer, object? variables = null, DateTime? timestamp = null) =>
        SendAsync(query, variables, signer, timestamp);

    private async Task<JsonDocument> SendAsync(
        string query, object? variables, IRequestSigner? signer, DateTime? timestamp = null)
    {
        var payload = variables is null
            ? new Dictionary<string, object> { ["query"] = query }
            : new Dictionary<string, object> { ["query"] = query, ["variables"] = variables };

        // Serialize once: the signature covers these exact bytes.
        var body = JsonSerializer.Serialize(payload);

        var request = new HttpRequestMessage(HttpMethod.Post, "/graphql")
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        };

        if (signer is not null)
        {
            var ts = timestamp ?? DateTime.UtcNow;
            var bodyHash = Utils.Bytes2HexString(CryptoHelper.Hash(body));
            var signed = $"POST:/graphql:{bodyHash}:{ts.ToUniversalTime():o}";
            var signature = await signer.SignAsync(signed);

            request.Headers.Add("X-SS58-Address", signer.Address);
            request.Headers.Add("X-Signature", signer.EncodeSignature(signature));
            request.Headers.Add("X-Timestamp", ts.ToUniversalTime().ToString("o"));
        }

        var response = await Client.SendAsync(request);
        var content = await response.Content.ReadAsStringAsync();

        return JsonDocument.Parse(content);
    }

    public async ValueTask DisposeAsync()
    {
        Client.Dispose();
        await _host.StopAsync();
        _host.Dispose();
        await _connection.DisposeAsync();
    }
}

internal static class JsonDocumentExtensions
{
    /// <summary>The first error's stable code, or null when the response carried no errors.</summary>
    public static string? FirstErrorCode(this JsonDocument document)
    {
        if (!document.RootElement.TryGetProperty("errors", out var errors)
            || errors.GetArrayLength() == 0)
        {
            return null;
        }

        return errors[0].TryGetProperty("extensions", out var extensions)
               && extensions.TryGetProperty("code", out var code)
            ? code.GetString()
            : null;
    }

    public static JsonElement Data(this JsonDocument document, string field) =>
        document.RootElement.GetProperty("data").GetProperty(field);
}
