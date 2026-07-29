using System.Linq;
using Microsoft.Extensions.DependencyInjection;
using StrawberryShake;
using Substrate.NET.Wallet.Keyring;
using Substrate.NetApi.Model.Types;
using XcavateProfileApiClient;
using XcavateProfileApiClient.Buckets;
using static Substrate.NetApi.Mnemonic;

// Substrate.NET.Wallet.Keyring also defines a Uri type.
using Uri = System.Uri;

namespace XcavateBuckets.Tests;

/// <summary>
/// Drives the StrawberryShake-generated client against the in-process server, so the published
/// schema, the generated operations and the request signing are verified as one path.
/// </summary>
[TestFixture]
public class GeneratedClientTests
{
    private static Account NewAccount(byte entropyFill)
    {
        var mnemonic = string.Join(
            " ", MnemonicFromEntropy(Enumerable.Repeat(entropyFill, 16).ToArray(), BIP39Wordlist.English));

        return new Keyring()
            .AddFromMnemonic(mnemonic, new Meta { Name = "BucketTests" }, KeyType.Sr25519)
            .Account;
    }

    /// <summary>
    /// Builds a generated client whose transport is the test server, signed by <paramref name="account"/>.
    /// </summary>
    private static (IXcavateBucketsClient Client, ServiceProvider Provider) CreateClient(
        GraphQLHost host, Account account)
    {
        var services = new ServiceCollection();

        services
            .AddXcavateBucketsClient()
            .ConfigureHttpClient(
                client => client.BaseAddress = new Uri("http://localhost/graphql"),
                builder => builder.ConfigurePrimaryHttpMessageHandler(_ =>
                    new SigningHttpMessageHandler(account)
                    {
                        InnerHandler = host.CreateTestMessageHandler()
                    }));

        var provider = services.BuildServiceProvider();
        return (provider.GetRequiredService<IXcavateBucketsClient>(), provider);
    }

    [Test]
    public async Task Generated_client_creates_and_reads_back_a_namespace()
    {
        var alice = NewAccount(0x31);
        await using var host = await GraphQLHost.StartAsync();
        var (client, provider) = CreateClient(host, alice);
        await using var _ = provider;

        var created = await client.CreateNamespace.ExecuteAsync(
            new NamespaceMetadataInput { Name = "generated" });

        Assert.That(created.Errors, Is.Empty, string.Join("; ", created.Errors.Select(e => e.Message)));
        Assert.Multiple(() =>
        {
            Assert.That(created.Data!.CreateNamespace.Name, Is.EqualTo("generated"));
            Assert.That(created.Data.CreateNamespace.Creator, Is.EqualTo(alice.Value));
        });

        var listed = await client.GetNamespaces.ExecuteAsync(first: 10, after: null);

        Assert.That(listed.Errors, Is.Empty,
            string.Join("; ", listed.Errors.Select(e => $"{e.Message} {e.Exception?.Message}")));
        Assert.Multiple(() =>
        {
            Assert.That(listed.Data!.Namespaces!.TotalCount, Is.EqualTo(1));
            Assert.That(listed.Data.Namespaces.Nodes![0]!.Name, Is.EqualTo("generated"));
            Assert.That(listed.Data.Namespaces.Nodes[0]!.Managers[0].Manager, Is.EqualTo(alice.Value));
        });
    }

