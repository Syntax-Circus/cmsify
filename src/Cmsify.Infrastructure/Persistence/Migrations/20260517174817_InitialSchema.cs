using System;
using System.Text.Json;
using Microsoft.EntityFrameworkCore.Migrations;
using NpgsqlTypes;

#nullable disable

namespace Cmsify.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class InitialSchema : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterDatabase()
                .Annotation("Npgsql:PostgresExtension:pgcrypto", ",,");

            migrationBuilder.CreateTable(
                name: "audit_logs",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    entity_type = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    entity_id = table.Column<Guid>(type: "uuid", nullable: false),
                    action = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    actor_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    actor_api_client_id = table.Column<Guid>(type: "uuid", nullable: true),
                    timestamp = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    change_delta = table.Column<JsonElement>(type: "jsonb", nullable: true),
                    workspace_id = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_audit_logs", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "users",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    email = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: false),
                    display_name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    password_hash = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    role = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    must_change_password = table.Column<bool>(type: "boolean", nullable: false),
                    time_zone_id = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    is_active = table.Column<bool>(type: "boolean", nullable: false),
                    last_login_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    is_deleted = table.Column<bool>(type: "boolean", nullable: false, defaultValue: false),
                    deleted_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    deleted_by_user_id = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_users", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "workspaces",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    slug = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    description = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    is_deleted = table.Column<bool>(type: "boolean", nullable: false, defaultValue: false),
                    deleted_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    deleted_by_user_id = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_workspaces", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "api_clients",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    description = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    token_hash = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    role = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    workspace_id = table.Column<Guid>(type: "uuid", nullable: true),
                    is_active = table.Column<bool>(type: "boolean", nullable: false),
                    expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    created_by_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    last_used_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    is_deleted = table.Column<bool>(type: "boolean", nullable: false, defaultValue: false),
                    deleted_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    deleted_by_user_id = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_api_clients", x => x.id);
                    table.ForeignKey(
                        name: "fk_api_clients_users_created_by_user_id",
                        column: x => x.created_by_user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_api_clients_workspaces_workspace_id",
                        column: x => x.workspace_id,
                        principalTable: "workspaces",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "media_assets",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    workspace_id = table.Column<Guid>(type: "uuid", nullable: false),
                    file_name = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    mime_type = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    size_bytes = table.Column<long>(type: "bigint", nullable: false),
                    storage_key = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false),
                    storage_provider = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    alt_text = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    created_by_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    is_deleted = table.Column<bool>(type: "boolean", nullable: false, defaultValue: false),
                    deleted_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    deleted_by_user_id = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_media_assets", x => x.id);
                    table.ForeignKey(
                        name: "fk_media_assets_workspaces_workspace_id",
                        column: x => x.workspace_id,
                        principalTable: "workspaces",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "tags",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    workspace_id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    is_deleted = table.Column<bool>(type: "boolean", nullable: false, defaultValue: false),
                    deleted_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    deleted_by_user_id = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_tags", x => x.id);
                    table.ForeignKey(
                        name: "fk_tags_workspaces_workspace_id",
                        column: x => x.workspace_id,
                        principalTable: "workspaces",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "webhook_endpoints",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    workspace_id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    url = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: false),
                    secret = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false),
                    is_active = table.Column<bool>(type: "boolean", nullable: false),
                    created_by_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    is_deleted = table.Column<bool>(type: "boolean", nullable: false, defaultValue: false),
                    deleted_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    deleted_by_user_id = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_webhook_endpoints", x => x.id);
                    table.ForeignKey(
                        name: "fk_webhook_endpoints_users_created_by_user_id",
                        column: x => x.created_by_user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_webhook_endpoints_workspaces_workspace_id",
                        column: x => x.workspace_id,
                        principalTable: "workspaces",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "webhook_delivery_logs",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    webhook_endpoint_id = table.Column<Guid>(type: "uuid", nullable: false),
                    event_type = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    payload = table.Column<JsonElement>(type: "jsonb", nullable: false),
                    attempt_count = table.Column<int>(type: "integer", nullable: false),
                    last_attempt_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    next_retry_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    status_code = table.Column<int>(type: "integer", nullable: true),
                    is_delivered = table.Column<bool>(type: "boolean", nullable: false),
                    is_failed = table.Column<bool>(type: "boolean", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_webhook_delivery_logs", x => x.id);
                    table.ForeignKey(
                        name: "fk_webhook_delivery_logs_webhook_endpoints_webhook_endpoint_id",
                        column: x => x.webhook_endpoint_id,
                        principalTable: "webhook_endpoints",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "webhook_subscriptions",
                columns: table => new
                {
                    webhook_endpoint_id = table.Column<Guid>(type: "uuid", nullable: false),
                    event_type = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_webhook_subscriptions", x => new { x.webhook_endpoint_id, x.event_type });
                    table.ForeignKey(
                        name: "fk_webhook_subscriptions_webhook_endpoints_webhook_endpoint_id",
                        column: x => x.webhook_endpoint_id,
                        principalTable: "webhook_endpoints",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "content_field_values",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    content_item_id = table.Column<Guid>(type: "uuid", nullable: false),
                    field_id = table.Column<Guid>(type: "uuid", nullable: false),
                    order = table.Column<int>(type: "integer", nullable: false),
                    value_kind = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    text_value = table.Column<string>(type: "text", nullable: true),
                    bool_value = table.Column<bool>(type: "boolean", nullable: true),
                    media_asset_id = table.Column<Guid>(type: "uuid", nullable: true),
                    file_asset_id = table.Column<Guid>(type: "uuid", nullable: true),
                    child_content_item_id = table.Column<Guid>(type: "uuid", nullable: true),
                    json_value = table.Column<JsonElement>(type: "jsonb", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_content_field_values", x => x.id);
                    table.ForeignKey(
                        name: "fk_content_field_values_media_assets_file_asset_id",
                        column: x => x.file_asset_id,
                        principalTable: "media_assets",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_content_field_values_media_assets_media_asset_id",
                        column: x => x.media_asset_id,
                        principalTable: "media_assets",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "content_item_tags",
                columns: table => new
                {
                    content_item_id = table.Column<Guid>(type: "uuid", nullable: false),
                    tag_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_content_item_tags", x => new { x.content_item_id, x.tag_id });
                    table.ForeignKey(
                        name: "fk_content_item_tags_tags_tag_id",
                        column: x => x.tag_id,
                        principalTable: "tags",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "content_items",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    workspace_id = table.Column<Guid>(type: "uuid", nullable: false),
                    template_version_id = table.Column<Guid>(type: "uuid", nullable: false),
                    status = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    slug = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    locale_code = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    translation_group_id = table.Column<Guid>(type: "uuid", nullable: true),
                    publish_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    published_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    archived_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    search_vector = table.Column<NpgsqlTsVector>(type: "tsvector", nullable: true),
                    created_by_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    updated_by_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    is_deleted = table.Column<bool>(type: "boolean", nullable: false, defaultValue: false),
                    deleted_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    deleted_by_user_id = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_content_items", x => x.id);
                    table.ForeignKey(
                        name: "fk_content_items_workspaces_workspace_id",
                        column: x => x.workspace_id,
                        principalTable: "workspaces",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "template_field_allowed_types",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    field_id = table.Column<Guid>(type: "uuid", nullable: false),
                    primitive_type = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    allowed_template_id = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_template_field_allowed_types", x => x.id);
                    table.CheckConstraint("ck_template_field_allowed_types_type_shape", "(primitive_type IS NOT NULL AND allowed_template_id IS NULL) OR (primitive_type IS NULL AND allowed_template_id IS NOT NULL)");
                });

            migrationBuilder.CreateTable(
                name: "template_fields",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    template_version_id = table.Column<Guid>(type: "uuid", nullable: false),
                    section_id = table.Column<Guid>(type: "uuid", nullable: true),
                    key = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    label = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    help_text = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    order = table.Column<int>(type: "integer", nullable: false),
                    is_required = table.Column<bool>(type: "boolean", nullable: false),
                    min_occurrences = table.Column<int>(type: "integer", nullable: false),
                    max_occurrences = table.Column<int>(type: "integer", nullable: true),
                    is_open = table.Column<bool>(type: "boolean", nullable: false),
                    composition_mode = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    primitive_type = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    template_id = table.Column<Guid>(type: "uuid", nullable: true),
                    field_config = table.Column<JsonElement>(type: "jsonb", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_template_fields", x => x.id);
                    table.CheckConstraint("ck_template_fields_type_shape", "(is_open = true AND primitive_type IS NULL AND template_id IS NULL) OR (is_open = false AND ((primitive_type IS NOT NULL AND template_id IS NULL) OR (primitive_type IS NULL AND template_id IS NOT NULL)))");
                });

            migrationBuilder.CreateTable(
                name: "template_sections",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    template_version_id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    description = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    order = table.Column<int>(type: "integer", nullable: false),
                    is_collapsible = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_template_sections", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "template_versions",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    template_id = table.Column<Guid>(type: "uuid", nullable: false),
                    version_number = table.Column<int>(type: "integer", nullable: false),
                    status = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    published_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    created_by_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    notes = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    is_deleted = table.Column<bool>(type: "boolean", nullable: false, defaultValue: false),
                    deleted_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    deleted_by_user_id = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_template_versions", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "templates",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    workspace_id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    slug = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    description = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    package_namespace = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    package_id = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    package_version = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    title_field_key = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    current_version_id = table.Column<Guid>(type: "uuid", nullable: true),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    is_deleted = table.Column<bool>(type: "boolean", nullable: false, defaultValue: false),
                    deleted_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    deleted_by_user_id = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_templates", x => x.id);
                    table.ForeignKey(
                        name: "fk_templates_template_versions_current_version_id",
                        column: x => x.current_version_id,
                        principalTable: "template_versions",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "fk_templates_workspaces_workspace_id",
                        column: x => x.workspace_id,
                        principalTable: "workspaces",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_api_clients_created_by_user_id",
                table: "api_clients",
                column: "created_by_user_id");

            migrationBuilder.CreateIndex(
                name: "ix_api_clients_workspace_id",
                table: "api_clients",
                column: "workspace_id");

            migrationBuilder.CreateIndex(
                name: "ix_audit_logs_entity_type_entity_id",
                table: "audit_logs",
                columns: new[] { "entity_type", "entity_id" });

            migrationBuilder.CreateIndex(
                name: "ix_audit_logs_timestamp",
                table: "audit_logs",
                column: "timestamp");

            migrationBuilder.CreateIndex(
                name: "ix_audit_logs_workspace_id",
                table: "audit_logs",
                column: "workspace_id");

            migrationBuilder.CreateIndex(
                name: "ix_content_field_values_child_content_item_id",
                table: "content_field_values",
                column: "child_content_item_id");

            migrationBuilder.CreateIndex(
                name: "ix_content_field_values_content_item_id_field_id_order",
                table: "content_field_values",
                columns: new[] { "content_item_id", "field_id", "order" });

            migrationBuilder.CreateIndex(
                name: "ix_content_field_values_field_id",
                table: "content_field_values",
                column: "field_id");

            migrationBuilder.CreateIndex(
                name: "ix_content_field_values_file_asset_id",
                table: "content_field_values",
                column: "file_asset_id");

            migrationBuilder.CreateIndex(
                name: "ix_content_field_values_media_asset_id",
                table: "content_field_values",
                column: "media_asset_id");

            migrationBuilder.CreateIndex(
                name: "ix_content_item_tags_tag_id",
                table: "content_item_tags",
                column: "tag_id");

            migrationBuilder.CreateIndex(
                name: "ix_content_items_search_vector",
                table: "content_items",
                column: "search_vector")
                .Annotation("Npgsql:IndexMethod", "GIN");

            migrationBuilder.CreateIndex(
                name: "ix_content_items_status_publish_at",
                table: "content_items",
                columns: new[] { "status", "publish_at" });

            migrationBuilder.CreateIndex(
                name: "ix_content_items_template_version_id",
                table: "content_items",
                column: "template_version_id");

            migrationBuilder.CreateIndex(
                name: "ix_content_items_translation_group_id",
                table: "content_items",
                column: "translation_group_id");

            migrationBuilder.CreateIndex(
                name: "ix_content_items_workspace_id",
                table: "content_items",
                column: "workspace_id");

            migrationBuilder.CreateIndex(
                name: "ix_content_items_workspace_id_template_version_id_slug",
                table: "content_items",
                columns: new[] { "workspace_id", "template_version_id", "slug" },
                unique: true,
                filter: "slug IS NOT NULL AND is_deleted = false");

            migrationBuilder.CreateIndex(
                name: "ix_media_assets_workspace_id",
                table: "media_assets",
                column: "workspace_id");

            migrationBuilder.CreateIndex(
                name: "ix_tags_workspace_id_name",
                table: "tags",
                columns: new[] { "workspace_id", "name" },
                unique: true,
                filter: "is_deleted = false");

            migrationBuilder.CreateIndex(
                name: "ix_template_field_allowed_types_allowed_template_id",
                table: "template_field_allowed_types",
                column: "allowed_template_id");

            migrationBuilder.CreateIndex(
                name: "ix_template_field_allowed_types_field_id_allowed_template_id",
                table: "template_field_allowed_types",
                columns: new[] { "field_id", "allowed_template_id" },
                unique: true,
                filter: "allowed_template_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_template_field_allowed_types_field_id_primitive_type",
                table: "template_field_allowed_types",
                columns: new[] { "field_id", "primitive_type" },
                unique: true,
                filter: "primitive_type IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_template_fields_section_id",
                table: "template_fields",
                column: "section_id");

            migrationBuilder.CreateIndex(
                name: "ix_template_fields_template_id",
                table: "template_fields",
                column: "template_id");

            migrationBuilder.CreateIndex(
                name: "ix_template_fields_template_version_id_key",
                table: "template_fields",
                columns: new[] { "template_version_id", "key" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_template_sections_template_version_id_order",
                table: "template_sections",
                columns: new[] { "template_version_id", "order" });

            migrationBuilder.CreateIndex(
                name: "ix_template_versions_one_draft_per_template",
                table: "template_versions",
                column: "template_id",
                unique: true,
                filter: "status = 'Draft' AND is_deleted = false");

            migrationBuilder.CreateIndex(
                name: "ix_template_versions_template_id_version_number",
                table: "template_versions",
                columns: new[] { "template_id", "version_number" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_templates_current_version_id",
                table: "templates",
                column: "current_version_id");

            migrationBuilder.CreateIndex(
                name: "ix_templates_workspace_id_slug",
                table: "templates",
                columns: new[] { "workspace_id", "slug" },
                unique: true,
                filter: "is_deleted = false");

            migrationBuilder.CreateIndex(
                name: "ix_users_email",
                table: "users",
                column: "email",
                unique: true,
                filter: "is_deleted = false");

            migrationBuilder.CreateIndex(
                name: "ix_webhook_delivery_logs_is_delivered_is_failed_next_retry_at",
                table: "webhook_delivery_logs",
                columns: new[] { "is_delivered", "is_failed", "next_retry_at" });

            migrationBuilder.CreateIndex(
                name: "ix_webhook_delivery_logs_webhook_endpoint_id",
                table: "webhook_delivery_logs",
                column: "webhook_endpoint_id");

            migrationBuilder.CreateIndex(
                name: "ix_webhook_endpoints_created_by_user_id",
                table: "webhook_endpoints",
                column: "created_by_user_id");

            migrationBuilder.CreateIndex(
                name: "ix_webhook_endpoints_workspace_id_name",
                table: "webhook_endpoints",
                columns: new[] { "workspace_id", "name" },
                unique: true,
                filter: "is_deleted = false");

            migrationBuilder.CreateIndex(
                name: "ix_workspaces_slug",
                table: "workspaces",
                column: "slug",
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "fk_content_field_values_content_items_child_content_item_id",
                table: "content_field_values",
                column: "child_content_item_id",
                principalTable: "content_items",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "fk_content_field_values_content_items_content_item_id",
                table: "content_field_values",
                column: "content_item_id",
                principalTable: "content_items",
                principalColumn: "id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "fk_content_field_values_template_fields_field_id",
                table: "content_field_values",
                column: "field_id",
                principalTable: "template_fields",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "fk_content_item_tags_content_items_content_item_id",
                table: "content_item_tags",
                column: "content_item_id",
                principalTable: "content_items",
                principalColumn: "id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "fk_content_items_template_versions_template_version_id",
                table: "content_items",
                column: "template_version_id",
                principalTable: "template_versions",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "fk_template_field_allowed_types_template_fields_field_id",
                table: "template_field_allowed_types",
                column: "field_id",
                principalTable: "template_fields",
                principalColumn: "id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "fk_template_field_allowed_types_templates_allowed_template_id",
                table: "template_field_allowed_types",
                column: "allowed_template_id",
                principalTable: "templates",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "fk_template_fields_template_sections_section_id",
                table: "template_fields",
                column: "section_id",
                principalTable: "template_sections",
                principalColumn: "id",
                onDelete: ReferentialAction.SetNull);

            migrationBuilder.AddForeignKey(
                name: "fk_template_fields_template_versions_template_version_id",
                table: "template_fields",
                column: "template_version_id",
                principalTable: "template_versions",
                principalColumn: "id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "fk_template_fields_templates_template_id",
                table: "template_fields",
                column: "template_id",
                principalTable: "templates",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "fk_template_sections_template_versions_template_version_id",
                table: "template_sections",
                column: "template_version_id",
                principalTable: "template_versions",
                principalColumn: "id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "fk_template_versions_templates_template_id",
                table: "template_versions",
                column: "template_id",
                principalTable: "templates",
                principalColumn: "id",
                onDelete: ReferentialAction.Cascade);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "fk_templates_workspaces_workspace_id",
                table: "templates");

            migrationBuilder.DropForeignKey(
                name: "fk_templates_template_versions_current_version_id",
                table: "templates");

            migrationBuilder.DropTable(
                name: "api_clients");

            migrationBuilder.DropTable(
                name: "audit_logs");

            migrationBuilder.DropTable(
                name: "content_field_values");

            migrationBuilder.DropTable(
                name: "content_item_tags");

            migrationBuilder.DropTable(
                name: "template_field_allowed_types");

            migrationBuilder.DropTable(
                name: "webhook_delivery_logs");

            migrationBuilder.DropTable(
                name: "webhook_subscriptions");

            migrationBuilder.DropTable(
                name: "media_assets");

            migrationBuilder.DropTable(
                name: "content_items");

            migrationBuilder.DropTable(
                name: "tags");

            migrationBuilder.DropTable(
                name: "template_fields");

            migrationBuilder.DropTable(
                name: "webhook_endpoints");

            migrationBuilder.DropTable(
                name: "template_sections");

            migrationBuilder.DropTable(
                name: "users");

            migrationBuilder.DropTable(
                name: "workspaces");

            migrationBuilder.DropTable(
                name: "template_versions");

            migrationBuilder.DropTable(
                name: "templates");
        }
    }
}
