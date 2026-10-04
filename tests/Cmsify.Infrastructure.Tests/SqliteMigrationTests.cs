using Cmsify.Core.Domain.Entities;
using Cmsify.Core.Interfaces.Services;
using Cmsify.Infrastructure.Extensions;
using Cmsify.Infrastructure.Persistence;
using Cmsify.Infrastructure.Sqlite.Extensions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Shouldly;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Cmsify.Infrastructure.Persistence.Repositories;
using Cmsify.Core.Domain.Enums;
using Cmsify.Core.Interfaces.Repositories;

namespace Cmsify.Infrastructure.Tests;

public sealed class SqliteMigrationTests : IDisposable
{
    private readonly string file = Path.Combine(Path.GetTempPath(), $"cmsify-migration-{Guid.NewGuid():N}.db");
    private ServiceProvider Provider(string? connection = null)
    {
        var services = SqliteRegistrationTests.Services();
        services.AddCmsifySqliteInfrastructure(SqliteRegistrationTests.Configuration(connection ?? $"Data Source={file};Pooling=False"),
            new() { Workers = CmsifyWorkers.None });
        return services.BuildServiceProvider();
    }
    private static Task Migrate(IServiceProvider provider) => provider.GetRequiredService<ICmsifyDatabaseMigrator>()
        .MigrateAsync(TestContext.Current.CancellationToken);
    private async Task Execute(string sql)
    {
        await using var connection = new SqliteConnection($"Data Source={file};Pooling=False");
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task FreshMigration_IsSchemaOnlyAndRepeatable()
    {
        using var provider = Provider();
        using var scope = provider.CreateScope();
        // Exercise the public operation without any administrator password setting.
        using var host = new TestHost(provider);
        await host.MigrateCmsifyDatabaseAsync(TestContext.Current.CancellationToken);
        var context = scope.ServiceProvider.GetRequiredService<CmsifyDbContext>();
        (await context.Database.GetAppliedMigrationsAsync(TestContext.Current.CancellationToken)).ShouldNotBeEmpty();
        (await context.Database.GetPendingMigrationsAsync(TestContext.Current.CancellationToken)).ShouldBeEmpty();
        (await context.Users.CountAsync(TestContext.Current.CancellationToken)).ShouldBe(0);
        (await context.Workspaces.CountAsync(TestContext.Current.CancellationToken)).ShouldBe(0);
        var repository = new WorkspaceRepository(context,
            new CurrentActorInfo(Guid.NewGuid(), null, UserRole.Admin, null, true, true));
        var workspace = await repository.CreateAsync(new CreateWorkspaceCommand("Retained", "retained", null), TestContext.Current.CancellationToken);
        var template = new Template { Name = "Article", Slug = "article", WorkspaceId = workspace.Id };
        var templateVersion = new TemplateVersion { TemplateId = template.Id, VersionNumber = 1 };
        var item = new ContentItem { WorkspaceId = workspace.Id, TemplateVersionId = templateVersion.Id, Slug = "retained-content" };
        var version = new ContentVersion { ContentItemId = item.Id, WorkspaceId = workspace.Id,
            TemplateVersionId = templateVersion.Id, VersionNumber = 1, Tags = ["puppy", "article"] };
        context.AddRange(template, templateVersion, item, version);
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);
        await Migrate(scope.ServiceProvider);
        context.ChangeTracker.Clear();
        (await repository.GetAsync(workspace.Id, TestContext.Current.CancellationToken))!.Name.ShouldBe("Retained");
        (await context.ContentItems.SingleAsync(TestContext.Current.CancellationToken)).Slug.ShouldBe("retained-content");
        var retainedVersion = await context.ContentVersions.SingleAsync(TestContext.Current.CancellationToken);
        retainedVersion.Tags.ShouldBe(["puppy", "article"]);
        retainedVersion.EffectiveStartAt = new DateTimeOffset(2026, 10, 3, 0, 0, 0, TimeSpan.Zero);
        var invalidRange = await Should.ThrowAsync<DbUpdateException>(() => context.SaveChangesAsync(TestContext.Current.CancellationToken));
        invalidRange.InnerException.ShouldBeOfType<SqliteException>().SqliteExtendedErrorCode.ShouldBe(275);
    }

