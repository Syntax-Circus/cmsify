using System.Text.Json;
using Cmsify.Core.Domain.Entities;
using Cmsify.Core.Domain.Enums;
using Cmsify.Core.Interfaces.Services;
using Cmsify.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Cmsify.Infrastructure.BackgroundServices;

public sealed class InProcessScheduledPublishingDispatcher : IScheduledPublishingDispatcher
{
    private readonly CmsifyDbContext dbContext;
    private readonly IContentPublishingService publishingService;
    private readonly IWebhookQueue webhookQueue;

    public InProcessScheduledPublishingDispatcher(CmsifyDbContext dbContext, IContentPublishingService publishingService, IWebhookQueue webhookQueue)
    {
        this.dbContext = dbContext;
        this.publishingService = publishingService;
        this.webhookQueue = webhookQueue;
    }

    public async Task RunOnceAsync(CancellationToken ct = default)
    {
        var now = DateTimeOffset.UtcNow;
        var pending = await dbContext.ContentItems
            .Include(content => content.FieldValues)
            .Include(content => content.Tags)
            .Where(content => content.Status == ContentStatus.Approved && content.PublishAt <= now && !content.IsDeleted)
            .OrderBy(content => content.PublishAt)
            .Take(100)
            .ToListAsync(ct);

        foreach (var item in pending)
        {
            item.Status = ContentStatus.Published;
            item.PublishedAt ??= now;
            item.UpdatedAt = now;
            var range = new ContentEffectiveRange(item.PendingEffectiveStartAt, item.PendingEffectiveEndAt);
            await publishingService.PublishSnapshotAsync(item, range, actorUserId: null, ct: ct);
            item.PublishAt = null;
            item.PendingEffectiveStartAt = null;
            item.PendingEffectiveEndAt = null;
            await dbContext.SaveChangesAsync(ct);

            var payload = JsonSerializer.SerializeToElement(new
            {
                contentItemId = item.Id,
                workspaceId = item.WorkspaceId,
                templateVersionId = item.TemplateVersionId,
                publishedAt = item.PublishedAt
            });

            await webhookQueue.EnqueueAsync(new WebhookEvent("content.published", item.WorkspaceId, item.Id, payload, DateTimeOffset.UtcNow), ct);
        }
    }
}
