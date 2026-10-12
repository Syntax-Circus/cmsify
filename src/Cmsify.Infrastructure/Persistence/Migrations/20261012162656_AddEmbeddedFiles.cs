using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Cmsify.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddEmbeddedFiles : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "embedded_file_upload_sessions",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    workspace_id = table.Column<Guid>(type: "uuid", nullable: false),
                    asset_id = table.Column<Guid>(type: "uuid", nullable: false),
                    actor_id = table.Column<Guid>(type: "uuid", nullable: false),
                    operation_key = table.Column<Guid>(type: "uuid", nullable: false),
                    consumer_namespace = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    fingerprint = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    display_file_name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    declared_length = table.Column<long>(type: "bigint", nullable: false),
                    expected_sha256 = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    state = table.Column<int>(type: "integer", nullable: false),
                    write_operation_key = table.Column<Guid>(type: "uuid", nullable: true),
                    lease_token = table.Column<Guid>(type: "uuid", nullable: true),
                    lease_until = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    failure_code = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_embedded_file_upload_sessions", x => x.id);
                    table.UniqueConstraint("ak_embedded_file_upload_sessions_id_workspace_id_asset_id", x => new { x.id, x.workspace_id, x.asset_id });
                    table.ForeignKey(
                        name: "fk_embedded_file_upload_sessions_media_assets_asset_id",
                        column: x => x.asset_id,
                        principalTable: "media_assets",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_embedded_file_upload_sessions_workspaces_workspace_id",
                        column: x => x.workspace_id,
                        principalTable: "workspaces",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "embedded_file_upload_attempts",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    session_id = table.Column<Guid>(type: "uuid", nullable: false),
                    storage_provider = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    storage_key = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_embedded_file_upload_attempts", x => x.id);
                    table.ForeignKey(
                        name: "fk_embedded_file_upload_attempts_embedded_file_upload_sessions",
                        column: x => x.session_id,
                        principalTable: "embedded_file_upload_sessions",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "embedded_file_upload_receipts",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    session_id = table.Column<Guid>(type: "uuid", nullable: false),
                    workspace_id = table.Column<Guid>(type: "uuid", nullable: false),
                    asset_id = table.Column<Guid>(type: "uuid", nullable: false),
                    consumer_namespace = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    length = table.Column<long>(type: "bigint", nullable: false),
                    source_sha256 = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    storage_provider = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    storage_key = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_embedded_file_upload_receipts", x => x.id);
                    table.ForeignKey(
                        name: "fk_embedded_file_upload_receipts_embedded_file_upload_sessions",
                        columns: x => new { x.session_id, x.workspace_id, x.asset_id },
                        principalTable: "embedded_file_upload_sessions",
                        principalColumns: new[] { "id", "workspace_id", "asset_id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_embedded_file_upload_attempts_session_id",
                table: "embedded_file_upload_attempts",
                column: "session_id");

            migrationBuilder.CreateIndex(
                name: "ix_embedded_file_upload_attempts_storage_provider_storage_key",
                table: "embedded_file_upload_attempts",
                columns: new[] { "storage_provider", "storage_key" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_embedded_file_upload_receipts_asset_id",
                table: "embedded_file_upload_receipts",
                column: "asset_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_embedded_file_upload_receipts_session_id",
                table: "embedded_file_upload_receipts",
                column: "session_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_embedded_file_upload_receipts_session_id_workspace_id_asset",
                table: "embedded_file_upload_receipts",
                columns: new[] { "session_id", "workspace_id", "asset_id" });

            migrationBuilder.CreateIndex(
                name: "ix_embedded_file_upload_sessions_asset_id",
                table: "embedded_file_upload_sessions",
                column: "asset_id");

            migrationBuilder.CreateIndex(
                name: "ix_embedded_file_upload_sessions_workspace_id_asset_id",
                table: "embedded_file_upload_sessions",
                columns: new[] { "workspace_id", "asset_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_embedded_file_upload_sessions_workspace_id_consumer_namespa",
                table: "embedded_file_upload_sessions",
                columns: new[] { "workspace_id", "consumer_namespace", "operation_key" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "embedded_file_upload_attempts");

            migrationBuilder.DropTable(
                name: "embedded_file_upload_receipts");

            migrationBuilder.DropTable(
                name: "embedded_file_upload_sessions");
        }
    }
}
