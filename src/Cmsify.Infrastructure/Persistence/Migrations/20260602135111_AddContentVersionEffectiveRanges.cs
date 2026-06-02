using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Cmsify.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddContentVersionEffectiveRanges : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_content_versions_content_item_id_status",
                table: "content_versions");

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "effective_end_at",
                table: "content_versions",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "effective_start_at",
                table: "content_versions",
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

            migrationBuilder.Sql("""
                CREATE TEMP TABLE cmsify_content_version_backfill (
                    content_item_id uuid PRIMARY KEY,
                    content_version_id uuid NOT NULL
                ) ON COMMIT DROP;

                INSERT INTO cmsify_content_version_backfill (content_item_id, content_version_id)
                SELECT content.id, gen_random_uuid()
                FROM content_items content
                WHERE content.status = 'Published'
                  AND content.is_deleted = false
                  AND NOT EXISTS (
                      SELECT 1
                      FROM content_versions version
                      WHERE version.content_item_id = content.id
                        AND version.status = 'Published'
                  );

                INSERT INTO content_versions (
                    id,
                    content_item_id,
                    workspace_id,
                    version_number,
                    status,
                    template_version_id,
                    slug,
                    locale_code,
                    translation_group_id,
                    tags,
                    effective_start_at,
                    effective_end_at,
                    published_at,
                    retired_at,
                    published_by_user_id,
                    rolled_back_from_version_number)
                SELECT
                    backfill.content_version_id,
                    content.id,
                    content.workspace_id,
                    COALESCE((
                        SELECT MAX(existing.version_number)
                        FROM content_versions existing
                        WHERE existing.content_item_id = content.id
                    ), 0) + 1,
                    'Published',
                    content.template_version_id,
                    content.slug,
                    content.locale_code,
                    content.translation_group_id,
                    COALESCE((
                        SELECT array_agg(tag.name ORDER BY tag.name)
                        FROM content_item_tags item_tag
                        JOIN tags tag ON tag.id = item_tag.tag_id
                        WHERE item_tag.content_item_id = content.id
                          AND tag.is_deleted = false
                    ), ARRAY[]::text[]),
                    NULL,
                    NULL,
                    COALESCE(content.published_at, content.updated_at),
                    NULL,
                    content.updated_by_user_id,
                    NULL
                FROM cmsify_content_version_backfill backfill
                JOIN content_items content ON content.id = backfill.content_item_id;

                INSERT INTO content_version_field_values (
                    id,
                    content_version_id,
                    field_id,
                    "order",
                    value_kind,
                    text_value,
                    bool_value,
                    media_asset_id,
                    file_asset_id,
                    child_content_item_id,
                    json_value)
                SELECT
                    gen_random_uuid(),
                    backfill.content_version_id,
                    value.field_id,
                    value."order",
                    value.value_kind,
                    value.text_value,
                    value.bool_value,
                    value.media_asset_id,
                    value.file_asset_id,
                    value.child_content_item_id,
                    value.json_value
                FROM cmsify_content_version_backfill backfill
                JOIN content_field_values value ON value.content_item_id = backfill.content_item_id;
                """);

            migrationBuilder.CreateIndex(
                name: "ix_content_versions_content_item_id",
                table: "content_versions",
                column: "content_item_id",
                unique: true,
                filter: "status = 'Published' AND effective_start_at IS NULL AND effective_end_at IS NULL");

            migrationBuilder.CreateIndex(
                name: "ix_content_versions_content_item_id_status_effective_start_at_",
                table: "content_versions",
                columns: new[] { "content_item_id", "status", "effective_start_at", "effective_end_at" });

            migrationBuilder.AddCheckConstraint(
                name: "ck_content_versions_effective_range",
                table: "content_versions",
                sql: "(effective_start_at IS NULL AND effective_end_at IS NULL) OR (effective_start_at IS NOT NULL AND effective_end_at IS NOT NULL AND effective_start_at < effective_end_at)");

            migrationBuilder.CreateIndex(
                name: "ix_content_items_status_publish_at_pending_effective_start_at_",
                table: "content_items",
                columns: new[] { "status", "publish_at", "pending_effective_start_at", "pending_effective_end_at" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_content_versions_content_item_id",
                table: "content_versions");

            migrationBuilder.DropIndex(
                name: "ix_content_versions_content_item_id_status_effective_start_at_",
                table: "content_versions");

            migrationBuilder.DropCheckConstraint(
                name: "ck_content_versions_effective_range",
                table: "content_versions");

            migrationBuilder.DropIndex(
                name: "ix_content_items_status_publish_at_pending_effective_start_at_",
                table: "content_items");

            migrationBuilder.DropColumn(
                name: "effective_end_at",
                table: "content_versions");

            migrationBuilder.DropColumn(
                name: "effective_start_at",
                table: "content_versions");

            migrationBuilder.DropColumn(
                name: "pending_effective_end_at",
                table: "content_items");

            migrationBuilder.DropColumn(
                name: "pending_effective_start_at",
                table: "content_items");

            migrationBuilder.CreateIndex(
                name: "ix_content_versions_content_item_id_status",
                table: "content_versions",
                columns: new[] { "content_item_id", "status" },
                unique: true,
                filter: "status = 'Published'");
        }
    }
}
