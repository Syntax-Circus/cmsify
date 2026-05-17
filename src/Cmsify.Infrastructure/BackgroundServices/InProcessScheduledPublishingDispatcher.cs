using System.Text.Json;
using Cmsify.Core.Domain.Entities;
using Cmsify.Core.Domain.Enums;
using Cmsify.Core.Interfaces.Repositories;
using Cmsify.Core.Interfaces.Services;

namespace Cmsify.Infrastructure.BackgroundServices;

public sealed class InProcessScheduledPublishingDispatcher : IScheduledPublishingDispatcher
{
    private readonly IContentItemRepository contentItemRepository;
    private readonly IWebhookQueue webhookQueue;

    public InProcessScheduledPublishingDispatcher(IContentItemRepository contentItemRepository, IWebhookQueue webhookQueue)
    {
        this.contentItemRepository = contentItemRepository;
        this.webhookQueue = webhookQueue;
    }

    public async Task RunOnceAsync(CancellationToken ct = default)
    {
        var pending = await contentItemRepository.GetPendingScheduledPublishAsync(DateTimeOffset.UtcNow, ct: ct);

        foreach (var item in pending)
        {
            var published = await contentItemRepository.SetStatusAsync(item.Id, ContentStatus.Published, Guid.Empty, ct);
            var payload = JsonSerializer.SerializeToElement(new
            {
                contentItemId = published.Id,
                workspaceId = published.WorkspaceId,
                templateVersionId = published.TemplateVersionId,
                publishedAt = published.PublishedAt
            });

            await webhookQueue.EnqueueAsync(new WebhookEvent("content.published", published.WorkspaceId, published.Id, payload, DateTimeOffset.UtcNow), ct);
        }
    }
}
