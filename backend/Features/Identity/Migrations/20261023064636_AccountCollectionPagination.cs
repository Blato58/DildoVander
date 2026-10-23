using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Kufr.Backend.Features.Identity.Migrations
{
    /// <inheritdoc />
    public partial class AccountCollectionPagination : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "CollectionRevision",
                table: "ArchiveAssociations",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.CreateTable(
                name: "ArchiveCollectionStates",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Revision = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ArchiveCollectionStates", x => x.Id);
                });

            // Assign deterministic revisions to existing links and retain a
            // source-only singleton watermark for later claims and reclamations.
            migrationBuilder.Sql("""
                WITH ranked AS (
                    SELECT UserId,Locator,ROW_NUMBER() OVER(ORDER BY LinkedAt,UserId,Locator) AS Revision
                    FROM ArchiveAssociations
                )
                UPDATE ArchiveAssociations
                SET CollectionRevision=(SELECT Revision FROM ranked
                    WHERE ranked.UserId=ArchiveAssociations.UserId AND ranked.Locator=ArchiveAssociations.Locator);
                INSERT INTO ArchiveCollectionStates(Id,Revision)
                SELECT 1,COALESCE(MAX(CollectionRevision),0) FROM ArchiveAssociations;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ArchiveCollectionStates");

            migrationBuilder.DropColumn(
                name: "CollectionRevision",
                table: "ArchiveAssociations");
        }
    }
}
