using Cmsify.Core.Domain.Entities;
using Cmsify.Infrastructure.Persistence;
using Cmsify.Infrastructure.Persistence.Repositories;
using Cmsify.Core.Domain.Enums;
using Cmsify.Core.Interfaces.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Shouldly;
using Xunit;
using SyntaxCircus.EntityFrameworkCore.Postgres;
using System.Text.Json;

namespace Cmsify.Infrastructure.Tests;

public sealed class ProviderModelTests
{
    [Fact]
    public async Task Sqlite_IndependentConnectionsClaimDueVersionOnlyOnce()
    {
        var ct = TestContext.Current.CancellationToken;
        var path = Path.Combine(Path.GetTempPath(), $"cmsify-claims-{Guid.NewGuid():N}.db");
        var options = new DbContextOptionsBuilder<CmsifyDbContext>()
            .UseSqlite($"Data Source={path};Pooling=False;Default Timeout=15").UseSnakeCaseNamingConvention().Options;
        try
        {
            var now = DateTimeOffset.UtcNow;
            await using (var setup = new CmsifyDbContext(options))
            {
                await setup.Database.EnsureCreatedAsync(ct);
                var workspace = new Workspace { Name = "Claims", Slug = "claims" };
                var template = new Template { WorkspaceId = workspace.Id, Name = "Profile", Slug = "profile" };
                var templateVersion = new TemplateVersion { TemplateId = template.Id, VersionNumber = 1 };
                var content = new ContentItem { WorkspaceId = workspace.Id, TemplateVersionId = templateVersion.Id };
                var version = new ContentVersion
                {
                    WorkspaceId = workspace.Id, ContentItemId = content.Id,
                    TemplateVersionId = templateVersion.Id, VersionNumber = 1,
                    Status = ContentStatus.Approved, PublishAt = now.AddSeconds(-1)
                };
                setup.AddRange(workspace, template, templateVersion, content, version);
                await setup.SaveChangesAsync(ct);
            }
            var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var claimers = Enumerable.Range(0, 4).Select(worker => Task.Run(async () =>
            {
                await start.Task.WaitAsync(ct);
                await using var db = new CmsifyDbContext(options);
                var repository = new ScheduledPublishingRepository(db,
                    new ContentPublishingService(db, CurrentActorInfo.Anonymous), new EfWebhookOutbox(db));
                return await repository.ClaimDueContentAsync($"worker-{worker}", now, TimeSpan.FromMinutes(1), 1, ct);
            }, ct)).ToArray();
            start.SetResult();
            var results = await Task.WhenAll(claimers);
            results.SelectMany(claims => claims).Count().ShouldBe(1);
        }
        finally
        {
            foreach (var suffix in new[] { "", "-wal", "-shm" }) File.Delete(path + suffix);
        }
    }

    [Fact]
    public void Postgres_ModelStillMatchesExistingMigrationSnapshot()
    {
        using var context = new CmsifyDbContext(new DbContextOptionsBuilder<CmsifyDbContext>()
            .UseNpgsql("Host=localhost;Database=unused_model_only")
            .UseSyntaxCircusSnakeCaseNamingConvention().Options);
        context.Database.HasPendingModelChanges().ShouldBeFalse();
    }

