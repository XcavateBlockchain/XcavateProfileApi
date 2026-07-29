using Microsoft.EntityFrameworkCore;
using XcavateBuckets.Domain;
using XcavateBuckets.Domain.Entities;

namespace XcavateBuckets.Tests;

[TestFixture]
public class BucketServiceTests
{
    private static CancellationToken Ct => TestContext.CurrentContext.CancellationToken;

    private TestDb _fixture = null!;

    [SetUp]
    public void SetUp() => _fixture = new TestDb();

    [TearDown]
    public void TearDown() => _fixture.Dispose();

    [Test]
    public async Task CreateAsync_starts_the_bucket_locked_with_no_key()
    {
        var ns = await _fixture.SeedNamespaceAsync(TestDb.Alice);

        var bucket = await _fixture.Buckets.CreateAsync(
            TestDb.Alice, ns.NamespaceId, "deeds", "legal", null, Ct);

        Assert.Multiple(() =>
        {
            Assert.That(bucket.IsWritable, Is.False, "Status::default() is Locked");
            Assert.That(bucket.EncryptionKey, Is.Null);
            Assert.That(bucket.NextMessageId, Is.Zero);
            Assert.That(bucket.Creator, Is.EqualTo(TestDb.Alice));
            Assert.That(bucket.Name, Is.EqualTo("deeds"));
            Assert.That(bucket.Category, Is.EqualTo("legal"));
        });
    }

    [Test]
    public async Task CreateAsync_rejects_a_caller_who_is_not_a_namespace_manager()
    {
        var ns = await _fixture.SeedNamespaceAsync(TestDb.Alice);

        var ex = Assert.ThrowsAsync<BucketException>(() => _fixture.Buckets.CreateAsync(
            TestDb.Bob, ns.NamespaceId, "deeds", "legal", null, Ct))!;

        Assert.That(ex.Code, Is.EqualTo(BucketErrorCode.NotManager));
    }

    [Test]
    public void CreateAsync_rejects_a_missing_namespace()
    {
        var ex = Assert.ThrowsAsync<BucketException>(() => _fixture.Buckets.CreateAsync(
            TestDb.Alice, 999, "deeds", "legal", null, Ct))!;

        Assert.That(ex.Code, Is.EqualTo(BucketErrorCode.UnknownNamespace));
    }

    [Test]
    public async Task CreateAsync_without_a_namespace_allows_any_signed_caller()
    {
        var bucket = await _fixture.Buckets.CreateAsync(
            TestDb.Bob, null, "deeds", "legal", null, Ct);

        Assert.Multiple(() =>
        {
            Assert.That(bucket.NamespaceId, Is.Null);
            Assert.That(bucket.Creator, Is.EqualTo(TestDb.Bob));
            Assert.That(bucket.IsWritable, Is.False, "Status::default() is Locked");
            Assert.That(bucket.EncryptionKey, Is.Null);
        });
    }

    [Test]
    public async Task Namespaceless_bucket_is_addressed_with_a_null_namespace_id()
    {
        var bucket = await _fixture.SeedBucketAsync(
            null, isWritable: true, admins: [TestDb.Bob]);

        var paused = await _fixture.Buckets.PauseWritingAsync(
            TestDb.Bob, null, bucket.BucketId, Ct);

        Assert.That(paused.IsWritable, Is.False);
    }

    [Test]
    public async Task Namespaceless_bucket_reads_as_unknown_under_a_namespace_id()
    {
        var ns = await _fixture.SeedNamespaceAsync(TestDb.Alice);
        var bucket = await _fixture.SeedBucketAsync(
            null, isWritable: true, admins: [TestDb.Bob]);

        var ex = Assert.ThrowsAsync<BucketException>(() => _fixture.Buckets.PauseWritingAsync(
            TestDb.Bob, ns.NamespaceId, bucket.BucketId, Ct))!;

        Assert.That(ex.Code, Is.EqualTo(BucketErrorCode.UnknownBucket));
    }

    [Test]
    public async Task Namespaced_bucket_reads_as_unknown_under_a_null_namespace_id()
    {
        var ns = await _fixture.SeedNamespaceAsync(TestDb.Alice);
        var bucket = await _fixture.SeedBucketAsync(
            ns.NamespaceId, isWritable: true, admins: [TestDb.Bob]);

        var ex = Assert.ThrowsAsync<BucketException>(() => _fixture.Buckets.PauseWritingAsync(
            TestDb.Bob, null, bucket.BucketId, Ct))!;

        Assert.That(ex.Code, Is.EqualTo(BucketErrorCode.UnknownBucket));
    }

