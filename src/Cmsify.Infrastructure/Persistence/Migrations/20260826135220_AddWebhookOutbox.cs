using System;
using System.Text.Json;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Cmsify.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddWebhookOutbox : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "webhook_outbox_events",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    event_type = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    workspace_id = table.Column<Guid>(type: "uuid", nullable: true),
                    entity_id = table.Column<Guid>(type: "uuid", nullable: false),
                    payload = table.Column<JsonElement>(type: "jsonb", nullable: false),
                    occurred_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    processed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    lease_owner = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    lease_token = table.Column<Guid>(type: "uuid", nullable: true),
                    lease_expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_webhook_outbox_events", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_webhook_outbox_events_event_type_workspace_id_occurred_at",
                table: "webhook_outbox_events",
                columns: new[] { "event_type", "workspace_id", "occurred_at" });

            migrationBuilder.CreateIndex(
                name: "ix_webhook_outbox_events_processed_at_lease_expires_at",
                table: "webhook_outbox_events",
                columns: new[] { "processed_at", "lease_expires_at" });

            migrationBuilder.AddColumn<Guid>(name: "webhook_event_id", table: "webhook_delivery_logs", type: "uuid", nullable: true);
            // Historical delivery logs predate durable outbox records.  Give each
            // one a durable, independent identity before enforcing the current
            // non-null event/endpoint uniqueness invariant.
            migrationBuilder.Sql("UPDATE webhook_delivery_logs SET webhook_event_id = gen_random_uuid() WHERE webhook_event_id IS NULL;");
            migrationBuilder.AlterColumn<Guid>(
                name: "webhook_event_id",
                table: "webhook_delivery_logs",
                type: "uuid",
                nullable: false,
                oldClrType: typeof(Guid),
                oldType: "uuid",
                oldNullable: true);
            migrationBuilder.AddColumn<string>(name: "lease_owner", table: "webhook_delivery_logs", type: "character varying(200)", maxLength: 200, nullable: true);
            migrationBuilder.AddColumn<Guid>(name: "lease_token", table: "webhook_delivery_logs", type: "uuid", nullable: true);
            migrationBuilder.AddColumn<string>(name: "last_error", table: "webhook_delivery_logs", type: "character varying(4000)", maxLength: 4000, nullable: true);
            migrationBuilder.AddColumn<bool>(name: "is_dead_letter", table: "webhook_delivery_logs", type: "boolean", nullable: false, defaultValue: false);
            migrationBuilder.AddColumn<DateTimeOffset>(name: "dead_lettered_at", table: "webhook_delivery_logs", type: "timestamp with time zone", nullable: true);
            migrationBuilder.CreateIndex(name: "ix_webhook_delivery_logs_webhook_event_id_webhook_endpoint_id", table: "webhook_delivery_logs", columns: new[] { "webhook_event_id", "webhook_endpoint_id" }, unique: true);
            migrationBuilder.AddColumn<string>(name: "publish_lease_owner", table: "content_items", type: "character varying(200)", maxLength: 200, nullable: true);
            migrationBuilder.AddColumn<Guid>(name: "publish_lease_token", table: "content_items", type: "uuid", nullable: true);
            migrationBuilder.AddColumn<DateTimeOffset>(name: "publish_lease_expires_at", table: "content_items", type: "timestamp with time zone", nullable: true);
            migrationBuilder.CreateIndex(name: "ix_content_items_status_publish_at_publish_lease_expires_at", table: "content_items", columns: new[] { "status", "publish_at", "publish_lease_expires_at" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(name: "ix_webhook_delivery_logs_webhook_event_id_webhook_endpoint_id", table: "webhook_delivery_logs");
            migrationBuilder.DropColumn(name: "webhook_event_id", table: "webhook_delivery_logs");
            migrationBuilder.DropColumn(name: "lease_owner", table: "webhook_delivery_logs");
            migrationBuilder.DropColumn(name: "lease_token", table: "webhook_delivery_logs");
            migrationBuilder.DropColumn(name: "last_error", table: "webhook_delivery_logs");
            migrationBuilder.DropColumn(name: "is_dead_letter", table: "webhook_delivery_logs");
            migrationBuilder.DropColumn(name: "dead_lettered_at", table: "webhook_delivery_logs");
            migrationBuilder.DropIndex(name: "ix_content_items_status_publish_at_publish_lease_expires_at", table: "content_items");
            migrationBuilder.DropColumn(name: "publish_lease_owner", table: "content_items");
            migrationBuilder.DropColumn(name: "publish_lease_token", table: "content_items");
            migrationBuilder.DropColumn(name: "publish_lease_expires_at", table: "content_items");
            migrationBuilder.DropTable(
                name: "webhook_outbox_events");
        }
    }
}
