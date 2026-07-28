using Microsoft.EntityFrameworkCore;
using XcavateBuckets.Domain;
using XcavateBuckets.Domain.Entities;
using XcavateBuckets.Domain.Services;

namespace XcavateBuckets.Tests;

[TestFixture]
public class MessageServiceTests
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
        _bucket = await _fixture.SeedBucketAsync(
            _ns.NamespaceId, isWritable: true, admins: [TestDb.Bob], contributors: [TestDb.Carol]);
    }

    [TearDown]
    public void TearDown() => _fixture.Dispose();

    private static MessageWriteRequest Request(string? tag = null, string? ipfsContent = null) =>
        new(
            Reference: "bafybeigdyrzt5example",
            Tag: tag,
            IpfsContent: ipfsContent,
            Description: "a deed",
            ContentType: "text/plain",
            ContentHash: TestDb.Hash32,
            Properties: null);

    [Test]
    public async Task WriteAsync_stores_the_message_with_id_zero()
    {
        var message = await _fixture.Messages.WriteAsync(
            TestDb.Carol, _ns.NamespaceId, _bucket.BucketId, Request(), Ct);

        Assert.Multiple(() =>
        {
            Assert.That(message.MessageId, Is.Zero);
            Assert.That(message.Contributor, Is.EqualTo(TestDb.Carol));
            Assert.That(message.Reference, Is.EqualTo("bafybeigdyrzt5example"));
            Assert.That(message.ContentHash, Is.EqualTo(TestDb.Hash32));
        });
    }

    [Test]
    public async Task WriteAsync_assigns_sequential_ids_within_a_bucket()
    {
        await _fixture.Messages.WriteAsync(
            TestDb.Carol, _ns.NamespaceId, _bucket.BucketId, Request(), Ct);
        var second = await _fixture.Messages.WriteAsync(
            TestDb.Carol, _ns.NamespaceId, _bucket.BucketId, Request(), Ct);

        Assert.That(second.MessageId, Is.EqualTo(1));
    }

    [Test]
    public async Task WriteAsync_restarts_ids_in_a_second_bucket()
    {
        await _fixture.Messages.WriteAsync(
            TestDb.Carol, _ns.NamespaceId, _bucket.BucketId, Request(), Ct);

        var other = await _fixture.SeedBucketAsync(
            _ns.NamespaceId, isWritable: true, contributors: [TestDb.Carol]);
        var message = await _fixture.Messages.WriteAsync(
            TestDb.Carol, _ns.NamespaceId, other.BucketId, Request(), Ct);

        Assert.That(message.MessageId, Is.Zero, "next_message_id lives on the bucket");
    }

    [Test]
    public async Task WriteAsync_accepts_a_missing_description()
    {
        var request = Request() with { Description = null };

        var message = await _fixture.Messages.WriteAsync(
            TestDb.Carol, _ns.NamespaceId, _bucket.BucketId, request, Ct);

        Assert.That(message.Description, Is.Null);
    }

    [Test]
    public void WriteAsync_rejects_an_over_long_description()
    {
        var request = Request() with
        {
            Description = new string('x', _fixture.Options.MaxNameLen + 1)
        };

        var ex = Assert.ThrowsAsync<BucketException>(() => _fixture.Messages.WriteAsync(
            TestDb.Carol, _ns.NamespaceId, _bucket.BucketId, request, Ct))!;

        Assert.That(ex.Code, Is.EqualTo(BucketErrorCode.InvalidInput));
    }

    [Test]
    public async Task WriteAsync_stores_supplied_ipfs_content_verbatim()
    {
        var message = await _fixture.Messages.WriteAsync(
            TestDb.Carol, _ns.NamespaceId, _bucket.BucketId, Request(ipfsContent: "hello"), Ct);

        Assert.That(message.IpfsContent, Is.EqualTo("hello"));
    }

    [Test]
    public async Task WriteAsync_rejects_a_locked_bucket()
    {
        var locked = await _fixture.SeedBucketAsync(
            _ns.NamespaceId, contributors: [TestDb.Carol]);

        var ex = Assert.ThrowsAsync<BucketException>(() => _fixture.Messages.WriteAsync(
            TestDb.Carol, _ns.NamespaceId, locked.BucketId, Request(), Ct))!;

        Assert.That(ex.Code, Is.EqualTo(BucketErrorCode.BucketIsLocked));
    }

    [Test]
    public void WriteAsync_rejects_a_non_contributor()
    {
        var ex = Assert.ThrowsAsync<BucketException>(() => _fixture.Messages.WriteAsync(
            TestDb.Bob, _ns.NamespaceId, _bucket.BucketId, Request(), Ct))!;

        Assert.That(ex.Code, Is.EqualTo(BucketErrorCode.NotContributor),
            "being a bucket admin does not grant write access");
    }

    [Test]
    public async Task WriteAsync_checks_writability_before_contributor_status()
    {
        var locked = await _fixture.SeedBucketAsync(_ns.NamespaceId);

        var ex = Assert.ThrowsAsync<BucketException>(() => _fixture.Messages.WriteAsync(
            TestDb.Dave, _ns.NamespaceId, locked.BucketId, Request(), Ct))!;

        Assert.That(ex.Code, Is.EqualTo(BucketErrorCode.BucketIsLocked),
            "do_create_message checks is_writable before ensure_is_contributor");
    }

    [Test]
    public void WriteAsync_rejects_an_unknown_tag()
    {
        var ex = Assert.ThrowsAsync<BucketException>(() => _fixture.Messages.WriteAsync(
            TestDb.Carol, _ns.NamespaceId, _bucket.BucketId, Request(tag: "nope"), Ct))!;

        Assert.That(ex.Code, Is.EqualTo(BucketErrorCode.UnknownTag));
    }

    [Test]
    public async Task WriteAsync_increments_the_tag_counter()
    {
        await _fixture.Tags.CreateAsync(TestDb.Bob, _bucket.BucketId, "legal", Ct);

        await _fixture.Messages.WriteAsync(
            TestDb.Carol, _ns.NamespaceId, _bucket.BucketId, Request(tag: "legal"), Ct);

        var counter = await _fixture.Db.TagMessageCounts
            .SingleAsync(c => c.BucketId == _bucket.BucketId && c.TagName == "legal", Ct);

        Assert.That(counter.Count, Is.EqualTo(1));
    }

    [Test]
    public void WriteAsync_rejects_a_content_hash_that_is_not_32_bytes()
    {
        var request = Request() with { ContentHash = "0xdeadbeef" };

        var ex = Assert.ThrowsAsync<BucketException>(() => _fixture.Messages.WriteAsync(
            TestDb.Carol, _ns.NamespaceId, _bucket.BucketId, request, Ct))!;

        Assert.That(ex.Code, Is.EqualTo(BucketErrorCode.InvalidInput));
    }

    [Test]
    public void WriteAsync_rejects_over_long_ipfs_content()
    {
        var request = Request(ipfsContent: new string('x', 1_048_577));

        var ex = Assert.ThrowsAsync<BucketException>(() => _fixture.Messages.WriteAsync(
            TestDb.Carol, _ns.NamespaceId, _bucket.BucketId, request, Ct))!;

        Assert.That(ex.Code, Is.EqualTo(BucketErrorCode.InvalidInput));
    }

    [Test]
    public async Task ForceRemoveAsync_decrements_the_tag_counter()
    {
        await _fixture.Tags.CreateAsync(TestDb.Bob, _bucket.BucketId, "legal", Ct);
        var message = await _fixture.Messages.WriteAsync(
            TestDb.Carol, _ns.NamespaceId, _bucket.BucketId, Request(tag: "legal"), Ct);

        await _fixture.Messages.ForceRemoveAsync(_bucket.BucketId, message.MessageId, Ct);

        var counter = await _fixture.Db.TagMessageCounts
            .SingleAsync(c => c.BucketId == _bucket.BucketId && c.TagName == "legal", Ct);

        Assert.That(counter.Count, Is.Zero);
    }

    [Test]
    public void ForceRemoveAsync_rejects_an_absent_message()
    {
        var ex = Assert.ThrowsAsync<BucketException>(() =>
            _fixture.Messages.ForceRemoveAsync(_bucket.BucketId, 42, Ct))!;

        Assert.That(ex.Code, Is.EqualTo(BucketErrorCode.UnknownMessage));
    }

    [Test]
    public async Task Message_ids_are_never_reused_after_removal()
    {
        var first = await _fixture.Messages.WriteAsync(
            TestDb.Carol, _ns.NamespaceId, _bucket.BucketId, Request(), Ct);
        await _fixture.Messages.ForceRemoveAsync(_bucket.BucketId, first.MessageId, Ct);

        var second = await _fixture.Messages.WriteAsync(
            TestDb.Carol, _ns.NamespaceId, _bucket.BucketId, Request(), Ct);

        Assert.That(second.MessageId, Is.EqualTo(1),
            "the pallet never rewinds next_message_id");
    }
}
