using XcavateBuckets.Domain;
using XcavateBuckets.Domain.Entities;

namespace XcavateBuckets.Tests;

[TestFixture]
public class MembershipServiceTests
{
    private static CancellationToken Ct => TestContext.CurrentContext.CancellationToken;

    private TestDb _fixture = null!;
    private Namespace _ns = null!;
    private Bucket _bucket = null!;

    [SetUp]
    public async Task SetUp()
    {
        _fixture = new TestDb();
        // Alice manages the namespace; Bob administers the bucket. Keeping them distinct is what
        // makes the manager/admin asymmetry testable.
        _ns = await _fixture.SeedNamespaceAsync(TestDb.Alice);
        _bucket = await _fixture.SeedBucketAsync(_ns.NamespaceId, admins: [TestDb.Bob]);
    }

    [TearDown]
    public void TearDown() => _fixture.Dispose();

    [Test]
    public async Task AddAdminAsync_is_performed_by_a_namespace_manager()
    {
        await _fixture.Memberships.AddAdminAsync(
            TestDb.Alice, _ns.NamespaceId, _bucket.BucketId, TestDb.Carol, Ct);

        Assert.That(await _fixture.Auth.IsAdminAsync(_bucket.BucketId, TestDb.Carol, Ct), Is.True);
    }

    [Test]
    public void AddAdminAsync_rejects_an_existing_bucket_admin()
    {
        var ex = Assert.ThrowsAsync<BucketException>(() => _fixture.Memberships.AddAdminAsync(
            TestDb.Bob, _ns.NamespaceId, _bucket.BucketId, TestDb.Carol, Ct))!;

        Assert.That(ex.Code, Is.EqualTo(BucketErrorCode.NotManager),
            "admins cannot appoint further admins; only namespace managers can");
    }

    [Test]
    public async Task RemoveAdminAsync_is_performed_by_a_namespace_manager()
    {
        await _fixture.Memberships.RemoveAdminAsync(
            TestDb.Alice, _ns.NamespaceId, _bucket.BucketId, TestDb.Bob, Ct);

        Assert.That(await _fixture.Auth.IsAdminAsync(_bucket.BucketId, TestDb.Bob, Ct), Is.False);
    }

    [Test]
    public async Task AddAdminAsync_on_a_namespaceless_bucket_is_performed_by_its_creator()
    {
        var bucket = await _fixture.SeedBucketAsync(null, creator: TestDb.Carol);

        await _fixture.Memberships.AddAdminAsync(
            TestDb.Carol, null, bucket.BucketId, TestDb.Dave, Ct);

        Assert.That(await _fixture.Auth.IsAdminAsync(bucket.BucketId, TestDb.Dave, Ct), Is.True);
    }

    [Test]
    public async Task AddAdminAsync_on_a_namespaceless_bucket_rejects_a_non_creator()
    {
        var bucket = await _fixture.SeedBucketAsync(null, creator: TestDb.Carol);

        var ex = Assert.ThrowsAsync<BucketException>(() => _fixture.Memberships.AddAdminAsync(
            TestDb.Dave, null, bucket.BucketId, TestDb.Dave, Ct))!;

        Assert.That(ex.Code, Is.EqualTo(BucketErrorCode.NotManager),
            "with no namespace to manage, only the bucket's creator may appoint admins");
    }

    [Test]
    public async Task RemoveAdminAsync_on_a_namespaceless_bucket_is_performed_by_its_creator()
    {
        var bucket = await _fixture.SeedBucketAsync(
            null, creator: TestDb.Carol, admins: [TestDb.Dave]);

        await _fixture.Memberships.RemoveAdminAsync(
            TestDb.Carol, null, bucket.BucketId, TestDb.Dave, Ct);

        Assert.That(await _fixture.Auth.IsAdminAsync(bucket.BucketId, TestDb.Dave, Ct), Is.False);
    }

    [Test]
    public async Task AddContributorAsync_is_performed_by_a_bucket_admin()
    {
        await _fixture.Memberships.AddContributorAsync(
            TestDb.Bob, _ns.NamespaceId, _bucket.BucketId, TestDb.Carol, Ct);

        Assert.That(await _fixture.Auth.IsContributorAsync(_bucket.BucketId, TestDb.Carol, Ct),
            Is.True);
    }

    [Test]
    public void AddContributorAsync_rejects_a_namespace_manager_who_is_not_a_bucket_admin()
    {
        var ex = Assert.ThrowsAsync<BucketException>(() =>
            _fixture.Memberships.AddContributorAsync(
                TestDb.Alice, _ns.NamespaceId, _bucket.BucketId, TestDb.Carol, Ct))!;

        Assert.That(ex.Code, Is.EqualTo(BucketErrorCode.NotAdmin));
    }

