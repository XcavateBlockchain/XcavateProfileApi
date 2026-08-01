using XcavateBuckets.Domain;
using XcavateBuckets.Domain.Services;

namespace XcavateBuckets.Tests;

/// <summary>
/// The write services raise notifier events exactly on the paths that change state: a persisted
/// message, and a membership row actually inserted. Idempotent re-adds and failed writes stay
/// silent, so no user is ever pushed about something that did not happen.
/// </summary>
[TestFixture]
public class BucketNotifierTests
{
    private static CancellationToken Ct => TestContext.CurrentContext.CancellationToken;

    private TestDb _fixture = null!;

    [SetUp]
    public void SetUp() => _fixture = new TestDb();

    [TearDown]
    public void TearDown() => _fixture.Dispose();

    private static MessageWriteRequest Request() =>
        new(
            Reference: "bafybeigdyrzt5example",
            Tag: null,
            IpfsContent: null,
            Description: null,
            ContentType: "text/plain",
            ContentHash: TestDb.Hash32,
            Properties: null);

    [Test]
    public async Task Writing_a_message_raises_one_message_event()
    {
        var bucket = await _fixture.SeedBucketAsync(
            null, isWritable: true, admins: [TestDb.Alice], contributors: [TestDb.Bob]);

        var message = await _fixture.Messages.WriteAsync(
            TestDb.Bob, null, bucket.BucketId, Request(), Ct);

        Assert.That(_fixture.Notifier.Messages, Is.EqualTo(new[]
        {
            new RecordingNotifier.MessageEvent(bucket.BucketId, message.MessageId, TestDb.Bob)
        }));
    }

    [Test]
    public async Task A_rejected_write_raises_nothing()
    {
        var bucket = await _fixture.SeedBucketAsync(
            null, isWritable: false, contributors: [TestDb.Bob]);

        Assert.ThrowsAsync<BucketException>(() => _fixture.Messages.WriteAsync(
            TestDb.Bob, null, bucket.BucketId, Request(), Ct));

        Assert.That(_fixture.Notifier.Messages, Is.Empty);
    }

    [Test]
    public async Task Adding_a_contributor_raises_a_member_event_only_on_the_first_add()
    {
        var bucket = await _fixture.SeedBucketAsync(null, admins: [TestDb.Alice]);

        await _fixture.Memberships.AddContributorAsync(
            TestDb.Alice, null, bucket.BucketId, TestDb.Bob, Ct);
        await _fixture.Memberships.AddContributorAsync(
            TestDb.Alice, null, bucket.BucketId, TestDb.Bob, Ct);

        Assert.That(_fixture.Notifier.Members, Is.EqualTo(new[]
        {
            new RecordingNotifier.MemberEvent(
                bucket.BucketId, TestDb.Bob, BucketMemberRole.Contributor)
        }));
    }

    [Test]
    public async Task Adding_an_admin_raises_a_member_event_only_on_the_first_add()
    {
        // Standalone bucket: the creator stands in for the namespace manager.
        var bucket = await _fixture.SeedBucketAsync(null, creator: TestDb.Alice);

        await _fixture.Memberships.AddAdminAsync(
            TestDb.Alice, null, bucket.BucketId, TestDb.Carol, Ct);
        await _fixture.Memberships.AddAdminAsync(
            TestDb.Alice, null, bucket.BucketId, TestDb.Carol, Ct);

        Assert.That(_fixture.Notifier.Members, Is.EqualTo(new[]
        {
            new RecordingNotifier.MemberEvent(bucket.BucketId, TestDb.Carol, BucketMemberRole.Admin)
        }));
    }
}
