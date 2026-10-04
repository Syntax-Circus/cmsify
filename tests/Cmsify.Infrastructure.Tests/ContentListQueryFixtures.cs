using Cmsify.Core.Domain.Entities;
using Cmsify.Core.Domain.Enums;
using Cmsify.Infrastructure.Extensions;
using Cmsify.Infrastructure.Persistence;
using Cmsify.Infrastructure.Sqlite.Extensions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SyntaxCircus.EntityFrameworkCore.Postgres;
using Testcontainers.PostgreSql;

namespace Cmsify.Infrastructure.Tests;

/// <summary>Owned, migrated real-provider databases. No test-created schema.</summary>
internal sealed class ContentListQueryFixtures : IAsyncDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"cmsify-content-query-{Guid.NewGuid():N}.db");
    private PostgreSqlContainer? _postgres;
    private ServiceProvider? _services;
    private string? _connectionString;
    public required bool Sqlite { get; init; }
    public DbContextOptions<CmsifyDbContext> Options { get; private set; } = null!;
    public Workspace Workspace { get; } = new() { Name = "Query feasibility", Slug = "query-feasibility" };
    public TemplateVersion TemplateVersion { get; private set; } = null!;

    public static async Task<ContentListQueryFixtures> Create(bool sqlite)
    {
        var fixture = new ContentListQueryFixtures { Sqlite = sqlite };
        try
        {
            if (sqlite)
            {
                var services = SqliteRegistrationTests.Services();
                services.AddCmsifySqliteInfrastructure(SqliteRegistrationTests.Configuration($"Data Source={fixture._path};Pooling=True"),
                    new() { Workers = CmsifyWorkers.None });
                fixture._services = services.BuildServiceProvider();
                using var scope = fixture._services.CreateScope();
                var context = scope.ServiceProvider.GetRequiredService<CmsifyDbContext>();
                fixture._connectionString = context.Database.GetConnectionString();
                fixture.Options = new DbContextOptionsBuilder<CmsifyDbContext>()
                    .UseSqlite(fixture._connectionString, options => options.MigrationsAssembly("Cmsify.Infrastructure.Sqlite")
                        .MigrationsHistoryTable("__CmsifyMigrationsHistory"))
                    .UseSnakeCaseNamingConvention().Options;
                await scope.ServiceProvider.GetRequiredService<ICmsifyDatabaseMigrator>()
                    .MigrateAsync(TestContext.Current.CancellationToken);
            }
            else
            {
                fixture._postgres = new PostgreSqlBuilder("postgres:17-alpine")
                    .WithDatabase("content_query_feasibility").WithUsername("cmsify").WithPassword("cmsify")
                    .WithEnvironment("POSTGRES_INITDB_ARGS", "--locale=en_US.utf8").Build();
                await fixture._postgres.StartAsync(TestContext.Current.CancellationToken);
                fixture.Options = new DbContextOptionsBuilder<CmsifyDbContext>()
                    .UseNpgsql(fixture._postgres.GetConnectionString()).UseSyntaxCircusSnakeCaseNamingConvention().Options;
                await using var context = fixture.Context();
                await context.Database.MigrateAsync(TestContext.Current.CancellationToken);
            }
            await using var seed = fixture.Context();
            Assert.NotEmpty(await seed.Database.GetAppliedMigrationsAsync(TestContext.Current.CancellationToken));
            var template = new Template { WorkspaceId = fixture.Workspace.Id, Name = "Article", Slug = "article" };
            fixture.TemplateVersion = new TemplateVersion { TemplateId = template.Id, VersionNumber = 1 };
            seed.AddRange(fixture.Workspace, template, fixture.TemplateVersion);
            await seed.SaveChangesAsync(TestContext.Current.CancellationToken);
            return fixture;
        }
        catch
        {
            await fixture.DisposeAsync();
            throw;
        }
    }

    public CmsifyDbContext Context() => new(Options);
    public ContentItem Item(string slug) => new() { WorkspaceId = Workspace.Id, TemplateVersionId = TemplateVersion.Id, Slug = slug };
    public ContentVersion Version(ContentItem item, int number, DateTimeOffset? start = null, DateTimeOffset? end = null) => new()
    {
        ContentItemId = item.Id, WorkspaceId = Workspace.Id, TemplateVersionId = TemplateVersion.Id,
        VersionNumber = number, Status = ContentStatus.Published, Slug = item.Slug,
        EffectiveStartAt = start, EffectiveEndAt = end
    };

    public async ValueTask DisposeAsync()
    {
        if (_services is not null) await _services.DisposeAsync();
        if (_postgres is not null) await _postgres.DisposeAsync();
        if (Sqlite)
        {
            using var pool = new SqliteConnection(_connectionString ?? $"Data Source={_path};Pooling=True");
            SqliteConnection.ClearPool(pool);
            foreach (var suffix in new[] { "", "-wal", "-shm" }) File.Delete(_path + suffix);
        }
    }
}
