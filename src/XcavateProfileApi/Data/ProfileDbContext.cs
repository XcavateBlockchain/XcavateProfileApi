using Microsoft.EntityFrameworkCore;
using XcavateProfile.Client;

namespace XcavateProfileApi.Data;

[Serializable]
public class ProfileDbContext : DbContext
{
    public ProfileDbContext(DbContextOptions<ProfileDbContext> options)
        : base(options)
    {
    }

    public DbSet<Profile> Profiles { get; set; } = default!;

    public DbSet<Company> Companies { get; set; } = default!;

    public DbSet<WalletMigration> WalletMigrations { get; set; } = default!;

    public override int SaveChanges(bool acceptAllChangesOnSuccess)
    {
        SyncNicknameKeys();
        return base.SaveChanges(acceptAllChangesOnSuccess);
    }

    public override Task<int> SaveChangesAsync(
        bool acceptAllChangesOnSuccess, CancellationToken cancellationToken = default)
    {
        SyncNicknameKeys();
        return base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        // Configure Profile entity
        modelBuilder.Entity<Profile>(entity =>
        {
            entity.HasKey(p => p.Ss58Address);
            entity.Property(p => p.Ss58Address).IsRequired();

            entity.Property(p => p.Nickname).IsRequired(false);

            // Uniqueness is on the case-folded copy, not on the nickname itself: "tester" and
            // "Tester" are the same name, so only one of them can be registered. The stored
            // nickname keeps the case the user typed; SyncNicknameKeys below keeps the copy in
            // step. Nulls stay out of a unique index, so profiles without a nickname do not clash.
            entity.Property<string>(Nicknames.NormalizedProperty).IsRequired(false);
            entity.HasIndex(Nicknames.NormalizedProperty).IsUnique();

            entity.Property(p => p.Bio).IsRequired(false);
            entity.Property(p => p.ProfilePicture).IsRequired(false);
            entity.Property(p => p.X25519Key).IsRequired(false);

            // Stored even though it always equals the key: consumers of the platform address a user
            // by userId, and having the column means a query or a projection never has to know that
            // the two are the same value. ProfilesController is what keeps them equal.
            entity.Property(p => p.UserId).IsRequired(false).HasMaxLength(64);

            entity.Property(p => p.Name).IsRequired(false).HasMaxLength(128);
            entity.Property(p => p.Email).IsRequired(false).HasMaxLength(256);
            entity.Property(p => p.Phone).IsRequired(false).HasMaxLength(32);
            entity.Property(p => p.Address).IsRequired(false).HasMaxLength(512);
            entity.Property(p => p.Title).IsRequired(false).HasMaxLength(128);
            entity.Property(p => p.Background).IsRequired(false).HasMaxLength(2000);

            entity.Property(p => p.Roles)
                .HasConversion(JsonColumn.Converter<List<UserRole>>(), JsonColumn.Comparer<List<UserRole>>())
                .IsRequired(false);

            entity.Property(p => p.Permission)
                .HasConversion(JsonColumn.Converter<UserPermissions>(), JsonColumn.Comparer<UserPermissions>())
                .IsRequired(false);

            entity.Property(p => p.CreatedAt).IsRequired(false);
            entity.Property(p => p.UpdatedAt).IsRequired(false);
        });

        // Configure Company entity. Keyed by its generated companyId rather than by a wallet,
        // because one wallet may register several companies — which is also why UserId is indexed
        // but not unique.
        modelBuilder.Entity<Company>(entity =>
        {
            entity.HasKey(c => c.CompanyId);
            entity.Property(c => c.CompanyId).IsRequired().HasMaxLength(64);

            entity.Property(c => c.UserId).IsRequired().HasMaxLength(64);
            entity.HasIndex(c => c.UserId);

            // No foreign key to Profile: a company may be registered by a wallet that has not filled
            // in a profile yet, and the platform treats the two records as independent.
            entity.Property(c => c.CompanyWalletAddress).IsRequired().HasMaxLength(64);
            entity.HasIndex(c => c.CompanyWalletAddress);

            entity.Property(c => c.Name).IsRequired().HasMaxLength(128);
            entity.Property(c => c.Email).IsRequired().HasMaxLength(256);

            entity.Property(c => c.Logo).IsRequired(false);
            entity.Property(c => c.Website).IsRequired(false).HasMaxLength(512);
            entity.Property(c => c.Summary).IsRequired(false).HasMaxLength(2000);
            entity.Property(c => c.Address).IsRequired(false).HasMaxLength(512);

            entity.Property(c => c.Permission)
                .HasConversion(
                    JsonColumn.Converter<CompanyPermissions>(), JsonColumn.Comparer<CompanyPermissions>())
                .IsRequired(false);

            entity.Property(c => c.CreatedAt).IsRequired(false);
            entity.Property(c => c.UpdatedAt).IsRequired(false);
        });

        // Configure WalletMigration entity. The SS58 address is the key: a Polkadot account
        // migrates to exactly one Solana wallet, while one Solana wallet may be the target of
        // several migrations (a user consolidating accounts), so SolanaAddress is not unique.
        modelBuilder.Entity<WalletMigration>(entity =>
        {
            entity.HasKey(m => m.Ss58Address);
            entity.Property(m => m.Ss58Address).IsRequired();
            entity.Property(m => m.SolanaAddress).IsRequired();
        });
    }

    /// <summary>
    /// Writes the case-folded nickname of every profile being saved into its shadow column, so that
    /// the unique index and every lookup see the same key.
    /// </summary>
    /// <remarks>
    /// It lives here rather than in the controller on purpose: whatever changes a nickname — an
    /// endpoint, a seed, a future background job — goes through a save, and none of them can forget
    /// to keep the key in step.
    /// </remarks>
    private void SyncNicknameKeys()
    {
        foreach (var entry in ChangeTracker.Entries<Profile>())
        {
            if (entry.State is EntityState.Added or EntityState.Modified)
            {
                entry.Property<string?>(Nicknames.NormalizedProperty).CurrentValue =
                    Nicknames.Normalize(entry.Entity.Nickname);
            }
        }
    }
}
