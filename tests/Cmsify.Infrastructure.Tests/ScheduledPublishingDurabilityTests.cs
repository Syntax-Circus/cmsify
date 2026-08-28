using Cmsify.Core.Domain.Entities;
using Cmsify.Core.Domain.Enums;
using Cmsify.Core.Interfaces.Repositories;
using Cmsify.Core.Interfaces.Services;
using Cmsify.Infrastructure.BackgroundServices;
using Cmsify.Infrastructure.Persistence;
using Cmsify.Infrastructure.Persistence.Repositories;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using SyntaxCircus.EntityFrameworkCore.Postgres;
using Testcontainers.PostgreSql;
using System.Data.Common;

namespace Cmsify.Infrastructure.Tests;

public sealed class ScheduledPublishingDurabilityTests : IAsyncLifetime
{
    private readonly PostgreSqlContainer postgres = new PostgreSqlBuilder("postgres:17-alpine")
        .WithDatabase("cmsify")
        .WithUsername("cmsify")
        .WithPassword("cmsify")
        .Build();

    public ValueTask InitializeAsync() => new(postgres.StartAsync());

    public async ValueTask DisposeAsync() => await postgres.DisposeAsync();

    [Fact]
    public async Task ConcurrentDispatchers_PublishOneDueItemExactlyOnce()
    {
        var dueAt = DateTimeOffset.Parse("2026-08-26T10:00:00Z");
        await using (var setup = await CreateContextAsync())
        {
            var workspace = new Workspace { Name = "Scheduled durability", Slug = "scheduled-durability" };
            var template = new Template { WorkspaceId = workspace.Id, Name = "Scheduled page", Slug = "scheduled-page" };
            var version = new TemplateVersion { TemplateId = template.Id, VersionNumber = 1, Status = TemplateVersionStatus.Published, PublishedAt = dueAt };
            var content = new ContentItem
            {
                WorkspaceId = workspace.Id,
                TemplateVersionId = version.Id,
                Status = ContentStatus.Approved,
                Slug = "due-content",
                PublishAt = dueAt
            };
            setup.AddRange(workspace, template, version, content);
            await setup.SaveChangesAsync();
            template.CurrentVersionId = version.Id;
            await setup.SaveChangesAsync();
        }

        await using var firstContext = await CreateContextAsync();
        await using var secondContext = await CreateContextAsync();
        var first = CreateDispatcher(firstContext);
        var second = CreateDispatcher(secondContext);
        var release = new ConcurrentStartGate(2);

        var results = await Task.WhenAll(RunAsync(first, "worker-a", dueAt, release), RunAsync(second, "worker-b", dueAt, release));

        await using var verification = await CreateContextAsync();
        var persisted = await verification.ContentItems.SingleAsync(item => item.Slug == "due-content");
        Assert.Equal(ContentStatus.Published, persisted.Status);
        Assert.Null(persisted.PublishAt);
        Assert.Equal(1, await verification.ContentVersions.CountAsync(item => item.ContentItemId == persisted.Id));
        Assert.Equal(1, await verification.WebhookOutboxEvents.CountAsync(item => item.EntityId == persisted.Id && item.EventType == "content.published"));
        Assert.Equal(1, results.Count(result => result));
    }

    [Fact]
    public async Task ActiveLeaseIsNotStolen_ExpiredLeaseIsReclaimed_AndStaleTokenCannotPublish()
    {
        var now = DateTimeOffset.Parse("2026-08-26T11:00:00Z");
        var contentId = await SeedDueContentAsync("lease-content", now);
        await using var firstContext = await CreateContextAsync();
        var first = CreateDispatcher(firstContext);
        var firstClaim = Assert.Single(await first.ClaimDueAsync("worker-a", now, TimeSpan.FromMinutes(1), 1));

        await using var secondContext = await CreateContextAsync();
        var second = CreateDispatcher(secondContext);
        Assert.Empty(await second.ClaimDueAsync("worker-b", now.AddSeconds(30), TimeSpan.FromMinutes(1), 1));
        var secondClaim = Assert.Single(await second.ClaimDueAsync("worker-b", now.AddMinutes(1), TimeSpan.FromMinutes(1), 1));
        Assert.NotEqual(firstClaim.LeaseToken, secondClaim.LeaseToken);
        Assert.False(await first.CompleteClaimAsync(firstClaim, now.AddMinutes(1)));
        Assert.True(await second.CompleteClaimAsync(secondClaim, now.AddMinutes(1)));

        await using var verification = await CreateContextAsync();
        var persisted = await verification.ContentItems.SingleAsync(item => item.Id == contentId);
        Assert.Equal(ContentStatus.Published, persisted.Status);
        Assert.Null(persisted.PublishLeaseOwner);
        Assert.Null(persisted.PublishLeaseToken);
        Assert.Null(persisted.PublishLeaseExpiresAt);
        Assert.Equal(1, await verification.ContentVersions.CountAsync(item => item.ContentItemId == contentId));
        Assert.Equal(1, await verification.WebhookOutboxEvents.CountAsync(item => item.EntityId == contentId && item.EventType == "content.published"));
    }

