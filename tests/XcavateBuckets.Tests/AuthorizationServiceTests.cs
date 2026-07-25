using XcavateBuckets.Domain;

namespace XcavateBuckets.Tests;

[TestFixture]
public class AuthorizationServiceTests
{
    private TestDb _fixture = null!;

    [SetUp]
    public void SetUp() => _fixture = new TestDb();

    [TearDown]
    public void TearDown() => _fixture.Dispose();

    [Test]
    public async Task IsManagerAsync_is_true_for_a_manager()
    {
        var ns = await _fixture.SeedNamespaceAsync(TestDb.Alice);

        Assert.That(await _fixture.Auth.IsManagerAsync(ns.NamespaceId, TestDb.Alice,
            TestContext.CurrentContext.CancellationToken), Is.True);
    }

    [Test]
    public async Task IsManagerAsync_is_false_for_a_stranger()
    {
        var ns = await _fixture.SeedNamespaceAsync(TestDb.Alice);

        Assert.That(await _fixture.Auth.IsManagerAsync(ns.NamespaceId, TestDb.Bob,
            TestContext.CurrentContext.CancellationToken), Is.False);
    }

    [Test]
    public async Task IsAdminAsync_is_true_for_a_bucket_admin()
    {
        var ns = await _fixture.SeedNamespaceAsync(TestDb.Alice);
        var bucket = await _fixture.SeedBucketAsync(ns.NamespaceId, admins: [TestDb.Bob]);

        Assert.That(await _fixture.Auth.IsAdminAsync(bucket.BucketId, TestDb.Bob,
            TestContext.CurrentContext.CancellationToken), Is.True);
    }

    [Test]
    public async Task IsAdminAsync_is_false_for_a_namespace_manager_who_is_not_a_bucket_admin()
    {
        var ns = await _fixture.SeedNamespaceAsync(TestDb.Alice);
        var bucket = await _fixture.SeedBucketAsync(ns.NamespaceId);

        Assert.That(await _fixture.Auth.IsAdminAsync(bucket.BucketId, TestDb.Alice,
            TestContext.CurrentContext.CancellationToken), Is.False,
            "manager and admin are distinct roles in the pallet");
    }

    [Test]
    public async Task IsContributorAsync_is_true_for_a_contributor()
    {
        var ns = await _fixture.SeedNamespaceAsync(TestDb.Alice);
        var bucket = await _fixture.SeedBucketAsync(ns.NamespaceId, contributors: [TestDb.Carol]);

        Assert.That(await _fixture.Auth.IsContributorAsync(bucket.BucketId, TestDb.Carol,
            TestContext.CurrentContext.CancellationToken), Is.True);
    }

    [Test]
    public async Task EnsureIsManagerAsync_throws_not_manager_for_a_stranger()
    {
        var ns = await _fixture.SeedNamespaceAsync(TestDb.Alice);

        var ex = Assert.ThrowsAsync<BucketException>(() => _fixture.Auth.EnsureIsManagerAsync(
            ns.NamespaceId, TestDb.Bob, TestContext.CurrentContext.CancellationToken))!;

        Assert.That(ex.Code, Is.EqualTo(BucketErrorCode.NotManager));
    }

    [Test]
    public async Task EnsureIsManagerAsync_passes_for_a_manager()
    {
        var ns = await _fixture.SeedNamespaceAsync(TestDb.Alice);

        Assert.DoesNotThrowAsync(() => _fixture.Auth.EnsureIsManagerAsync(
            ns.NamespaceId, TestDb.Alice, TestContext.CurrentContext.CancellationToken));
    }

    [Test]
    public async Task EnsureIsAdminAsync_throws_not_admin_for_a_stranger()
    {
        var ns = await _fixture.SeedNamespaceAsync(TestDb.Alice);
        var bucket = await _fixture.SeedBucketAsync(ns.NamespaceId, admins: [TestDb.Bob]);

        var ex = Assert.ThrowsAsync<BucketException>(() => _fixture.Auth.EnsureIsAdminAsync(
            bucket.BucketId, TestDb.Carol, TestContext.CurrentContext.CancellationToken))!;

        Assert.That(ex.Code, Is.EqualTo(BucketErrorCode.NotAdmin));
    }

    [Test]
    public async Task EnsureIsContributorAsync_throws_not_contributor_for_a_stranger()
    {
        var ns = await _fixture.SeedNamespaceAsync(TestDb.Alice);
        var bucket = await _fixture.SeedBucketAsync(ns.NamespaceId);

        var ex = Assert.ThrowsAsync<BucketException>(() => _fixture.Auth.EnsureIsContributorAsync(
            bucket.BucketId, TestDb.Carol, TestContext.CurrentContext.CancellationToken))!;

        Assert.That(ex.Code, Is.EqualTo(BucketErrorCode.NotContributor));
    }

    [Test]
    public void EnsureNamespaceExistsAsync_throws_unknown_namespace_for_a_missing_id()
    {
        var ex = Assert.ThrowsAsync<BucketException>(() =>
            _fixture.Auth.EnsureNamespaceExistsAsync(
                999, TestContext.CurrentContext.CancellationToken))!;

        Assert.That(ex.Code, Is.EqualTo(BucketErrorCode.UnknownNamespace));
    }

    [Test]
    public async Task GetBucketAsync_returns_the_bucket_when_the_namespace_matches()
    {
        var ns = await _fixture.SeedNamespaceAsync(TestDb.Alice);
        var bucket = await _fixture.SeedBucketAsync(ns.NamespaceId);

        var found = await _fixture.Auth.GetBucketAsync(ns.NamespaceId, bucket.BucketId,
            TestContext.CurrentContext.CancellationToken);

        Assert.That(found.BucketId, Is.EqualTo(bucket.BucketId));
    }

    [Test]
    public async Task GetBucketAsync_throws_unknown_bucket_for_a_missing_bucket()
    {
        var ns = await _fixture.SeedNamespaceAsync(TestDb.Alice);

        var ex = Assert.ThrowsAsync<BucketException>(() => _fixture.Auth.GetBucketAsync(
            ns.NamespaceId, 999, TestContext.CurrentContext.CancellationToken))!;

        Assert.That(ex.Code, Is.EqualTo(BucketErrorCode.UnknownBucket));
    }

    [Test]
    public async Task GetBucketAsync_throws_unknown_bucket_when_the_bucket_is_in_another_namespace()
    {
        var first = await _fixture.SeedNamespaceAsync(TestDb.Alice);
        var second = await _fixture.SeedNamespaceAsync(TestDb.Bob);
        var bucket = await _fixture.SeedBucketAsync(second.NamespaceId);

        var ex = Assert.ThrowsAsync<BucketException>(() => _fixture.Auth.GetBucketAsync(
            first.NamespaceId, bucket.BucketId, TestContext.CurrentContext.CancellationToken))!;

        Assert.That(ex.Code, Is.EqualTo(BucketErrorCode.UnknownBucket),
            "the pallet keys Buckets by (namespace_id, bucket_id), so a cross-namespace "
            + "lookup must miss");
    }
}