    [Fact]
    public async Task HostTables_ArePreserved()
    {
        await Execute("CREATE TABLE host_notes (note TEXT); INSERT INTO host_notes VALUES ('retain'); CREATE TABLE __EFMigrationsHistory (MigrationId TEXT); INSERT INTO __EFMigrationsHistory VALUES ('host-only');");
        using var provider = Provider();
        using var scope = provider.CreateScope();
        await Migrate(scope.ServiceProvider);
        await using var connection = new SqliteConnection($"Data Source={file};Pooling=False");
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT note FROM host_notes";
        (await command.ExecuteScalarAsync(TestContext.Current.CancellationToken)).ShouldBe("retain");
        command.CommandText = "SELECT MigrationId FROM __EFMigrationsHistory";
        (await command.ExecuteScalarAsync(TestContext.Current.CancellationToken)).ShouldBe("host-only");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UnmanagedCmsTables_AreRejected(bool emptyHistory)
    {
        await Execute("CREATE TABLE workspaces (id TEXT, name TEXT); INSERT INTO workspaces VALUES ('existing', 'retain');");
        if (emptyHistory) await Execute("CREATE TABLE __CmsifyMigrationsHistory (migration_id TEXT NOT NULL PRIMARY KEY, product_version TEXT NOT NULL);");
        using var provider = Provider();
        using var scope = provider.CreateScope();
        var exception = await Should.ThrowAsync<InvalidOperationException>(() => Migrate(scope.ServiceProvider));
        exception.Message.ShouldContain("unmanaged", Case.Insensitive);
        await using var connection = new SqliteConnection($"Data Source={file};Pooling=False");
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT name FROM workspaces";
        (await command.ExecuteScalarAsync(TestContext.Current.CancellationToken)).ShouldBe("retain");
    }

    [Fact]
    public async Task UnknownOrGappedHistory_IsRejected()
    {
        await Execute("CREATE TABLE __CmsifyMigrationsHistory (migration_id TEXT NOT NULL PRIMARY KEY, product_version TEXT NOT NULL); INSERT INTO __CmsifyMigrationsHistory VALUES ('99999999999999_Unknown', '10.0.11');");
        using var provider = Provider();
        using var scope = provider.CreateScope();
        (await Should.ThrowAsync<InvalidOperationException>(() => Migrate(scope.ServiceProvider))).Message.ShouldContain("history", Case.Insensitive);
    }

    [Fact]
    public void RuntimeModel_MatchesSnapshot()
    {
        using var provider = Provider();
        using var scope = provider.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<CmsifyDbContext>();
        context.Database.GetMigrations().ShouldNotBeEmpty();
        context.Database.HasPendingModelChanges().ShouldBeFalse();
    }

    [Fact]
    public async Task RecognizedHistory_WithPartialSchema_IsRejected()
    {
        using var provider = Provider();
        using var scope = provider.CreateScope();
        await Migrate(scope.ServiceProvider);
        await Execute("DROP TABLE audit_logs;");
        (await Should.ThrowAsync<InvalidOperationException>(() => Migrate(scope.ServiceProvider))).Message.ShouldContain("missing", Case.Insensitive);
    }

    [Fact]
    public async Task MalformedHistory_IsRejected()
    {
        await Execute("CREATE TABLE __CmsifyMigrationsHistory (wrong TEXT);");
        using var provider = Provider();
        using var scope = provider.CreateScope();
        (await Should.ThrowAsync<InvalidOperationException>(() => Migrate(scope.ServiceProvider))).Message.ShouldContain("history", Case.Insensitive);
    }

    [Fact]
    public async Task MigratedSchema_PreservesJsonUtcAndRejectsStaleWrites()
    {
        using var provider = Provider();
        using var first = provider.CreateScope();
        await Migrate(first.ServiceProvider);
        var context = first.ServiceProvider.GetRequiredService<CmsifyDbContext>();
        var workspace = new Workspace { Name = "Original", Slug = "original" };
        var timestamp = new DateTimeOffset(2026, 10, 3, 12, 30, 0, TimeSpan.FromHours(-6));
        var audit = new AuditLog { EntityType = "Fixture", EntityId = workspace.Id,
            Timestamp = timestamp, ChangeDelta = JsonSerializer.Deserialize<JsonElement>("{\"label\":\"puppy\",\"count\":2}") };
        context.AddRange(workspace, audit);
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);
        using var second = provider.CreateScope();
        var other = second.ServiceProvider.GetRequiredService<CmsifyDbContext>();
        var stale = await other.Workspaces.SingleAsync(TestContext.Current.CancellationToken);
        workspace.Name = "Winner";
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);
        stale.Name = "Stale";
        await Should.ThrowAsync<DbUpdateConcurrencyException>(() => other.SaveChangesAsync(TestContext.Current.CancellationToken));
        other.ChangeTracker.Clear();
        var restored = await other.AuditLogs.SingleAsync(log => log.Id == audit.Id, TestContext.Current.CancellationToken);
        restored.Timestamp.ShouldBe(timestamp.ToUniversalTime());
        restored.Timestamp.Offset.ShouldBe(TimeSpan.Zero);
        restored.ChangeDelta!.Value.GetProperty("label").GetString().ShouldBe("puppy");
        restored.ChangeDelta.Value.GetProperty("count").GetInt32().ShouldBe(2);
        await Migrate(second.ServiceProvider);
        (await other.Workspaces.SingleAsync(TestContext.Current.CancellationToken)).Name.ShouldBe("Winner");
    }

    [Fact]
    public async Task MigratedSchema_EnforcesUniqueIndexesAndForeignKeys()
    {
        using var provider = Provider();
        using var scope = provider.CreateScope();
        await Migrate(scope.ServiceProvider);
        var context = scope.ServiceProvider.GetRequiredService<CmsifyDbContext>();
        context.Workspaces.Add(new Workspace { Name = "First", Slug = "unique" });
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);
        context.Workspaces.Add(new Workspace { Name = "Duplicate", Slug = "unique" });
        var duplicate = await Should.ThrowAsync<DbUpdateException>(() => context.SaveChangesAsync(TestContext.Current.CancellationToken));
        duplicate.InnerException.ShouldBeOfType<SqliteException>().SqliteExtendedErrorCode.ShouldBe(2067);
        context.ChangeTracker.Clear();
        context.Templates.Add(new Template { Name = "Orphan", Slug = "orphan", WorkspaceId = Guid.NewGuid() });
        var orphan = await Should.ThrowAsync<DbUpdateException>(() => context.SaveChangesAsync(TestContext.Current.CancellationToken));
        orphan.InnerException.ShouldBeOfType<SqliteException>().SqliteExtendedErrorCode.ShouldBe(787);
    }

    [Fact]
    public async Task ExplicitSeeder_IsSeparateAndRepeatable()
    {
        using var provider = Provider();
        using var scope = provider.CreateScope();
        await Migrate(scope.ServiceProvider);
        var context = scope.ServiceProvider.GetRequiredService<CmsifyDbContext>();
        var settings = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Seed:Admin:Password"] = "Fixture-Admin-123!",
            ["Auth:BcryptCost"] = "4"
        }).Build();
        var seeder = new DbSeeder(context, settings);
        await seeder.SeedAsync(TestContext.Current.CancellationToken);
        await seeder.SeedAsync(TestContext.Current.CancellationToken);
        (await context.Workspaces.CountAsync(TestContext.Current.CancellationToken)).ShouldBe(1);
        (await context.Users.SingleAsync(TestContext.Current.CancellationToken)).Email.ShouldBe("admin@localhost");
    }

    [Theory]
    [InlineData("CREATE TABLE __CmsifyMigrationsHistory (migration_id TEXT NOT NULL, product_version TEXT NOT NULL);")]
    [InlineData("CREATE TABLE __CmsifyMigrationsHistory (migration_id TEXT NOT NULL PRIMARY KEY, product_version TEXT NOT NULL); INSERT INTO __CmsifyMigrationsHistory VALUES ('known', 'garbage');")]
    [InlineData("CREATE VIEW __CmsifyMigrationsHistory AS SELECT 'known' AS migration_id, '10.0.11' AS product_version;")]
    public async Task MalformedHistoryShapeOrValues_IsRejected(string sql)
    {
        await Execute(sql);
        using var provider = Provider();
        using var scope = provider.CreateScope();
        (await Should.ThrowAsync<InvalidOperationException>(() => Migrate(scope.ServiceProvider))).Message.ShouldContain("Malformed", Case.Insensitive);
    }

    [Fact]
    public async Task ReadOnlyFile_FailsWithoutReportingSuccess()
    {
        await Execute("CREATE TABLE host_notes (note TEXT);");
        using var provider = Provider($"Data Source={file};Mode=ReadOnly;Pooling=False");
        using var scope = provider.CreateScope();
        await Should.ThrowAsync<SqliteException>(() => Migrate(scope.ServiceProvider));
    }

    public void Dispose() { File.Delete(file); File.Delete(file + "-wal"); File.Delete(file + "-shm"); }
    private sealed class TestHost(IServiceProvider services) : IHost
    {
        public IServiceProvider Services => services;
        public void Dispose() { }
        public Task StartAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task StopAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
