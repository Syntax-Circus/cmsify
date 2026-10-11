using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Cmsify.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddPhotoIntakeRetention : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddUniqueConstraint(
                name: "ak_embedded_photo_upload_receipts_id_asset_id_workspace_id",
                table: "embedded_photo_upload_receipts",
                columns: new[] { "id", "asset_id", "workspace_id" });

            migrationBuilder.CreateTable(
                name: "embedded_photo_intake_retentions",
                columns: table => new
                {
                    asset_id = table.Column<Guid>(type: "uuid", nullable: false),
                    workspace_id = table.Column<Guid>(type: "uuid", nullable: false),
                    upload_receipt_id = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_embedded_photo_intake_retentions", x => x.asset_id);
                    table.ForeignKey(
                        name: "fk_embedded_photo_intake_retentions_embedded_photo_upload_rece",
                        columns: x => new { x.upload_receipt_id, x.asset_id, x.workspace_id },
                        principalTable: "embedded_photo_upload_receipts",
                        principalColumns: new[] { "id", "asset_id", "workspace_id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_embedded_photo_intake_retentions_media_assets_asset_id_work",
                        columns: x => new { x.asset_id, x.workspace_id },
                        principalTable: "media_assets",
                        principalColumns: new[] { "id", "workspace_id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_embedded_photo_intake_retentions_asset_id_workspace_id",
                table: "embedded_photo_intake_retentions",
                columns: new[] { "asset_id", "workspace_id" });

            migrationBuilder.CreateIndex(
                name: "ix_embedded_photo_intake_retentions_upload_receipt_id",
                table: "embedded_photo_intake_retentions",
                column: "upload_receipt_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_embedded_photo_intake_retentions_upload_receipt_id_asset_id",
                table: "embedded_photo_intake_retentions",
                columns: new[] { "upload_receipt_id", "asset_id", "workspace_id" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "embedded_photo_intake_retentions");

            migrationBuilder.DropUniqueConstraint(
                name: "ak_embedded_photo_upload_receipts_id_asset_id_workspace_id",
                table: "embedded_photo_upload_receipts");
        }
    }
}
