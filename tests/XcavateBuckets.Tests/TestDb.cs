using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using XcavateBuckets.Domain;
using XcavateBuckets.Domain.Data;
using XcavateBuckets.Domain.Entities;
using XcavateBuckets.Domain.Services;

namespace XcavateBuckets.Tests;

/// <summary>
/// A throwaway SQLite-backed context plus the domain services under test. The connection is held
/// open for the helper's lifetime, because an in-memory SQLite database vanishes when its last
/// connection closes.
/// </summary>
public sealed class TestDb : IDisposable
{
    public const string Alice = "5GrwvaEF5zKbXCEe9qGjZL23Y641mot2Ff6hS3s8jF3g3k3W";
    public const string Bob = "5FHneW46xGXgs5mUiveU4sbTyGBzmstUspZC92UhjJM694ty";
    public const string Carol = "5FLSigC9HGRKVhB9FiEo4Y3koPsNmBmLJbpXg2mp1hXcS59Y";
    public const string Dave = "5DAAnrj7VHTznn2AWBemMuyBwZWs6FNFjdyVXUeYum3PTXFy";

    public const string Key32 = "0x1111111111111111111111111111111111111111111111111111111111111111";
    public const string OtherKey32 = "0x2222222222222222222222222222222222222222222222222222222222222222";
    public const string ViewerKey32 = "0x3333333333333333333333333333333333333333333333333333333333333333";
    public const string Hash32 = "0x4444444444444444444444444444444444444444444444444444444444444444";

    private readonly SqliteConnection _connection;

    public TestDb()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();

        Db = new BucketDbContext(new DbContextOptionsBuilder<BucketDbContext>()
            .UseSqlite(_connection)
            .Options);
        Db.Database.EnsureCreated();

        Clock = new FakeTimeProvider(new DateTime(2026, 7, 25, 12, 0, 0, DateTimeKind.Utc));
        Options = new BucketOptions();
        Validator = new InputValidator(Options);
        Auth = new AuthorizationService(Db);
    }

    public BucketDbContext Db { get; }

    public FakeTimeProvider Clock { get; }

    public BucketOptions Options { get; }

    public InputValidator Validator { get; }

    public AuthorizationService Auth { get; }

    /// <summary>Inserts a namespace directly, bypassing the service layer.</summary>
    public async Task<Namespace> SeedNamespaceAsync(params string[] managers)
    {
        var ns = new Namespace
        {
            Name = "seeded",
            Creator = managers.FirstOrDefault(),
            CreatedAt = Clock.GetUtcNow().UtcDateTime,
            UpdatedAt = Clock.GetUtcNow().UtcDateTime
        };
        Db.Namespaces.Add(ns);
        await Db.SaveChangesAsync();

        foreach (var manager in managers)
        {
            Db.NamespaceManagers.Add(new NamespaceManager
            {
                NamespaceId = ns.NamespaceId,
                Manager = manager,
                AddedAt = Clock.GetUtcNow().UtcDateTime
            });
        }

        await Db.SaveChangesAsync();
        return ns;
    }

    /// <summary>Inserts a bucket directly, bypassing the service layer.</summary>
    public async Task<Bucket> SeedBucketAsync(
        long namespaceId,
        bool isWritable = false,
        string[]? admins = null,
        string[]? contributors = null)
    {
        var bucket = new Bucket
        {
            NamespaceId = namespaceId,
            Name = "seeded",
            Category = "test",
            IsWritable = isWritable,
            EncryptionKey = isWritable ? Key32 : null,
            NextMessageId = 0,
            CreatedAt = Clock.GetUtcNow().UtcDateTime,
            UpdatedAt = Clock.GetUtcNow().UtcDateTime
        };
        Db.Buckets.Add(bucket);
        await Db.SaveChangesAsync();

        foreach (var admin in admins ?? [])
        {
            Db.BucketAdmins.Add(new BucketAdmin
            {
                BucketId = bucket.BucketId,
                SubjectId = admin,
                AddedAt = Clock.GetUtcNow().UtcDateTime
            });
        }

        foreach (var contributor in contributors ?? [])
        {
            Db.BucketContributors.Add(new BucketContributor
            {
                BucketId = bucket.BucketId,
                SubjectId = contributor,
                AddedAt = Clock.GetUtcNow().UtcDateTime
            });
        }

        await Db.SaveChangesAsync();
        return bucket;
    }

    public void Dispose()
    {
        Db.Dispose();
        _connection.Dispose();
    }
}

/// <summary>A clock the tests can advance, so timestamp assertions stay deterministic.</summary>
public sealed class FakeTimeProvider(DateTime utcNow) : TimeProvider
{
    private DateTimeOffset _now = new(utcNow, TimeSpan.Zero);

    public override DateTimeOffset GetUtcNow() => _now;

    public void Advance(TimeSpan by) => _now = _now.Add(by);
}
