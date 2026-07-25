using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using XcavateBuckets.Domain.Data;
using XcavateBuckets.Domain.Entities;

namespace XcavateBuckets.Tests;

[TestFixture]
public class SchemaTests
{
    private SqliteConnection _connection = null!;
    private BucketDbContext _db = null!;

    [SetUp]
    public void SetUp()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        _db = new BucketDbContext(new DbContextOptionsBuilder<BucketDbContext>()
            .UseSqlite(_connection)
            .Options);
    }

    [TearDown]
    public void TearDown()
    {
        _db.Dispose();
        _connection.Dispose();
    }

    [Test]
    public void Namespace_manager_is_keyed_by_namespace_and_manager()
    {
        var key = _db.Model.FindEntityType(typeof(NamespaceManager))!.FindPrimaryKey()!;

        Assert.That(key.Properties.Select(p => p.Name),
            Is.EquivalentTo(new[] { "NamespaceId", "Manager" }));
    }

    [Test]
    public void Message_is_keyed_by_bucket_and_message_id()
    {
        var key = _db.Model.FindEntityType(typeof(Message))!.FindPrimaryKey()!;

        Assert.That(key.Properties.Select(p => p.Name),
            Is.EquivalentTo(new[] { "BucketId", "MessageId" }));
    }

    [Test]
    public void Bucket_viewer_is_keyed_by_bucket_and_viewer_id()
    {
        var key = _db.Model.FindEntityType(typeof(BucketViewer))!.FindPrimaryKey()!;

        Assert.That(key.Properties.Select(p => p.Name),
            Is.EquivalentTo(new[] { "BucketId", "ViewerId" }));
    }

    [Test]
    public void Tag_message_count_is_keyed_by_bucket_and_tag_name()
    {
        var key = _db.Model.FindEntityType(typeof(TagMessageCount))!.FindPrimaryKey()!;

        Assert.That(key.Properties.Select(p => p.Name),
            Is.EquivalentTo(new[] { "BucketId", "TagName" }));
    }

    [Test]
    public void All_foreign_keys_restrict_deletes()
    {
        var cascading = _db.Model.GetEntityTypes()
            .SelectMany(e => e.GetForeignKeys())
            .Where(fk => fk.DeleteBehavior != DeleteBehavior.Restrict)
            .Select(fk => $"{fk.DeclaringEntityType.ClrType.Name} -> " +
                          $"{fk.PrincipalEntityType.ClrType.Name} ({fk.DeleteBehavior})")
            .ToList();

        Assert.That(cascading, Is.Empty,
            "pallet parity: dangling-resource rules are explicit checks, never database cascades");
    }

    [Test]
    public void Namespace_and_bucket_ids_are_database_generated()
    {
        var namespaceId = _db.Model.FindEntityType(typeof(Namespace))!
            .FindProperty(nameof(Namespace.NamespaceId))!;
        var bucketId = _db.Model.FindEntityType(typeof(Bucket))!
            .FindProperty(nameof(Bucket.BucketId))!;

        Assert.Multiple(() =>
        {
            Assert.That(namespaceId.ValueGenerated, Is.EqualTo(ValueGenerated.OnAdd));
            Assert.That(bucketId.ValueGenerated, Is.EqualTo(ValueGenerated.OnAdd));
        });
    }

    [Test]
    public void Message_id_is_never_database_generated()
    {
        var messageId = _db.Model.FindEntityType(typeof(Message))!
            .FindProperty(nameof(Message.MessageId))!;

        Assert.That(messageId.ValueGenerated, Is.EqualTo(ValueGenerated.Never),
            "message ids come from Bucket.NextMessageId so they restart per bucket");
    }

    [Test]
    public void Schema_can_be_created()
    {
        Assert.That(_db.Database.EnsureCreated(), Is.True);
    }
}