    [Fact]
    public async Task Sqlite_ScheduledPublicationReclaimsExpiredLeaseAndFencesOldCompletion()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync(ct);
        var options = new DbContextOptionsBuilder<CmsifyDbContext>()
            .UseSqlite(connection).UseSnakeCaseNamingConvention().Options;
        var now = DateTimeOffset.UtcNow;
        await using (var setup = new CmsifyDbContext(options))
        {
            await setup.Database.EnsureCreatedAsync(ct);
            var workspace = new Workspace { Name = "Test", Slug = "test" };
            var template = new Template { WorkspaceId = workspace.Id, Name = "Profile", Slug = "profile" };
            var templateVersion = new TemplateVersion { TemplateId = template.Id, VersionNumber = 1 };
            var content = new ContentItem { WorkspaceId = workspace.Id, TemplateVersionId = templateVersion.Id };
            var version = new ContentVersion
            {
                WorkspaceId = workspace.Id, ContentItemId = content.Id,
                TemplateVersionId = templateVersion.Id, VersionNumber = 1,
                Status = ContentStatus.Approved, PublishAt = now.AddMinutes(-1)
            };
            setup.AddRange(workspace, template, templateVersion, content, version);
            await setup.SaveChangesAsync(ct);
        }
        await using var first = new CmsifyDbContext(options);
        var firstRepository = new ScheduledPublishingRepository(first,
            new ContentPublishingService(first, CurrentActorInfo.Anonymous), new EfWebhookOutbox(first));
        var original = (await firstRepository.ClaimDueContentAsync("first", now, TimeSpan.FromSeconds(1), 1, ct)).Single();
        await using var second = new CmsifyDbContext(options);
        var secondRepository = new ScheduledPublishingRepository(second,
            new ContentPublishingService(second, CurrentActorInfo.Anonymous), new EfWebhookOutbox(second));
        (await secondRepository.ClaimDueContentAsync("second", now, TimeSpan.FromSeconds(1), 1, ct)).ShouldBeEmpty();
        var reclaimed = (await secondRepository.ClaimDueContentAsync("second", now.AddSeconds(2), TimeSpan.FromSeconds(5), 1, ct)).Single();
        reclaimed.WasReclaimed.ShouldBeTrue();
        reclaimed.LeaseToken.ShouldNotBe(original.LeaseToken);
        (await firstRepository.CompleteClaimAsync(original, now.AddSeconds(3), ct)).ShouldBeFalse();
        (await secondRepository.CompleteClaimAsync(reclaimed, now.AddSeconds(3), ct)).ShouldBeTrue();
        (await secondRepository.CompleteClaimAsync(reclaimed, now.AddSeconds(3), ct)).ShouldBeFalse();
        await using var verification = new CmsifyDbContext(options);
        (await verification.ContentVersions.SingleAsync(ct)).Status.ShouldBe(ContentStatus.Published);
        (await verification.WebhookOutboxEvents.CountAsync(ct)).ShouldBe(1);
    }

    [Fact]
    public async Task Sqlite_SharedEntitiesPersistTagsAndRejectStaleUpdate()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync(cancellationToken);
        var options = new DbContextOptionsBuilder<CmsifyDbContext>()
            .UseSqlite(connection).UseSnakeCaseNamingConvention().Options;
        await using var first = new CmsifyDbContext(options);
        await first.Database.EnsureCreatedAsync(cancellationToken);
        var workspace = new Workspace { Name = "Before", Slug = "portable" };
        var template = new Template { WorkspaceId = workspace.Id, Name = "Profile", Slug = "profile" };
        var templateVersion = new TemplateVersion { TemplateId = template.Id, VersionNumber = 1 };
        var content = new ContentItem { WorkspaceId = workspace.Id, TemplateVersionId = templateVersion.Id };
        var version = new ContentVersion
        {
            WorkspaceId = workspace.Id, ContentItemId = content.Id,
            TemplateVersionId = templateVersion.Id, VersionNumber = 1,
            Tags = ["café", "puppy"]
        };
        version.FieldValues.Add(new ContentVersionFieldValue
        {
            ContentVersionId = version.Id, FieldId = Guid.NewGuid(),
            JsonValue = JsonSerializer.SerializeToElement(new { name = "café", count = 3 })
        });
        first.AddRange(workspace, template, templateVersion, content, version);
        await first.SaveChangesAsync(cancellationToken);
        await using var second = new CmsifyDbContext(options);
        var stale = await second.Workspaces.SingleAsync(cancellationToken);
        (await second.ContentVersions.SingleAsync(cancellationToken)).Tags.ShouldBe(new[] { "café", "puppy" });
        var json = (await second.ContentVersionFieldValues.SingleAsync(cancellationToken)).JsonValue!.Value;
        json.GetProperty("name").GetString().ShouldBe("café");
        json.GetProperty("count").GetInt32().ShouldBe(3);
        version.Tags.Add("reserved");
        workspace.Name = "Winner";
        await first.SaveChangesAsync(cancellationToken);
        stale.Name = "Loser";
        await Should.ThrowAsync<DbUpdateConcurrencyException>(() => second.SaveChangesAsync(cancellationToken));
        await using var verification = new CmsifyDbContext(options);
        (await verification.Workspaces.SingleAsync(cancellationToken)).Name.ShouldBe("Winner");
        (await verification.ContentVersions.SingleAsync(cancellationToken)).Tags.ShouldBe(new[] { "café", "puppy", "reserved" });
    }
}
