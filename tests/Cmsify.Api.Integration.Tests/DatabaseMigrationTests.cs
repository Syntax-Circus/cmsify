using Cmsify.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Npgsql;
using SyntaxCircus.EntityFrameworkCore.Postgres;
using Testcontainers.PostgreSql;

namespace Cmsify.Api.Integration.Tests;

public sealed class DatabaseMigrationTests : IAsyncLifetime
{
    private readonly PostgreSqlContainer postgres = new PostgreSqlBuilder()
        .WithImage("postgres:17-alpine")
        .WithDatabase("cmsify")
        .WithUsername("cmsify")
        .WithPassword("cmsify")
        .Build();

    public async Task InitializeAsync() => await postgres.StartAsync();

    public async Task DisposeAsync() => await postgres.DisposeAsync().AsTask();

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

        await migrator.MigrateAsync();

        await using var connection = new NpgsqlConnection(postgres.GetConnectionString());
        await connection.OpenAsync();

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
