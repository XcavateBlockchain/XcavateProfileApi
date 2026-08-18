using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace XcavateProfileApi.Migrations
{
    /// <inheritdoc />
    public partial class AddUserAttributesAndCompanies : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Address",
                table: "Profiles",
                type: "character varying(512)",
                maxLength: 512,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Background",
                table: "Profiles",
                type: "character varying(2000)",
                maxLength: 2000,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "CreatedAt",
                table: "Profiles",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Email",
                table: "Profiles",
                type: "character varying(256)",
                maxLength: 256,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Name",
                table: "Profiles",
                type: "character varying(128)",
                maxLength: 128,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Permission",
                table: "Profiles",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Phone",
                table: "Profiles",
                type: "character varying(32)",
                maxLength: 32,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Roles",
                table: "Profiles",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Title",
                table: "Profiles",
                type: "character varying(128)",
                maxLength: 128,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "UpdatedAt",
                table: "Profiles",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "UserId",
                table: "Profiles",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            // Rows written before the column existed. userId is defined as the wallet address, so it
            // is backfilled here rather than left null for every profile that already exists —
            // otherwise the invariant only holds for profiles touched after this deploy.
            // createdAt and updatedAt are deliberately left null: those values are genuinely unknown
            // for old rows, and inventing a timestamp would be worse than admitting that.
            migrationBuilder.Sql(
                @"UPDATE ""Profiles"" SET ""UserId"" = ""Ss58Address"" WHERE ""UserId"" IS NULL;");

            migrationBuilder.CreateTable(
                name: "Companies",
                columns: table => new
                {
                    CompanyId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    UserId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    CompanyWalletAddress = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Name = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    Email = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    Logo = table.Column<string>(type: "text", nullable: true),
                    Website = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                    Summary = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    Address = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                    Permission = table.Column<string>(type: "text", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Companies", x => x.CompanyId);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Companies_CompanyWalletAddress",
                table: "Companies",
                column: "CompanyWalletAddress");

            migrationBuilder.CreateIndex(
                name: "IX_Companies_UserId",
                table: "Companies",
                column: "UserId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Companies");

            migrationBuilder.DropColumn(
                name: "Address",
                table: "Profiles");

            migrationBuilder.DropColumn(
                name: "Background",
                table: "Profiles");

            migrationBuilder.DropColumn(
                name: "CreatedAt",
                table: "Profiles");

            migrationBuilder.DropColumn(
                name: "Email",
                table: "Profiles");

            migrationBuilder.DropColumn(
                name: "Name",
                table: "Profiles");

            migrationBuilder.DropColumn(
                name: "Permission",
                table: "Profiles");

            migrationBuilder.DropColumn(
                name: "Phone",
                table: "Profiles");

            migrationBuilder.DropColumn(
                name: "Roles",
                table: "Profiles");

            migrationBuilder.DropColumn(
                name: "Title",
                table: "Profiles");

            migrationBuilder.DropColumn(
                name: "UpdatedAt",
                table: "Profiles");

            migrationBuilder.DropColumn(
                name: "UserId",
                table: "Profiles");
        }
    }
}
