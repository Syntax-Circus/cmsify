using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Cmsify.Infrastructure.Sqlite.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddEmbeddedTemplateContracts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "embedded_template_registrations",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "TEXT", nullable: false),
                    workspace_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    contract_key = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    fingerprint = table.Column<string>(type: "char(64)", nullable: false),
                    template_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    template_version_id = table.Column<Guid>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_embedded_template_registrations", x => x.id);
                    table.ForeignKey(
                        name: "fk_embedded_template_registrations_template_versions_template_version_id",
                        column: x => x.template_version_id,
                        principalTable: "template_versions",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_embedded_template_registrations_templates_template_id",
                        column: x => x.template_id,
                        principalTable: "templates",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_embedded_template_registrations_workspaces_workspace_id",
                        column: x => x.workspace_id,
                        principalTable: "workspaces",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_embedded_template_registrations_template_id",
                table: "embedded_template_registrations",
                column: "template_id");

            migrationBuilder.CreateIndex(
                name: "ix_embedded_template_registrations_template_version_id",
                table: "embedded_template_registrations",
                column: "template_version_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_embedded_template_registrations_workspace_id_contract_key",
                table: "embedded_template_registrations",
                columns: new[] { "workspace_id", "contract_key" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "embedded_template_registrations");
        }
    }
}