    [Test]
    public async Task Generated_client_runs_the_full_message_lifecycle()
    {
        var alice = NewAccount(0x31);
        await using var host = await GraphQLHost.StartAsync();
        var (client, provider) = CreateClient(host, alice);
        await using var _ = provider;

        var ns = await client.CreateNamespace.ExecuteAsync(
            new NamespaceMetadataInput { Name = "deeds" });
        ns.EnsureNoErrors();
        var namespaceId = ns.Data!.CreateNamespace.NamespaceId;

        var bucket = await client.CreateBucket.ExecuteAsync(
            namespaceId, new BucketMetadataInput { Name = "titles", Category = "legal" });
        bucket.EnsureNoErrors();
        var bucketId = bucket.Data!.CreateBucket.BucketId;

        Assert.That(bucket.Data.CreateBucket.IsWritable, Is.False,
            "a new bucket is locked until resumeWriting supplies a key");

        (await client.AddAdmin.ExecuteAsync(namespaceId, bucketId, alice.Value)).EnsureNoErrors();
        (await client.AddContributor.ExecuteAsync(namespaceId, bucketId, alice.Value)).EnsureNoErrors();
        (await client.CreateTag.ExecuteAsync(bucketId, "deed-scan")).EnsureNoErrors();

        var resumed = await client.ResumeWriting.ExecuteAsync(namespaceId, bucketId, TestDb.Key32);
        resumed.EnsureNoErrors();
        Assert.That(resumed.Data!.ResumeWriting.IsWritable, Is.True);

        var written = await client.WriteMessage.ExecuteAsync(namespaceId, bucketId, new MessageInput
        {
            Reference = "bafybeigdyrzt5example",
            Tag = "deed-scan",
            IpfsContent = "the deed text",
            Metadata = new MessageMetadataInput
            {
                Description = "a deed",
                ContentType = "text/plain",
                ContentHash = TestDb.Hash32
            }
        });
        written.EnsureNoErrors();

        Assert.Multiple(() =>
        {
            Assert.That(written.Data!.Write.Id, Is.EqualTo($"{bucketId}-0"));
            // BigInt is a string on the wire, so the generated client surfaces it as string.
            Assert.That(written.Data.Write.MessageId, Is.EqualTo("0"));
            Assert.That(written.Data.Write.Contributor, Is.EqualTo(alice.Value));
        });

        var read = await client.GetBucketById.ExecuteAsync(bucketId.ToString());
        read.EnsureNoErrors();

        var readBucket = read.Data!.Bucket!;
        Assert.Multiple(() =>
        {
            Assert.That(readBucket.EncryptionKey, Is.EqualTo(TestDb.Key32));
            Assert.That(readBucket.Namespace, Is.Not.Null);
            Assert.That(readBucket.Namespace!.Name, Is.EqualTo("deeds"));
            Assert.That(readBucket.Messages, Has.Count.EqualTo(1));
            Assert.That(readBucket.Messages[0].IpfsContent, Is.EqualTo("the deed text"));
            Assert.That(readBucket.Messages[0].Tag, Is.EqualTo("deed-scan"));
        });
    }

    [Test]
    public async Task Generated_client_runs_the_lifecycle_of_a_bucket_without_a_namespace()
    {
        var alice = NewAccount(0x31);
        await using var host = await GraphQLHost.StartAsync();
        var (client, provider) = CreateClient(host, alice);
        await using var _ = provider;

        var bucket = await client.CreateBucket.ExecuteAsync(
            null, new BucketMetadataInput { Name = "standalone", Category = "personal" });
        bucket.EnsureNoErrors();
        var bucketId = bucket.Data!.CreateBucket.BucketId;

        // With no namespace, the creator stands in for the manager and appoints herself admin.
        (await client.AddAdmin.ExecuteAsync(null, bucketId, alice.Value)).EnsureNoErrors();
        (await client.AddContributor.ExecuteAsync(null, bucketId, alice.Value)).EnsureNoErrors();
        (await client.ResumeWriting.ExecuteAsync(null, bucketId, TestDb.Key32)).EnsureNoErrors();

        var written = await client.WriteMessage.ExecuteAsync(null, bucketId, new MessageInput
        {
            Reference = "bafybeigdyrzt5example",
            Metadata = new MessageMetadataInput
            {
                ContentType = "text/plain",
                ContentHash = TestDb.Hash32
            }
        });
        written.EnsureNoErrors();

        var read = await client.GetBucketById.ExecuteAsync(bucketId.ToString());
        read.EnsureNoErrors();

        var readBucket = read.Data!.Bucket!;
        Assert.Multiple(() =>
        {
            Assert.That(readBucket.NamespaceId, Is.Null);
            Assert.That(readBucket.Namespace, Is.Null);
            Assert.That(readBucket.Messages, Has.Count.EqualTo(1));
        });
    }

    [Test]
    public async Task Generated_client_surfaces_pallet_error_codes()
    {
        var alice = NewAccount(0x31);
        var bob = NewAccount(0x32);
        await using var host = await GraphQLHost.StartAsync();

        var (aliceClient, aliceProvider) = CreateClient(host, alice);
        await using var _ = aliceProvider;
        var (bobClient, bobProvider) = CreateClient(host, bob);
        await using var __ = bobProvider;

        var ns = await aliceClient.CreateNamespace.ExecuteAsync(
            new NamespaceMetadataInput { Name = "deeds" });
        ns.EnsureNoErrors();

        // Bob is not a manager of Alice's namespace.
        var refused = await bobClient.CreateBucket.ExecuteAsync(
            ns.Data!.CreateNamespace.NamespaceId,
            new BucketMetadataInput { Name = "b", Category = "c" });

        Assert.That(refused.Errors, Is.Not.Empty);
        Assert.That(refused.Errors[0].Extensions!["code"], Is.EqualTo("NOT_MANAGER"));
    }
}
