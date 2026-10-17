using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace XcavateProfileApi.Migrations
{
    /// <inheritdoc />
    public partial class AddLawyerTermsDocuments : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "LawyerTermsDocuments",
                columns: table => new
                {
                    Ss58Address = table.Column<string>(type: "text", nullable: false),
                    Cid = table.Column<string>(type: "text", nullable: false),
                    FileName = table.Column<string>(type: "text", nullable: false),
                    ContentType = table.Column<string>(type: "text", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LawyerTermsDocuments", x => x.Ss58Address);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "LawyerTermsDocuments");
        }
    }
}
