using System;
using System.Text.Json;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Cmsify.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddContentVersions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "content_versions",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    content_item_id = table.Column<Guid>(type: "uuid", nullable: false),
                    workspace_id = table.Column<Guid>(type: "uuid", nullable: false),
                    version_number = table.Column<int>(type: "integer", nullable: false),
                    status = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    template_version_id = table.Column<Guid>(type: "uuid", nullable: false),
                    slug = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    locale_code = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    translation_group_id = table.Column<Guid>(type: "uuid", nullable: true),
                    tags = table.Column<string[]>(type: "text[]", nullable: false),
                    published_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    retired_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    published_by_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    rolled_back_from_version_number = table.Column<int>(type: "integer", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_content_versions", x => x.id);
                    table.ForeignKey(
                        name: "fk_content_versions_content_items_content_item_id",
                        column: x => x.content_item_id,
                        principalTable: "content_items",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_content_versions_template_versions_template_version_id",
                        column: x => x.template_version_id,
                        principalTable: "template_versions",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_content_versions_workspaces_workspace_id",
                        column: x => x.workspace_id,
                        principalTable: "workspaces",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "content_version_field_values",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    content_version_id = table.Column<Guid>(type: "uuid", nullable: false),
                    field_id = table.Column<Guid>(type: "uuid", nullable: false),
                    order = table.Column<int>(type: "integer", nullable: false),
                    value_kind = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    text_value = table.Column<string>(type: "text", nullable: true),
                    bool_value = table.Column<bool>(type: "boolean", nullable: true),
                    media_asset_id = table.Column<Guid>(type: "uuid", nullable: true),
                    file_asset_id = table.Column<Guid>(type: "uuid", nullable: true),
                    child_content_item_id = table.Column<Guid>(type: "uuid", nullable: true),
                    json_value = table.Column<JsonElement>(type: "jsonb", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_content_version_field_values", x => x.id);
                    table.ForeignKey(
                        name: "fk_content_version_field_values_content_versions_content_versi",
                        column: x => x.content_version_id,
                        principalTable: "content_versions",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_content_version_field_values_content_version_id_field_id_or",
                table: "content_version_field_values",
                columns: new[] { "content_version_id", "field_id", "order" });

            migrationBuilder.CreateIndex(
                name: "ix_content_versions_content_item_id_status",
                table: "content_versions",
                columns: new[] { "content_item_id", "status" },
                unique: true,
                filter: "status = 'Published'");

            migrationBuilder.CreateIndex(
                name: "ix_content_versions_content_item_id_version_number",
                table: "content_versions",
                columns: new[] { "content_item_id", "version_number" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_content_versions_template_version_id",
                table: "content_versions",
                column: "template_version_id");

            migrationBuilder.CreateIndex(
                name: "ix_content_versions_workspace_id",
                table: "content_versions",
                column: "workspace_id");

            // Backfill: synthesize a v1 Published ContentVersion for every existing Published ContentItem.
            migrationBuilder.Sql("""
                WITH inserted AS (
                    INSERT INTO content_versions (
                        id, content_item_id, workspace_id, version_number, status,
                        template_version_id, slug, locale_code, translation_group_id,
                        tags, published_at, retired_at, published_by_user_id, rolled_back_from_version_number)
                    SELECT
                        gen_random_uuid(),
                        ci.id,
                        ci.workspace_id,
                        1,
                        'Published',
                        ci.template_version_id,
                        ci.slug,
                        ci.locale_code,
                        ci.translation_group_id,
                        COALESCE(ARRAY(
                            SELECT t.name
                            FROM content_item_tags cit
                            JOIN tags t ON t.id = cit.tag_id AND t.is_deleted = false
                            WHERE cit.content_item_id = ci.id
                            ORDER BY t.name
                        ), ARRAY[]::text[]),
                        COALESCE(ci.published_at, ci.updated_at, now()),
                        NULL,
                        ci.updated_by_user_id,
                        NULL
                    FROM content_items ci
                    WHERE ci.status = 'Published' AND ci.is_deleted = false
                    RETURNING id, content_item_id
                )
                INSERT INTO content_version_field_values (
                    id, content_version_id, field_id, "order", value_kind,
                    text_value, bool_value, media_asset_id, file_asset_id, child_content_item_id, json_value)
                SELECT
                    gen_random_uuid(),
                    inserted.id,
                    cfv.field_id,
                    cfv."order",
                    cfv.value_kind,
                    cfv.text_value,
                    cfv.bool_value,
                    cfv.media_asset_id,
                    cfv.file_asset_id,
                    cfv.child_content_item_id,
                    cfv.json_value
                FROM inserted
                JOIN content_field_values cfv ON cfv.content_item_id = inserted.content_item_id;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "content_version_field_values");

            migrationBuilder.DropTable(
                name: "content_versions");
        }
    }
}