    [Fact]
    public async Task FailedCompletionRollsBackPublicationAndRecoversAfterLeaseExpiry()
    {
        var now = DateTimeOffset.Parse("2026-08-26T12:00:00Z");
        var contentId = await SeedDueContentAsync("rollback-content", now);
        await using var firstContext = await CreateContextAsync();
        var first = CreateDispatcher(firstContext);
        var firstClaim = Assert.Single(await first.ClaimDueAsync("worker-a", now, TimeSpan.FromMinutes(1), 1));
        await firstContext.Database.ExecuteSqlRawAsync("ALTER TABLE webhook_outbox_events ADD CONSTRAINT reject_scheduled_publish CHECK (event_type <> 'content.published')");

        await Assert.ThrowsAsync<DbUpdateException>(() => first.CompleteClaimAsync(firstClaim, now));

        await using (var rolledBack = await CreateContextAsync())
        {
            var persisted = await rolledBack.ContentItems.SingleAsync(item => item.Id == contentId);
            Assert.Equal(ContentStatus.Approved, persisted.Status);
            Assert.Equal(now, persisted.PublishAt);
            Assert.Equal("worker-a", persisted.PublishLeaseOwner);
            Assert.Equal(firstClaim.LeaseToken, persisted.PublishLeaseToken);
            Assert.Equal(0, await rolledBack.ContentVersions.CountAsync(item => item.ContentItemId == contentId));
            Assert.Equal(0, await rolledBack.WebhookOutboxEvents.CountAsync(item => item.EntityId == contentId));
        }

        await firstContext.Database.ExecuteSqlRawAsync("ALTER TABLE webhook_outbox_events DROP CONSTRAINT reject_scheduled_publish");
        await using var recoveryContext = await CreateContextAsync();
        var recovery = CreateDispatcher(recoveryContext);
        var recoveryClaim = Assert.Single(await recovery.ClaimDueAsync("worker-b", now.AddMinutes(1), TimeSpan.FromMinutes(1), 1));
        Assert.True(await recovery.CompleteClaimAsync(recoveryClaim, now.AddMinutes(1)));
        await using var verification = await CreateContextAsync();
        Assert.Equal(1, await verification.ContentVersions.CountAsync(item => item.ContentItemId == contentId));
        Assert.Equal(1, await verification.WebhookOutboxEvents.CountAsync(item => item.EntityId == contentId && item.EventType == "content.published"));
    }

    [Fact]
    public async Task ExpiredScheduleLease_CannotPublishBeforeAnotherWorkerReclaimsIt()
    {
        var now = DateTimeOffset.Parse("2026-08-26T12:30:00Z");
        var contentId = await SeedDueContentAsync("expired-schedule", now);
        var leaseToken = Guid.CreateVersion7();
        await using (var setup = await CreateContextAsync())
        {
            var content = await setup.ContentItems.SingleAsync(item => item.Id == contentId);
            content.PublishLeaseOwner = "expired-worker";
            content.PublishLeaseToken = leaseToken;
            content.PublishLeaseExpiresAt = now.AddTicks(-1);
            await setup.SaveChangesAsync();
        }

        await using var worker = await CreateContextAsync();
        var completed = await CreateDispatcher(worker).CompleteClaimAsync(
            new ScheduledContentClaimDto(contentId, "expired-worker", leaseToken), now);

        Assert.False(completed);
        await using var verification = await CreateContextAsync();
        var persisted = await verification.ContentItems.SingleAsync(item => item.Id == contentId);
        Assert.Equal(ContentStatus.Approved, persisted.Status);
        Assert.Equal("expired-worker", persisted.PublishLeaseOwner);
        Assert.Equal(leaseToken, persisted.PublishLeaseToken);
    }

