using Microsoft.EntityFrameworkCore;
using XcavateProfile.Client;

namespace XcavateProfileApi.Data;

/// <remarks>
/// Not wired up: <see cref="ProfileDbContext.OnModelCreating"/> holds the mapping the migrations and
/// the running API are built from, including the company and the user attributes this method knows
/// nothing about. Kept only because it pins the intended PostgreSQL column types.
/// </remarks>
public static class ModelBuilderExtensions
{
    public static void ConfigureProfile(this ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Profile>(entity =>
        {
            entity.HasKey(p => p.Ss58Address);
            entity.Property(p => p.Ss58Address).IsRequired().HasColumnType("varchar(64)");

            // Unique on the case-folded copy rather than on the nickname itself, as in
            // ProfileDbContext: "tester" and "Tester" are one name.
            entity.Property(p => p.Nickname).HasColumnType("varchar(64)");
            entity.Property<string>(Nicknames.NormalizedProperty).HasColumnType("varchar(64)");
            entity.HasIndex(Nicknames.NormalizedProperty).IsUnique();

            entity.Property(p => p.Bio).HasColumnType("text");
            entity.Property(p => p.ProfilePicture).HasColumnType("text");
            entity.Property(p => p.X25519Key).HasColumnType("varchar(64)");
        });
    }
}
