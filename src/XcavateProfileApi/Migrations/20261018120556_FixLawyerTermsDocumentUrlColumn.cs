using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace XcavateProfileApi.Migrations
{
    /// <summary>
    /// Repairs the LawyerTermsDocuments schema drift left by the in-place edit of
    /// 20261017143610_AddLawyerTermsDocuments: databases that applied that migration in its
    /// original form have a "Cid" column, while the model (and databases created after the
    /// edit) use "Url". The original migration is recorded as applied everywhere, so EF will
    /// never re-run it — this guarded rename fixes exactly the drifted databases and is a
    /// no-op everywhere else (fresh databases, and databases without the table, skip it).
    /// </summary>
    public partial class FixLawyerTermsDocumentUrlColumn : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DO $$
                BEGIN
                    IF EXISTS (
                        SELECT 1 FROM information_schema.columns
                        WHERE table_schema = 'public' AND table_name = 'LawyerTermsDocuments' AND column_name = 'Cid'
                    ) THEN
                        ALTER TABLE "LawyerTermsDocuments" RENAME COLUMN "Cid" TO "Url";
                    END IF;
                END $$;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Mirror of Up: only rename back when the rename actually happened (a "Url"
            // column present with no "Cid" alongside it). Databases that always had "Url"
            // are left alone... which is also indistinguishable from a renamed one, so treat
            // Down as best-effort; it never runs in practice (startup only migrates forward).
            migrationBuilder.Sql("""
                DO $$
                BEGIN
                    IF EXISTS (
                        SELECT 1 FROM information_schema.columns
                        WHERE table_schema = 'public' AND table_name = 'LawyerTermsDocuments' AND column_name = 'Url'
                    ) AND NOT EXISTS (
                        SELECT 1 FROM information_schema.columns
                        WHERE table_schema = 'public' AND table_name = 'LawyerTermsDocuments' AND column_name = 'Cid'
                    ) THEN
                        ALTER TABLE "LawyerTermsDocuments" RENAME COLUMN "Url" TO "Cid";
                    END IF;
                END $$;
                """);
        }
    }
}
