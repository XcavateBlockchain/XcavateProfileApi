using Microsoft.EntityFrameworkCore;
using XcavateBuckets.Domain;
using XcavateBuckets.Domain.Entities;

namespace XcavateBuckets.Tests;

[TestFixture]
public class TagServiceTests
{
    private static CancellationToken Ct => TestContext.CurrentContext.CancellationToken;

    private TestDb _fixture = null!;
    private Namespace _ns = null!;
    private Bucket _bucket = null!;

    [SetUp]
    public async Task SetUp()
    {
        _fixture = new TestDb();
        _ns = await _fixture.SeedNamespaceAsync(TestDb.Alice);
        _bucket = await _fixture.SeedBucketAsync(_ns.NamespaceId, admins: [TestDb.Bob]);
    }

    [TearDown]
    public void TearDown() => _fixture.Dispose();

    [Test]
    public async Task CreateAsync_stores_the_tag_with_its_creator()
    {
        var tag = await _fixture.Tags.CreateAsync(TestDb.Bob, _bucket.BucketId, "legal", Ct);

        Assert.Multiple(() =>
        {
            Assert.That(tag.TagName, Is.EqualTo("legal"));
            Assert.That(tag.Creator, Is.EqualTo(TestDb.Bob));
        });
    }

    [Test]
    public async Task CreateAsync_also_creates_a_zero_message_counter()
    {
        await _fixture.Tags.CreateAsync(TestDb.Bob, _bucket.BucketId, "legal", Ct);

        var counter = await _fixture.Db.TagMessageCounts
            .SingleAsync(c => c.BucketId == _bucket.BucketId && c.TagName == "legal", Ct);

        Assert.That(counter.Count, Is.Zero);
    }

    [Test]
    public void CreateAsync_rejects_a_non_admin()
    {
        var ex = Assert.ThrowsAsync<BucketException>(() =>
            _fixture.Tags.CreateAsync(TestDb.Alice, _bucket.BucketId, "legal", Ct))!;

        Assert.That(ex.Code, Is.EqualTo(BucketErrorCode.NotAdmin));
    }

    [Test]
    public void CreateAsync_rejects_an_over_long_tag()
    {
        var ex = Assert.ThrowsAsync<BucketException>(() =>
            _fixture.Tags.CreateAsync(TestDb.Bob, _bucket.BucketId, new string('t', 65), Ct))!;

        Assert.That(ex.Code, Is.EqualTo(BucketErrorCode.InvalidInput));
    }

    [Test]
    public async Task ForceRemoveAsync_deletes_an_unused_tag_and_its_counter()
    {
        await _fixture.Tags.CreateAsync(TestDb.Bob, _bucket.BucketId, "legal", Ct);

        await _fixture.Tags.ForceRemoveAsync(_bucket.BucketId, "legal", Ct);

        Assert.Multiple(async () =>
        {
            Assert.That(await _fixture.Db.Tags.AnyAsync(t => t.BucketId == _bucket.BucketId, Ct),
                Is.False);
            Assert.That(
                await _fixture.Db.TagMessageCounts.AnyAsync(c => c.BucketId == _bucket.BucketId, Ct),
                Is.False);
        });
    }

    [Test]
    public async Task ForceRemoveAsync_refuses_a_tag_that_messages_still_reference()
    {
        await _fixture.Tags.CreateAsync(TestDb.Bob, _bucket.BucketId, "legal", Ct);
        var counter = await _fixture.Db.TagMessageCounts
            .SingleAsync(c => c.BucketId == _bucket.BucketId && c.TagName == "legal", Ct);
        counter.Count = 1;
        await _fixture.Db.SaveChangesAsync(Ct);

        var ex = Assert.ThrowsAsync<BucketException>(() =>
            _fixture.Tags.ForceRemoveAsync(_bucket.BucketId, "legal", Ct))!;

        Assert.That(ex.Code, Is.EqualTo(BucketErrorCode.DanglingMessages));
    }

    [Test]
    public void ForceRemoveAsync_rejects_an_absent_tag()
    {
        var ex = Assert.ThrowsAsync<BucketException>(() =>
            _fixture.Tags.ForceRemoveAsync(_bucket.BucketId, "nope", Ct))!;

        Assert.That(ex.Code, Is.EqualTo(BucketErrorCode.UnknownTag));
    }
}
