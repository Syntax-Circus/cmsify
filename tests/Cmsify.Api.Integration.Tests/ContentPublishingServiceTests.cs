using Cmsify.Core.Domain.Entities;
using Cmsify.Core.Domain.Enums;
using Cmsify.Core.Interfaces.Services;
using Cmsify.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Testcontainers.PostgreSql;

namespace Cmsify.Api.Integration.Tests;

public sealed class ContentPublishingServiceTests : IAsyncLifetime
{
    private readonly PostgreSqlContainer postgres = new PostgreSqlBuilder("postgres:17-alpine")
        .WithDatabase("cmsify")
        .WithUsername("cmsify")
        .WithPassword("cmsify")
        .Build();

    private CmsifyDbContext dbContext = null!;

    public async ValueTask InitializeAsync()
    {
        await postgres.StartAsync();
        var options = new DbContextOptionsBuilder<CmsifyDbContext>()
            .UseNpgsql(postgres.GetConnectionString())
            .Options;
        dbContext = new CmsifyDbContext(options);
        await dbContext.Database.MigrateAsync();
    }

    public async ValueTask DisposeAsync()
    {
        await dbContext.DisposeAsync();
        await postgres.DisposeAsync();
    }

    [Fact]
    public async Task PublishAsync_TransitionsExistingDraftVersion_WithoutCreatingNewRow()
    {
        var (item, version) = await SeedDraftVersionAsync(effectiveStartAt: null, effectiveEndAt: null);
        var service = new ContentPublishingService(dbContext, CurrentActorInfo.Anonymous);

        var result = await service.PublishAsync(version, actorUserId: item.CreatedByUserId, TestContext.Current.CancellationToken);
        await dbContext.SaveChangesAsync(TestContext.Current.CancellationToken);

        Assert.Equal(version.Id, result.Version.Id);
        Assert.Equal(ContentStatus.Published, result.Version.Status);
        Assert.NotNull(result.Version.PublishedAt);
        Assert.Empty(result.Warnings);
        var versionCount = await dbContext.ContentVersions.CountAsync(v => v.ContentItemId == item.Id, TestContext.Current.CancellationToken);
        Assert.Equal(1, versionCount);
    }

    [Fact]
    public async Task PublishAsync_ArchivesPriorDefaultVersion_WhenPublishingNewDefault()
    {
        var (item, firstDefault) = await SeedDraftVersionAsync(effectiveStartAt: null, effectiveEndAt: null);
        var service = new ContentPublishingService(dbContext, CurrentActorInfo.Anonymous);
        await service.PublishAsync(firstDefault, ct: TestContext.Current.CancellationToken);
        await dbContext.SaveChangesAsync(TestContext.Current.CancellationToken);

        var secondDefault = await SeedAdditionalVersionAsync(item, effectiveStartAt: null, effectiveEndAt: null);
        await service.PublishAsync(secondDefault, ct: TestContext.Current.CancellationToken);
        await dbContext.SaveChangesAsync(TestContext.Current.CancellationToken);

        var reloadedFirst = await dbContext.ContentVersions.AsNoTracking().FirstAsync(v => v.Id == firstDefault.Id, TestContext.Current.CancellationToken);
        Assert.Equal(ContentStatus.Archived, reloadedFirst.Status);
        Assert.NotNull(reloadedFirst.ArchivedAt);
    }

    [Fact]
    public async Task PublishAsync_WarnsOnEqualSpecificityOverlap_ForBoundedRanges()
    {
        var (item, _) = await SeedDraftVersionAsync(effectiveStartAt: null, effectiveEndAt: null);
        var service = new ContentPublishingService(dbContext, CurrentActorInfo.Anonymous);
        var rangeA = await SeedAdditionalVersionAsync(item, DateTimeOffset.Parse("2026-12-01T00:00:00Z"), DateTimeOffset.Parse("2026-12-08T00:00:00Z"));
        await service.PublishAsync(rangeA, ct: TestContext.Current.CancellationToken);
        await dbContext.SaveChangesAsync(TestContext.Current.CancellationToken);

        var rangeB = await SeedAdditionalVersionAsync(item, DateTimeOffset.Parse("2026-12-04T00:00:00Z"), DateTimeOffset.Parse("2026-12-11T00:00:00Z"));
        var result = await service.PublishAsync(rangeB, ct: TestContext.Current.CancellationToken);

        Assert.Single(result.Warnings);
    }

    private async Task<(ContentItem Item, ContentVersion Version)> SeedDraftVersionAsync(DateTimeOffset? effectiveStartAt, DateTimeOffset? effectiveEndAt)
    {
        var workspace = new Workspace { Name = "Test", Slug = $"test-{Guid.CreateVersion7()}" };
        var template = new Template { WorkspaceId = workspace.Id, Name = "Page", Slug = $"page-{Guid.CreateVersion7()}" };
        var templateVersion = new TemplateVersion { TemplateId = template.Id, VersionNumber = 1, Status = TemplateVersionStatus.Published, PublishedAt = DateTimeOffset.UtcNow };
        var item = new ContentItem { WorkspaceId = workspace.Id, TemplateVersionId = templateVersion.Id, Slug = $"item-{Guid.CreateVersion7()}" };
        dbContext.Workspaces.Add(workspace);
        dbContext.Templates.Add(template);
        dbContext.TemplateVersions.Add(templateVersion);
        dbContext.ContentItems.Add(item);
        await dbContext.SaveChangesAsync(TestContext.Current.CancellationToken);

        var version = await SeedAdditionalVersionAsync(item, effectiveStartAt, effectiveEndAt);
        return (item, version);
    }

    private async Task<ContentVersion> SeedAdditionalVersionAsync(ContentItem item, DateTimeOffset? effectiveStartAt, DateTimeOffset? effectiveEndAt)
    {
        var nextNumber = 1 + await dbContext.ContentVersions.Where(v => v.ContentItemId == item.Id).Select(v => (int?)v.VersionNumber).MaxAsync(TestContext.Current.CancellationToken) ?? 1;
        var version = new ContentVersion
        {
            ContentItemId = item.Id,
            WorkspaceId = item.WorkspaceId,
            VersionNumber = nextNumber,
            Status = ContentStatus.Approved,
            TemplateVersionId = item.TemplateVersionId,
            Slug = item.Slug,
            EffectiveStartAt = effectiveStartAt,
            EffectiveEndAt = effectiveEndAt
        };
        dbContext.ContentVersions.Add(version);
        await dbContext.SaveChangesAsync(TestContext.Current.CancellationToken);
        return version;
    }
}
