using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace XcavateProfileApi.Migrations
{
    /// <inheritdoc />
    public partial class AddWalletMigrations : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "WalletMigrations",
                columns: table => new
                {
                    Ss58Address = table.Column<string>(type: "text", nullable: false),
                    SolanaAddress = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WalletMigrations", x => x.Ss58Address);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "WalletMigrations");
        }
    }
}
