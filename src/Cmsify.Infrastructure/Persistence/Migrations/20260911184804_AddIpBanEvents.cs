using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Cmsify.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddIpBanEvents : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ip_ban_events",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    ip_address = table.Column<string>(type: "character varying(45)", maxLength: 45, nullable: false),
                    rejection_count = table.Column<int>(type: "integer", nullable: false),
                    banned_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    banned_until = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    request_path = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_ip_ban_events", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_ip_ban_events_banned_at",
                table: "ip_ban_events",
                column: "banned_at");

            migrationBuilder.CreateIndex(
                name: "ix_ip_ban_events_ip_address",
                table: "ip_ban_events",
                column: "ip_address");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ip_ban_events");
        }
    }
}
