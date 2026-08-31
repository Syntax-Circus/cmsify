using System.Text.Json;
using Cmsify.Core.Domain.Entities;
using Cmsify.Core.Domain.Enums;
using Cmsify.Core.Domain.ValueObjects;
using Cmsify.Core.Interfaces.Repositories;
using Cmsify.Core.Interfaces.Services;
using Cmsify.Infrastructure.BackgroundServices;
using Microsoft.EntityFrameworkCore;

namespace Cmsify.Infrastructure.Persistence.Repositories;

public sealed class ScheduledPublishingRepository(
    CmsifyDbContext dbContext,
    IContentPublishingService publishingService,
    IWebhookOutbox webhookOutbox) : IScheduledPublishingRepository
{
    public async Task<IReadOnlyList<ScheduledContentClaimDto>> ClaimDueContentAsync(string workerId, DateTimeOffset now, TimeSpan leaseDuration, int limit, CancellationToken ct = default)
    {
        ValidateClaimArguments(workerId, leaseDuration, limit);
        await using var transaction = await dbContext.Database.BeginTransactionAsync(ct);
        var ids = await dbContext.Database.SqlQuery<Guid>($"""
            SELECT id AS "Value" FROM content_items
            WHERE status = 'Approved' AND publish_at <= {now} AND NOT is_deleted
              AND (publish_lease_expires_at IS NULL OR publish_lease_expires_at <= {now})
            ORDER BY publish_at, id
            FOR UPDATE SKIP LOCKED
            LIMIT {limit}
            """).ToListAsync(ct);
        var items = await dbContext.ContentItems.Where(item => ids.Contains(item.Id)).ToListAsync(ct);

        var reclaimed = new Dictionary<Guid, bool>();
        foreach (var item in items)
        {
            reclaimed[item.Id] = item.PublishLeaseExpiresAt.HasValue;
            item.PublishLeaseOwner = workerId;
            item.PublishLeaseToken = Guid.CreateVersion7();
            item.PublishLeaseExpiresAt = now.Add(leaseDuration);
        }

        await dbContext.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        var claims = items.Select(item => new ScheduledContentClaimDto(item.Id, item.PublishLeaseOwner!, item.PublishLeaseToken!.Value, reclaimed[item.Id])).ToArray();
        foreach (var claim in claims)
        {
            CmsifyOperationalMetrics.RecordScheduledClaim(claim.WasReclaimed);
        }
        CmsifyOperationalMetrics.ReportDueScheduledDepth(await dbContext.ContentItems.CountAsync(item => item.Status == ContentStatus.Approved && !item.IsDeleted && item.PublishAt <= now, ct));
        return claims;
    }

    public async Task<bool> CompleteClaimAsync(ScheduledContentClaimDto claim, DateTimeOffset now, CancellationToken ct = default)
    {
        await using var transaction = await dbContext.Database.BeginTransactionAsync(ct);
        var claimedId = await dbContext.Database.SqlQuery<Guid>($"""
            SELECT id AS "Value" FROM content_items
            WHERE id = {claim.ContentItemId} AND status = 'Approved' AND publish_at <= {now} AND NOT is_deleted
              AND publish_lease_owner = {claim.LeaseOwner} AND publish_lease_token = {claim.LeaseToken}
              AND publish_lease_expires_at > {now}
            FOR UPDATE
            """).SingleOrDefaultAsync(ct);
        if (claimedId == Guid.Empty)
        {
            await transaction.RollbackAsync(ct);
            return false;
        }

        var content = await dbContext.ContentItems
            .Include(item => item.FieldValues)
            .Include(item => item.Tags)
            .FirstOrDefaultAsync(item => item.Id == claimedId, ct);
        if (content is null)
        {
            await transaction.RollbackAsync(ct);
            return false;
        }

        var range = new ContentEffectiveRange(content.PendingEffectiveStartAt, content.PendingEffectiveEndAt);
        content.Status = ContentStatus.Published;
        content.PublishedAt ??= now;
        content.UpdatedAt = now;
        content.PublishAt = null;
        content.PendingEffectiveStartAt = null;
        content.PendingEffectiveEndAt = null;
        content.PublishLeaseOwner = null;
        content.PublishLeaseToken = null;
        content.PublishLeaseExpiresAt = null;

        var snapshot = await publishingService.PublishSnapshotAsync(content, range, actorUserId: null, ct: ct);
        snapshot.Version.PublishedAt = now;
        webhookOutbox.Enqueue(
            "content.published",
            content.WorkspaceId,
            content.Id,
            JsonSerializer.SerializeToElement(new
            {
                contentItemId = content.Id,
                workspaceId = content.WorkspaceId,
                templateVersionId = content.TemplateVersionId,
                publishedAt = content.PublishedAt
            }),
            now);
        await dbContext.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        CmsifyOperationalMetrics.RecordScheduledPublished();
        return true;
    }

    private static void ValidateClaimArguments(string workerId, TimeSpan leaseDuration, int limit)
    {
        if (string.IsNullOrWhiteSpace(workerId) || workerId.Length > 200)
        {
            throw new ArgumentException("Scheduled publishing worker IDs must be nonblank and at most 200 characters.", nameof(workerId));
        }

        if (leaseDuration < TimeSpan.FromSeconds(1) || leaseDuration > TimeSpan.FromMinutes(30))
        {
            throw new ArgumentOutOfRangeException(nameof(leaseDuration));
        }

        if (limit is < 1 or > 500)
        {
            throw new ArgumentOutOfRangeException(nameof(limit));
        }
    }
}
