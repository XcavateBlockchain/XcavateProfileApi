using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace XcavateBuckets.Domain.Migrations
{
    /// <inheritdoc />
    public partial class InitBuckets : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "namespaces",
                columns: table => new
                {
                    NamespaceId = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Name = table.Column<string>(type: "text", nullable: true),
                    SchemaUri = table.Column<string>(type: "text", nullable: true),
                    Properties = table.Column<string>(type: "jsonb", nullable: true),
                    Creator = table.Column<string>(type: "text", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_namespaces", x => x.NamespaceId);
                });

            migrationBuilder.CreateTable(
                name: "buckets",
                columns: table => new
                {
                    BucketId = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    NamespaceId = table.Column<long>(type: "bigint", nullable: false),
                    Creator = table.Column<string>(type: "text", nullable: true),
                    Name = table.Column<string>(type: "text", nullable: true),
                    Category = table.Column<string>(type: "text", nullable: true),
                    Properties = table.Column<string>(type: "jsonb", nullable: true),
                    IsWritable = table.Column<bool>(type: "boolean", nullable: false),
                    EncryptionKey = table.Column<string>(type: "text", nullable: true),
                    NextMessageId = table.Column<long>(type: "bigint", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_buckets", x => x.BucketId);
                    table.ForeignKey(
                        name: "FK_buckets_namespaces_NamespaceId",
                        column: x => x.NamespaceId,
                        principalTable: "namespaces",
                        principalColumn: "NamespaceId",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "namespace_managers",
                columns: table => new
                {
                    NamespaceId = table.Column<long>(type: "bigint", nullable: false),
                    Manager = table.Column<string>(type: "text", nullable: false),
                    AddedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_namespace_managers", x => new { x.NamespaceId, x.Manager });
                    table.ForeignKey(
                        name: "FK_namespace_managers_namespaces_NamespaceId",
                        column: x => x.NamespaceId,
                        principalTable: "namespaces",
                        principalColumn: "NamespaceId",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "bucket_admins",
                columns: table => new
                {
                    BucketId = table.Column<long>(type: "bigint", nullable: false),
                    SubjectId = table.Column<string>(type: "text", nullable: false),
                    AddedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_bucket_admins", x => new { x.BucketId, x.SubjectId });
                    table.ForeignKey(
                        name: "FK_bucket_admins_buckets_BucketId",
                        column: x => x.BucketId,
                        principalTable: "buckets",
                        principalColumn: "BucketId",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "bucket_contributors",
                columns: table => new
                {
                    BucketId = table.Column<long>(type: "bigint", nullable: false),
                    SubjectId = table.Column<string>(type: "text", nullable: false),
                    AddedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_bucket_contributors", x => new { x.BucketId, x.SubjectId });
                    table.ForeignKey(
                        name: "FK_bucket_contributors_buckets_BucketId",
                        column: x => x.BucketId,
                        principalTable: "buckets",
                        principalColumn: "BucketId",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "bucket_viewers",
                columns: table => new
                {
                    BucketId = table.Column<long>(type: "bigint", nullable: false),
                    ViewerId = table.Column<string>(type: "text", nullable: false),
                    AddedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_bucket_viewers", x => new { x.BucketId, x.ViewerId });
                    table.ForeignKey(
                        name: "FK_bucket_viewers_buckets_BucketId",
                        column: x => x.BucketId,
                        principalTable: "buckets",
                        principalColumn: "BucketId",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "messages",
                columns: table => new
                {
                    BucketId = table.Column<long>(type: "bigint", nullable: false),
                    MessageId = table.Column<long>(type: "bigint", nullable: false),
                    Contributor = table.Column<string>(type: "text", nullable: false),
                    Reference = table.Column<string>(type: "text", nullable: true),
                    Tag = table.Column<string>(type: "text", nullable: true),
                    Description = table.Column<string>(type: "text", nullable: true),
                    ContentType = table.Column<string>(type: "text", nullable: true),
                    ContentHash = table.Column<string>(type: "text", nullable: true),
                    Properties = table.Column<string>(type: "jsonb", nullable: true),
                    IpfsContent = table.Column<string>(type: "text", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_messages", x => new { x.BucketId, x.MessageId });
                    table.ForeignKey(
                        name: "FK_messages_buckets_BucketId",
                        column: x => x.BucketId,
                        principalTable: "buckets",
                        principalColumn: "BucketId",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "tag_message_counts",
                columns: table => new
                {
                    BucketId = table.Column<long>(type: "bigint", nullable: false),
                    TagName = table.Column<string>(type: "text", nullable: false),
                    Count = table.Column<int>(type: "integer", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_tag_message_counts", x => new { x.BucketId, x.TagName });
                    table.ForeignKey(
                        name: "FK_tag_message_counts_buckets_BucketId",
                        column: x => x.BucketId,
                        principalTable: "buckets",
                        principalColumn: "BucketId",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "tags",
                columns: table => new
                {
                    BucketId = table.Column<long>(type: "bigint", nullable: false),
                    TagName = table.Column<string>(type: "text", nullable: false),
                    Creator = table.Column<string>(type: "text", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_tags", x => new { x.BucketId, x.TagName });
                    table.ForeignKey(
                        name: "FK_tags_buckets_BucketId",
                        column: x => x.BucketId,
                        principalTable: "buckets",
                        principalColumn: "BucketId",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_bucket_admins_SubjectId",
                table: "bucket_admins",
                column: "SubjectId");

            migrationBuilder.CreateIndex(
                name: "IX_bucket_contributors_SubjectId",
                table: "bucket_contributors",
                column: "SubjectId");

            migrationBuilder.CreateIndex(
                name: "IX_bucket_viewers_ViewerId",
                table: "bucket_viewers",
                column: "ViewerId");

            migrationBuilder.CreateIndex(
                name: "IX_buckets_NamespaceId",
                table: "buckets",
                column: "NamespaceId");

            migrationBuilder.CreateIndex(
                name: "IX_messages_Contributor",
                table: "messages",
                column: "Contributor");

            migrationBuilder.CreateIndex(
                name: "IX_messages_MessageId",
                table: "messages",
                column: "MessageId");

            migrationBuilder.CreateIndex(
                name: "IX_messages_Tag",
                table: "messages",
                column: "Tag");

            migrationBuilder.CreateIndex(
                name: "IX_namespace_managers_Manager",
                table: "namespace_managers",
                column: "Manager");

            migrationBuilder.CreateIndex(
                name: "IX_tag_message_counts_TagName",
                table: "tag_message_counts",
                column: "TagName");

            migrationBuilder.CreateIndex(
                name: "IX_tags_TagName",
                table: "tags",
                column: "TagName");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "bucket_admins");

            migrationBuilder.DropTable(
                name: "bucket_contributors");

            migrationBuilder.DropTable(
                name: "bucket_viewers");

            migrationBuilder.DropTable(
                name: "messages");

            migrationBuilder.DropTable(
                name: "namespace_managers");

            migrationBuilder.DropTable(
                name: "tag_message_counts");

            migrationBuilder.DropTable(
                name: "tags");

            migrationBuilder.DropTable(
                name: "buckets");

            migrationBuilder.DropTable(
                name: "namespaces");
        }
    }
}
