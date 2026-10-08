using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Cmsify.Infrastructure.Sqlite.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddEmbeddedContentReceipts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "embedded_content_receipts",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "TEXT", nullable: false),
                    workspace_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    kind = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    operation_key = table.Column<Guid>(type: "TEXT", nullable: false),
                    content_item_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    content_version_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    version_number = table.Column<int>(type: "INTEGER", nullable: false),
                    template_version_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    contract_fingerprint = table.Column<string>(type: "char(64)", nullable: false),
                    input_fingerprint = table.Column<string>(type: "char(64)", nullable: false),
                    committed_revision = table.Column<long>(type: "INTEGER", nullable: false),
                    actor_subject = table.Column<Guid>(type: "TEXT", nullable: false),
                    committed_at = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_embedded_content_receipts", x => x.id);
                    table.ForeignKey(
                        name: "fk_embedded_content_receipts_content_items_content_item_id",
                        column: x => x.content_item_id,
                        principalTable: "content_items",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_embedded_content_receipts_content_versions_content_version_id",
                        column: x => x.content_version_id,
                        principalTable: "content_versions",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_embedded_content_receipts_template_versions_template_version_id",
                        column: x => x.template_version_id,
                        principalTable: "template_versions",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_embedded_content_receipts_workspaces_workspace_id",
                        column: x => x.workspace_id,
                        principalTable: "workspaces",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_embedded_content_receipts_content_item_id",
                table: "embedded_content_receipts",
                column: "content_item_id");

            migrationBuilder.CreateIndex(
                name: "ix_embedded_content_receipts_content_version_id",
                table: "embedded_content_receipts",
                column: "content_version_id");

            migrationBuilder.CreateIndex(
                name: "ix_embedded_content_receipts_template_version_id",
                table: "embedded_content_receipts",
                column: "template_version_id");

            migrationBuilder.CreateIndex(
                name: "ix_embedded_content_receipts_workspace_id_kind_operation_key",
                table: "embedded_content_receipts",
                columns: new[] { "workspace_id", "kind", "operation_key" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "embedded_content_receipts");
        }
    }
}
