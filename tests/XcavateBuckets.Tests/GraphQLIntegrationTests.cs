using System.Linq;
using System.Text.Json;
using Substrate.NET.Wallet.Keyring;
using Substrate.NetApi.Model.Types;
using static Substrate.NetApi.Mnemonic;

namespace XcavateBuckets.Tests;

/// <summary>
/// Drives the shipped pipeline over HTTP: signature verification, field authorization, the domain
/// rules, and error-code mapping.
/// </summary>
[TestFixture]
public class GraphQLIntegrationTests
{
    private const string Key32 = TestDb.Key32;
    private const string Hash32 = TestDb.Hash32;

    /// <summary>
    /// Deterministic sr25519 accounts, built the way the existing profile tests build them: a
    /// mnemonic from fixed entropy, so the BIP39 checksum is valid and addresses are stable.
    /// </summary>
    private static Account NewAccount(byte entropyFill)
    {
        var mnemonic = string.Join(
            " ", MnemonicFromEntropy(Enumerable.Repeat(entropyFill, 16).ToArray(), BIP39Wordlist.English));

        return new Keyring()
            .AddFromMnemonic(mnemonic, new Meta { Name = "BucketTests" }, KeyType.Sr25519)
            .Account;
    }

    private static Account Alice() => NewAccount(0x21);

    private static Account Bob() => NewAccount(0x22);

