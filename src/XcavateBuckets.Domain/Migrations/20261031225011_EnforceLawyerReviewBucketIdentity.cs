using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace XcavateBuckets.Domain.Migrations
{
    /// <inheritdoc />
    public partial class EnforceLawyerReviewBucketIdentity : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "ux_buckets_lawyer_document_review",
                table: "buckets",
                columns: new[] { "NamespaceId", "Category", "Name" },
                unique: true,
                filter: "\"Category\" = 'lawyer-document-review'");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ux_buckets_lawyer_document_review",
                table: "buckets");
        }
    }
}
