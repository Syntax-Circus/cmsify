using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Cmsify.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddEmbeddedPhotoFoundation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddUniqueConstraint(
                name: "ak_media_assets_id_workspace_id",
                table: "media_assets",
                columns: new[] { "id", "workspace_id" });

            migrationBuilder.CreateTable(
                name: "embedded_media_reference_sets",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    workspace_id = table.Column<Guid>(type: "uuid", nullable: false),
                    consumer_namespace = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    reference_key = table.Column<Guid>(type: "uuid", nullable: false),
                    operation_key = table.Column<Guid>(type: "uuid", nullable: false),
                    input_fingerprint = table.Column<string>(type: "char(64)", nullable: false),
                    protection_revision = table.Column<int>(type: "integer", nullable: false),
                    state = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    release_operation_key = table.Column<Guid>(type: "uuid", nullable: true),
                    release_receipt_id = table.Column<Guid>(type: "uuid", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    released_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_embedded_media_reference_sets", x => x.id);
                    table.UniqueConstraint("ak_embedded_media_reference_sets_id_workspace_id", x => new { x.id, x.workspace_id });
                    table.ForeignKey(
                        name: "fk_embedded_media_reference_sets_workspaces_workspace_id",
                        column: x => x.workspace_id,
                        principalTable: "workspaces",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "embedded_photo_operation_receipts",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    workspace_id = table.Column<Guid>(type: "uuid", nullable: false),
                    kind = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    operation_key = table.Column<Guid>(type: "uuid", nullable: false),
                    input_fingerprint = table.Column<string>(type: "char(64)", nullable: false),
                    output_json = table.Column<string>(type: "jsonb", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_embedded_photo_operation_receipts", x => x.id);
                    table.ForeignKey(
                        name: "fk_embedded_photo_operation_receipts_workspaces_workspace_id",
                        column: x => x.workspace_id,
                        principalTable: "workspaces",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "embedded_photo_revisions",
                columns: table => new
                {
                    asset_id = table.Column<Guid>(type: "uuid", nullable: false),
                    revision = table.Column<int>(type: "integer", nullable: false),
                    workspace_id = table.Column<Guid>(type: "uuid", nullable: false),
                    operation_key = table.Column<Guid>(type: "uuid", nullable: false),
                    source_sha256 = table.Column<string>(type: "char(64)", nullable: false),
                    policy_id = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    processor_version = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    quarter_turns_clockwise = table.Column<int>(type: "integer", nullable: false),
                    state = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    retry_generation = table.Column<int>(type: "integer", nullable: false),
                    failure_code = table.Column<string>(type: "text", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_embedded_photo_revisions", x => new { x.asset_id, x.revision });
                    table.UniqueConstraint("ak_embedded_photo_revisions_asset_id_revision_workspace_id", x => new { x.asset_id, x.revision, x.workspace_id });
                    table.CheckConstraint("ck_photo_revision_instruction", "revision > 0 AND quarter_turns_clockwise BETWEEN 0 AND 3 AND retry_generation >= 0");
                    table.ForeignKey(
                        name: "fk_embedded_photo_revisions_media_assets_asset_id_workspace_id",
                        columns: x => new { x.asset_id, x.workspace_id },
                        principalTable: "media_assets",
                        principalColumns: new[] { "id", "workspace_id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "embedded_photo_upload_sessions",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    workspace_id = table.Column<Guid>(type: "uuid", nullable: false),
                    asset_id = table.Column<Guid>(type: "uuid", nullable: false),
                    operation_key = table.Column<Guid>(type: "uuid", nullable: false),
                    display_file_name = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    declared_length = table.Column<long>(type: "bigint", nullable: false),
                    policy_id = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    processor_version = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    state = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    attempt_generation = table.Column<int>(type: "integer", nullable: false),
                    lease_token = table.Column<Guid>(type: "uuid", nullable: true),
                    lease_owner = table.Column<Guid>(type: "uuid", nullable: true),
                    lease_expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    write_operation_key = table.Column<Guid>(type: "uuid", nullable: true),
                    failure_code = table.Column<string>(type: "text", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    completed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_embedded_photo_upload_sessions", x => x.id);
                    table.UniqueConstraint("ak_embedded_photo_upload_sessions_id_workspace_id", x => new { x.id, x.workspace_id });
                    table.CheckConstraint("ck_photo_declared_length", "declared_length > 0 AND declared_length <= 20971520");
                    table.ForeignKey(
                        name: "fk_embedded_photo_upload_sessions_media_assets_asset_id_worksp",
                        columns: x => new { x.asset_id, x.workspace_id },
                        principalTable: "media_assets",
                        principalColumns: new[] { "id", "workspace_id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "embedded_photo_jobs",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    asset_id = table.Column<Guid>(type: "uuid", nullable: false),
                    workspace_id = table.Column<Guid>(type: "uuid", nullable: false),
                    revision = table.Column<int>(type: "integer", nullable: false),
                    source_sha256 = table.Column<string>(type: "char(64)", nullable: false),
                    policy_id = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    processor_version = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    state = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    retry_generation = table.Column<int>(type: "integer", nullable: false),
                    automatic_attempt_count = table.Column<int>(type: "integer", nullable: false),
                    lease_owner = table.Column<Guid>(type: "uuid", nullable: true),
                    lease_token = table.Column<Guid>(type: "uuid", nullable: true),
                    lease_expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    next_attempt_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    failure_code = table.Column<string>(type: "text", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_embedded_photo_jobs", x => x.id);
                    table.UniqueConstraint("ak_embedded_photo_jobs_id_workspace_id", x => new { x.id, x.workspace_id });
                    table.CheckConstraint("ck_photo_job_budget", "retry_generation >= 0 AND automatic_attempt_count BETWEEN 0 AND 3");
                    table.ForeignKey(
                        name: "fk_embedded_photo_jobs_embedded_photo_revisions_asset_id_revis",
                        columns: x => new { x.asset_id, x.revision, x.workspace_id },
                        principalTable: "embedded_photo_revisions",
                        principalColumns: new[] { "asset_id", "revision", "workspace_id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "embedded_photo_manifests",
                columns: table => new
                {
                    asset_id = table.Column<Guid>(type: "uuid", nullable: false),
                    revision = table.Column<int>(type: "integer", nullable: false),
                    workspace_id = table.Column<Guid>(type: "uuid", nullable: false),
                    manifest_sha256 = table.Column<string>(type: "char(64)", nullable: false),
                    source_sha256 = table.Column<string>(type: "char(64)", nullable: false),
                    policy_id = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    processor_version = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    quarter_turns_clockwise = table.Column<int>(type: "integer", nullable: false),
                    metadata_json = table.Column<string>(type: "jsonb", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_embedded_photo_manifests", x => new { x.asset_id, x.revision });
                    table.UniqueConstraint("ak_embedded_photo_manifests_asset_id_revision_workspace_id", x => new { x.asset_id, x.revision, x.workspace_id });
                    table.UniqueConstraint("ak_embedded_photo_manifests_asset_id_revision_workspace_id_man", x => new { x.asset_id, x.revision, x.workspace_id, x.manifest_sha256 });
                    table.ForeignKey(
                        name: "fk_embedded_photo_manifests_embedded_photo_revisions_asset_id_",
                        columns: x => new { x.asset_id, x.revision, x.workspace_id },
                        principalTable: "embedded_photo_revisions",
                        principalColumns: new[] { "asset_id", "revision", "workspace_id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "embedded_photo_originals",
                columns: table => new
                {
                    asset_id = table.Column<Guid>(type: "uuid", nullable: false),
                    workspace_id = table.Column<Guid>(type: "uuid", nullable: false),
                    session_id = table.Column<Guid>(type: "uuid", nullable: false),
                    storage_provider = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    storage_key = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false),
                    length = table.Column<long>(type: "bigint", nullable: false),
                    source_sha256 = table.Column<string>(type: "char(64)", nullable: false),
                    metadata_json = table.Column<string>(type: "jsonb", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_embedded_photo_originals", x => x.asset_id);
                    table.CheckConstraint("ck_photo_original_length", "length > 0 AND length <= 20971520");
                    table.ForeignKey(
                        name: "fk_embedded_photo_originals_embedded_photo_upload_sessions_ses",
                        columns: x => new { x.session_id, x.workspace_id },
                        principalTable: "embedded_photo_upload_sessions",
                        principalColumns: new[] { "id", "workspace_id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_embedded_photo_originals_media_assets_asset_id_workspace_id",
                        columns: x => new { x.asset_id, x.workspace_id },
                        principalTable: "media_assets",
                        principalColumns: new[] { "id", "workspace_id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "embedded_photo_upload_attempts",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    session_id = table.Column<Guid>(type: "uuid", nullable: false),
                    workspace_id = table.Column<Guid>(type: "uuid", nullable: false),
                    generation = table.Column<int>(type: "integer", nullable: false),
                    token = table.Column<Guid>(type: "uuid", nullable: false),
                    owner_id = table.Column<Guid>(type: "uuid", nullable: false),
                    storage_provider = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    storage_key = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    retired_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_embedded_photo_upload_attempts", x => x.id);
                    table.ForeignKey(
                        name: "fk_embedded_photo_upload_attempts_embedded_photo_upload_sessio",
                        columns: x => new { x.session_id, x.workspace_id },
                        principalTable: "embedded_photo_upload_sessions",
                        principalColumns: new[] { "id", "workspace_id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "embedded_photo_upload_receipts",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    workspace_id = table.Column<Guid>(type: "uuid", nullable: false),
                    session_id = table.Column<Guid>(type: "uuid", nullable: false),
                    asset_id = table.Column<Guid>(type: "uuid", nullable: false),
                    operation_key = table.Column<Guid>(type: "uuid", nullable: false),
                    source_sha256 = table.Column<string>(type: "char(64)", nullable: false),
                    length = table.Column<long>(type: "bigint", nullable: false),
                    initial_revision = table.Column<int>(type: "integer", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_embedded_photo_upload_receipts", x => x.id);
                    table.ForeignKey(
                        name: "fk_embedded_photo_upload_receipts_embedded_photo_upload_sessio",
                        columns: x => new { x.session_id, x.workspace_id },
                        principalTable: "embedded_photo_upload_sessions",
                        principalColumns: new[] { "id", "workspace_id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_embedded_photo_upload_receipts_media_assets_asset_id_worksp",
                        columns: x => new { x.asset_id, x.workspace_id },
                        principalTable: "media_assets",
                        principalColumns: new[] { "id", "workspace_id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "embedded_photo_processing_attempts",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    job_id = table.Column<Guid>(type: "uuid", nullable: false),
                    workspace_id = table.Column<Guid>(type: "uuid", nullable: false),
                    token = table.Column<Guid>(type: "uuid", nullable: false),
                    retry_generation = table.Column<int>(type: "integer", nullable: false),
                    variant_key = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    storage_provider = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    storage_key = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    retired_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_embedded_photo_processing_attempts", x => x.id);
                    table.ForeignKey(
                        name: "fk_embedded_photo_processing_attempts_embedded_photo_jobs_job_",
                        columns: x => new { x.job_id, x.workspace_id },
                        principalTable: "embedded_photo_jobs",
                        principalColumns: new[] { "id", "workspace_id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "embedded_media_reference_entries",
                columns: table => new
                {
                    reference_set_id = table.Column<Guid>(type: "uuid", nullable: false),
                    ordinal = table.Column<int>(type: "integer", nullable: false),
                    workspace_id = table.Column<Guid>(type: "uuid", nullable: false),
                    asset_id = table.Column<Guid>(type: "uuid", nullable: false),
                    revision = table.Column<int>(type: "integer", nullable: false),
                    manifest_sha256 = table.Column<string>(type: "char(64)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_embedded_media_reference_entries", x => new { x.reference_set_id, x.ordinal });
                    table.CheckConstraint("ck_media_reference_ordinal", "ordinal >= 0");
                    table.ForeignKey(
                        name: "fk_embedded_media_reference_entries_embedded_media_reference_s",
                        columns: x => new { x.reference_set_id, x.workspace_id },
                        principalTable: "embedded_media_reference_sets",
                        principalColumns: new[] { "id", "workspace_id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_embedded_media_reference_entries_embedded_photo_manifests_a",
                        columns: x => new { x.asset_id, x.revision, x.workspace_id, x.manifest_sha256 },
                        principalTable: "embedded_photo_manifests",
                        principalColumns: new[] { "asset_id", "revision", "workspace_id", "manifest_sha256" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "embedded_photo_variants",
                columns: table => new
                {
                    asset_id = table.Column<Guid>(type: "uuid", nullable: false),
                    revision = table.Column<int>(type: "integer", nullable: false),
                    variant_key = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    workspace_id = table.Column<Guid>(type: "uuid", nullable: false),
                    content_type = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    width = table.Column<int>(type: "integer", nullable: false),
                    height = table.Column<int>(type: "integer", nullable: false),
                    length = table.Column<long>(type: "bigint", nullable: false),
                    sha256 = table.Column<string>(type: "char(64)", nullable: false),
                    storage_provider = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    storage_key = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_embedded_photo_variants", x => new { x.asset_id, x.revision, x.variant_key });
                    table.CheckConstraint("ck_photo_variant_size", "width > 0 AND height > 0 AND length > 0");
                    table.ForeignKey(
                        name: "fk_embedded_photo_variants_embedded_photo_manifests_asset_id_r",
                        columns: x => new { x.asset_id, x.revision, x.workspace_id },
                        principalTable: "embedded_photo_manifests",
                        principalColumns: new[] { "asset_id", "revision", "workspace_id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_embedded_media_reference_entries_asset_id_revision_workspac",
                table: "embedded_media_reference_entries",
                columns: new[] { "asset_id", "revision", "workspace_id", "manifest_sha256" });

            migrationBuilder.CreateIndex(
                name: "ix_embedded_media_reference_entries_reference_set_id_workspace",
                table: "embedded_media_reference_entries",
                columns: new[] { "reference_set_id", "workspace_id" });

            migrationBuilder.CreateIndex(
                name: "ix_embedded_media_reference_sets_workspace_id_consumer_namespa",
                table: "embedded_media_reference_sets",
                columns: new[] { "workspace_id", "consumer_namespace", "operation_key" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_embedded_media_reference_sets_workspace_id_consumer_namespa1",
                table: "embedded_media_reference_sets",
                columns: new[] { "workspace_id", "consumer_namespace", "reference_key" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_embedded_photo_jobs_asset_id_revision_workspace_id",
                table: "embedded_photo_jobs",
                columns: new[] { "asset_id", "revision", "workspace_id" });

            migrationBuilder.CreateIndex(
                name: "ix_embedded_photo_jobs_asset_id_source_sha256_revision_policy_",
                table: "embedded_photo_jobs",
                columns: new[] { "asset_id", "source_sha256", "revision", "policy_id", "processor_version" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_embedded_photo_jobs_state_next_attempt_at",
                table: "embedded_photo_jobs",
                columns: new[] { "state", "next_attempt_at" });

            migrationBuilder.CreateIndex(
                name: "ix_embedded_photo_operation_receipts_workspace_id_kind_operati",
                table: "embedded_photo_operation_receipts",
                columns: new[] { "workspace_id", "kind", "operation_key" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_embedded_photo_originals_asset_id_workspace_id",
                table: "embedded_photo_originals",
                columns: new[] { "asset_id", "workspace_id" });

            migrationBuilder.CreateIndex(
                name: "ix_embedded_photo_originals_session_id_workspace_id",
                table: "embedded_photo_originals",
                columns: new[] { "session_id", "workspace_id" });

            migrationBuilder.CreateIndex(
                name: "ix_embedded_photo_originals_storage_provider_storage_key",
                table: "embedded_photo_originals",
                columns: new[] { "storage_provider", "storage_key" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_embedded_photo_processing_attempts_job_id_token_variant_key",
                table: "embedded_photo_processing_attempts",
                columns: new[] { "job_id", "token", "variant_key" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_embedded_photo_processing_attempts_job_id_workspace_id",
                table: "embedded_photo_processing_attempts",
                columns: new[] { "job_id", "workspace_id" });

            migrationBuilder.CreateIndex(
                name: "ix_embedded_photo_processing_attempts_storage_provider_storage",
                table: "embedded_photo_processing_attempts",
                columns: new[] { "storage_provider", "storage_key" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_embedded_photo_revisions_asset_id_workspace_id",
                table: "embedded_photo_revisions",
                columns: new[] { "asset_id", "workspace_id" });

            migrationBuilder.CreateIndex(
                name: "ix_embedded_photo_upload_attempts_session_id_generation",
                table: "embedded_photo_upload_attempts",
                columns: new[] { "session_id", "generation" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_embedded_photo_upload_attempts_session_id_workspace_id",
                table: "embedded_photo_upload_attempts",
                columns: new[] { "session_id", "workspace_id" });

            migrationBuilder.CreateIndex(
                name: "ix_embedded_photo_upload_attempts_storage_provider_storage_key",
                table: "embedded_photo_upload_attempts",
                columns: new[] { "storage_provider", "storage_key" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_embedded_photo_upload_receipts_asset_id_workspace_id",
                table: "embedded_photo_upload_receipts",
                columns: new[] { "asset_id", "workspace_id" });

            migrationBuilder.CreateIndex(
                name: "ix_embedded_photo_upload_receipts_session_id",
                table: "embedded_photo_upload_receipts",
                column: "session_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_embedded_photo_upload_receipts_session_id_workspace_id",
                table: "embedded_photo_upload_receipts",
                columns: new[] { "session_id", "workspace_id" });

            migrationBuilder.CreateIndex(
                name: "ix_embedded_photo_upload_receipts_workspace_id_operation_key",
                table: "embedded_photo_upload_receipts",
                columns: new[] { "workspace_id", "operation_key" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_embedded_photo_upload_sessions_asset_id",
                table: "embedded_photo_upload_sessions",
                column: "asset_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_embedded_photo_upload_sessions_asset_id_workspace_id",
                table: "embedded_photo_upload_sessions",
                columns: new[] { "asset_id", "workspace_id" });

            migrationBuilder.CreateIndex(
                name: "ix_embedded_photo_upload_sessions_workspace_id_operation_key",
                table: "embedded_photo_upload_sessions",
                columns: new[] { "workspace_id", "operation_key" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_embedded_photo_variants_asset_id_revision_workspace_id",
                table: "embedded_photo_variants",
                columns: new[] { "asset_id", "revision", "workspace_id" });

            migrationBuilder.CreateIndex(
                name: "ix_embedded_photo_variants_storage_provider_storage_key",
                table: "embedded_photo_variants",
                columns: new[] { "storage_provider", "storage_key" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "embedded_media_reference_entries");

            migrationBuilder.DropTable(
                name: "embedded_photo_operation_receipts");

            migrationBuilder.DropTable(
                name: "embedded_photo_originals");

            migrationBuilder.DropTable(
                name: "embedded_photo_processing_attempts");

            migrationBuilder.DropTable(
                name: "embedded_photo_upload_attempts");

            migrationBuilder.DropTable(
                name: "embedded_photo_upload_receipts");

            migrationBuilder.DropTable(
                name: "embedded_photo_variants");

            migrationBuilder.DropTable(
                name: "embedded_media_reference_sets");

            migrationBuilder.DropTable(
                name: "embedded_photo_jobs");

            migrationBuilder.DropTable(
                name: "embedded_photo_upload_sessions");

            migrationBuilder.DropTable(
                name: "embedded_photo_manifests");

            migrationBuilder.DropTable(
                name: "embedded_photo_revisions");

            migrationBuilder.DropUniqueConstraint(
                name: "ak_media_assets_id_workspace_id",
                table: "media_assets");
        }
    }
}
