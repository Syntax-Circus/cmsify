using System;
using System.Text.Json;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Cmsify.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class UnifyContentVersionLifecycle : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.RenameColumn(
                name: "retired_at",
                table: "content_versions",
                newName: "archived_at");

            migrationBuilder.AlterColumn<DateTimeOffset>(
                name: "published_at",
                table: "content_versions",
                type: "timestamp with time zone",
                nullable: true,
                oldClrType: typeof(DateTimeOffset),
                oldType: "timestamp with time zone");

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "publish_lease_expires_at",
                table: "content_versions",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "created_at",
                table: "content_versions",
                type: "timestamp with time zone",
                nullable: false,
                defaultValue: new DateTimeOffset(new DateTime(1, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 0, 0, 0, 0)));

            migrationBuilder.AddColumn<Guid>(
                name: "created_by_user_id",
                table: "content_versions",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "publish_at",
                table: "content_versions",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "publish_lease_owner",
                table: "content_versions",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "publish_lease_token",
                table: "content_versions",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "updated_at",
                table: "content_versions",
                type: "timestamp with time zone",
                nullable: false,
                defaultValue: new DateTimeOffset(new DateTime(1, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 0, 0, 0, 0)));

            migrationBuilder.AddColumn<Guid>(
                name: "updated_by_user_id",
                table: "content_versions",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "ix_content_versions_status_publish_at",
                table: "content_versions",
                columns: new[] { "status", "publish_at" });

            migrationBuilder.CreateIndex(
                name: "ix_content_versions_status_publish_at_publish_lease_expires_at",
                table: "content_versions",
                columns: new[] { "status", "publish_at", "publish_lease_expires_at" });

            migrationBuilder.Sql("""
                UPDATE content_versions SET status = 'Archived' WHERE status = 'Retired';

                CREATE TEMP TABLE backfill_version_map (
                    content_item_id uuid PRIMARY KEY,
                    new_version_id uuid NOT NULL,
                    new_version_number int NOT NULL
                ) ON COMMIT DROP;

                INSERT INTO backfill_version_map (content_item_id, new_version_id, new_version_number)
                SELECT
                    ci.id,
                    gen_random_uuid(),
                    COALESCE((SELECT MAX(cv.version_number) FROM content_versions cv WHERE cv.content_item_id = ci.id), 0) + 1
                FROM content_items ci
                WHERE ci.status <> 'Published' AND NOT ci.is_deleted;

                INSERT INTO content_versions (
                    id, content_item_id, workspace_id, version_number, status, template_version_id,
                    slug, locale_code, translation_group_id, tags, effective_start_at, effective_end_at,
                    published_at, archived_at, published_by_user_id, rolled_back_from_version_number,
                    publish_at, publish_lease_owner, publish_lease_token, publish_lease_expires_at,
                    created_at, updated_at, created_by_user_id, updated_by_user_id
                )
                SELECT
                    m.new_version_id, ci.id, ci.workspace_id, m.new_version_number, ci.status, ci.template_version_id,
                    ci.slug, ci.locale_code, ci.translation_group_id,
                    COALESCE((
                        SELECT array_agg(t.name ORDER BY t.name)
                        FROM content_item_tags cit
                        JOIN tags t ON t.id = cit.tag_id
                        WHERE cit.content_item_id = ci.id
                    ), ARRAY[]::text[]),
                    NULL, NULL,
                    NULL, NULL, NULL, NULL,
                    CASE WHEN ci.status = 'Approved' THEN ci.publish_at ELSE NULL END,
                    NULL, NULL, NULL,
                    ci.created_at, ci.updated_at, ci.created_by_user_id, ci.updated_by_user_id
                FROM content_items ci
                JOIN backfill_version_map m ON m.content_item_id = ci.id;

                INSERT INTO content_version_field_values (
                    id, content_version_id, field_id, "order", value_kind, text_value, display_label,
                    bool_value, media_asset_id, file_asset_id, child_content_item_id, json_value
                )
                SELECT
                    gen_random_uuid(), m.new_version_id, cfv.field_id, cfv."order", cfv.value_kind, cfv.text_value, NULL,
                    cfv.bool_value, cfv.media_asset_id, cfv.file_asset_id, cfv.child_content_item_id, cfv.json_value
                FROM content_field_values cfv
                JOIN backfill_version_map m ON m.content_item_id = cfv.content_item_id;

                -- content_versions rows that already existed before this migration got created_at /
                -- updated_at from the columns' DateTimeOffset.MinValue default (which Npgsql stores
                -- as '-infinity'); the backfill INSERT above only sets them on rows it materialises.
                -- Seed them from the row's own publication time where there is one, and from the
                -- migration time otherwise. The <= comparison covers both '-infinity' and a literal
                -- '0001-01-01' should the default ever be stored that way instead.
                UPDATE content_versions
                SET created_at = COALESCE(published_at, CURRENT_TIMESTAMP),
                    updated_at = COALESCE(published_at, CURRENT_TIMESTAMP)
                WHERE created_at <= TIMESTAMPTZ '0001-01-01 00:00:00+00';
                """);

            migrationBuilder.DropTable(
                name: "content_field_values");

            migrationBuilder.DropIndex(
                name: "ix_content_items_status_publish_at",
                table: "content_items");

            migrationBuilder.DropIndex(
                name: "ix_content_items_status_publish_at_pending_effective_start_at_",
                table: "content_items");

            migrationBuilder.DropIndex(
                name: "ix_content_items_status_publish_at_publish_lease_expires_at",
                table: "content_items");

            migrationBuilder.DropColumn(
                name: "archived_at",
                table: "content_items");

            migrationBuilder.DropColumn(
                name: "pending_effective_end_at",
                table: "content_items");

            migrationBuilder.DropColumn(
                name: "pending_effective_start_at",
                table: "content_items");

            migrationBuilder.DropColumn(
                name: "publish_at",
                table: "content_items");

            migrationBuilder.DropColumn(
                name: "publish_lease_expires_at",
                table: "content_items");

            migrationBuilder.DropColumn(
                name: "publish_lease_owner",
                table: "content_items");

            migrationBuilder.DropColumn(
                name: "publish_lease_token",
                table: "content_items");

            migrationBuilder.DropColumn(
                name: "published_at",
                table: "content_items");

            migrationBuilder.DropColumn(
                name: "status",
                table: "content_items");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_content_versions_status_publish_at",
                table: "content_versions");

            migrationBuilder.DropIndex(
                name: "ix_content_versions_status_publish_at_publish_lease_expires_at",
                table: "content_versions");

            migrationBuilder.DropColumn(
                name: "publish_lease_expires_at",
                table: "content_versions");

            migrationBuilder.DropColumn(
                name: "created_at",
                table: "content_versions");

            migrationBuilder.DropColumn(
                name: "created_by_user_id",
                table: "content_versions");

            migrationBuilder.DropColumn(
                name: "publish_at",
                table: "content_versions");

            migrationBuilder.DropColumn(
                name: "publish_lease_owner",
                table: "content_versions");

            migrationBuilder.DropColumn(
                name: "publish_lease_token",
                table: "content_versions");

            migrationBuilder.DropColumn(
                name: "updated_at",
                table: "content_versions");

            migrationBuilder.DropColumn(
                name: "updated_by_user_id",
                table: "content_versions");

            migrationBuilder.RenameColumn(
                name: "archived_at",
                table: "content_versions",
                newName: "retired_at");

            migrationBuilder.AlterColumn<DateTimeOffset>(
                name: "published_at",
                table: "content_versions",
                type: "timestamp with time zone",
                nullable: false,
                defaultValue: new DateTimeOffset(new DateTime(1, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 0, 0, 0, 0)),
                oldClrType: typeof(DateTimeOffset),
                oldType: "timestamp with time zone",
                oldNullable: true);

            migrationBuilder.Sql("-- Note: Down() restores dropped columns/table structure but does not reverse the data backfill performed in Up().");

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "archived_at",
                table: "content_items",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "pending_effective_end_at",
                table: "content_items",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "pending_effective_start_at",
                table: "content_items",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "publish_at",
                table: "content_items",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "publish_lease_expires_at",
                table: "content_items",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "publish_lease_owner",
                table: "content_items",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "publish_lease_token",
                table: "content_items",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "published_at",
                table: "content_items",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "status",
                table: "content_items",
                type: "character varying(50)",
                maxLength: 50,
                nullable: false,
                defaultValue: "");

            migrationBuilder.CreateTable(
                name: "content_field_values",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    bool_value = table.Column<bool>(type: "boolean", nullable: true),
                    child_content_item_id = table.Column<Guid>(type: "uuid", nullable: true),
                    content_item_id = table.Column<Guid>(type: "uuid", nullable: false),
                    field_id = table.Column<Guid>(type: "uuid", nullable: false),
                    file_asset_id = table.Column<Guid>(type: "uuid", nullable: true),
                    json_value = table.Column<JsonElement>(type: "jsonb", nullable: true),
                    media_asset_id = table.Column<Guid>(type: "uuid", nullable: true),
                    order = table.Column<int>(type: "integer", nullable: false),
                    text_value = table.Column<string>(type: "text", nullable: true),
                    value_kind = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_content_field_values", x => x.id);
                    table.ForeignKey(
                        name: "fk_content_field_values_content_items_child_content_item_id",
                        column: x => x.child_content_item_id,
                        principalTable: "content_items",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_content_field_values_content_items_content_item_id",
                        column: x => x.content_item_id,
                        principalTable: "content_items",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_content_field_values_media_assets_file_asset_id",
                        column: x => x.file_asset_id,
                        principalTable: "media_assets",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_content_field_values_media_assets_media_asset_id",
                        column: x => x.media_asset_id,
                        principalTable: "media_assets",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_content_field_values_template_fields_field_id",
                        column: x => x.field_id,
                        principalTable: "template_fields",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_content_items_status_publish_at",
                table: "content_items",
                columns: new[] { "status", "publish_at" });

            migrationBuilder.CreateIndex(
                name: "ix_content_items_status_publish_at_pending_effective_start_at_",
                table: "content_items",
                columns: new[] { "status", "publish_at", "pending_effective_start_at", "pending_effective_end_at" });

            migrationBuilder.CreateIndex(
                name: "ix_content_items_status_publish_at_publish_lease_expires_at",
                table: "content_items",
                columns: new[] { "status", "publish_at", "publish_lease_expires_at" });

            migrationBuilder.CreateIndex(
                name: "ix_content_field_values_child_content_item_id",
                table: "content_field_values",
                column: "child_content_item_id");

            migrationBuilder.CreateIndex(
                name: "ix_content_field_values_content_item_id_field_id_order",
                table: "content_field_values",
                columns: new[] { "content_item_id", "field_id", "order" });

            migrationBuilder.CreateIndex(
                name: "ix_content_field_values_field_id",
                table: "content_field_values",
                column: "field_id");

            migrationBuilder.CreateIndex(
                name: "ix_content_field_values_file_asset_id",
                table: "content_field_values",
                column: "file_asset_id");

            migrationBuilder.CreateIndex(
                name: "ix_content_field_values_media_asset_id",
                table: "content_field_values",
                column: "media_asset_id");
        }
    }
}
