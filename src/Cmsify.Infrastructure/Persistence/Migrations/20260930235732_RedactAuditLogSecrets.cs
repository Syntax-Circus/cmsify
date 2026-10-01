using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Cmsify.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class RedactAuditLogSecrets : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Replaces secret values already stored in audit_logs.change_delta with the same fingerprint
            // AuditDeltaBuilder.Fingerprint produces: 'redacted:' + first 12 lowercase hex chars of SHA-256(UTF-8).
            // Keep the key list in sync with AuditDeltaBuilder.SensitivePropertyNames.
            migrationBuilder.Sql("""
                DO $$
                DECLARE
                    sensitive_key text;
                BEGIN
                    FOREACH sensitive_key IN ARRAY ARRAY['PasswordHash', 'Secret', 'TokenHash'] LOOP
                        UPDATE audit_logs
                        SET change_delta = jsonb_set(
                            change_delta,
                            ARRAY[sensitive_key],
                            COALESCE(
                                (SELECT jsonb_object_agg(
                                    side.key,
                                    CASE
                                        WHEN jsonb_typeof(side.value) = 'string' AND (side.value #>> '{}') NOT LIKE 'redacted:%'
                                            THEN to_jsonb('redacted:' || left(encode(sha256(convert_to(side.value #>> '{}', 'UTF8')), 'hex'), 12))
                                        ELSE side.value
                                    END)
                                 FROM jsonb_each(change_delta -> sensitive_key) AS side),
                                '{}'::jsonb))
                        WHERE jsonb_typeof(change_delta) = 'object'
                          AND jsonb_typeof(change_delta -> sensitive_key) = 'object';
                    END LOOP;
                END $$;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Irreversible: the original secret values are intentionally discarded.
        }
    }
}
