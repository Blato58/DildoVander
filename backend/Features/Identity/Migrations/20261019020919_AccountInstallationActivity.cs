using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Kufr.Backend.Features.Identity.Migrations
{
    /// <inheritdoc />
    public partial class AccountInstallationActivity : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "LastSeenAt",
                table: "Sessions",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0L);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "LastSeenAt",
                table: "Sessions");
        }
    }
}
