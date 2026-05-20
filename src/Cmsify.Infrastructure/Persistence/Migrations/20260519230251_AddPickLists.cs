using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Cmsify.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddPickLists : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "pick_lists",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    workspace_id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    slug = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    description = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    is_deleted = table.Column<bool>(type: "boolean", nullable: false, defaultValue: false),
                    deleted_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    deleted_by_user_id = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_pick_lists", x => x.id);
                    table.ForeignKey(
                        name: "fk_pick_lists_workspaces_workspace_id",
                        column: x => x.workspace_id,
                        principalTable: "workspaces",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "pick_list_options",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    pick_list_id = table.Column<Guid>(type: "uuid", nullable: false),
                    label = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    value = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    order = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_pick_list_options", x => x.id);
                    table.ForeignKey(
                        name: "fk_pick_list_options_pick_lists_pick_list_id",
                        column: x => x.pick_list_id,
                        principalTable: "pick_lists",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_pick_list_options_pick_list_id_order",
                table: "pick_list_options",
                columns: new[] { "pick_list_id", "order" });

            migrationBuilder.CreateIndex(
                name: "ix_pick_list_options_pick_list_id_value",
                table: "pick_list_options",
                columns: new[] { "pick_list_id", "value" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_pick_lists_workspace_id_slug",
                table: "pick_lists",
                columns: new[] { "workspace_id", "slug" },
                unique: true,
                filter: "is_deleted = false");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "pick_list_options");

            migrationBuilder.DropTable(
                name: "pick_lists");
        }
    }
}
