using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Cmsify.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddWorkspaceAccessGrants : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "is_super_admin",
                table: "users",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.Sql("""
                UPDATE users
                SET is_super_admin = true
                WHERE id = (
                    SELECT id
                    FROM users
                    WHERE role = 'Admin' AND is_deleted = false
                    ORDER BY created_at
                    LIMIT 1
                );
                """);

            migrationBuilder.CreateTable(
                name: "user_workspace_accesses",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    workspace_id = table.Column<Guid>(type: "uuid", nullable: false),
                    access_level = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_user_workspace_accesses", x => x.id);
                    table.ForeignKey(
                        name: "fk_user_workspace_accesses_users_user_id",
                        column: x => x.user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_user_workspace_accesses_workspaces_workspace_id",
                        column: x => x.workspace_id,
                        principalTable: "workspaces",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_user_workspace_accesses_user_id_workspace_id",
                table: "user_workspace_accesses",
                columns: new[] { "user_id", "workspace_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_user_workspace_accesses_workspace_id",
                table: "user_workspace_accesses",
                column: "workspace_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "user_workspace_accesses");

            migrationBuilder.DropColumn(
                name: "is_super_admin",
                table: "users");
        }
    }
}