    [Test]
    public async Task Client_signing_handler_is_accepted_by_the_server()
    {
        var alice = Alice();
        await using var host = await GraphQLHost.StartAsync();
        using var client = host.CreateSigningClient(alice);

        var body = JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["query"] = """mutation { createNamespace(metadata: { name: "via-handler" }) { id name creator } }"""
        });

        var response = await client.PostAsync(
            "/graphql", new StringContent(body, System.Text.Encoding.UTF8, "application/json"));
        var result = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        Assert.That(result.FirstErrorCode(), Is.Null, result.RootElement.ToString());
        Assert.That(result.Data("createNamespace").GetProperty("creator").GetString(),
            Is.EqualTo(alice.Value),
            "SigningHttpMessageHandler and GraphQLSignatureMiddleware must agree on the payload");
    }

    [Test]
    public async Task Unsigned_query_succeeds()
    {
        await using var host = await GraphQLHost.StartAsync();

        var result = await host.QueryAsync("{ namespaces { totalCount nodes { id name } } }");

        Assert.Multiple(() =>
        {
            Assert.That(result.FirstErrorCode(), Is.Null);
            Assert.That(result.Data("namespaces").GetProperty("totalCount").GetInt32(), Is.Zero);
        });
    }

    [Test]
    public async Task Unsigned_mutation_is_rejected_as_unauthorized()
    {
        await using var host = await GraphQLHost.StartAsync();

        var result = await host.QueryAsync(
            """mutation { createNamespace(metadata: { name: "deeds" }) { id } }""");

        Assert.That(result.FirstErrorCode(), Is.EqualTo("UNAUTHORIZED"));
    }

    [Test]
    public async Task Signed_mutation_creates_a_namespace_and_makes_the_caller_manager()
    {
        var alice = Alice();
        await using var host = await GraphQLHost.StartAsync();

        var created = await host.SignedAsync(
            """mutation { createNamespace(metadata: { name: "deeds" }) { id name creator } }""",
            alice);

        Assert.That(created.FirstErrorCode(), Is.Null, created.RootElement.ToString());

        var ns = created.Data("createNamespace");
        Assert.Multiple(() =>
        {
            Assert.That(ns.GetProperty("name").GetString(), Is.EqualTo("deeds"));
            Assert.That(ns.GetProperty("creator").GetString(), Is.EqualTo(alice.Value));
        });

        var read = await host.QueryAsync(
            "{ namespaces { nodes { id name managers { manager } } } }");
        var managers = read.Data("namespaces").GetProperty("nodes")[0].GetProperty("managers");

        Assert.That(managers[0].GetProperty("manager").GetString(), Is.EqualTo(alice.Value));
    }

    [Test]
    public async Task Tampered_body_fails_signature_verification()
    {
        var alice = Alice();
        await using var host = await GraphQLHost.StartAsync();

        // Sign one document, then send a different one under the same headers.
        var request = new HttpRequestMessage(HttpMethod.Post, "/graphql");
        var signedBody = JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["query"] = """mutation { createNamespace(metadata: { name: "deeds" }) { id } }"""
        });
        var tamperedBody = signedBody.Replace("deeds", "other");

        var ts = DateTime.UtcNow;
        var bodyHash = Substrate.NetApi.Utils.Bytes2HexString(
            XcavateProfile.Client.CryptoHelper.Hash(signedBody));
        var signature = await XcavateProfile.Client.CryptoHelper.SignAsync(
            $"POST:/graphql:{bodyHash}:{ts.ToUniversalTime():o}", alice);

        request.Content = new StringContent(
            tamperedBody, System.Text.Encoding.UTF8, "application/json");
        request.Headers.Add("X-SS58-Address", alice.Value);
        request.Headers.Add("X-Signature", Substrate.NetApi.Utils.Bytes2HexString(signature));
        request.Headers.Add("X-Timestamp", ts.ToUniversalTime().ToString("o"));

        var response = await host.Client.SendAsync(request);
        var result = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        Assert.That(result.FirstErrorCode(), Is.EqualTo("INVALID_SIGNATURE"));
    }

    [Test]
    public async Task Stale_timestamp_is_rejected()
    {
        var alice = Alice();
        await using var host = await GraphQLHost.StartAsync();

        var result = await host.SignedAsync(
            """mutation { createNamespace(metadata: { name: "deeds" }) { id } }""",
            alice,
            timestamp: DateTime.UtcNow.AddMinutes(-30));

        Assert.That(result.FirstErrorCode(), Is.EqualTo("TIMESTAMP_OUT_OF_RANGE"));
    }

    [Test]
    public async Task Force_mutation_is_forbidden_for_a_non_admin()
    {
        var alice = Alice();
        await using var host = await GraphQLHost.StartAsync();

        var result = await host.SignedAsync(
            "mutation { forceRemoveNamespace(namespaceId: \"1\") }", alice);

        Assert.That(result.FirstErrorCode(), Is.EqualTo("FORBIDDEN"));
    }

    [Test]
    public async Task Force_mutation_is_allowed_for_an_admin_address()
    {
        var alice = Alice();
        await using var host = await GraphQLHost.StartAsync(alice.Value);

        await host.SignedAsync(
            """mutation { createNamespace(metadata: { name: "deeds" }) { id } }""", alice);

        // Alice is the sole manager, so the namespace is not yet removable.
        var result = await host.SignedAsync(
            "mutation { forceRemoveNamespace(namespaceId: \"1\") }", alice);

        Assert.That(result.FirstErrorCode(), Is.EqualTo("DANGLING_MANAGERS"),
            "admin passed authorization and reached the domain rule");
    }

    [Test]
    public async Task Non_manager_creating_a_bucket_is_reported_as_not_manager()
    {
        var alice = Alice();
        var bob = Bob();
        await using var host = await GraphQLHost.StartAsync();

        await host.SignedAsync(
            """mutation { createNamespace(metadata: { name: "deeds" }) { id } }""", alice);

        var result = await host.SignedAsync(
            """
            mutation {
              createBucket(namespaceId: "1", metadata: { name: "b", category: "c" }) { id }
            }
            """, bob);

        Assert.That(result.FirstErrorCode(), Is.EqualTo("NOT_MANAGER"));
    }

    [Test]
    public async Task Full_lifecycle_writes_and_reads_a_message_through_nested_selection()
    {
        var alice = Alice();
        await using var host = await GraphQLHost.StartAsync();

        async Task<JsonDocument> Run(string mutation)
        {
            var response = await host.SignedAsync(mutation, alice);
            Assert.That(response.FirstErrorCode(), Is.Null, response.RootElement.ToString());
            return response;
        }

        await Run("""mutation { createNamespace(metadata: { name: "deeds" }) { id } }""");
        await Run("""
                  mutation {
                    createBucket(namespaceId: "1", metadata: { name: "titles", category: "legal" }) {
                      id isWritable
                    }
                  }
                  """);
        // Alice manages the namespace, so she can appoint herself bucket admin.
        await Run($$"""mutation { addAdmin(namespaceId: "1", bucketId: "1", admin: "{{alice.Value}}") { id } }""");
        await Run($$"""mutation { addContributor(namespaceId: "1", bucketId: "1", contributor: "{{alice.Value}}") { id } }""");
        await Run("""mutation { createTag(bucketId: "1", newTag: "deed-scan") { id tagName } }""");
        await Run($$"""mutation { resumeWriting(namespaceId: "1", bucketId: "1", newEncryptionKey: "{{Key32}}") { isWritable } }""");

        await Run($$"""
                    mutation {
                      write(namespaceId: "1", bucketId: "1", message: {
                        reference: "bafybeigdyrzt5example"
                        tag: "deed-scan"
                        ipfsContent: "the deed text"
                        metadata: {
                          description: "a deed"
                          contentType: "text/plain"
                          contentHash: "{{Hash32}}"
                        }
                      }) { id messageId contributor }
                    }
                    """);

        var read = await host.QueryAsync(
            """
            {
              buckets {
                totalCount
                nodes {
                  id name isWritable encryptionKey
                  namespace { id name }
                  admins { subjectId }
                  contributors { subjectId }
                  tags { tagName messageCount }
                  messages { id messageId tag ipfsContent contentHash }
                }
              }
            }
            """);

        Assert.That(read.FirstErrorCode(), Is.Null, read.RootElement.ToString());

        var bucket = read.Data("buckets").GetProperty("nodes")[0];
        var message = bucket.GetProperty("messages")[0];

        Assert.Multiple(() =>
        {
            Assert.That(bucket.GetProperty("isWritable").GetBoolean(), Is.True);
            Assert.That(bucket.GetProperty("encryptionKey").GetString(), Is.EqualTo(Key32));
            Assert.That(bucket.GetProperty("namespace").GetProperty("name").GetString(),
                Is.EqualTo("deeds"));
            Assert.That(bucket.GetProperty("admins")[0].GetProperty("subjectId").GetString(),
                Is.EqualTo(alice.Value));
            Assert.That(bucket.GetProperty("tags")[0].GetProperty("messageCount").GetInt32(),
                Is.EqualTo(1), "writing a tagged message moves the counter");
            Assert.That(message.GetProperty("id").GetString(), Is.EqualTo("1-0"));
            Assert.That(message.GetProperty("messageId").GetString(), Is.EqualTo("0"),
                "BigInt is a string on the wire");
            Assert.That(message.GetProperty("ipfsContent").GetString(), Is.EqualTo("the deed text"));
        });
    }

    [Test]
    public async Task Writing_to_a_locked_bucket_reports_bucket_is_locked()
    {
        var alice = Alice();
        await using var host = await GraphQLHost.StartAsync();

        await host.SignedAsync("""mutation { createNamespace(metadata: { name: "deeds" }) { id } }""", alice);
        await host.SignedAsync("""
                               mutation {
                                 createBucket(namespaceId: "1", metadata: { name: "t", category: "c" }) { id }
                               }
                               """, alice);
        await host.SignedAsync($$"""mutation { addAdmin(namespaceId: "1", bucketId: "1", admin: "{{alice.Value}}") { id } }""", alice);
        await host.SignedAsync($$"""mutation { addContributor(namespaceId: "1", bucketId: "1", contributor: "{{alice.Value}}") { id } }""", alice);

        // No resumeWriting, so the bucket is still in its default locked state.
        var result = await host.SignedAsync($$"""
                                             mutation {
                                               write(namespaceId: "1", bucketId: "1", message: {
                                                 reference: "baf"
                                                 metadata: {
                                                   description: "d"
                                                   contentType: "text/plain"
                                                   contentHash: "{{Hash32}}"
                                                 }
                                               }) { id }
                                             }
                                             """, alice);

        Assert.That(result.FirstErrorCode(), Is.EqualTo("BUCKET_IS_LOCKED"));
    }
}
