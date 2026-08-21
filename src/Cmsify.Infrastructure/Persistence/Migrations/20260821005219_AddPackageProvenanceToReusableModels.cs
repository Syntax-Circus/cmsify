using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Cmsify.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddPackageProvenanceToReusableModels : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "package_id",
                table: "pick_lists",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "package_namespace",
                table: "pick_lists",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "package_version",
                table: "pick_lists",
                type: "character varying(50)",
                maxLength: 50,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "package_id",
                table: "components",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "package_namespace",
                table: "components",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "package_version",
                table: "components",
                type: "character varying(50)",
                maxLength: 50,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "package_id",
                table: "pick_lists");

            migrationBuilder.DropColumn(
                name: "package_namespace",
                table: "pick_lists");

            migrationBuilder.DropColumn(
                name: "package_version",
                table: "pick_lists");

            migrationBuilder.DropColumn(
                name: "package_id",
                table: "components");

            migrationBuilder.DropColumn(
                name: "package_namespace",
                table: "components");

            migrationBuilder.DropColumn(
                name: "package_version",
                table: "components");
        }
    }
}
