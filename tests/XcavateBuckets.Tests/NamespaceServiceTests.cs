using Microsoft.EntityFrameworkCore;
using XcavateBuckets.Domain;

namespace XcavateBuckets.Tests;

[TestFixture]
public class NamespaceServiceTests
{
    private static CancellationToken Ct => TestContext.CurrentContext.CancellationToken;

    private TestDb _fixture = null!;

    [SetUp]
    public void SetUp() => _fixture = new TestDb();

    [TearDown]
    public void TearDown() => _fixture.Dispose();

    [Test]
    public async Task CreateAsync_stores_the_namespace_with_its_metadata()
    {
        var ns = await _fixture.Namespaces.CreateAsync(
            TestDb.Alice, "deeds", "ipfs://schema", new Dictionary<string, string>
            {
                ["region"] = "uk"
            }, Ct);

        Assert.Multiple(() =>
        {
            Assert.That(ns.Name, Is.EqualTo("deeds"));
            Assert.That(ns.SchemaUri, Is.EqualTo("ipfs://schema"));
            Assert.That(ns.Properties, Is.EqualTo("""{"region":"uk"}"""));
            Assert.That(ns.Creator, Is.EqualTo(TestDb.Alice));
            Assert.That(ns.CreatedAt, Is.EqualTo(_fixture.Clock.GetUtcNow().UtcDateTime));
        });
    }

    [Test]
    public async Task CreateAsync_makes_the_caller_the_first_manager()
    {
        var ns = await _fixture.Namespaces.CreateAsync(TestDb.Alice, "deeds", null, null, Ct);

        var managers = await _fixture.Db.NamespaceManagers
            .Where(m => m.NamespaceId == ns.NamespaceId)
            .Select(m => m.Manager)
            .ToListAsync(Ct);

        Assert.That(managers, Is.EqualTo(new[] { TestDb.Alice }));
    }

    [Test]
    public async Task CreateAsync_assigns_increasing_ids()
    {
        var first = await _fixture.Namespaces.CreateAsync(TestDb.Alice, "one", null, null, Ct);
        var second = await _fixture.Namespaces.CreateAsync(TestDb.Alice, "two", null, null, Ct);

        Assert.That(second.NamespaceId, Is.GreaterThan(first.NamespaceId));
    }

    [Test]
    public void CreateAsync_rejects_a_missing_name()
    {
        var ex = Assert.ThrowsAsync<BucketException>(() =>
            _fixture.Namespaces.CreateAsync(TestDb.Alice, "", null, null, Ct))!;

        Assert.That(ex.Code, Is.EqualTo(BucketErrorCode.InvalidInput));
    }

    [Test]
    public async Task AddManagerAsync_lets_a_manager_add_another()
    {
        var ns = await _fixture.Namespaces.CreateAsync(TestDb.Alice, "deeds", null, null, Ct);

        await _fixture.Namespaces.AddManagerAsync(TestDb.Alice, ns.NamespaceId, TestDb.Bob, Ct);

        Assert.That(await _fixture.Auth.IsManagerAsync(ns.NamespaceId, TestDb.Bob, Ct), Is.True);
    }

    [Test]
    public async Task AddManagerAsync_is_idempotent()
    {
        var ns = await _fixture.Namespaces.CreateAsync(TestDb.Alice, "deeds", null, null, Ct);

        await _fixture.Namespaces.AddManagerAsync(TestDb.Alice, ns.NamespaceId, TestDb.Bob, Ct);
        await _fixture.Namespaces.AddManagerAsync(TestDb.Alice, ns.NamespaceId, TestDb.Bob, Ct);

        var count = await _fixture.Db.NamespaceManagers
            .CountAsync(m => m.NamespaceId == ns.NamespaceId && m.Manager == TestDb.Bob, Ct);

        Assert.That(count, Is.EqualTo(1), "the pallet uses insert, which overwrites");
    }

    [Test]
    public async Task AddManagerAsync_rejects_a_non_manager()
    {
        var ns = await _fixture.Namespaces.CreateAsync(TestDb.Alice, "deeds", null, null, Ct);

        var ex = Assert.ThrowsAsync<BucketException>(() =>
            _fixture.Namespaces.AddManagerAsync(TestDb.Bob, ns.NamespaceId, TestDb.Carol, Ct))!;

        Assert.That(ex.Code, Is.EqualTo(BucketErrorCode.NotManager));
    }

    [Test]
    public void AddManagerAsync_rejects_a_missing_namespace()
    {
        var ex = Assert.ThrowsAsync<BucketException>(() =>
            _fixture.Namespaces.AddManagerAsync(TestDb.Alice, 999, TestDb.Bob, Ct))!;

        Assert.That(ex.Code, Is.EqualTo(BucketErrorCode.UnknownNamespace));
    }

