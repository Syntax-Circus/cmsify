using Cmsify.Core.Domain.Entities;
using Cmsify.Core.Domain.Enums;
using Cmsify.Infrastructure.Persistence;
using Cmsify.Infrastructure.Persistence.EmbeddedMedia;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using SyntaxCircus.EntityFrameworkCore.Postgres;
using Testcontainers.PostgreSql;

namespace Cmsify.Infrastructure.Tests.Fixtures;

internal sealed class EmbeddedPhotoFixture : IAsyncDisposable
{
    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("postgres:17-alpine")
        .WithDatabase("embedded_photo_tests").WithUsername("cmsify").WithPassword("cmsify").Build();
    public static CancellationToken Ct => TestContext.Current.CancellationToken;
    public DbContextOptions<CmsifyDbContext> Options { get; private set; } = null!;
    public Workspace Workspace { get; } = new() { Name = "Photo tests", Slug = "photo-tests" };
    public CmsifyDbContext Context() => new(Options);
    public string ConnectionString => _postgres.GetConnectionString();
    public static async Task<EmbeddedPhotoFixture> Create(string? migration = null)
    {
        var fixture = new EmbeddedPhotoFixture();
        try
        {
            await fixture._postgres.StartAsync(Ct);
            fixture.Options = new DbContextOptionsBuilder<CmsifyDbContext>().UseNpgsql(fixture._postgres.GetConnectionString())
                .UseSyntaxCircusSnakeCaseNamingConvention().Options;
            await using var db = fixture.Context();
            await db.GetService<IMigrator>().MigrateAsync(migration, Ct);
            db.Workspaces.Add(fixture.Workspace);
            await db.SaveChangesAsync(Ct);
            return fixture;
        }
        catch { await fixture.DisposeAsync(); throw; }
    }
    public MediaAsset Asset(string key) => new()
    {
        WorkspaceId = Workspace.Id, FileName = "phone.jpg", MimeType = "image/jpeg", SizeBytes = 99,
        StorageProvider = "local", StorageKey = key, BlobState = MediaBlobState.Available
    };
    public PhotoUploadSessionRecord Session(MediaAsset asset, Guid? operation = null) => new()
    {
        WorkspaceId = Workspace.Id, AssetId = asset.Id, OperationKey = operation ?? Guid.NewGuid(),
        DisplayFileName = asset.FileName, DeclaredLength = 99, PolicyId = "test-policy", ProcessorVersion = "test-processor"
    };
    public ValueTask DisposeAsync() => _postgres.DisposeAsync();
}
