using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.OpenApi.Models;
using XcavateProfileApi.Controllers;
using XcavateProfileApi.Swagger;

namespace XcavateBuckets.Tests;

/// <summary>
/// The OpenAPI document asserted the way a consumer reads it — the same Swagger pipeline
/// <c>Program.cs</c> registers (XML comments, the exclude filter, the signed-request filter and
/// the document filter) driven through an in-process host, then fetched as raw
/// <c>swagger.json</c>. The document is the documentation contract: what the description must
/// explain about the signature scheme, and which operations advertise the three headers.
/// </summary>
[TestFixture]
public class SwaggerDocumentationTests
{
    private JsonDocument _swagger = default!;

    [OneTimeSetUp]
    public async Task GenerateDocumentAsync()
    {
        var builder = Host.CreateDefaultBuilder();
        builder.ConfigureWebHost(web =>
        {
            web.UseTestServer();
            web.ConfigureServices(services =>
            {
                // The controller assembly is not the host's entry assembly in-process, so the
                // parts must be added explicitly — same trick RestHost and MigrationsHost use.
                services
                    .AddControllers()
                    .AddApplicationPart(typeof(ProfilesController).Assembly);

                // Mirrors Program.cs exactly: the same title, the same XML files, the same
                // filters in the same order.
                services.AddSwaggerGen(c =>
                {
                    c.SwaggerDoc("v1", new OpenApiInfo
                    {
                        Title = "XcavateProfile API",
                        Version = "v1",
                        Description = "A Substrate/Polkadot profile registration and management API"
                    });

                    c.IncludeXmlComments(Path.Combine(AppContext.BaseDirectory, "XcavateProfileApi.xml"));
                    c.IncludeXmlComments(Path.Combine(AppContext.BaseDirectory, "XcavateProfileApiClient.xml"), true);

                    c.OperationFilter<ExcludeSwaggerOperationFilter>();
                    c.OperationFilter<SignedRequestOperationFilter>();
                    c.DocumentFilter<ApiInfoDocumentFilter>();
                });
            });
            web.Configure(app =>
            {
                // Routing must run before Swagger: the /swagger.json document is only built from
                // the controller API descriptions once the router has built the endpoint data
                // sources — the order WebApplication imposes automatically.
                app.UseRouting();
                app.UseSwagger();
                app.UseEndpoints(e => e.MapControllers());
            });
        });

        using var host = await builder.StartAsync();
        var response = await host.GetTestClient().GetAsync("/swagger/v1/swagger.json");

        response.EnsureSuccessStatusCode();
        _swagger = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
    }

    [OneTimeTearDown]
    public void DisposeDocument() => _swagger.Dispose();

    private JsonElement Operation(string path, string method) =>
        _swagger.RootElement.GetProperty("paths").GetProperty(path).GetProperty(method);

    /// <summary>The scheme names every security requirement of an operation references, flattened.</summary>
    private static string[] SecuritySchemes(JsonElement operation)
    {
        if (!operation.TryGetProperty("security", out var security))
        {
            return [];
        }

        var names = new List<string>();
        foreach (var requirement in security.EnumerateArray())
        {
            foreach (var property in requirement.EnumerateObject())
            {
                names.Add(property.Name);
            }
        }

        return [.. names];
    }

    private static readonly string[] SignatureSchemes =
    {
        ApiInfoDocumentFilter.AddressScheme,
        ApiInfoDocumentFilter.SignatureScheme,
        ApiInfoDocumentFilter.TimestampScheme
    };

    // ---- the front page: the signature scheme, documented end to end ---------------------------

    [Test]
    public void The_api_description_documents_the_signature_scheme_end_to_end()
    {
        var description = _swagger.RootElement.GetProperty("info").GetProperty("description").GetString()!;

        // The whole contract a signer must implement, in one place.
        Assert.Multiple(() =>
        {
            Assert.That(description, Does.Contain("X-SS58-Address"), "the header names");
            Assert.That(description, Does.Contain("X-Signature"));
            Assert.That(description, Does.Contain("X-Timestamp"));
            Assert.That(description, Does.Contain("{METHOD}:{path}:{body_hash}:{timestamp}"),
                "the signed payload template");
            Assert.That(description, Does.Contain("case-insensitive"),
                "the route-casing gotcha — sign the lowercase route");
            Assert.That(description, Does.Contain("lowercase form"),
                "the signed path is the lowercase route, not the document's Pascal-case keys");
            Assert.That(description, Does.Contain("Blake2b-128"), "the body-hash algorithm");
            Assert.That(description, Does.Contain("sr25519"), "scheme one");
            Assert.That(description, Does.Contain("ed25519"), "scheme two");
            Assert.That(description, Does.Contain("uppercase"), "the hash case gotcha");
            Assert.That(description, Does.Contain("7 fractional digits"), "the timestamp gotcha");
            Assert.That(description, Does.Contain("5 minutes"), "the replay window");
            Assert.That(description, Does.Contain("/graphql"), "the bucket surface is referenced");
            Assert.That(description, Does.Contain("401"), "REST auth failures");
            Assert.That(description, Does.Contain("403"), "REST authorization failures");
            Assert.That(description, Does.Contain("INVALID_SIGNATURE"), "GraphQL error codes");
            Assert.That(description, Does.Contain("TIMESTAMP_OUT_OF_RANGE"));
        });
    }

