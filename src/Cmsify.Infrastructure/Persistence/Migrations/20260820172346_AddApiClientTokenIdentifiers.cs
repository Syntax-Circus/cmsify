using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Cmsify.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddApiClientTokenIdentifiers : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "token_identifier",
                table: "api_clients",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "ix_api_clients_token_identifier",
                table: "api_clients",
                column: "token_identifier",
                unique: true,
                filter: "token_identifier IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_api_clients_token_identifier",
                table: "api_clients");

            migrationBuilder.DropColumn(
                name: "token_identifier",
                table: "api_clients");
        }
    }
}