    [Test]
    public async Task PauseWritingAsync_locks_a_writable_bucket()
    {
        var ns = await _fixture.SeedNamespaceAsync(TestDb.Alice);
        var bucket = await _fixture.SeedBucketAsync(
            ns.NamespaceId, isWritable: true, admins: [TestDb.Bob]);

        var paused = await _fixture.Buckets.PauseWritingAsync(
            TestDb.Bob, ns.NamespaceId, bucket.BucketId, Ct);

        Assert.That(paused.IsWritable, Is.False);
    }

    [Test]
    public async Task PauseWritingAsync_rejects_a_non_admin()
    {
        var ns = await _fixture.SeedNamespaceAsync(TestDb.Alice);
        var bucket = await _fixture.SeedBucketAsync(ns.NamespaceId, isWritable: true);

        var ex = Assert.ThrowsAsync<BucketException>(() => _fixture.Buckets.PauseWritingAsync(
            TestDb.Alice, ns.NamespaceId, bucket.BucketId, Ct))!;

        Assert.That(ex.Code, Is.EqualTo(BucketErrorCode.NotAdmin),
            "a namespace manager is not automatically a bucket admin");
    }

    [Test]
    public async Task ResumeWritingAsync_unlocks_a_locked_bucket_and_sets_the_key()
    {
        var ns = await _fixture.SeedNamespaceAsync(TestDb.Alice);
        var bucket = await _fixture.SeedBucketAsync(ns.NamespaceId, admins: [TestDb.Bob]);

        var resumed = await _fixture.Buckets.ResumeWritingAsync(
            TestDb.Bob, ns.NamespaceId, bucket.BucketId, TestDb.OtherKey32, Ct);

        Assert.Multiple(() =>
        {
            Assert.That(resumed.IsWritable, Is.True);
            Assert.That(resumed.EncryptionKey, Is.EqualTo(TestDb.OtherKey32));
        });
    }

    [Test]
    public async Task RotateKeyAsync_rejects_a_locked_bucket()
    {
        var ns = await _fixture.SeedNamespaceAsync(TestDb.Alice);
        var bucket = await _fixture.SeedBucketAsync(ns.NamespaceId, admins: [TestDb.Bob]);

        var ex = Assert.ThrowsAsync<BucketException>(() => _fixture.Buckets.RotateKeyAsync(
            TestDb.Bob, ns.NamespaceId, bucket.BucketId, TestDb.OtherKey32, Ct))!;

        Assert.That(ex.Code, Is.EqualTo(BucketErrorCode.BucketIsLocked),
            "rotate_key is do_set_key(allow_locked: false)");
    }

    [Test]
    public async Task RotateKeyAsync_replaces_the_key_on_a_writable_bucket()
    {
        var ns = await _fixture.SeedNamespaceAsync(TestDb.Alice);
        var bucket = await _fixture.SeedBucketAsync(
            ns.NamespaceId, isWritable: true, admins: [TestDb.Bob]);

        var rotated = await _fixture.Buckets.RotateKeyAsync(
            TestDb.Bob, ns.NamespaceId, bucket.BucketId, TestDb.OtherKey32, Ct);

        Assert.Multiple(() =>
        {
            Assert.That(rotated.IsWritable, Is.True);
            Assert.That(rotated.EncryptionKey, Is.EqualTo(TestDb.OtherKey32));
        });
    }

    [Test]
    public async Task RotateKeyAsync_rejects_a_key_that_is_not_32_bytes()
    {
        var ns = await _fixture.SeedNamespaceAsync(TestDb.Alice);
        var bucket = await _fixture.SeedBucketAsync(
            ns.NamespaceId, isWritable: true, admins: [TestDb.Bob]);

        var ex = Assert.ThrowsAsync<BucketException>(() => _fixture.Buckets.RotateKeyAsync(
            TestDb.Bob, ns.NamespaceId, bucket.BucketId, "0xdeadbeef", Ct))!;

        Assert.That(ex.Code, Is.EqualTo(BucketErrorCode.InvalidInput));
    }

    [Test]
    public async Task Bucket_from_another_namespace_reads_as_unknown()
    {
        var first = await _fixture.SeedNamespaceAsync(TestDb.Alice);
        var second = await _fixture.SeedNamespaceAsync(TestDb.Bob);
        var bucket = await _fixture.SeedBucketAsync(second.NamespaceId, admins: [TestDb.Bob]);

        var ex = Assert.ThrowsAsync<BucketException>(() => _fixture.Buckets.PauseWritingAsync(
            TestDb.Bob, first.NamespaceId, bucket.BucketId, Ct))!;

        Assert.That(ex.Code, Is.EqualTo(BucketErrorCode.UnknownBucket));
    }

