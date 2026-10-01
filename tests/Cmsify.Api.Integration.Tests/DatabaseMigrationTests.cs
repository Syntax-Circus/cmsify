using Cmsify.Infrastructure.Persistence;
using Cmsify.Infrastructure.Persistence.Interceptors;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Npgsql;
using SyntaxCircus.EntityFrameworkCore.Postgres;
using Testcontainers.PostgreSql;

namespace Cmsify.Api.Integration.Tests;

public sealed class DatabaseMigrationTests : IAsyncLifetime
{
    private readonly PostgreSqlContainer postgres = new PostgreSqlBuilder("postgres:17-alpine")
        .WithDatabase("cmsify")
        .WithUsername("cmsify")
        .WithPassword("cmsify")
        .Build();

    public ValueTask InitializeAsync() => new(postgres.StartAsync());

    public async ValueTask DisposeAsync() => await postgres.DisposeAsync();

    [Fact]
    public async Task Migrations_ApplyCleanlyAndCreateExpectedIndexes()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Seed:Admin:Email"] = "admin@example.test",
                ["Seed:Admin:Password"] = "change-this-temporary-password",
                ["Seed:DefaultWorkspace:Name"] = "Default",
                ["Seed:DefaultWorkspace:Slug"] = "default"
            })
            .Build();
        var options = new DbContextOptionsBuilder<CmsifyDbContext>()
            .UseNpgsql(postgres.GetConnectionString())
            .UseSyntaxCircusSnakeCaseNamingConvention()
            .Options;

        await using var context = new CmsifyDbContext(options);
        var migrator = new CmsifyDatabaseMigrator(context, new DbSeeder(context, configuration));

        await migrator.MigrateAsync(TestContext.Current.CancellationToken);

        await using var connection = new NpgsqlConnection(postgres.GetConnectionString());
        await connection.OpenAsync(TestContext.Current.CancellationToken);

        var migrations = await QueryStringsAsync(connection, "SELECT \"MigrationId\" FROM \"__EFMigrationsHistory\" ORDER BY \"MigrationId\";");
        Assert.Contains(migrations, migration => migration.EndsWith("_InitialSchema", StringComparison.Ordinal));
        Assert.Contains(migrations, migration => migration.EndsWith("_AddUserSessions", StringComparison.Ordinal));
        Assert.Contains("ix_workspaces_slug", await QueryStringsAsync(connection, "SELECT indexname FROM pg_indexes WHERE schemaname = 'public' AND tablename = 'workspaces';"));
        Assert.Contains("ix_template_versions_one_draft_per_template", await QueryStringsAsync(connection, "SELECT indexname FROM pg_indexes WHERE schemaname = 'public' AND tablename = 'template_versions';"));
        Assert.Contains("ix_content_items_search_vector", await QueryStringsAsync(connection, "SELECT indexname FROM pg_indexes WHERE schemaname = 'public' AND tablename = 'content_items';"));
        Assert.Contains("ix_user_sessions_token_hash", await QueryStringsAsync(connection, "SELECT indexname FROM pg_indexes WHERE schemaname = 'public' AND tablename = 'user_sessions';"));
        Assert.Contains("ix_user_workspace_accesses_user_id_workspace_id", await QueryStringsAsync(connection, "SELECT indexname FROM pg_indexes WHERE schemaname = 'public' AND tablename = 'user_workspace_accesses';"));
        Assert.Equal(["true"], await QueryStringsAsync(connection, "SELECT is_super_admin::text FROM users WHERE email = 'admin@example.test';"));
    }

    [Fact]
    public async Task RedactAuditLogSecretsMigration_RewritesExistingSecretsWithCSharpCompatibleFingerprints()
    {
        var options = new DbContextOptionsBuilder<CmsifyDbContext>()
            .UseNpgsql(postgres.GetConnectionString())
            .UseSyntaxCircusSnakeCaseNamingConvention()
            .Options;
        await using var context = new CmsifyDbContext(options);
        var migrator = context.GetService<IMigrator>();
        var migrations = context.Database.GetMigrations().ToList();
        var targetIndex = migrations.FindIndex(m => m.EndsWith("_RedactAuditLogSecrets", StringComparison.Ordinal));
        Assert.True(targetIndex > 0);
        await migrator.MigrateAsync(migrations[targetIndex - 1], TestContext.Current.CancellationToken);

        const string unicodeSecret = "päss-中文-$2a$12$abc";
        await using var connection = new NpgsqlConnection(postgres.GetConnectionString());
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        var userRow = Guid.NewGuid();
        var otherRow = Guid.NewGuid();
        var userDelta = System.Text.Json.JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["PasswordHash"] = new { before = "$2a$12$old", after = unicodeSecret },
            ["Email"] = new { after = "a@example.test" }
        });
        var otherDelta = System.Text.Json.JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["Name"] = new { after = "x" },
            ["Secret"] = new { after = (string?)null }
        });
        foreach (var (id, type, delta) in new[] { (userRow, "User", userDelta), (otherRow, "Workspace", otherDelta) })
        {
            await using var insert = new NpgsqlCommand(
                "INSERT INTO audit_logs (id, entity_type, entity_id, action, timestamp, change_delta) VALUES (@id, @type, gen_random_uuid(), 'Modified', now(), @delta::jsonb);",
                connection);
            insert.Parameters.AddWithValue("id", id);
            insert.Parameters.AddWithValue("type", type);
            insert.Parameters.AddWithValue("delta", delta);
            await insert.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        }

        await migrator.MigrateAsync(migrations[targetIndex], TestContext.Current.CancellationToken);

        var user = (await QueryStringsAsync(connection, $"SELECT change_delta::text FROM audit_logs WHERE id = '{userRow}';")).Single();
        using var userDoc = System.Text.Json.JsonDocument.Parse(user);
        var password = userDoc.RootElement.GetProperty("PasswordHash");
        Assert.Equal(AuditDeltaBuilder.Fingerprint("$2a$12$old"), password.GetProperty("before").GetString());
        Assert.Equal(AuditDeltaBuilder.Fingerprint(unicodeSecret), password.GetProperty("after").GetString());
        Assert.Equal("a@example.test", userDoc.RootElement.GetProperty("Email").GetProperty("after").GetString());

        var other = (await QueryStringsAsync(connection, $"SELECT change_delta::text FROM audit_logs WHERE id = '{otherRow}';")).Single();
        using var otherDoc = System.Text.Json.JsonDocument.Parse(other);
        Assert.Equal(System.Text.Json.JsonValueKind.Null, otherDoc.RootElement.GetProperty("Secret").GetProperty("after").ValueKind);
    }

    [Fact]
    public async Task Seeder_DoesNotResetExistingAdminPassword_WhenRunAgainWithDifferentSeedConfig()
    {
        var options = new DbContextOptionsBuilder<CmsifyDbContext>()
            .UseNpgsql(postgres.GetConnectionString())
            .UseSyntaxCircusSnakeCaseNamingConvention()
            .Options;
        await using var context = new CmsifyDbContext(options);
        await context.Database.MigrateAsync(TestContext.Current.CancellationToken);

        static IConfiguration Config(string password) => new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Seed:Admin:Email"] = "admin@example.test",
                ["Seed:Admin:Password"] = password,
                ["Auth:BcryptCost"] = "4"
            })
            .Build();

        await new DbSeeder(context, Config("first-seed-password")).SeedAsync(TestContext.Current.CancellationToken);
        var admin = await context.Users.SingleAsync(TestContext.Current.CancellationToken);
        admin.PasswordHash = BCrypt.Net.BCrypt.HashPassword("operator-chosen-password", 4);
        admin.MustChangePassword = false;
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);
        var hashBefore = admin.PasswordHash;

        await using var restartContext = new CmsifyDbContext(options);
        await new DbSeeder(restartContext, Config("different-seed-password")).SeedAsync(TestContext.Current.CancellationToken);

        var users = await restartContext.Users.IgnoreQueryFilters().ToListAsync(TestContext.Current.CancellationToken);
        var only = Assert.Single(users);
        Assert.Equal(hashBefore, only.PasswordHash);
        Assert.False(only.MustChangePassword);
        Assert.True(BCrypt.Net.BCrypt.Verify("operator-chosen-password", only.PasswordHash));
    }

    [Fact]
    public async Task Seeder_FailsFast_WhenSeedAdminPasswordHasInvisibleOrPaddingCharacters()
    {
        var options = new DbContextOptionsBuilder<CmsifyDbContext>()
            .UseNpgsql(postgres.GetConnectionString())
            .UseSyntaxCircusSnakeCaseNamingConvention()
            .Options;
        await using var context = new CmsifyDbContext(options);
        await context.Database.MigrateAsync(TestContext.Current.CancellationToken);
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Seed:Admin:Email"] = "admin@example.test",
                ["Seed:Admin:Password"] = "valid-looking-password\n"
            })
            .Build();

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => new DbSeeder(context, configuration).SeedAsync(TestContext.Current.CancellationToken));

        Assert.Contains("Seed:Admin:Password", exception.Message);
        Assert.False(await context.Users.AnyAsync(TestContext.Current.CancellationToken));
    }

    private static async Task<IReadOnlyList<string>> QueryStringsAsync(NpgsqlConnection connection, string sql)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        await using var reader = await command.ExecuteReaderAsync();
        var values = new List<string>();
        while (await reader.ReadAsync())
        {
            values.Add(reader.GetString(0));
        }

        return values;
    }
}
