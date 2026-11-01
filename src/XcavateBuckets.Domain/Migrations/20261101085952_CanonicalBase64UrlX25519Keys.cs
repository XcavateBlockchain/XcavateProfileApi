using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace XcavateBuckets.Domain.Migrations
{
    /// <inheritdoc />
    public partial class CanonicalBase64UrlX25519Keys : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                CREATE TEMPORARY TABLE normalized_bucket_viewers ON COMMIT DROP AS
                SELECT ctid AS "RowId", "BucketId", "ViewerId", "AddedAt",
                    CASE
                        WHEN "ViewerId" ~ '^0[xX][0-9A-Fa-f]{64}$' THEN
                            REPLACE(REPLACE(REPLACE(
                                encode(decode(substring("ViewerId" from 3), 'hex'), 'base64'),
                                '+', '-'), '/', '_'), '=', '')
                        WHEN "ViewerId" ~ '^[0-9A-Fa-f]{64}$' THEN
                            REPLACE(REPLACE(REPLACE(
                                encode(decode("ViewerId", 'hex'), 'base64'),
                                '+', '-'), '/', '_'), '=', '')
                        ELSE "ViewerId"
                    END AS "CanonicalViewerId"
                FROM "bucket_viewers";

                DELETE FROM "bucket_viewers"
                WHERE ctid IN (
                    SELECT "RowId" FROM (
                        SELECT "RowId",
                               ROW_NUMBER() OVER (
                                   PARTITION BY "BucketId", "CanonicalViewerId"
                                   ORDER BY "AddedAt", "RowId") AS duplicate_number
                        FROM normalized_bucket_viewers
                    ) AS duplicates
                    WHERE duplicate_number > 1
                );

                UPDATE "buckets"
                SET "EncryptionKey" = REPLACE(REPLACE(REPLACE(
                    encode(decode(substring("EncryptionKey" from 3), 'hex'), 'base64'),
                    '+', '-'), '/', '_'), '=', '')
                WHERE "EncryptionKey" ~ '^0[xX][0-9A-Fa-f]{64}$';

                UPDATE "buckets"
                SET "EncryptionKey" = REPLACE(REPLACE(REPLACE(
                    encode(decode("EncryptionKey", 'hex'), 'base64'),
                    '+', '-'), '/', '_'), '=', '')
                WHERE "EncryptionKey" ~ '^[0-9A-Fa-f]{64}$';

                UPDATE "bucket_viewers"
                SET "ViewerId" = REPLACE(REPLACE(REPLACE(
                    encode(decode(substring("ViewerId" from 3), 'hex'), 'base64'),
                    '+', '-'), '/', '_'), '=', '')
                WHERE "ViewerId" ~ '^0[xX][0-9A-Fa-f]{64}$';

                UPDATE "bucket_viewers"
                SET "ViewerId" = REPLACE(REPLACE(REPLACE(
                    encode(decode("ViewerId", 'hex'), 'base64'),
                    '+', '-'), '/', '_'), '=', '')
                WHERE "ViewerId" ~ '^[0-9A-Fa-f]{64}$';
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                UPDATE "buckets"
                SET "EncryptionKey" = '0x' || encode(decode(
                    rpad(
                        REPLACE(REPLACE("EncryptionKey", '-', '+'), '_', '/'),
                        (length("EncryptionKey") + 3) / 4 * 4,
                        '='),
                    'base64'), 'hex')
                WHERE "EncryptionKey" ~ '^[A-Za-z0-9_-]{43}$';

                UPDATE "bucket_viewers"
                SET "ViewerId" = '0x' || encode(decode(
                    rpad(
                        REPLACE(REPLACE("ViewerId", '-', '+'), '_', '/'),
                        (length("ViewerId") + 3) / 4 * 4,
                        '='),
                    'base64'), 'hex')
                WHERE "ViewerId" ~ '^[A-Za-z0-9_-]{43}$';
                """);
        }
    }
}
