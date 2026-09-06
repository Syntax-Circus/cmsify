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
            SELECT id AS "Value" FROM content_versions
            WHERE status = 'Approved' AND publish_at <= {now}
              AND EXISTS (SELECT 1 FROM content_items ci WHERE ci.id = content_versions.content_item_id AND NOT ci.is_deleted)
              AND (publish_lease_expires_at IS NULL OR publish_lease_expires_at <= {now})
            ORDER BY publish_at, id
            FOR UPDATE SKIP LOCKED
            LIMIT {limit}
            """).ToListAsync(ct);
        var versions = await dbContext.ContentVersions.Where(version => ids.Contains(version.Id)).ToListAsync(ct);

        var reclaimed = new Dictionary<Guid, bool>();
        foreach (var version in versions)
        {
            reclaimed[version.Id] = version.PublishLeaseExpiresAt.HasValue;
            version.PublishLeaseOwner = workerId;
            version.PublishLeaseToken = Guid.CreateVersion7();
            version.PublishLeaseExpiresAt = now.Add(leaseDuration);
        }

        await dbContext.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        var claims = versions.Select(version => new ScheduledContentClaimDto(version.Id, version.PublishLeaseOwner!, version.PublishLeaseToken!.Value, reclaimed[version.Id])).ToArray();
        foreach (var claim in claims)
        {
            CmsifyOperationalMetrics.RecordScheduledClaim(claim.WasReclaimed);
        }
        CmsifyOperationalMetrics.ReportDueScheduledDepth(await dbContext.ContentVersions.CountAsync(version => version.Status == ContentStatus.Approved && version.PublishAt <= now, ct));
        return claims;
    }

    public async Task<bool> CompleteClaimAsync(ScheduledContentClaimDto claim, DateTimeOffset now, CancellationToken ct = default)
    {
        await using var transaction = await dbContext.Database.BeginTransactionAsync(ct);
        var claimedId = await dbContext.Database.SqlQuery<Guid>($"""
            SELECT id AS "Value" FROM content_versions
            WHERE id = {claim.ContentVersionId} AND status = 'Approved' AND publish_at <= {now}
              AND publish_lease_owner = {claim.LeaseOwner} AND publish_lease_token = {claim.LeaseToken}
              AND publish_lease_expires_at > {now}
            FOR UPDATE
            """).SingleOrDefaultAsync(ct);
        if (claimedId == Guid.Empty)
        {
            await transaction.RollbackAsync(ct);
            return false;
        }

        var version = await dbContext.ContentVersions.FirstOrDefaultAsync(candidate => candidate.Id == claimedId, ct);
        if (version is null)
        {
            await transaction.RollbackAsync(ct);
            return false;
        }

        version.PublishAt = null;
        version.PublishLeaseOwner = null;
        version.PublishLeaseToken = null;
        version.PublishLeaseExpiresAt = null;

        var publishResult = await publishingService.PublishAsync(version, actorUserId: null, ct);
        webhookOutbox.Enqueue(
            "content.published",
            version.WorkspaceId,
            version.ContentItemId,
            JsonSerializer.SerializeToElement(new
            {
                contentItemId = version.ContentItemId,
                contentVersionId = version.Id,
                versionNumber = version.VersionNumber,
                workspaceId = version.WorkspaceId,
                templateVersionId = version.TemplateVersionId,
                publishedAt = publishResult.Version.PublishedAt
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