    [Test]
    public async Task RemoveManagerAsync_refuses_to_remove_the_last_manager()
    {
        var ns = await _fixture.Namespaces.CreateAsync(TestDb.Alice, "deeds", null, null, Ct);

        var ex = Assert.ThrowsAsync<BucketException>(() =>
            _fixture.Namespaces.RemoveManagerAsync(TestDb.Alice, ns.NamespaceId, TestDb.Alice, Ct))!;

        Assert.That(ex.Code, Is.EqualTo(BucketErrorCode.LastManagerRemoval));
    }

    [Test]
    public async Task RemoveManagerAsync_removes_one_of_two_managers()
    {
        var ns = await _fixture.Namespaces.CreateAsync(TestDb.Alice, "deeds", null, null, Ct);
        await _fixture.Namespaces.AddManagerAsync(TestDb.Alice, ns.NamespaceId, TestDb.Bob, Ct);

        await _fixture.Namespaces.RemoveManagerAsync(TestDb.Alice, ns.NamespaceId, TestDb.Bob, Ct);

        Assert.Multiple(async () =>
        {
            Assert.That(await _fixture.Auth.IsManagerAsync(ns.NamespaceId, TestDb.Bob, Ct), Is.False);
            Assert.That(await _fixture.Auth.IsManagerAsync(ns.NamespaceId, TestDb.Alice, Ct), Is.True);
        });
    }

    [Test]
    public async Task RemoveManagerAsync_rejects_a_non_manager_caller()
    {
        var ns = await _fixture.Namespaces.CreateAsync(TestDb.Alice, "deeds", null, null, Ct);
        await _fixture.Namespaces.AddManagerAsync(TestDb.Alice, ns.NamespaceId, TestDb.Bob, Ct);

        var ex = Assert.ThrowsAsync<BucketException>(() =>
            _fixture.Namespaces.RemoveManagerAsync(TestDb.Carol, ns.NamespaceId, TestDb.Bob, Ct))!;

        Assert.That(ex.Code, Is.EqualTo(BucketErrorCode.NotManager));
    }

    [Test]
    public async Task ForceAddManagerAsync_skips_the_caller_check()
    {
        var ns = await _fixture.Namespaces.CreateAsync(TestDb.Alice, "deeds", null, null, Ct);

        await _fixture.Namespaces.ForceAddManagerAsync(ns.NamespaceId, TestDb.Dave, Ct);

        Assert.That(await _fixture.Auth.IsManagerAsync(ns.NamespaceId, TestDb.Dave, Ct), Is.True);
    }

    [Test]
    public void ForceAddManagerAsync_still_requires_the_namespace()
    {
        var ex = Assert.ThrowsAsync<BucketException>(() =>
            _fixture.Namespaces.ForceAddManagerAsync(999, TestDb.Dave, Ct))!;

        Assert.That(ex.Code, Is.EqualTo(BucketErrorCode.UnknownNamespace));
    }

    [Test]
    public async Task ForceRemoveAsync_rejects_a_namespace_that_still_has_buckets()
    {
        var ns = await _fixture.SeedNamespaceAsync();
        await _fixture.SeedBucketAsync(ns.NamespaceId);

        var ex = Assert.ThrowsAsync<BucketException>(() =>
            _fixture.Namespaces.ForceRemoveAsync(ns.NamespaceId, Ct))!;

        Assert.That(ex.Code, Is.EqualTo(BucketErrorCode.DanglingBuckets));
    }

    [Test]
    public async Task ForceRemoveAsync_rejects_a_namespace_that_still_has_managers()
    {
        var ns = await _fixture.SeedNamespaceAsync(TestDb.Alice);

        var ex = Assert.ThrowsAsync<BucketException>(() =>
            _fixture.Namespaces.ForceRemoveAsync(ns.NamespaceId, Ct))!;

        Assert.That(ex.Code, Is.EqualTo(BucketErrorCode.DanglingManagers));
    }

    [Test]
    public async Task ForceRemoveAsync_reports_dangling_buckets_before_dangling_managers()
    {
        var ns = await _fixture.SeedNamespaceAsync(TestDb.Alice);
        await _fixture.SeedBucketAsync(ns.NamespaceId);

        var ex = Assert.ThrowsAsync<BucketException>(() =>
            _fixture.Namespaces.ForceRemoveAsync(ns.NamespaceId, Ct))!;

        Assert.That(ex.Code, Is.EqualTo(BucketErrorCode.DanglingBuckets),
            "the pallet checks buckets first");
    }

    [Test]
    public async Task ForceRemoveAsync_deletes_a_clean_namespace()
    {
        var ns = await _fixture.SeedNamespaceAsync();

        await _fixture.Namespaces.ForceRemoveAsync(ns.NamespaceId, Ct);

        Assert.That(await _fixture.Db.Namespaces.AnyAsync(n => n.NamespaceId == ns.NamespaceId, Ct),
            Is.False);
    }

    [Test]
    public void ForceRemoveAsync_rejects_a_missing_namespace()
    {
        var ex = Assert.ThrowsAsync<BucketException>(() =>
            _fixture.Namespaces.ForceRemoveAsync(999, Ct))!;

        Assert.That(ex.Code, Is.EqualTo(BucketErrorCode.UnknownNamespace));
    }
}