    [Test]
    public void The_three_header_schemes_are_declared_as_api_key_security_schemes()
    {
        var schemes = _swagger.RootElement.GetProperty("components").GetProperty("securitySchemes");

        Assert.Multiple(() =>
        {
            foreach (var name in SignatureSchemes)
            {
                var scheme = schemes.GetProperty(name);
                Assert.That(scheme.GetProperty("type").GetString(), Is.EqualTo("apiKey"), name);
                Assert.That(scheme.GetProperty("in").GetString(), Is.EqualTo("header"), name);
                Assert.That(scheme.GetProperty("name").GetString(), Is.EqualTo(name),
                    "the scheme name is the header itself");
            }
            var count = 0;
            foreach (var _ in schemes.EnumerateObject())
            {
                count++;
            }

            Assert.That(count, Is.EqualTo(3), "no other schemes are advertised");
        });
    }

    // ---- which operations carry the requirement -------------------------------------------------

    [Test]
    public void Every_signed_operation_carries_the_signature_security_requirement()
    {
        // The nine actions that verify the headers in code, by their OpenAPI path and verb.
        // The paths are the document's own keys — Pascal-case, because [Route("api/[controller]")]
        // resolves the controller token to the class name and routing is case-insensitive. The
        // *signed* payload always uses the lowercase route (see the casing note in the description).
        var signedOperations = new[]
        {
            (Path: "/api/Profiles", Method: "post"),
            (Path: "/api/Profiles/{ss58address}", Method: "put"),
            (Path: "/api/Profiles/{ss58address}", Method: "delete"),
            (Path: "/api/Profiles/{ss58address}/image", Method: "post"),
            (Path: "/api/Companies", Method: "post"),
            (Path: "/api/Companies/{companyId}", Method: "put"),
            (Path: "/api/Companies/{companyId}", Method: "delete"),
            (Path: "/api/Companies/{companyId}/logo", Method: "post"),
            (Path: "/api/Migrations", Method: "post")
        };

        Assert.Multiple(() =>
        {
            foreach (var (path, method) in signedOperations)
            {
                var operation = Operation(path, method);
                var schemes = SecuritySchemes(operation);

                foreach (var scheme in SignatureSchemes)
                {
                    Assert.That(schemes, Does.Contain(scheme), $"{path} {method}");
                }
            }
        });
    }

    [Test]
    public void The_public_reads_advertise_no_security_requirement()
    {
        var publicReads = new[]
        {
            (Path: "/api/Profiles", Method: "get"),
            (Path: "/api/Profiles/{ss58address}", Method: "get"),
            (Path: "/api/Profiles/nickname/{nickname}", Method: "get"),
            (Path: "/api/Companies", Method: "get"),
            (Path: "/api/Companies/{companyId}", Method: "get"),
            (Path: "/api/Companies/user/{userId}", Method: "get"),
            (Path: "/api/Migrations", Method: "get"),
            (Path: "/api/Migrations/{ss58address}", Method: "get")
        };

        Assert.Multiple(() =>
        {
            foreach (var (path, method) in publicReads)
            {
                Assert.That(SecuritySchemes(Operation(path, method)), Is.Empty, $"{path} {method}");
            }
            Assert.That(_swagger.RootElement.TryGetProperty("security", out _), Is.False,
                "no global requirement either — the document must not imply every operation is signed");
        });
    }

    // ---- the XML comments actually flow through --------------------------------------------------

    [Test]
    public void Operation_summaries_come_from_the_xml_comments()
    {
        Assert.Multiple(() =>
        {
            Assert.That(
                Operation("/api/Profiles", "get").GetProperty("summary").GetString(),
                Is.EqualTo("Lists every profile. Public read — no signature."),
                "the web project's doc file is included");
            Assert.That(
                Operation("/api/Migrations", "post").GetProperty("summary").GetString(),
                Does.StartWith("Registers a Polkadot"),
                "and the signed actions are documented too");
        });
    }
}
