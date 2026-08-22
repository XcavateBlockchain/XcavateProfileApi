using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace XcavateProfileApi.Migrations
{
    /// <summary>
    /// Makes nicknames unique without regard to case: "tester" and "Tester" become one name that
    /// only one profile can hold, and either spelling finds it.
    /// </summary>
    /// <remarks>
    /// The nickname column keeps the case the user typed. What is added is NicknameNormalized, the
    /// lower-cased copy the unique index and every lookup use — see <c>Nicknames</c>, which is what
    /// keeps the column filled from here on.
    /// <para>
    /// The guard below is deliberate: if two rows already differ only in case, one of them would have
    /// to lose its nickname for the index to be created, and picking which is not a migration's call.
    /// It stops the deploy with the offending names instead of a bare unique-violation, so whoever
    /// runs it can settle the clash with the users concerned and re-run.
    /// </para>
    /// </remarks>
    public partial class AddCaseInsensitiveNicknames : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "NicknameNormalized",
                table: "Profiles",
                type: "text",
                nullable: true);

            // Blank is stored as NULL, matching Nicknames.Normalize: a unique index ignores NULLs,
            // so profiles without a nickname do not collide with each other.
            // PostgreSQL's lower() and .NET's ToLowerInvariant() can disagree on a handful of exotic
            // characters; a profile written after this point is re-normalized in .NET on save, which
            // is the value the lookups compare against.
            migrationBuilder.Sql(
                @"UPDATE ""Profiles""
                     SET ""NicknameNormalized"" = lower(""Nickname"")
                   WHERE ""Nickname"" IS NOT NULL AND btrim(""Nickname"") <> '';");

            migrationBuilder.Sql(
                @"DO $$
                  DECLARE clashes text;
                  BEGIN
                      SELECT string_agg(""NicknameNormalized"", ', ' ORDER BY ""NicknameNormalized"")
                        INTO clashes
                        FROM (
                            SELECT ""NicknameNormalized""
                              FROM ""Profiles""
                             WHERE ""NicknameNormalized"" IS NOT NULL
                             GROUP BY ""NicknameNormalized""
                            HAVING count(*) > 1
                        ) duplicates;

                      IF clashes IS NOT NULL THEN
                          RAISE EXCEPTION
                              'Nicknames are about to become unique regardless of case, and these are held by more than one profile: %. Free up all but one spelling of each, then run the migration again.',
                              clashes;
                      END IF;
                  END $$;");

            // Replaced, not kept alongside: case-insensitive uniqueness already implies the
            // case-sensitive kind this index enforced.
            migrationBuilder.DropIndex(
                name: "IX_Profiles_Nickname",
                table: "Profiles");

            migrationBuilder.CreateIndex(
                name: "IX_Profiles_NicknameNormalized",
                table: "Profiles",
                column: "NicknameNormalized",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Profiles_NicknameNormalized",
                table: "Profiles");

            migrationBuilder.DropColumn(
                name: "NicknameNormalized",
                table: "Profiles");

            migrationBuilder.CreateIndex(
                name: "IX_Profiles_Nickname",
                table: "Profiles",
                column: "Nickname",
                unique: true);
        }
    }
}
