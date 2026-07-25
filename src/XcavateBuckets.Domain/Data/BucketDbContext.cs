using Microsoft.EntityFrameworkCore;
using XcavateBuckets.Domain.Entities;

namespace XcavateBuckets.Domain.Data;

/// <summary>
/// Persistence for the ported bucket pallet. Separate from ProfileDbContext so profile and bucket
/// migrations stay independent, though both target the same database.
/// </summary>
public class BucketDbContext(DbContextOptions<BucketDbContext> options) : DbContext(options)
{
    /// <summary>Migrations history table, keeping bucket migrations apart from profile ones.</summary>
    public const string MigrationsHistoryTable = "__EFMigrationsHistory_Buckets";

    public DbSet<Namespace> Namespaces => Set<Namespace>();

    public DbSet<NamespaceManager> NamespaceManagers => Set<NamespaceManager>();

    public DbSet<Bucket> Buckets => Set<Bucket>();

    public DbSet<BucketAdmin> BucketAdmins => Set<BucketAdmin>();

    public DbSet<BucketContributor> BucketContributors => Set<BucketContributor>();

    public DbSet<BucketViewer> BucketViewers => Set<BucketViewer>();

    public DbSet<Tag> Tags => Set<Tag>();

    public DbSet<TagMessageCount> TagMessageCounts => Set<TagMessageCount>();

    public DbSet<Message> Messages => Set<Message>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        // jsonb is Postgres-only; the test suite runs on SQLite, which has no such type.
        var propertiesColumnType = Database.IsNpgsql() ? "jsonb" : null;

        modelBuilder.Entity<Namespace>(entity =>
        {
            entity.ToTable("namespaces");
            entity.HasKey(e => e.NamespaceId);
            entity.Property(e => e.NamespaceId).ValueGeneratedOnAdd();
            if (propertiesColumnType is not null)
            {
                entity.Property(e => e.Properties).HasColumnType(propertiesColumnType);
            }
        });

        modelBuilder.Entity<NamespaceManager>(entity =>
        {
            entity.ToTable("namespace_managers");
            entity.HasKey(e => new { e.NamespaceId, e.Manager });
            entity.HasIndex(e => e.Manager);
            entity.HasOne(e => e.Namespace)
                .WithMany(n => n.Managers)
                .HasForeignKey(e => e.NamespaceId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<Bucket>(entity =>
        {
            entity.ToTable("buckets");
            entity.HasKey(e => e.BucketId);
            entity.Property(e => e.BucketId).ValueGeneratedOnAdd();
            entity.HasIndex(e => e.NamespaceId);
            if (propertiesColumnType is not null)
            {
                entity.Property(e => e.Properties).HasColumnType(propertiesColumnType);
            }

            entity.HasOne(e => e.Namespace)
                .WithMany(n => n.Buckets)
                .HasForeignKey(e => e.NamespaceId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<BucketAdmin>(entity =>
        {
            entity.ToTable("bucket_admins");
            entity.HasKey(e => new { e.BucketId, e.SubjectId });
            entity.HasIndex(e => e.SubjectId);
            entity.HasOne(e => e.Bucket)
                .WithMany(b => b.Admins)
                .HasForeignKey(e => e.BucketId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<BucketContributor>(entity =>
        {
            entity.ToTable("bucket_contributors");
            entity.HasKey(e => new { e.BucketId, e.SubjectId });
            entity.HasIndex(e => e.SubjectId);
            entity.HasOne(e => e.Bucket)
                .WithMany(b => b.Contributors)
                .HasForeignKey(e => e.BucketId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<BucketViewer>(entity =>
        {
            entity.ToTable("bucket_viewers");
            entity.HasKey(e => new { e.BucketId, e.ViewerId });
            entity.HasIndex(e => e.ViewerId);
            entity.HasOne(e => e.Bucket)
                .WithMany(b => b.Viewers)
                .HasForeignKey(e => e.BucketId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<Tag>(entity =>
        {
            entity.ToTable("tags");
            entity.HasKey(e => new { e.BucketId, e.TagName });
            entity.HasIndex(e => e.TagName);
            entity.HasOne(e => e.Bucket)
                .WithMany(b => b.Tags)
                .HasForeignKey(e => e.BucketId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<TagMessageCount>(entity =>
        {
            entity.ToTable("tag_message_counts");
            entity.HasKey(e => new { e.BucketId, e.TagName });
            entity.HasIndex(e => e.TagName);
            entity.HasOne(e => e.Bucket)
                .WithMany()
                .HasForeignKey(e => e.BucketId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<Message>(entity =>
        {
            entity.ToTable("messages");
            entity.HasKey(e => new { e.BucketId, e.MessageId });
            entity.HasIndex(e => e.MessageId);
            entity.HasIndex(e => e.Contributor);
            entity.HasIndex(e => e.Tag);
            if (propertiesColumnType is not null)
            {
                entity.Property(e => e.Properties).HasColumnType(propertiesColumnType);
            }

            // MessageId comes from Bucket.NextMessageId, never from the database.
            entity.Property(e => e.MessageId).ValueGeneratedNever();

            entity.HasOne(e => e.Bucket)
                .WithMany(b => b.Messages)
                .HasForeignKey(e => e.BucketId)
                .OnDelete(DeleteBehavior.Restrict);
        });
    }
}
