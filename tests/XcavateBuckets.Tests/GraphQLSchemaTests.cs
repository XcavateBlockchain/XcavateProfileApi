using HotChocolate.Execution;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using XcavateBuckets.Domain;
using XcavateBuckets.Domain.Data;
using XcavateBuckets.Domain.Services;
using XcavateProfileApi.GraphQL;
using XcavateProfileApi.GraphQL.Auth;

namespace XcavateBuckets.Tests;

/// <summary>
/// Guards the published schema against accidental drift. The shapes asserted here are the contract
/// the SubQuery indexer's consumers depend on.
/// </summary>
[TestFixture]
public class GraphQLSchemaTests
{
    private static async Task<string> BuildSdlAsync()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDbContext<BucketDbContext>(o => o.UseSqlite("DataSource=:memory:"));
        services.AddSingleton(new BucketOptions());
        services.AddSingleton(TimeProvider.System);
        services.AddScoped<InputValidator>();
        services.AddScoped<AuthorizationService>();
        services.AddScoped<NamespaceService>();
        services.AddScoped<BucketService>();
        services.AddScoped<MembershipService>();
        services.AddScoped<TagService>();
        services.AddScoped<MessageService>();
        services.AddScoped<IBucketNotifier, NullBucketNotifier>();
        services.AddScoped<CallerContext>();
        services.AddScoped<ICallerContext>(sp => sp.GetRequiredService<CallerContext>());

        var schema = await services
            .AddGraphQLServer()
            .AddQueryType<BucketQueries>()
            .AddMutationType<BucketMutations>()
            .AddType<BigIntType>()
            .AddType<NamespaceType>()
            .AddType<NamespaceManagerType>()
            .AddType<BucketType>()
            .AddType<BucketAdminType>()
            .AddType<BucketContributorType>()
            .AddType<BucketViewerType>()
            .AddType<TagType>()
            .AddType<TagMessageCountType>()
            .AddType<MessageType>()
            .BindRuntimeType<long, BigIntType>()
            .AddFiltering()
            .AddSorting()
            .AddErrorFilter<BucketErrorFilter>()
            .BuildSchemaAsync();

