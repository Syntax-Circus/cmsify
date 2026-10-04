using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Cmsify.Infrastructure.Sqlite.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class InitialSqlite : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "audit_logs",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "TEXT", nullable: false),
                    entity_type = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    entity_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    action = table.Column<string>(type: "TEXT", maxLength: 50, nullable: false),
                    actor_user_id = table.Column<Guid>(type: "TEXT", nullable: true),
                    actor_api_client_id = table.Column<Guid>(type: "TEXT", nullable: true),
                    timestamp = table.Column<long>(type: "INTEGER", nullable: false),
                    change_delta = table.Column<string>(type: "TEXT", nullable: true),
                    workspace_id = table.Column<Guid>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_audit_logs", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "ip_ban_events",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "TEXT", nullable: false),
                    ip_address = table.Column<string>(type: "TEXT", maxLength: 45, nullable: false),
                    rejection_count = table.Column<int>(type: "INTEGER", nullable: false),
                    banned_at = table.Column<long>(type: "INTEGER", nullable: false),
                    banned_until = table.Column<long>(type: "INTEGER", nullable: false),
                    request_path = table.Column<string>(type: "TEXT", maxLength: 2048, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_ip_ban_events", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "media_reconciliation_checkpoints",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "TEXT", nullable: false),
                    provider = table.Column<string>(type: "TEXT", maxLength: 50, nullable: false),
                    prefix = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: false),
                    after_key = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: true),
                    lease_owner = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    lease_token = table.Column<Guid>(type: "TEXT", nullable: true),
                    lease_expires_at = table.Column<long>(type: "INTEGER", nullable: true),
                    last_scan_started_at = table.Column<long>(type: "INTEGER", nullable: true),
                    last_scan_completed_at = table.Column<long>(type: "INTEGER", nullable: true),
                    created_at = table.Column<long>(type: "INTEGER", nullable: false),
                    updated_at = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_media_reconciliation_checkpoints", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "users",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "TEXT", nullable: false),
                    email = table.Column<string>(type: "TEXT", maxLength: 320, nullable: false),
                    display_name = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    password_hash = table.Column<string>(type: "TEXT", maxLength: 500, nullable: false),
                    role = table.Column<string>(type: "TEXT", maxLength: 50, nullable: false),
                    is_super_admin = table.Column<bool>(type: "INTEGER", nullable: false, defaultValue: false),
                    must_change_password = table.Column<bool>(type: "INTEGER", nullable: false),
                    time_zone_id = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true),
                    theme = table.Column<string>(type: "TEXT", maxLength: 20, nullable: true),
                    is_active = table.Column<bool>(type: "INTEGER", nullable: false),
                    last_login_at = table.Column<long>(type: "INTEGER", nullable: true),
                    row_version = table.Column<uint>(type: "INTEGER", nullable: false),
                    created_at = table.Column<long>(type: "INTEGER", nullable: false),
                    updated_at = table.Column<long>(type: "INTEGER", nullable: false),
                    is_deleted = table.Column<bool>(type: "INTEGER", nullable: false, defaultValue: false),
                    deleted_at = table.Column<long>(type: "INTEGER", nullable: true),
                    deleted_by_user_id = table.Column<Guid>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_users", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "webhook_outbox_events",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "TEXT", nullable: false),
                    event_type = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    workspace_id = table.Column<Guid>(type: "TEXT", nullable: true),
                    entity_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    payload = table.Column<string>(type: "TEXT", nullable: false),
                    occurred_at = table.Column<long>(type: "INTEGER", nullable: false),
                    created_at = table.Column<long>(type: "INTEGER", nullable: false),
                    processed_at = table.Column<long>(type: "INTEGER", nullable: true),
                    lease_owner = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    lease_token = table.Column<Guid>(type: "TEXT", nullable: true),
                    lease_expires_at = table.Column<long>(type: "INTEGER", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_webhook_outbox_events", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "workspaces",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "TEXT", nullable: false),
                    name = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    slug = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    description = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: true),
                    row_version = table.Column<uint>(type: "INTEGER", nullable: false),
                    created_at = table.Column<long>(type: "INTEGER", nullable: false),
                    updated_at = table.Column<long>(type: "INTEGER", nullable: false),
                    is_deleted = table.Column<bool>(type: "INTEGER", nullable: false, defaultValue: false),
                    deleted_at = table.Column<long>(type: "INTEGER", nullable: true),
                    deleted_by_user_id = table.Column<Guid>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_workspaces", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "user_sessions",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "TEXT", nullable: false),
                    user_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    token_hash = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    created_at = table.Column<long>(type: "INTEGER", nullable: false),
                    expires_at = table.Column<long>(type: "INTEGER", nullable: false),
                    last_seen_at = table.Column<long>(type: "INTEGER", nullable: true),
                    ip_address = table.Column<string>(type: "TEXT", maxLength: 128, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_user_sessions", x => x.id);
                    table.ForeignKey(
                        name: "fk_user_sessions_users_user_id",
                        column: x => x.user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "api_clients",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "TEXT", nullable: false),
                    name = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    description = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: true),
                    token_hash = table.Column<string>(type: "TEXT", maxLength: 500, nullable: false),
                    token_identifier = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    role = table.Column<string>(type: "TEXT", maxLength: 50, nullable: false),
                    workspace_id = table.Column<Guid>(type: "TEXT", nullable: true),
                    is_active = table.Column<bool>(type: "INTEGER", nullable: false),
                    expires_at = table.Column<long>(type: "INTEGER", nullable: true),
                    created_by_user_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    last_used_at = table.Column<long>(type: "INTEGER", nullable: true),
                    row_version = table.Column<uint>(type: "INTEGER", nullable: false),
                    created_at = table.Column<long>(type: "INTEGER", nullable: false),
                    updated_at = table.Column<long>(type: "INTEGER", nullable: false),
                    is_deleted = table.Column<bool>(type: "INTEGER", nullable: false, defaultValue: false),
                    deleted_at = table.Column<long>(type: "INTEGER", nullable: true),
                    deleted_by_user_id = table.Column<Guid>(type: "TEXT", nullable: true)
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
                    id = table.Column<Guid>(type: "TEXT", nullable: false),
                    workspace_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    file_name = table.Column<string>(type: "TEXT", maxLength: 255, nullable: false),
                    mime_type = table.Column<string>(type: "TEXT", maxLength: 255, nullable: false),
                    size_bytes = table.Column<long>(type: "INTEGER", nullable: false),
                    storage_key = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: false),
                    storage_provider = table.Column<string>(type: "TEXT", maxLength: 50, nullable: false),
                    alt_text = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true),
                    created_by_user_id = table.Column<Guid>(type: "TEXT", nullable: true),
                    blob_state = table.Column<string>(type: "TEXT", maxLength: 50, nullable: false),
                    blob_state_changed_at = table.Column<long>(type: "INTEGER", nullable: false),
                    upload_completed_at = table.Column<long>(type: "INTEGER", nullable: true),
                    upload_failed_at = table.Column<long>(type: "INTEGER", nullable: true),
                    blob_verified_at = table.Column<long>(type: "INTEGER", nullable: true),
                    missing_detected_at = table.Column<long>(type: "INTEGER", nullable: true),
                    deletion_requested_at = table.Column<long>(type: "INTEGER", nullable: true),
                    purge_after = table.Column<long>(type: "INTEGER", nullable: true),
                    row_version = table.Column<uint>(type: "INTEGER", nullable: false),
                    created_at = table.Column<long>(type: "INTEGER", nullable: false),
                    updated_at = table.Column<long>(type: "INTEGER", nullable: false),
                    is_deleted = table.Column<bool>(type: "INTEGER", nullable: false, defaultValue: false),
                    deleted_at = table.Column<long>(type: "INTEGER", nullable: true),
                    deleted_by_user_id = table.Column<Guid>(type: "TEXT", nullable: true)
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
                    id = table.Column<Guid>(type: "TEXT", nullable: false),
                    workspace_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    name = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    row_version = table.Column<uint>(type: "INTEGER", nullable: false),
                    created_at = table.Column<long>(type: "INTEGER", nullable: false),
                    updated_at = table.Column<long>(type: "INTEGER", nullable: false),
                    is_deleted = table.Column<bool>(type: "INTEGER", nullable: false, defaultValue: false),
                    deleted_at = table.Column<long>(type: "INTEGER", nullable: true),
                    deleted_by_user_id = table.Column<Guid>(type: "TEXT", nullable: true)
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
                name: "user_workspace_accesses",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "TEXT", nullable: false),
                    user_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    workspace_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    access_level = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_user_workspace_accesses", x => x.id);
                    table.ForeignKey(
                        name: "fk_user_workspace_accesses_users_user_id",
                        column: x => x.user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_user_workspace_accesses_workspaces_workspace_id",
                        column: x => x.workspace_id,
                        principalTable: "workspaces",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "webhook_endpoints",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "TEXT", nullable: false),
                    workspace_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    name = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    url = table.Column<string>(type: "TEXT", maxLength: 2000, nullable: false),
                    secret = table.Column<string>(type: "text", nullable: false),
                    is_active = table.Column<bool>(type: "INTEGER", nullable: false),
                    created_by_user_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    row_version = table.Column<uint>(type: "INTEGER", nullable: false),
                    created_at = table.Column<long>(type: "INTEGER", nullable: false),
                    updated_at = table.Column<long>(type: "INTEGER", nullable: false),
                    is_deleted = table.Column<bool>(type: "INTEGER", nullable: false, defaultValue: false),
                    deleted_at = table.Column<long>(type: "INTEGER", nullable: true),
                    deleted_by_user_id = table.Column<Guid>(type: "TEXT", nullable: true)
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
                name: "media_deletion_intents",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "TEXT", nullable: false),
                    media_asset_id = table.Column<Guid>(type: "TEXT", nullable: true),
                    provider = table.Column<string>(type: "TEXT", maxLength: 50, nullable: false),
                    storage_key = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: false),
                    reason = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    not_before = table.Column<long>(type: "INTEGER", nullable: false),
                    next_attempt_at = table.Column<long>(type: "INTEGER", nullable: false),
                    attempt_count = table.Column<int>(type: "INTEGER", nullable: false),
                    last_error = table.Column<string>(type: "TEXT", maxLength: 2000, nullable: true),
                    completed_at = table.Column<long>(type: "INTEGER", nullable: true),
                    lease_owner = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    lease_token = table.Column<Guid>(type: "TEXT", nullable: true),
                    lease_expires_at = table.Column<long>(type: "INTEGER", nullable: true),
                    created_at = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_media_deletion_intents", x => x.id);
                    table.ForeignKey(
                        name: "fk_media_deletion_intents_media_assets_media_asset_id",
                        column: x => x.media_asset_id,
                        principalTable: "media_assets",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "webhook_delivery_logs",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "TEXT", nullable: false),
                    webhook_event_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    webhook_endpoint_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    event_type = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    payload = table.Column<string>(type: "TEXT", nullable: false),
                    attempt_count = table.Column<int>(type: "INTEGER", nullable: false),
                    last_attempt_at = table.Column<long>(type: "INTEGER", nullable: true),
                    next_retry_at = table.Column<long>(type: "INTEGER", nullable: true),
                    status_code = table.Column<int>(type: "INTEGER", nullable: true),
                    is_delivered = table.Column<bool>(type: "INTEGER", nullable: false),
                    is_failed = table.Column<bool>(type: "INTEGER", nullable: false),
                    lease_expires_at = table.Column<long>(type: "INTEGER", nullable: true),
                    lease_owner = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    lease_token = table.Column<Guid>(type: "TEXT", nullable: true),
                    last_error = table.Column<string>(type: "TEXT", maxLength: 4000, nullable: true),
                    is_dead_letter = table.Column<bool>(type: "INTEGER", nullable: false),
                    dead_lettered_at = table.Column<long>(type: "INTEGER", nullable: true),
                    created_at = table.Column<long>(type: "INTEGER", nullable: false)
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
                    webhook_endpoint_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    event_type = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false)
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
                name: "component_fields",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "TEXT", nullable: false),
                    component_version_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    key = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    label = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    help_text = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: true),
                    order = table.Column<int>(type: "INTEGER", nullable: false),
                    is_required = table.Column<bool>(type: "INTEGER", nullable: false),
                    min_occurrences = table.Column<int>(type: "INTEGER", nullable: false),
                    max_occurrences = table.Column<int>(type: "INTEGER", nullable: true),
                    primitive_type = table.Column<string>(type: "TEXT", maxLength: 50, nullable: true),
                    nested_component_id = table.Column<Guid>(type: "TEXT", nullable: true),
                    field_config = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_component_fields", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "component_versions",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "TEXT", nullable: false),
                    component_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    version_number = table.Column<int>(type: "INTEGER", nullable: false),
                    status = table.Column<string>(type: "TEXT", maxLength: 50, nullable: false),
                    published_at = table.Column<long>(type: "INTEGER", nullable: true),
                    notes = table.Column<string>(type: "TEXT", maxLength: 2000, nullable: true),
                    row_version = table.Column<uint>(type: "INTEGER", nullable: false),
                    created_at = table.Column<long>(type: "INTEGER", nullable: false),
                    updated_at = table.Column<long>(type: "INTEGER", nullable: false),
                    is_deleted = table.Column<bool>(type: "INTEGER", nullable: false, defaultValue: false),
                    deleted_at = table.Column<long>(type: "INTEGER", nullable: true),
                    deleted_by_user_id = table.Column<Guid>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_component_versions", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "components",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "TEXT", nullable: false),
                    workspace_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    name = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    slug = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    description = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: true),
                    package_namespace = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    package_id = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    package_version = table.Column<string>(type: "TEXT", maxLength: 50, nullable: true),
                    current_version_id = table.Column<Guid>(type: "TEXT", nullable: true),
                    row_version = table.Column<uint>(type: "INTEGER", nullable: false),
                    created_at = table.Column<long>(type: "INTEGER", nullable: false),
                    updated_at = table.Column<long>(type: "INTEGER", nullable: false),
                    is_deleted = table.Column<bool>(type: "INTEGER", nullable: false, defaultValue: false),
                    deleted_at = table.Column<long>(type: "INTEGER", nullable: true),
                    deleted_by_user_id = table.Column<Guid>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_components", x => x.id);
                    table.ForeignKey(
                        name: "fk_components_component_versions_current_version_id",
                        column: x => x.current_version_id,
                        principalTable: "component_versions",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_components_workspaces_workspace_id",
                        column: x => x.workspace_id,
                        principalTable: "workspaces",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "content_item_tags",
                columns: table => new
                {
                    content_item_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    tag_id = table.Column<Guid>(type: "TEXT", nullable: false)
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
                    id = table.Column<Guid>(type: "TEXT", nullable: false),
                    workspace_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    template_version_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    slug = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    locale_code = table.Column<string>(type: "TEXT", maxLength: 20, nullable: true),
                    translation_group_id = table.Column<Guid>(type: "TEXT", nullable: true),
                    search_vector = table.Column<string>(type: "TEXT", nullable: true),
                    created_by_user_id = table.Column<Guid>(type: "TEXT", nullable: true),
                    updated_by_user_id = table.Column<Guid>(type: "TEXT", nullable: true),
                    row_version = table.Column<uint>(type: "INTEGER", nullable: false),
                    created_at = table.Column<long>(type: "INTEGER", nullable: false),
                    updated_at = table.Column<long>(type: "INTEGER", nullable: false),
                    is_deleted = table.Column<bool>(type: "INTEGER", nullable: false, defaultValue: false),
                    deleted_at = table.Column<long>(type: "INTEGER", nullable: true),
                    deleted_by_user_id = table.Column<Guid>(type: "TEXT", nullable: true)
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
                name: "content_version_field_values",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "TEXT", nullable: false),
                    content_version_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    field_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    order = table.Column<int>(type: "INTEGER", nullable: false),
                    value_kind = table.Column<string>(type: "TEXT", maxLength: 50, nullable: false),
                    text_value = table.Column<string>(type: "TEXT", nullable: true),
                    display_label = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    bool_value = table.Column<bool>(type: "INTEGER", nullable: true),
                    media_asset_id = table.Column<Guid>(type: "TEXT", nullable: true),
                    file_asset_id = table.Column<Guid>(type: "TEXT", nullable: true),
                    child_content_item_id = table.Column<Guid>(type: "TEXT", nullable: true),
                    json_value = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_content_version_field_values", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "content_versions",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "TEXT", nullable: false),
                    content_item_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    workspace_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    version_number = table.Column<int>(type: "INTEGER", nullable: false),
                    status = table.Column<string>(type: "TEXT", maxLength: 50, nullable: false),
                    template_version_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    slug = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    locale_code = table.Column<string>(type: "TEXT", maxLength: 20, nullable: true),
                    translation_group_id = table.Column<Guid>(type: "TEXT", nullable: true),
                    tags = table.Column<string>(type: "TEXT", nullable: false),
                    effective_start_at = table.Column<long>(type: "INTEGER", nullable: true),
                    effective_end_at = table.Column<long>(type: "INTEGER", nullable: true),
                    publish_at = table.Column<long>(type: "INTEGER", nullable: true),
                    published_at = table.Column<long>(type: "INTEGER", nullable: true),
                    archived_at = table.Column<long>(type: "INTEGER", nullable: true),
                    published_by_user_id = table.Column<Guid>(type: "TEXT", nullable: true),
                    rolled_back_from_version_number = table.Column<int>(type: "INTEGER", nullable: true),
                    publish_lease_owner = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    publish_lease_token = table.Column<Guid>(type: "TEXT", nullable: true),
                    publish_lease_expires_at = table.Column<long>(type: "INTEGER", nullable: true),
                    created_at = table.Column<long>(type: "INTEGER", nullable: false),
                    updated_at = table.Column<long>(type: "INTEGER", nullable: false),
                    created_by_user_id = table.Column<Guid>(type: "TEXT", nullable: true),
                    updated_by_user_id = table.Column<Guid>(type: "TEXT", nullable: true),
                    row_version = table.Column<uint>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_content_versions", x => x.id);
                    table.CheckConstraint("ck_content_versions_effective_range", "(effective_start_at IS NULL AND effective_end_at IS NULL) OR (effective_start_at IS NOT NULL AND effective_end_at IS NOT NULL AND effective_start_at < effective_end_at)");
                    table.ForeignKey(
                        name: "fk_content_versions_content_items_content_item_id",
                        column: x => x.content_item_id,
                        principalTable: "content_items",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_content_versions_workspaces_workspace_id",
                        column: x => x.workspace_id,
                        principalTable: "workspaces",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "pick_list_options",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "TEXT", nullable: false),
                    pick_list_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    label = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    value = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    order = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_pick_list_options", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "pick_list_revision_options",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "TEXT", nullable: false),
                    pick_list_revision_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    label = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    value = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    order = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_pick_list_revision_options", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "pick_list_revisions",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "TEXT", nullable: false),
                    pick_list_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    version_number = table.Column<int>(type: "INTEGER", nullable: false),
                    created_at = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_pick_list_revisions", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "pick_lists",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "TEXT", nullable: false),
                    workspace_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    name = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    slug = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    description = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: true),
                    package_namespace = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    package_id = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    package_version = table.Column<string>(type: "TEXT", maxLength: 50, nullable: true),
                    current_revision_id = table.Column<Guid>(type: "TEXT", nullable: true),
                    row_version = table.Column<uint>(type: "INTEGER", nullable: false),
                    created_at = table.Column<long>(type: "INTEGER", nullable: false),
                    updated_at = table.Column<long>(type: "INTEGER", nullable: false),
                    is_deleted = table.Column<bool>(type: "INTEGER", nullable: false, defaultValue: false),
                    deleted_at = table.Column<long>(type: "INTEGER", nullable: true),
                    deleted_by_user_id = table.Column<Guid>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_pick_lists", x => x.id);
                    table.ForeignKey(
                        name: "fk_pick_lists_pick_list_revisions_current_revision_id",
                        column: x => x.current_revision_id,
                        principalTable: "pick_list_revisions",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_pick_lists_workspaces_workspace_id",
                        column: x => x.workspace_id,
                        principalTable: "workspaces",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "template_field_allowed_types",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "TEXT", nullable: false),
                    field_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    primitive_type = table.Column<string>(type: "TEXT", maxLength: 50, nullable: true),
                    allowed_template_id = table.Column<Guid>(type: "TEXT", nullable: true)
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
                    id = table.Column<Guid>(type: "TEXT", nullable: false),
                    template_version_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    section_id = table.Column<Guid>(type: "TEXT", nullable: true),
                    key = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    label = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    help_text = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: true),
                    order = table.Column<int>(type: "INTEGER", nullable: false),
                    is_required = table.Column<bool>(type: "INTEGER", nullable: false),
                    min_occurrences = table.Column<int>(type: "INTEGER", nullable: false),
                    max_occurrences = table.Column<int>(type: "INTEGER", nullable: true),
                    is_open = table.Column<bool>(type: "INTEGER", nullable: false),
                    composition_mode = table.Column<string>(type: "TEXT", maxLength: 50, nullable: false),
                    primitive_type = table.Column<string>(type: "TEXT", maxLength: 50, nullable: true),
                    template_id = table.Column<Guid>(type: "TEXT", nullable: true),
                    component_id = table.Column<Guid>(type: "TEXT", nullable: true),
                    field_config = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_template_fields", x => x.id);
                    table.CheckConstraint("ck_template_fields_type_shape", "(is_open = true AND primitive_type IS NULL AND template_id IS NULL AND component_id IS NULL) OR (is_open = false AND ((primitive_type IS NOT NULL AND template_id IS NULL AND component_id IS NULL) OR (primitive_type IS NULL AND template_id IS NOT NULL AND component_id IS NULL) OR (primitive_type IS NULL AND template_id IS NULL AND component_id IS NOT NULL)))");
                    table.ForeignKey(
                        name: "fk_template_fields_components_component_id",
                        column: x => x.component_id,
                        principalTable: "components",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "template_sections",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "TEXT", nullable: false),
                    template_version_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    name = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    description = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: true),
                    order = table.Column<int>(type: "INTEGER", nullable: false),
                    is_collapsible = table.Column<bool>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_template_sections", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "template_versions",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "TEXT", nullable: false),
                    template_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    version_number = table.Column<int>(type: "INTEGER", nullable: false),
                    status = table.Column<string>(type: "TEXT", maxLength: 50, nullable: false),
                    published_at = table.Column<long>(type: "INTEGER", nullable: true),
                    created_by_user_id = table.Column<Guid>(type: "TEXT", nullable: true),
                    notes = table.Column<string>(type: "TEXT", maxLength: 2000, nullable: true),
                    row_version = table.Column<uint>(type: "INTEGER", nullable: false),
                    created_at = table.Column<long>(type: "INTEGER", nullable: false),
                    updated_at = table.Column<long>(type: "INTEGER", nullable: false),
                    is_deleted = table.Column<bool>(type: "INTEGER", nullable: false, defaultValue: false),
                    deleted_at = table.Column<long>(type: "INTEGER", nullable: true),
                    deleted_by_user_id = table.Column<Guid>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_template_versions", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "templates",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "TEXT", nullable: false),
                    workspace_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    name = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    slug = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    description = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: true),
                    package_namespace = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    package_id = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    package_version = table.Column<string>(type: "TEXT", maxLength: 50, nullable: true),
                    title_field_key = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true),
                    current_version_id = table.Column<Guid>(type: "TEXT", nullable: true),
                    row_version = table.Column<uint>(type: "INTEGER", nullable: false),
                    created_at = table.Column<long>(type: "INTEGER", nullable: false),
                    updated_at = table.Column<long>(type: "INTEGER", nullable: false),
                    is_deleted = table.Column<bool>(type: "INTEGER", nullable: false, defaultValue: false),
                    deleted_at = table.Column<long>(type: "INTEGER", nullable: true),
                    deleted_by_user_id = table.Column<Guid>(type: "TEXT", nullable: true)
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
                name: "ix_api_clients_token_identifier",
                table: "api_clients",
                column: "token_identifier",
                unique: true,
                filter: "token_identifier IS NOT NULL");

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
                name: "ix_component_fields_component_version_id_key",
                table: "component_fields",
                columns: new[] { "component_version_id", "key" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_component_fields_nested_component_id",
                table: "component_fields",
                column: "nested_component_id");

            migrationBuilder.CreateIndex(
                name: "ix_component_versions_component_id",
                table: "component_versions",
                column: "component_id",
                unique: true,
                filter: "status = 'Draft' AND is_deleted = false");

            migrationBuilder.CreateIndex(
                name: "ix_component_versions_component_id_version_number",
                table: "component_versions",
                columns: new[] { "component_id", "version_number" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_components_current_version_id",
                table: "components",
                column: "current_version_id");

            migrationBuilder.CreateIndex(
                name: "ix_components_workspace_id_slug",
                table: "components",
                columns: new[] { "workspace_id", "slug" },
                unique: true,
                filter: "is_deleted = false");

            migrationBuilder.CreateIndex(
                name: "ix_content_item_tags_tag_id",
                table: "content_item_tags",
                column: "tag_id");

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
                name: "ix_content_version_field_values_content_version_id_field_id_order",
                table: "content_version_field_values",
                columns: new[] { "content_version_id", "field_id", "order" });

            migrationBuilder.CreateIndex(
                name: "ix_content_versions_content_item_id",
                table: "content_versions",
                column: "content_item_id",
                unique: true,
                filter: "status = 'Published' AND effective_start_at IS NULL AND effective_end_at IS NULL");

            migrationBuilder.CreateIndex(
                name: "ix_content_versions_content_item_id_status_effective_start_at_effective_end_at",
                table: "content_versions",
                columns: new[] { "content_item_id", "status", "effective_start_at", "effective_end_at" });

            migrationBuilder.CreateIndex(
                name: "ix_content_versions_content_item_id_version_number",
                table: "content_versions",
                columns: new[] { "content_item_id", "version_number" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_content_versions_status_publish_at",
                table: "content_versions",
                columns: new[] { "status", "publish_at" });

            migrationBuilder.CreateIndex(
                name: "ix_content_versions_status_publish_at_publish_lease_expires_at",
                table: "content_versions",
                columns: new[] { "status", "publish_at", "publish_lease_expires_at" });

            migrationBuilder.CreateIndex(
                name: "ix_content_versions_template_version_id",
                table: "content_versions",
                column: "template_version_id");

            migrationBuilder.CreateIndex(
                name: "ix_content_versions_workspace_id",
                table: "content_versions",
                column: "workspace_id");

            migrationBuilder.CreateIndex(
                name: "ix_ip_ban_events_banned_at",
                table: "ip_ban_events",
                column: "banned_at");

            migrationBuilder.CreateIndex(
                name: "ix_ip_ban_events_ip_address",
                table: "ip_ban_events",
                column: "ip_address");

            migrationBuilder.CreateIndex(
                name: "ix_media_assets_blob_state_blob_state_changed_at",
                table: "media_assets",
                columns: new[] { "blob_state", "blob_state_changed_at" });

            migrationBuilder.CreateIndex(
                name: "ix_media_assets_workspace_id",
                table: "media_assets",
                column: "workspace_id");

            migrationBuilder.CreateIndex(
                name: "ix_media_deletion_intents_completed_at_next_attempt_at_lease_expires_at",
                table: "media_deletion_intents",
                columns: new[] { "completed_at", "next_attempt_at", "lease_expires_at" });

            migrationBuilder.CreateIndex(
                name: "ix_media_deletion_intents_media_asset_id",
                table: "media_deletion_intents",
                column: "media_asset_id");

            migrationBuilder.CreateIndex(
                name: "ix_media_deletion_intents_provider_storage_key",
                table: "media_deletion_intents",
                columns: new[] { "provider", "storage_key" },
                unique: true,
                filter: "completed_at IS NULL");

            migrationBuilder.CreateIndex(
                name: "ix_media_reconciliation_checkpoints_provider_prefix",
                table: "media_reconciliation_checkpoints",
                columns: new[] { "provider", "prefix" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_pick_list_options_pick_list_id_order",
                table: "pick_list_options",
                columns: new[] { "pick_list_id", "order" });

            migrationBuilder.CreateIndex(
                name: "ix_pick_list_options_pick_list_id_value",
                table: "pick_list_options",
                columns: new[] { "pick_list_id", "value" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_pick_list_revision_options_pick_list_revision_id_order",
                table: "pick_list_revision_options",
                columns: new[] { "pick_list_revision_id", "order" });

            migrationBuilder.CreateIndex(
                name: "ix_pick_list_revision_options_pick_list_revision_id_value",
                table: "pick_list_revision_options",
                columns: new[] { "pick_list_revision_id", "value" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_pick_list_revisions_pick_list_id_version_number",
                table: "pick_list_revisions",
                columns: new[] { "pick_list_id", "version_number" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_pick_lists_current_revision_id",
                table: "pick_lists",
                column: "current_revision_id");

            migrationBuilder.CreateIndex(
                name: "ix_pick_lists_workspace_id_slug",
                table: "pick_lists",
                columns: new[] { "workspace_id", "slug" },
                unique: true,
                filter: "is_deleted = false");

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
                name: "ix_template_fields_component_id",
                table: "template_fields",
                column: "component_id");

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
                name: "ix_user_sessions_expires_at",
                table: "user_sessions",
                column: "expires_at");

            migrationBuilder.CreateIndex(
                name: "ix_user_sessions_token_hash",
                table: "user_sessions",
                column: "token_hash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_user_sessions_user_id",
                table: "user_sessions",
                column: "user_id");

            migrationBuilder.CreateIndex(
                name: "ix_user_workspace_accesses_user_id_workspace_id",
                table: "user_workspace_accesses",
                columns: new[] { "user_id", "workspace_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_user_workspace_accesses_workspace_id",
                table: "user_workspace_accesses",
                column: "workspace_id");

            migrationBuilder.CreateIndex(
                name: "ix_users_email",
                table: "users",
                column: "email",
                unique: true,
                filter: "is_deleted = false");

            migrationBuilder.CreateIndex(
                name: "ix_webhook_delivery_logs_is_delivered_is_failed_next_retry_at_lease_expires_at",
                table: "webhook_delivery_logs",
                columns: new[] { "is_delivered", "is_failed", "next_retry_at", "lease_expires_at" });

            migrationBuilder.CreateIndex(
                name: "ix_webhook_delivery_logs_webhook_endpoint_id",
                table: "webhook_delivery_logs",
                column: "webhook_endpoint_id");

            migrationBuilder.CreateIndex(
                name: "ix_webhook_delivery_logs_webhook_event_id_webhook_endpoint_id",
                table: "webhook_delivery_logs",
                columns: new[] { "webhook_event_id", "webhook_endpoint_id" },
                unique: true);

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
                name: "ix_webhook_outbox_events_event_type_workspace_id_occurred_at",
                table: "webhook_outbox_events",
                columns: new[] { "event_type", "workspace_id", "occurred_at" });

            migrationBuilder.CreateIndex(
                name: "ix_webhook_outbox_events_processed_at_lease_expires_at",
                table: "webhook_outbox_events",
                columns: new[] { "processed_at", "lease_expires_at" });

            migrationBuilder.CreateIndex(
                name: "ix_workspaces_slug",
                table: "workspaces",
                column: "slug",
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "fk_component_fields_component_versions_component_version_id",
                table: "component_fields",
                column: "component_version_id",
                principalTable: "component_versions",
                principalColumn: "id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "fk_component_fields_components_nested_component_id",
                table: "component_fields",
                column: "nested_component_id",
                principalTable: "components",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "fk_component_versions_components_component_id",
                table: "component_versions",
                column: "component_id",
                principalTable: "components",
                principalColumn: "id",
                onDelete: ReferentialAction.Cascade);

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
                name: "fk_content_version_field_values_content_versions_content_version_id",
                table: "content_version_field_values",
                column: "content_version_id",
                principalTable: "content_versions",
                principalColumn: "id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "fk_content_versions_template_versions_template_version_id",
                table: "content_versions",
                column: "template_version_id",
                principalTable: "template_versions",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "fk_pick_list_options_pick_lists_pick_list_id",
                table: "pick_list_options",
                column: "pick_list_id",
                principalTable: "pick_lists",
                principalColumn: "id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "fk_pick_list_revision_options_pick_list_revisions_pick_list_revision_id",
                table: "pick_list_revision_options",
                column: "pick_list_revision_id",
                principalTable: "pick_list_revisions",
                principalColumn: "id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "fk_pick_list_revisions_pick_lists_pick_list_id",
                table: "pick_list_revisions",
                column: "pick_list_id",
                principalTable: "pick_lists",
                principalColumn: "id",
                onDelete: ReferentialAction.Cascade);

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
                name: "fk_components_workspaces_workspace_id",
                table: "components");

            migrationBuilder.DropForeignKey(
                name: "fk_pick_lists_workspaces_workspace_id",
                table: "pick_lists");

            migrationBuilder.DropForeignKey(
                name: "fk_templates_workspaces_workspace_id",
                table: "templates");

            migrationBuilder.DropForeignKey(
                name: "fk_components_component_versions_current_version_id",
                table: "components");

            migrationBuilder.DropForeignKey(
                name: "fk_templates_template_versions_current_version_id",
                table: "templates");

            migrationBuilder.DropForeignKey(
                name: "fk_pick_list_revisions_pick_lists_pick_list_id",
                table: "pick_list_revisions");

            migrationBuilder.DropTable(
                name: "api_clients");

            migrationBuilder.DropTable(
                name: "audit_logs");

            migrationBuilder.DropTable(
                name: "component_fields");

            migrationBuilder.DropTable(
                name: "content_item_tags");

            migrationBuilder.DropTable(
                name: "content_version_field_values");

            migrationBuilder.DropTable(
                name: "ip_ban_events");

            migrationBuilder.DropTable(
                name: "media_deletion_intents");

            migrationBuilder.DropTable(
                name: "media_reconciliation_checkpoints");

            migrationBuilder.DropTable(
                name: "pick_list_options");

            migrationBuilder.DropTable(
                name: "pick_list_revision_options");

            migrationBuilder.DropTable(
                name: "template_field_allowed_types");

            migrationBuilder.DropTable(
                name: "user_sessions");

            migrationBuilder.DropTable(
                name: "user_workspace_accesses");

            migrationBuilder.DropTable(
                name: "webhook_delivery_logs");

            migrationBuilder.DropTable(
                name: "webhook_outbox_events");

            migrationBuilder.DropTable(
                name: "webhook_subscriptions");

            migrationBuilder.DropTable(
                name: "tags");

            migrationBuilder.DropTable(
                name: "content_versions");

            migrationBuilder.DropTable(
                name: "media_assets");

            migrationBuilder.DropTable(
                name: "template_fields");

            migrationBuilder.DropTable(
                name: "webhook_endpoints");

            migrationBuilder.DropTable(
                name: "content_items");

            migrationBuilder.DropTable(
                name: "template_sections");

            migrationBuilder.DropTable(
                name: "users");

            migrationBuilder.DropTable(
                name: "workspaces");

            migrationBuilder.DropTable(
                name: "component_versions");

            migrationBuilder.DropTable(
                name: "components");

            migrationBuilder.DropTable(
                name: "template_versions");

            migrationBuilder.DropTable(
                name: "templates");

            migrationBuilder.DropTable(
                name: "pick_lists");

            migrationBuilder.DropTable(
                name: "pick_list_revisions");
        }
    }
}
