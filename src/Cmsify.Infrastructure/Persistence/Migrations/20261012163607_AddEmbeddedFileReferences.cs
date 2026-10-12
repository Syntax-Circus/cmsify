using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Cmsify.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddEmbeddedFileReferences : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "retired_at",
                table: "embedded_file_upload_attempts",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddUniqueConstraint(
                name: "ak_embedded_file_upload_receipts_id_asset_id",
                table: "embedded_file_upload_receipts",
                columns: new[] { "id", "asset_id" });

            migrationBuilder.CreateTable(
                name: "embedded_file_reference_sets",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    workspace_id = table.Column<Guid>(type: "uuid", nullable: false),
                    consumer_namespace = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    reference_key = table.Column<Guid>(type: "uuid", nullable: false),
                    operation_key = table.Column<Guid>(type: "uuid", nullable: false),
                    actor_id = table.Column<Guid>(type: "uuid", nullable: false),
                    fingerprint = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    protection_revision = table.Column<int>(type: "integer", nullable: false),
                    state = table.Column<int>(type: "integer", nullable: false),
                    release_operation_key = table.Column<Guid>(type: "uuid", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_embedded_file_reference_sets", x => x.id);
                    table.ForeignKey(
                        name: "fk_embedded_file_reference_sets_workspaces_workspace_id",
                        column: x => x.workspace_id,
                        principalTable: "workspaces",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "embedded_file_reference_entries",
                columns: table => new
                {
                    reference_set_id = table.Column<Guid>(type: "uuid", nullable: false),
                    ordinal = table.Column<int>(type: "integer", nullable: false),
                    asset_id = table.Column<Guid>(type: "uuid", nullable: false),
                    receipt_id = table.Column<Guid>(type: "uuid", nullable: false),
                    length = table.Column<long>(type: "bigint", nullable: false),
                    source_sha256 = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_embedded_file_reference_entries", x => new { x.reference_set_id, x.ordinal });
                    table.ForeignKey(
                        name: "fk_embedded_file_reference_entries_embedded_file_reference_set",
                        column: x => x.reference_set_id,
                        principalTable: "embedded_file_reference_sets",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_embedded_file_reference_entries_embedded_file_upload_receip",
                        columns: x => new { x.receipt_id, x.asset_id },
                        principalTable: "embedded_file_upload_receipts",
                        principalColumns: new[] { "id", "asset_id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_embedded_file_reference_entries_receipt_id_asset_id",
                table: "embedded_file_reference_entries",
                columns: new[] { "receipt_id", "asset_id" });

            migrationBuilder.CreateIndex(
                name: "ix_embedded_file_reference_entries_reference_set_id_asset_id",
                table: "embedded_file_reference_entries",
                columns: new[] { "reference_set_id", "asset_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_embedded_file_reference_sets_workspace_id_consumer_namespac",
                table: "embedded_file_reference_sets",
                columns: new[] { "workspace_id", "consumer_namespace", "operation_key" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_embedded_file_reference_sets_workspace_id_consumer_namespac1",
                table: "embedded_file_reference_sets",
                columns: new[] { "workspace_id", "consumer_namespace", "reference_key" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_embedded_file_reference_sets_workspace_id_consumer_namespac2",
                table: "embedded_file_reference_sets",
                columns: new[] { "workspace_id", "consumer_namespace", "release_operation_key" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "embedded_file_reference_entries");

            migrationBuilder.DropTable(
                name: "embedded_file_reference_sets");

            migrationBuilder.DropUniqueConstraint(
                name: "ak_embedded_file_upload_receipts_id_asset_id",
                table: "embedded_file_upload_receipts");

            migrationBuilder.DropColumn(
                name: "retired_at",
                table: "embedded_file_upload_attempts");
        }
    }
}
