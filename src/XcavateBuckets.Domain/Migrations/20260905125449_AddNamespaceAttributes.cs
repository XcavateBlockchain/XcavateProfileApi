using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace XcavateBuckets.Domain.Migrations
{
    /// <inheritdoc />
    public partial class AddNamespaceAttributes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Category",
                table: "namespaces",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Cluster",
                table: "namespaces",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "PropertyId",
                table: "namespaces",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "RealXhubId",
                table: "namespaces",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "Slot",
                table: "namespaces",
                type: "bigint",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Category",
                table: "namespaces");

            migrationBuilder.DropColumn(
                name: "Cluster",
                table: "namespaces");

            migrationBuilder.DropColumn(
                name: "PropertyId",
                table: "namespaces");

            migrationBuilder.DropColumn(
                name: "RealXhubId",
                table: "namespaces");

            migrationBuilder.DropColumn(
                name: "Slot",
                table: "namespaces");
        }
    }
}