    [Test]
    public async Task RemoveContributorAsync_is_performed_by_a_bucket_admin()
    {
        await _fixture.Memberships.AddContributorAsync(
            TestDb.Bob, _ns.NamespaceId, _bucket.BucketId, TestDb.Carol, Ct);

        await _fixture.Memberships.RemoveContributorAsync(
            TestDb.Bob, _ns.NamespaceId, _bucket.BucketId, TestDb.Carol, Ct);

        Assert.That(await _fixture.Auth.IsContributorAsync(_bucket.BucketId, TestDb.Carol, Ct),
            Is.False);
    }

    [Test]
    public async Task AddViewerAsync_is_performed_by_a_bucket_admin()
    {
        await _fixture.Memberships.AddViewerAsync(
            TestDb.Bob, _ns.NamespaceId, _bucket.BucketId, TestDb.ViewerKey32, Ct);

        Assert.That(await _fixture.Auth.IsViewerAsync(_bucket.BucketId, TestDb.ViewerKey32, Ct),
            Is.True);
    }

    [Test]
    public void AddViewerAsync_rejects_a_viewer_key_that_is_not_32_bytes()
    {
        var ex = Assert.ThrowsAsync<BucketException>(() => _fixture.Memberships.AddViewerAsync(
            TestDb.Bob, _ns.NamespaceId, _bucket.BucketId, "not-a-key", Ct))!;

        Assert.That(ex.Code, Is.EqualTo(BucketErrorCode.InvalidInput));
    }

    [Test]
    public void AddViewerAsync_rejects_a_non_admin()
    {
        var ex = Assert.ThrowsAsync<BucketException>(() => _fixture.Memberships.AddViewerAsync(
            TestDb.Alice, _ns.NamespaceId, _bucket.BucketId, TestDb.ViewerKey32, Ct))!;

        Assert.That(ex.Code, Is.EqualTo(BucketErrorCode.NotAdmin));
    }

    [Test]
    public async Task RemoveViewerAsync_is_performed_by_a_bucket_admin()
    {
        await _fixture.Memberships.AddViewerAsync(
            TestDb.Bob, _ns.NamespaceId, _bucket.BucketId, TestDb.ViewerKey32, Ct);

        await _fixture.Memberships.RemoveViewerAsync(
            TestDb.Bob, _ns.NamespaceId, _bucket.BucketId, TestDb.ViewerKey32, Ct);

        Assert.That(await _fixture.Auth.IsViewerAsync(_bucket.BucketId, TestDb.ViewerKey32, Ct),
            Is.False);
    }

    [Test]
    public void AddAdminAsync_rejects_a_missing_bucket()
    {
        var ex = Assert.ThrowsAsync<BucketException>(() => _fixture.Memberships.AddAdminAsync(
            TestDb.Alice, _ns.NamespaceId, 999, TestDb.Carol, Ct))!;

        Assert.That(ex.Code, Is.EqualTo(BucketErrorCode.UnknownBucket));
    }

    [Test]
    public async Task AddContributorAsync_rejects_a_bucket_in_another_namespace()
    {
        var other = await _fixture.SeedNamespaceAsync(TestDb.Dave);

        var ex = Assert.ThrowsAsync<BucketException>(() =>
            _fixture.Memberships.AddContributorAsync(
                TestDb.Bob, other.NamespaceId, _bucket.BucketId, TestDb.Carol, Ct))!;

        Assert.That(ex.Code, Is.EqualTo(BucketErrorCode.UnknownBucket));
    }

    [Test]
    public void RemoveContributorAsync_of_an_absent_member_succeeds_silently()
    {
        Assert.DoesNotThrowAsync(() => _fixture.Memberships.RemoveContributorAsync(
            TestDb.Bob, _ns.NamespaceId, _bucket.BucketId, TestDb.Dave, Ct));
    }

    [Test]
    public async Task AddAdminAsync_is_idempotent()
    {
        await _fixture.Memberships.AddAdminAsync(
            TestDb.Alice, _ns.NamespaceId, _bucket.BucketId, TestDb.Carol, Ct);
        await _fixture.Memberships.AddAdminAsync(
            TestDb.Alice, _ns.NamespaceId, _bucket.BucketId, TestDb.Carol, Ct);

        var count = _fixture.Db.BucketAdmins
            .Count(a => a.BucketId == _bucket.BucketId && a.SubjectId == TestDb.Carol);

        Assert.That(count, Is.EqualTo(1));
    }
}