    [Test]
    public async Task ForceRemoveAsync_reports_dangling_messages_first()
    {
        var ns = await _fixture.SeedNamespaceAsync(TestDb.Alice);
        var bucket = await _fixture.SeedBucketAsync(
            ns.NamespaceId, isWritable: true, admins: [TestDb.Bob], contributors: [TestDb.Carol]);
        _fixture.Db.Messages.Add(new Message
        {
            BucketId = bucket.BucketId,
            MessageId = 0,
            Contributor = TestDb.Carol,
            CreatedAt = _fixture.Clock.GetUtcNow().UtcDateTime
        });
        await _fixture.Db.SaveChangesAsync(Ct);

        var ex = Assert.ThrowsAsync<BucketException>(() =>
            _fixture.Buckets.ForceRemoveAsync(ns.NamespaceId, bucket.BucketId, Ct))!;

        Assert.That(ex.Code, Is.EqualTo(BucketErrorCode.DanglingMessages));
    }

    [Test]
    public async Task ForceRemoveAsync_reports_dangling_admins()
    {
        var ns = await _fixture.SeedNamespaceAsync(TestDb.Alice);
        var bucket = await _fixture.SeedBucketAsync(ns.NamespaceId, admins: [TestDb.Bob]);

        var ex = Assert.ThrowsAsync<BucketException>(() =>
            _fixture.Buckets.ForceRemoveAsync(ns.NamespaceId, bucket.BucketId, Ct))!;

        Assert.That(ex.Code, Is.EqualTo(BucketErrorCode.DanglingAdmins));
    }

    [Test]
    public async Task ForceRemoveAsync_reports_dangling_contributors()
    {
        var ns = await _fixture.SeedNamespaceAsync(TestDb.Alice);
        var bucket = await _fixture.SeedBucketAsync(ns.NamespaceId, contributors: [TestDb.Carol]);

        var ex = Assert.ThrowsAsync<BucketException>(() =>
            _fixture.Buckets.ForceRemoveAsync(ns.NamespaceId, bucket.BucketId, Ct))!;

        Assert.That(ex.Code, Is.EqualTo(BucketErrorCode.DanglingContributors));
    }

    [Test]
    public async Task ForceRemoveAsync_reports_dangling_viewers()
    {
        var ns = await _fixture.SeedNamespaceAsync(TestDb.Alice);
        var bucket = await _fixture.SeedBucketAsync(ns.NamespaceId);
        _fixture.Db.BucketViewers.Add(new BucketViewer
        {
            BucketId = bucket.BucketId,
            ViewerId = TestDb.ViewerKey32,
            AddedAt = _fixture.Clock.GetUtcNow().UtcDateTime
        });
        await _fixture.Db.SaveChangesAsync(Ct);

        var ex = Assert.ThrowsAsync<BucketException>(() =>
            _fixture.Buckets.ForceRemoveAsync(ns.NamespaceId, bucket.BucketId, Ct))!;

        Assert.That(ex.Code, Is.EqualTo(BucketErrorCode.DanglingViewers));
    }

    [Test]
    public async Task ForceRemoveAsync_reports_dangling_tags()
    {
        var ns = await _fixture.SeedNamespaceAsync(TestDb.Alice);
        var bucket = await _fixture.SeedBucketAsync(ns.NamespaceId);
        _fixture.Db.Tags.Add(new Tag
        {
            BucketId = bucket.BucketId,
            TagName = "legal",
            CreatedAt = _fixture.Clock.GetUtcNow().UtcDateTime
        });
        await _fixture.Db.SaveChangesAsync(Ct);

        var ex = Assert.ThrowsAsync<BucketException>(() =>
            _fixture.Buckets.ForceRemoveAsync(ns.NamespaceId, bucket.BucketId, Ct))!;

        Assert.That(ex.Code, Is.EqualTo(BucketErrorCode.DanglingTags));
    }

    [Test]
    public async Task ForceRemoveAsync_deletes_a_clean_bucket()
    {
        var ns = await _fixture.SeedNamespaceAsync(TestDb.Alice);
        var bucket = await _fixture.SeedBucketAsync(ns.NamespaceId);

        await _fixture.Buckets.ForceRemoveAsync(ns.NamespaceId, bucket.BucketId, Ct);

        Assert.That(await _fixture.Db.Buckets.AnyAsync(b => b.BucketId == bucket.BucketId, Ct),
            Is.False);
    }

    [Test]
    public async Task ForceRemoveAsync_rejects_a_missing_bucket()
    {
        var ns = await _fixture.SeedNamespaceAsync(TestDb.Alice);

        var ex = Assert.ThrowsAsync<BucketException>(() =>
            _fixture.Buckets.ForceRemoveAsync(ns.NamespaceId, 999, Ct))!;

        Assert.That(ex.Code, Is.EqualTo(BucketErrorCode.UnknownBucket));
    }
}
