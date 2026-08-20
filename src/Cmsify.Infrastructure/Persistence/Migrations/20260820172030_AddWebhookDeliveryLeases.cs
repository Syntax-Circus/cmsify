using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Cmsify.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddWebhookDeliveryLeases : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_webhook_delivery_logs_is_delivered_is_failed_next_retry_at",
                table: "webhook_delivery_logs");

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "lease_expires_at",
                table: "webhook_delivery_logs",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "ix_webhook_delivery_logs_is_delivered_is_failed_next_retry_at_",
                table: "webhook_delivery_logs",
                columns: new[] { "is_delivered", "is_failed", "next_retry_at", "lease_expires_at" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_webhook_delivery_logs_is_delivered_is_failed_next_retry_at_",
                table: "webhook_delivery_logs");

            migrationBuilder.DropColumn(
                name: "lease_expires_at",
                table: "webhook_delivery_logs");

            migrationBuilder.CreateIndex(
                name: "ix_webhook_delivery_logs_is_delivered_is_failed_next_retry_at",
                table: "webhook_delivery_logs",
                columns: new[] { "is_delivered", "is_failed", "next_retry_at" });
        }
    }
}
