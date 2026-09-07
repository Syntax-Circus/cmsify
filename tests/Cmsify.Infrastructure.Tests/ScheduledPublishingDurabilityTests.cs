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

/// <summary>
/// Durability coverage for the scheduled-publish worker. Scheduling now lives on
/// <see cref="ContentVersion"/>: a version that is <see cref="ContentStatus.Approved"/> with a due
/// <c>publish_at</c> is claimable, and completing a claim transitions that <em>existing</em> row to
/// <see cref="ContentStatus.Published"/> - it never materialises a new version row.
/// </summary>
public sealed class ScheduledPublishingDurabilityTests : IAsyncLifetime
{
    private const string PublishedEventType = "content.version_published";

    private readonly PostgreSqlContainer postgres = new PostgreSqlBuilder("postgres:17-alpine")
        .WithDatabase("cmsify")
        .WithUsername("cmsify")
        .WithPassword("cmsify")
        .Build();

    public ValueTask InitializeAsync() => new(postgres.StartAsync());

    public async ValueTask DisposeAsync() => await postgres.DisposeAsync();

    [Fact]
    public async Task ConcurrentDispatchers_PublishOneDueVersionExactlyOnce()
    {
        var dueAt = DateTimeOffset.Parse("2026-08-26T10:00:00Z");
        var seeded = await SeedDueContentAsync("due-content", dueAt);

        await using var firstContext = await CreateContextAsync();
        await using var secondContext = await CreateContextAsync();
        var first = CreateDispatcher(firstContext);
        var second = CreateDispatcher(secondContext);
        var release = new ConcurrentStartGate(2);

        var results = await Task.WhenAll(RunAsync(first, "worker-a", dueAt, release), RunAsync(second, "worker-b", dueAt, release));

        await using var verification = await CreateContextAsync();
        // The version that was already there is the one that got published - no second row was created.
        var version = await verification.ContentVersions.SingleAsync(candidate => candidate.ContentItemId == seeded.ItemId, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(seeded.VersionId, version.Id);
        Assert.Equal(ContentStatus.Published, version.Status);
        Assert.NotNull(version.PublishedAt);
        Assert.Null(version.PublishAt);
        Assert.Null(version.PublishLeaseOwner);
        Assert.Null(version.PublishLeaseToken);
        Assert.Null(version.PublishLeaseExpiresAt);
        Assert.Equal(1, await verification.WebhookOutboxEvents.CountAsync(item => item.EntityId == seeded.ItemId && item.EventType == PublishedEventType, cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal(1, results.Count(result => result));
    }

    [Fact]
    public async Task ActiveLeaseIsNotStolen_ExpiredLeaseIsReclaimed_AndStaleTokenCannotPublish()
    {
        var now = DateTimeOffset.Parse("2026-08-26T11:00:00Z");
        var seeded = await SeedDueContentAsync("lease-content", now);
        await using var firstContext = await CreateContextAsync();
        var first = CreateDispatcher(firstContext);
        var firstClaim = Assert.Single(await first.ClaimDueAsync("worker-a", now, TimeSpan.FromMinutes(1), 1, TestContext.Current.CancellationToken));
        Assert.Equal(seeded.VersionId, firstClaim.ContentVersionId);

        await using var secondContext = await CreateContextAsync();
        var second = CreateDispatcher(secondContext);
        Assert.Empty(await second.ClaimDueAsync("worker-b", now.AddSeconds(30), TimeSpan.FromMinutes(1), 1, TestContext.Current.CancellationToken));
        var secondClaim = Assert.Single(await second.ClaimDueAsync("worker-b", now.AddMinutes(1), TimeSpan.FromMinutes(1), 1, TestContext.Current.CancellationToken));
        Assert.NotEqual(firstClaim.LeaseToken, secondClaim.LeaseToken);
        Assert.False(await first.CompleteClaimAsync(firstClaim, now.AddMinutes(1), TestContext.Current.CancellationToken));
        Assert.True(await second.CompleteClaimAsync(secondClaim, now.AddMinutes(1), TestContext.Current.CancellationToken));

        await using var verification = await CreateContextAsync();
        var version = await verification.ContentVersions.SingleAsync(candidate => candidate.Id == seeded.VersionId, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(ContentStatus.Published, version.Status);
        Assert.Null(version.PublishAt);
        Assert.Null(version.PublishLeaseOwner);
        Assert.Null(version.PublishLeaseToken);
        Assert.Null(version.PublishLeaseExpiresAt);
        Assert.Equal(1, await verification.ContentVersions.CountAsync(candidate => candidate.ContentItemId == seeded.ItemId, cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal(1, await verification.WebhookOutboxEvents.CountAsync(item => item.EntityId == seeded.ItemId && item.EventType == PublishedEventType, cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task FailedCompletionRollsBackPublicationAndRecoversAfterLeaseExpiry()
    {
        var now = DateTimeOffset.Parse("2026-08-26T12:00:00Z");
        var seeded = await SeedDueContentAsync("rollback-content", now);
        await using var firstContext = await CreateContextAsync();
        var first = CreateDispatcher(firstContext);
        var firstClaim = Assert.Single(await first.ClaimDueAsync("worker-a", now, TimeSpan.FromMinutes(1), 1, TestContext.Current.CancellationToken));
        await firstContext.Database.ExecuteSqlRawAsync($"ALTER TABLE webhook_outbox_events ADD CONSTRAINT reject_scheduled_publish CHECK (event_type <> '{PublishedEventType}')", cancellationToken: TestContext.Current.CancellationToken);

        await Assert.ThrowsAsync<DbUpdateException>(() => first.CompleteClaimAsync(firstClaim, now, TestContext.Current.CancellationToken));

        await using (var rolledBack = await CreateContextAsync())
        {
            // The whole completion - status transition, lease release and outbox enqueue - is one
            // transaction, so a failed outbox insert must leave the version exactly as it was claimed.
            var version = await rolledBack.ContentVersions.SingleAsync(candidate => candidate.Id == seeded.VersionId, cancellationToken: TestContext.Current.CancellationToken);
            Assert.Equal(ContentStatus.Approved, version.Status);
            Assert.Null(version.PublishedAt);
            Assert.Equal(now, version.PublishAt);
            Assert.Equal("worker-a", version.PublishLeaseOwner);
            Assert.Equal(firstClaim.LeaseToken, version.PublishLeaseToken);
            Assert.Equal(1, await rolledBack.ContentVersions.CountAsync(candidate => candidate.ContentItemId == seeded.ItemId, cancellationToken: TestContext.Current.CancellationToken));
            Assert.Equal(0, await rolledBack.WebhookOutboxEvents.CountAsync(item => item.EntityId == seeded.ItemId, cancellationToken: TestContext.Current.CancellationToken));
        }

        await firstContext.Database.ExecuteSqlRawAsync("ALTER TABLE webhook_outbox_events DROP CONSTRAINT reject_scheduled_publish", cancellationToken: TestContext.Current.CancellationToken);
        await using var recoveryContext = await CreateContextAsync();
        var recovery = CreateDispatcher(recoveryContext);
        var recoveryClaim = Assert.Single(await recovery.ClaimDueAsync("worker-b", now.AddMinutes(1), TimeSpan.FromMinutes(1), 1, TestContext.Current.CancellationToken));
        Assert.True(await recovery.CompleteClaimAsync(recoveryClaim, now.AddMinutes(1), TestContext.Current.CancellationToken));
        await using var verification = await CreateContextAsync();
        var republished = await verification.ContentVersions.SingleAsync(candidate => candidate.Id == seeded.VersionId, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(ContentStatus.Published, republished.Status);
        Assert.Equal(1, await verification.ContentVersions.CountAsync(candidate => candidate.ContentItemId == seeded.ItemId, cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal(1, await verification.WebhookOutboxEvents.CountAsync(item => item.EntityId == seeded.ItemId && item.EventType == PublishedEventType, cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ExpiredScheduleLease_CannotPublishBeforeAnotherWorkerReclaimsIt()
    {
        var now = DateTimeOffset.Parse("2026-08-26T12:30:00Z");
        var seeded = await SeedDueContentAsync("expired-schedule", now);
        var leaseToken = Guid.CreateVersion7();
        await using (var setup = await CreateContextAsync())
        {
            var version = await setup.ContentVersions.SingleAsync(candidate => candidate.Id == seeded.VersionId, cancellationToken: TestContext.Current.CancellationToken);
            version.PublishLeaseOwner = "expired-worker";
            version.PublishLeaseToken = leaseToken;
            version.PublishLeaseExpiresAt = now.AddTicks(-1);
            await setup.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        await using var worker = await CreateContextAsync();
        var completed = await CreateDispatcher(worker).CompleteClaimAsync(new ScheduledContentClaimDto(seeded.VersionId, "expired-worker", leaseToken), now, TestContext.Current.CancellationToken);

        Assert.False(completed);
        await using var verification = await CreateContextAsync();
        var persisted = await verification.ContentVersions.SingleAsync(candidate => candidate.Id == seeded.VersionId, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(ContentStatus.Approved, persisted.Status);
        Assert.Equal("expired-worker", persisted.PublishLeaseOwner);
        Assert.Equal(leaseToken, persisted.PublishLeaseToken);
        Assert.Equal(0, await verification.WebhookOutboxEvents.CountAsync(item => item.EntityId == seeded.ItemId, cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task CompletionLock_PreventsExpiredScheduleClaimFromBeingReclaimedBeforeItCommits()
    {
        var now = DateTimeOffset.Parse("2026-08-26T12:40:00Z");
        var seeded = await SeedDueContentAsync("schedule-lock", now);
        await using var claimantContext = await CreateContextAsync();
        var claim = Assert.Single(await CreateDispatcher(claimantContext).ClaimDueAsync("worker-a", now, TimeSpan.FromSeconds(1), 1, TestContext.Current.CancellationToken));
        var pause = new PauseAfterForUpdateInterceptor();
        await using var completionContext = await CreateContextAsync(pause);
        var completing = CreateDispatcher(completionContext).CompleteClaimAsync(claim, now, TestContext.Current.CancellationToken);
        await pause.Reached.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        Assert.True(pause.SawCompletionLock);

        // The completing worker holds a row lock on the version; even though its lease has expired by
        // now.AddMinutes(1), the claim query's FOR UPDATE SKIP LOCKED must skip the locked row.
        await using var reclaimerContext = await CreateContextAsync();
        Assert.Empty(await CreateDispatcher(reclaimerContext).ClaimDueAsync("worker-b", now.AddMinutes(1), TimeSpan.FromMinutes(1), 1, TestContext.Current.CancellationToken));
        pause.Release.TrySetResult();
        Assert.True(await completing);

        await using var verification = await CreateContextAsync();
        var persisted = await verification.ContentVersions.SingleAsync(candidate => candidate.Id == seeded.VersionId, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(ContentStatus.Published, persisted.Status);
        Assert.Null(persisted.PublishLeaseOwner);
        Assert.Equal(1, await verification.ContentVersions.CountAsync(candidate => candidate.ContentItemId == seeded.ItemId, cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal(1, await verification.WebhookOutboxEvents.CountAsync(item => item.EntityId == seeded.ItemId && item.EventType == PublishedEventType, cancellationToken: TestContext.Current.CancellationToken));
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

    private async Task<SeededContent> SeedDueContentAsync(string slug, DateTimeOffset dueAt)
    {
        await using var setup = await CreateContextAsync();
        var workspace = new Workspace { Name = slug, Slug = slug };
        var template = new Template { WorkspaceId = workspace.Id, Name = slug, Slug = slug };
        var templateVersion = new TemplateVersion { TemplateId = template.Id, VersionNumber = 1, Status = TemplateVersionStatus.Published, PublishedAt = dueAt };
        var content = new ContentItem
        {
            WorkspaceId = workspace.Id,
            TemplateVersionId = templateVersion.Id,
            Slug = slug
        };
        var contentVersion = new ContentVersion
        {
            ContentItemId = content.Id,
            WorkspaceId = workspace.Id,
            TemplateVersionId = templateVersion.Id,
            VersionNumber = 1,
            Status = ContentStatus.Approved,
            Slug = slug,
            PublishAt = dueAt
        };
        setup.AddRange(workspace, template, templateVersion, content, contentVersion);
        await setup.SaveChangesAsync();
        template.CurrentVersionId = templateVersion.Id;
        await setup.SaveChangesAsync();
        return new SeededContent(content.Id, contentVersion.Id);
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

    private sealed record SeededContent(Guid ItemId, Guid VersionId);

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

    /// <summary>
    /// Pauses immediately after the completion path's <c>SELECT ... FROM content_versions WHERE id = ...
    /// FOR UPDATE</c> has executed (and therefore while its row lock is still held inside the open
    /// transaction), so a competing claim can be attempted against a locked row.
    /// </summary>
    private sealed class PauseAfterForUpdateInterceptor : DbCommandInterceptor
    {
        private int paused;
        public TaskCompletionSource Reached { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>True once the completion-path lock query was actually observed (guards against the
        /// filter silently matching nothing and the pause never happening for the right reason).</summary>
        public bool SawCompletionLock { get; private set; }

        public override async ValueTask<DbDataReader> ReaderExecutedAsync(DbCommand command, CommandExecutedEventData eventData, DbDataReader result, CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains("FROM content_versions", StringComparison.OrdinalIgnoreCase)
                && command.CommandText.Contains("FOR UPDATE", StringComparison.OrdinalIgnoreCase)
                && !command.CommandText.Contains("SKIP LOCKED", StringComparison.OrdinalIgnoreCase)
                && command.CommandText.Contains("WHERE id", StringComparison.OrdinalIgnoreCase)
                && Interlocked.Exchange(ref paused, 1) == 0)
            {
                SawCompletionLock = true;
                Reached.TrySetResult();
                await Release.Task.WaitAsync(cancellationToken);
            }

            return result;
        }
    }

}