    [Fact]
    public async Task CompletionLock_PreventsExpiredScheduleClaimFromBeingReclaimedBeforeItCommits()
    {
        var now = DateTimeOffset.Parse("2026-08-26T12:40:00Z");
        var contentId = await SeedDueContentAsync("schedule-lock", now);
        await using var claimantContext = await CreateContextAsync();
        var claim = Assert.Single(await CreateDispatcher(claimantContext).ClaimDueAsync("worker-a", now, TimeSpan.FromSeconds(1), 1));
        var pause = new PauseAfterForUpdateInterceptor();
        await using var completionContext = await CreateContextAsync(pause);
        var completing = CreateDispatcher(completionContext).CompleteClaimAsync(claim, now);
        await pause.Reached.Task.WaitAsync(TimeSpan.FromSeconds(10));

        await using var reclaimerContext = await CreateContextAsync();
        Assert.Empty(await CreateDispatcher(reclaimerContext).ClaimDueAsync("worker-b", now.AddMinutes(1), TimeSpan.FromMinutes(1), 1));
        pause.Release.TrySetResult();
        Assert.True(await completing);

        await using var verification = await CreateContextAsync();
        var persisted = await verification.ContentItems.SingleAsync(item => item.Id == contentId);
        Assert.Equal(ContentStatus.Published, persisted.Status);
        Assert.Null(persisted.PublishLeaseOwner);
        Assert.Equal(1, await verification.ContentVersions.CountAsync(item => item.ContentItemId == contentId));
        Assert.Equal(1, await verification.WebhookOutboxEvents.CountAsync(item => item.EntityId == contentId && item.EventType == "content.published"));
    }

    private static async Task<bool> RunAsync(IScheduledPublishingDispatcher dispatcher, string workerId, DateTimeOffset now, ConcurrentStartGate? release = null)
    {
        if (release is not null)
        {
            await release.WaitAsync();
        }

        var claims = await dispatcher.ClaimDueAsync(workerId, now, TimeSpan.FromMinutes(5), 1);
        var completed = false;
        foreach (var claim in claims)
        {
            completed |= await dispatcher.CompleteClaimAsync(claim, now);
        }

        return completed;
    }

    private static IScheduledPublishingDispatcher CreateDispatcher(CmsifyDbContext context) =>
        new ScheduledPublishingDispatcher(new ScheduledPublishingRepository(
            context,
            new ContentPublishingService(context, CurrentActorInfo.Anonymous),
            new EfWebhookOutbox(context)));

    private async Task<Guid> SeedDueContentAsync(string slug, DateTimeOffset dueAt)
    {
        await using var setup = await CreateContextAsync();
        var workspace = new Workspace { Name = slug, Slug = slug };
        var template = new Template { WorkspaceId = workspace.Id, Name = slug, Slug = slug };
        var version = new TemplateVersion { TemplateId = template.Id, VersionNumber = 1, Status = TemplateVersionStatus.Published, PublishedAt = dueAt };
        var content = new ContentItem { WorkspaceId = workspace.Id, TemplateVersionId = version.Id, Status = ContentStatus.Approved, Slug = slug, PublishAt = dueAt };
        setup.AddRange(workspace, template, version, content);
        await setup.SaveChangesAsync();
        template.CurrentVersionId = version.Id;
        await setup.SaveChangesAsync();
        return content.Id;
    }

    private async Task<CmsifyDbContext> CreateContextAsync(DbCommandInterceptor? interceptor = null)
    {
        var builder = new DbContextOptionsBuilder<CmsifyDbContext>()
            .UseNpgsql(postgres.GetConnectionString())
            .UseSyntaxCircusSnakeCaseNamingConvention();
        if (interceptor is not null) builder.AddInterceptors(interceptor);
        var options = builder.Options;
        var context = new CmsifyDbContext(options);
        await context.Database.MigrateAsync();
        return context;
    }

    private sealed class ConcurrentStartGate(int expected)
    {
        private int arrived;
        private readonly TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task WaitAsync()
        {
            if (Interlocked.Increment(ref arrived) == expected)
            {
                release.TrySetResult();
            }

            return release.Task;
        }
    }

    private sealed class PauseAfterForUpdateInterceptor : DbCommandInterceptor
    {
        private int paused;
        public TaskCompletionSource Reached { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override async ValueTask<DbDataReader> ReaderExecutedAsync(DbCommand command, CommandExecutedEventData eventData, DbDataReader result, CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains("FROM content_items", StringComparison.OrdinalIgnoreCase)
                && command.CommandText.Contains("FOR UPDATE", StringComparison.OrdinalIgnoreCase)
                && command.CommandText.Contains("WHERE id", StringComparison.OrdinalIgnoreCase)
                && Interlocked.Exchange(ref paused, 1) == 0)
            {
                Reached.TrySetResult();
                await Release.Task.WaitAsync(cancellationToken);
            }

            return result;
        }
    }

}
