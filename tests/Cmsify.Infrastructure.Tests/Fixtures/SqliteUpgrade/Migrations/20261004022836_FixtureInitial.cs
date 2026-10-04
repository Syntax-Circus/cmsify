using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Cmsify.Sqlite.MigrationProbe.Fixtures
{
    /// <inheritdoc />
    public partial class FixtureInitial : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "fixture_parents",
                columns: table => new
                {
                    id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_fixture_parents", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "fixture_children",
                columns: table => new
                {
                    id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    parent_id = table.Column<int>(type: "INTEGER", nullable: false),
                    name = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_fixture_children", x => x.id);
                    table.ForeignKey(
                        name: "FK_fixture_children_fixture_parents_parent_id",
                        column: x => x.parent_id,
                        principalTable: "fixture_parents",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_fixture_children_name",
                table: "fixture_children",
                column: "name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_fixture_children_parent_id",
                table: "fixture_children",
                column: "parent_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "fixture_children");

            migrationBuilder.DropTable(
                name: "fixture_parents");
        }
    }
}