        return schema.ToString();
    }

    /// <summary>
    /// Writes the SDL to a version-controlled snapshot. Committing it makes schema changes visible
    /// in review, and it is the input StrawberryShake generates the client from.
    /// </summary>
    [Test]
    public async Task Schema_snapshot_is_up_to_date()
    {
        var sdl = NormalizeLineEndings(await BuildSdlAsync());

        var repositoryRoot = FindRepositoryRoot();
        var snapshotPath = Path.Combine(repositoryRoot, "docs", "graphql", "schema.graphql");
        Directory.CreateDirectory(Path.GetDirectoryName(snapshotPath)!);

        var existing = File.Exists(snapshotPath)
            ? NormalizeLineEndings(await File.ReadAllTextAsync(snapshotPath))
            : null;

        if (existing != sdl)
        {
            await File.WriteAllTextAsync(snapshotPath, sdl);
            Assert.That(existing, Is.Not.Null.And.Not.Empty,
                $"schema snapshot was missing and has been written to {snapshotPath}; "
                + "commit it and re-run");
            Assert.Fail(
                $"schema snapshot at {snapshotPath} was stale and has been refreshed. "
                + "Review the diff and commit it.");
        }

        Assert.Pass();
    }

    /// <summary>
    /// HotChocolate serialises the SDL through a <see cref="StringWriter"/>, so it uses
    /// <see cref="Environment.NewLine"/>: CRLF on Windows, LF on the Linux CI runner. Without this the
    /// snapshot differs on every line depending on who last regenerated it. The snapshot is stored with
    /// LF, which .gitattributes keeps stable.
    /// </summary>
    private static string NormalizeLineEndings(string sdl) => sdl.Replace("\r\n", "\n");

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(TestContext.CurrentContext.TestDirectory);

        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "XcavateProfile.sln")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName
            ?? throw new InvalidOperationException("Could not locate the repository root.");
    }

    [Test]
    public async Task Schema_declares_all_nine_bucket_entities()
    {
        var sdl = await BuildSdlAsync();

        Assert.Multiple(() =>
        {
            Assert.That(sdl, Does.Contain("type Namespace"));
            Assert.That(sdl, Does.Contain("type NamespaceManager"));
            Assert.That(sdl, Does.Contain("type Bucket "));
            Assert.That(sdl, Does.Contain("type BucketAdmin"));
            Assert.That(sdl, Does.Contain("type BucketContributor"));
            Assert.That(sdl, Does.Contain("type BucketViewer"));
            Assert.That(sdl, Does.Contain("type Tag "));
            Assert.That(sdl, Does.Contain("type TagMessageCount"));
            Assert.That(sdl, Does.Contain("type Message "));
        });
    }

    [Test]
    public async Task Ids_use_the_BigInt_scalar()
    {
        var sdl = await BuildSdlAsync();

        Assert.Multiple(() =>
        {
            Assert.That(sdl, Does.Contain("namespaceId: BigInt!"));
            Assert.That(sdl, Does.Contain("bucketId: BigInt!"));
            Assert.That(sdl, Does.Contain("messageId: BigInt!"));
            Assert.That(sdl, Does.Contain("messageIdNumber: BigInt!"));
            Assert.That(sdl, Does.Contain("bucketIdNumber: BigInt!"));
            Assert.That(sdl, Does.Contain("scalar BigInt"));
        });
    }

    [Test]
    public async Task Block_height_fields_are_gone_in_favour_of_timestamps()
    {
        var sdl = await BuildSdlAsync();

        Assert.Multiple(() =>
        {
            Assert.That(sdl, Does.Not.Contain("createdBlock"));
            Assert.That(sdl, Does.Not.Contain("addedBlock"));
            Assert.That(sdl, Does.Not.Contain("updatedBlock"));
            Assert.That(sdl, Does.Contain("createdAt: DateTime!"));
            Assert.That(sdl, Does.Contain("addedAt: DateTime!"));
        });
    }

    [Test]
    public async Task Connections_expose_nodes_and_total_count()
    {
        var sdl = await BuildSdlAsync();

        Assert.Multiple(() =>
        {
            Assert.That(sdl, Does.Contain("type BucketsConnection"));
            Assert.That(sdl, Does.Contain("nodes: [Bucket!]"));
            Assert.That(sdl, Does.Contain("totalCount: Int!"));
            Assert.That(sdl, Does.Contain("pageInfo: PageInfo!"));
        });
    }

    [Test]
    public async Task Plural_queries_accept_where_and_order()
    {
        var sdl = await BuildSdlAsync();

        Assert.Multiple(() =>
        {
            Assert.That(sdl, Does.Contain("where: BucketFilterInput"));
            Assert.That(sdl, Does.Contain("order: [BucketSortInput!]"));
        });
    }

    [Test]
    public async Task Bucket_exposes_the_namespace_relation_the_indexer_only_declared()
    {
        var sdl = await BuildSdlAsync();

        var start = sdl.IndexOf("type Bucket ", StringComparison.Ordinal);
        Assert.That(start, Is.GreaterThanOrEqualTo(0));
        var bucketBlock = sdl[start..sdl.IndexOf('}', start)];

        Assert.Multiple(() =>
        {
            Assert.That(bucketBlock, Does.Contain("namespace: Namespace"),
                "the indexer's @derivedFrom(field: \"namespace\") pointed at a field Bucket lacked");
            Assert.That(bucketBlock, Does.Not.Contain("namespace: Namespace!"),
                "standalone buckets have no namespace, so the relation is nullable");
        });
    }

    [Test]
    public async Task CreateBucket_accepts_an_omitted_namespace()
    {
        var sdl = await BuildSdlAsync();

        Assert.That(sdl,
            Does.Contain("createBucket(namespaceId: BigInt metadata: BucketMetadataInput!)")
                .Or.Contain("createBucket(namespaceId: BigInt, metadata: BucketMetadataInput!)"),
            "namespaceId must be optional so a bucket can be created outside any namespace");
    }

    [Test]
    public async Task All_twenty_extrinsics_are_exposed_as_mutations()
    {
        var sdl = await BuildSdlAsync();

        string[] mutations =
        [
            "createNamespace(", "addContributor(", "removeContributor(", "addAdmin(",
            "removeAdmin(", "addManager(", "removeManager(", "createBucket(", "pauseWriting(",
            "resumeWriting(", "createTag(", "rotateKey(", "write(", "forceRemoveNamespace(",
            "forceRemoveBucket(", "forceRemoveTag(", "forceRemoveMessage(", "forceAddManager(",
            "addViewer(", "removeViewer("
        ];

        Assert.Multiple(() =>
        {
            foreach (var mutation in mutations)
            {
                Assert.That(sdl, Does.Contain(mutation), $"missing mutation {mutation}");
            }
        });
    }

    [Test]
    public async Task Message_input_carries_caller_supplied_content()
    {
        var sdl = await BuildSdlAsync();

        Assert.Multiple(() =>
        {
            Assert.That(sdl, Does.Contain("input MessageInput"));
            Assert.That(sdl, Does.Contain("ipfsContent: String"));
        });
    }
}
