using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Cmsify.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddMediaLifecycleReconciliation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "blob_state",
                table: "media_assets",
                type: "character varying(50)",
                maxLength: 50,
                nullable: false,
                defaultValue: "PendingUpload");

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "blob_state_changed_at",
                table: "media_assets",
                type: "timestamp with time zone",
                nullable: false,
                defaultValueSql: "CURRENT_TIMESTAMP");

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "blob_verified_at",
                table: "media_assets",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "deletion_requested_at",
                table: "media_assets",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "missing_detected_at",
                table: "media_assets",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "purge_after",
                table: "media_assets",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "upload_completed_at",
                table: "media_assets",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "upload_failed_at",
                table: "media_assets",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "media_deletion_intents",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    media_asset_id = table.Column<Guid>(type: "uuid", nullable: true),
                    provider = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    storage_key = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false),
                    reason = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    not_before = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    next_attempt_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    attempt_count = table.Column<int>(type: "integer", nullable: false),
                    last_error = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    completed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    lease_owner = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    lease_token = table.Column<Guid>(type: "uuid", nullable: true),
                    lease_expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_media_deletion_intents", x => x.id);
                    table.ForeignKey(
                        name: "fk_media_deletion_intents_media_assets_media_asset_id",
                        column: x => x.media_asset_id,
                        principalTable: "media_assets",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "media_reconciliation_checkpoints",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    provider = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    prefix = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false),
                    after_key = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    lease_owner = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    lease_token = table.Column<Guid>(type: "uuid", nullable: true),
                    lease_expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    last_scan_started_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    last_scan_completed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_media_reconciliation_checkpoints", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_media_assets_blob_state_blob_state_changed_at",
                table: "media_assets",
                columns: new[] { "blob_state", "blob_state_changed_at" });

            migrationBuilder.CreateIndex(
                name: "ix_media_deletion_intents_completed_at_next_attempt_at_lease_e",
                table: "media_deletion_intents",
                columns: new[] { "completed_at", "next_attempt_at", "lease_expires_at" });

            migrationBuilder.CreateIndex(
                name: "ix_media_deletion_intents_media_asset_id",
                table: "media_deletion_intents",
                column: "media_asset_id");

            migrationBuilder.CreateIndex(
                name: "ix_media_deletion_intents_provider_storage_key",
                table: "media_deletion_intents",
                columns: new[] { "provider", "storage_key" },
                unique: true,
                filter: "completed_at IS NULL");

            migrationBuilder.CreateIndex(
                name: "ix_media_reconciliation_checkpoints_provider_prefix",
                table: "media_reconciliation_checkpoints",
                columns: new[] { "provider", "prefix" },
                unique: true);

            migrationBuilder.Sql("""
                UPDATE media_assets
                SET blob_state = CASE WHEN is_deleted THEN 'DeletePending' ELSE 'Available' END,
                    blob_state_changed_at = CURRENT_TIMESTAMP,
                    upload_completed_at = CASE WHEN is_deleted THEN NULL ELSE COALESCE(updated_at, created_at) END,
                    blob_verified_at = CASE WHEN is_deleted THEN NULL ELSE CURRENT_TIMESTAMP END,
                    deletion_requested_at = CASE WHEN is_deleted THEN COALESCE(deleted_at, CURRENT_TIMESTAMP) ELSE NULL END,
                    purge_after = CASE WHEN is_deleted THEN CURRENT_TIMESTAMP + INTERVAL '30 days' ELSE NULL END;

                INSERT INTO media_deletion_intents
                    (id, media_asset_id, provider, storage_key, reason, not_before, next_attempt_at, attempt_count, created_at)
                SELECT gen_random_uuid(), id, storage_provider, storage_key, 'migration_deleted',
                       CURRENT_TIMESTAMP + INTERVAL '30 days', CURRENT_TIMESTAMP + INTERVAL '30 days', 0, CURRENT_TIMESTAMP
                FROM media_assets
                WHERE is_deleted;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "media_deletion_intents");

            migrationBuilder.DropTable(
                name: "media_reconciliation_checkpoints");

            migrationBuilder.DropIndex(
                name: "ix_media_assets_blob_state_blob_state_changed_at",
                table: "media_assets");

            migrationBuilder.DropColumn(
                name: "blob_state",
                table: "media_assets");

            migrationBuilder.DropColumn(
                name: "blob_state_changed_at",
                table: "media_assets");

            migrationBuilder.DropColumn(
                name: "blob_verified_at",
                table: "media_assets");

            migrationBuilder.DropColumn(
                name: "deletion_requested_at",
                table: "media_assets");

            migrationBuilder.DropColumn(
                name: "missing_detected_at",
                table: "media_assets");

            migrationBuilder.DropColumn(
                name: "purge_after",
                table: "media_assets");

            migrationBuilder.DropColumn(
                name: "upload_completed_at",
                table: "media_assets");

            migrationBuilder.DropColumn(
                name: "upload_failed_at",
                table: "media_assets");
        }
    }
}
