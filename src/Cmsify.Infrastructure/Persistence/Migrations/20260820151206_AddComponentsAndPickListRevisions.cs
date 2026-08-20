using System;
using System.Text.Json;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Cmsify.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddComponentsAndPickListRevisions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_template_fields_type_shape",
                table: "template_fields");

            migrationBuilder.AddColumn<Guid>(
                name: "component_id",
                table: "template_fields",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "current_revision_id",
                table: "pick_lists",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "display_label",
                table: "content_version_field_values",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "pick_list_revisions",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    pick_list_id = table.Column<Guid>(type: "uuid", nullable: false),
                    version_number = table.Column<int>(type: "integer", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_pick_list_revisions", x => x.id);
                    table.ForeignKey(
                        name: "fk_pick_list_revisions_pick_lists_pick_list_id",
                        column: x => x.pick_list_id,
                        principalTable: "pick_lists",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "pick_list_revision_options",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    pick_list_revision_id = table.Column<Guid>(type: "uuid", nullable: false),
                    label = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    value = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    order = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_pick_list_revision_options", x => x.id);
                    table.ForeignKey(
                        name: "fk_pick_list_revision_options_pick_list_revisions_pick_list_re",
                        column: x => x.pick_list_revision_id,
                        principalTable: "pick_list_revisions",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "component_fields",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    component_version_id = table.Column<Guid>(type: "uuid", nullable: false),
                    key = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    label = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    help_text = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    order = table.Column<int>(type: "integer", nullable: false),
                    is_required = table.Column<bool>(type: "boolean", nullable: false),
                    min_occurrences = table.Column<int>(type: "integer", nullable: false),
                    max_occurrences = table.Column<int>(type: "integer", nullable: true),
                    primitive_type = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    nested_component_id = table.Column<Guid>(type: "uuid", nullable: true),
                    field_config = table.Column<JsonElement>(type: "jsonb", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_component_fields", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "component_versions",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    component_id = table.Column<Guid>(type: "uuid", nullable: false),
                    version_number = table.Column<int>(type: "integer", nullable: false),
                    status = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    published_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    notes = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    is_deleted = table.Column<bool>(type: "boolean", nullable: false, defaultValue: false),
                    deleted_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    deleted_by_user_id = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_component_versions", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "components",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    workspace_id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    slug = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    description = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    current_version_id = table.Column<Guid>(type: "uuid", nullable: true),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    is_deleted = table.Column<bool>(type: "boolean", nullable: false, defaultValue: false),
                    deleted_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    deleted_by_user_id = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_components", x => x.id);
                    table.ForeignKey(
                        name: "fk_components_component_versions_current_version_id",
                        column: x => x.current_version_id,
                        principalTable: "component_versions",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_components_workspaces_workspace_id",
                        column: x => x.workspace_id,
                        principalTable: "workspaces",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_template_fields_component_id",
                table: "template_fields",
                column: "component_id");

            migrationBuilder.AddCheckConstraint(
                name: "ck_template_fields_type_shape",
                table: "template_fields",
                sql: "(is_open = true AND primitive_type IS NULL AND template_id IS NULL AND component_id IS NULL) OR (is_open = false AND ((primitive_type IS NOT NULL AND template_id IS NULL AND component_id IS NULL) OR (primitive_type IS NULL AND template_id IS NOT NULL AND component_id IS NULL) OR (primitive_type IS NULL AND template_id IS NULL AND component_id IS NOT NULL)))");

            migrationBuilder.CreateIndex(
                name: "ix_pick_lists_current_revision_id",
                table: "pick_lists",
                column: "current_revision_id");

            migrationBuilder.CreateIndex(
                name: "ix_component_fields_component_version_id_key",
                table: "component_fields",
                columns: new[] { "component_version_id", "key" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_component_fields_nested_component_id",
                table: "component_fields",
                column: "nested_component_id");

            migrationBuilder.CreateIndex(
                name: "ix_component_versions_component_id",
                table: "component_versions",
                column: "component_id",
                unique: true,
                filter: "status = 'Draft' AND is_deleted = false");

            migrationBuilder.CreateIndex(
                name: "ix_component_versions_component_id_version_number",
                table: "component_versions",
                columns: new[] { "component_id", "version_number" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_components_current_version_id",
                table: "components",
                column: "current_version_id");

            migrationBuilder.CreateIndex(
                name: "ix_components_workspace_id_slug",
                table: "components",
                columns: new[] { "workspace_id", "slug" },
                unique: true,
                filter: "is_deleted = false");

            migrationBuilder.CreateIndex(
                name: "ix_pick_list_revision_options_pick_list_revision_id_order",
                table: "pick_list_revision_options",
                columns: new[] { "pick_list_revision_id", "order" });

            migrationBuilder.CreateIndex(
                name: "ix_pick_list_revision_options_pick_list_revision_id_value",
                table: "pick_list_revision_options",
                columns: new[] { "pick_list_revision_id", "value" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_pick_list_revisions_pick_list_id_version_number",
                table: "pick_list_revisions",
                columns: new[] { "pick_list_id", "version_number" },
                unique: true);

            // Preserve every existing shared choice set as immutable revision 1 before
            // future edits begin creating subsequent revisions.
            migrationBuilder.Sql("""
                WITH inserted AS (
                    INSERT INTO pick_list_revisions (id, pick_list_id, version_number, created_at)
                    SELECT gen_random_uuid(), id, 1, NOW()
                    FROM pick_lists
                    RETURNING id, pick_list_id
                ), copied AS (
                    INSERT INTO pick_list_revision_options (id, pick_list_revision_id, label, value, "order")
                    SELECT gen_random_uuid(), inserted.id, option.label, option.value, option."order"
                    FROM inserted
                    JOIN pick_list_options option ON option.pick_list_id = inserted.pick_list_id
                )
                UPDATE pick_lists list
                SET current_revision_id = inserted.id
                FROM inserted
                WHERE list.id = inserted.pick_list_id;
                """);

            migrationBuilder.AddForeignKey(
                name: "fk_pick_lists_pick_list_revisions_current_revision_id",
                table: "pick_lists",
                column: "current_revision_id",
                principalTable: "pick_list_revisions",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "fk_template_fields_components_component_id",
                table: "template_fields",
                column: "component_id",
                principalTable: "components",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "fk_component_fields_component_versions_component_version_id",
                table: "component_fields",
                column: "component_version_id",
                principalTable: "component_versions",
                principalColumn: "id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "fk_component_fields_components_nested_component_id",
                table: "component_fields",
                column: "nested_component_id",
                principalTable: "components",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "fk_component_versions_components_component_id",
                table: "component_versions",
                column: "component_id",
                principalTable: "components",
                principalColumn: "id",
                onDelete: ReferentialAction.Cascade);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "fk_pick_lists_pick_list_revisions_current_revision_id",
                table: "pick_lists");

            migrationBuilder.DropForeignKey(
                name: "fk_template_fields_components_component_id",
                table: "template_fields");

            migrationBuilder.DropForeignKey(
                name: "fk_components_component_versions_current_version_id",
                table: "components");

            migrationBuilder.DropForeignKey(
                name: "fk_component_fields_component_versions_component_version_id",
                table: "component_fields");

            migrationBuilder.DropForeignKey(
                name: "fk_component_fields_components_nested_component_id",
                table: "component_fields");

            migrationBuilder.DropForeignKey(
                name: "fk_component_versions_components_component_id",
                table: "component_versions");

            migrationBuilder.DropTable(
                name: "component_fields");

            migrationBuilder.DropTable(
                name: "pick_list_revision_options");

            migrationBuilder.DropTable(
                name: "pick_list_revisions");

            migrationBuilder.DropTable(
                name: "component_versions");

            migrationBuilder.DropTable(
                name: "components");

            migrationBuilder.DropIndex(
                name: "ix_template_fields_component_id",
                table: "template_fields");

            migrationBuilder.DropCheckConstraint(
                name: "ck_template_fields_type_shape",
                table: "template_fields");

            migrationBuilder.DropIndex(
                name: "ix_pick_lists_current_revision_id",
                table: "pick_lists");

            migrationBuilder.DropColumn(
                name: "component_id",
                table: "template_fields");

            migrationBuilder.DropColumn(
                name: "current_revision_id",
                table: "pick_lists");

            migrationBuilder.DropColumn(
                name: "display_label",
                table: "content_version_field_values");

            migrationBuilder.AddCheckConstraint(
                name: "ck_template_fields_type_shape",
                table: "template_fields",
                sql: "(is_open = true AND primitive_type IS NULL AND template_id IS NULL) OR (is_open = false AND ((primitive_type IS NOT NULL AND template_id IS NULL) OR (primitive_type IS NULL AND template_id IS NOT NULL)))");
        }
    }
}
