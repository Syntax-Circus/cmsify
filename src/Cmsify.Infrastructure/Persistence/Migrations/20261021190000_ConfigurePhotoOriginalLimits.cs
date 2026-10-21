using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Cmsify.Infrastructure.Persistence.Migrations;

public sealed partial class ConfigurePhotoOriginalLimits : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) => Limits(migrationBuilder, 67108864);

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        // Never truncate or remove retained receipts to make a downgrade succeed.
        migrationBuilder.Sql("""
            DO $$ BEGIN
              IF EXISTS (SELECT 1 FROM embedded_photo_upload_sessions WHERE declared_length > 20971520)
                OR EXISTS (SELECT 1 FROM embedded_photo_originals WHERE length > 20971520) THEN
                RAISE EXCEPTION 'Cannot downgrade photo limits while larger originals or intents are retained.';
              END IF;
            END $$;
            """);
        Limits(migrationBuilder, 20971520);
    }

    private static void Limits(MigrationBuilder migrationBuilder, long maximum)
    {
        migrationBuilder.DropCheckConstraint("ck_photo_declared_length", "embedded_photo_upload_sessions");
        migrationBuilder.DropCheckConstraint("ck_photo_original_length", "embedded_photo_originals");
        migrationBuilder.AddCheckConstraint("ck_photo_declared_length", "embedded_photo_upload_sessions", $"declared_length > 0 AND declared_length <= {maximum}");
        migrationBuilder.AddCheckConstraint("ck_photo_original_length", "embedded_photo_originals", $"length > 0 AND length <= {maximum}");
    }
}
