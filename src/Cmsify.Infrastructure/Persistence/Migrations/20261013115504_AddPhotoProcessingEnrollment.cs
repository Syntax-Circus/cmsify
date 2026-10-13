using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Cmsify.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddPhotoProcessingEnrollment : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "processing_enrollment_revision",
                table: "embedded_photo_originals",
                type: "integer",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "ix_embedded_photo_originals_asset_id_processing_enrollment_rev",
                table: "embedded_photo_originals",
                columns: new[] { "asset_id", "processing_enrollment_revision", "workspace_id" });

            migrationBuilder.AddCheckConstraint(
                name: "ck_photo_enrollment_revision",
                table: "embedded_photo_originals",
                sql: "processing_enrollment_revision IS NULL OR processing_enrollment_revision > 1");

            migrationBuilder.AddForeignKey(
                name: "fk_embedded_photo_originals_photo_revisions_asset_id_processin",
                table: "embedded_photo_originals",
                columns: new[] { "asset_id", "processing_enrollment_revision", "workspace_id" },
                principalTable: "embedded_photo_revisions",
                principalColumns: new[] { "asset_id", "revision", "workspace_id" },
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "fk_embedded_photo_originals_photo_revisions_asset_id_processin",
                table: "embedded_photo_originals");

            migrationBuilder.DropIndex(
                name: "ix_embedded_photo_originals_asset_id_processing_enrollment_rev",
                table: "embedded_photo_originals");

            migrationBuilder.DropCheckConstraint(
                name: "ck_photo_enrollment_revision",
                table: "embedded_photo_originals");

            migrationBuilder.DropColumn(
                name: "processing_enrollment_revision",
                table: "embedded_photo_originals");
        }
    }
}
