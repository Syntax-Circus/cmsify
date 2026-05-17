using Cmsify.Core.Domain.Entities;
using Cmsify.Core.Domain.Enums;
using Cmsify.Core.Exceptions;
using Cmsify.Core.Interfaces.Services;

namespace Cmsify.Core.Services;

public sealed class ContentLifecycleService : IContentLifecycleService
{
    private static readonly IReadOnlySet<(ContentStatus From, ContentStatus To)> AllowedTransitions = new HashSet<(ContentStatus, ContentStatus)>
    {
        (ContentStatus.Draft, ContentStatus.Review),
        (ContentStatus.Review, ContentStatus.Draft),
        (ContentStatus.Review, ContentStatus.Approved),
        (ContentStatus.Approved, ContentStatus.Published),
        (ContentStatus.Published, ContentStatus.Archived),
        (ContentStatus.Archived, ContentStatus.Draft)
    };

    public bool CanTransition(ContentStatus from, ContentStatus to)
    {
        return from == to || AllowedTransitions.Contains((from, to));
    }

    public Task TransitionAsync(ContentItem item, ContentStatus to, Guid actorId)
    {
        ArgumentNullException.ThrowIfNull(item);

        if (!CanTransition(item.Status, to))
        {
            throw new DomainException($"Content cannot transition from {item.Status} to {to}.");
        }

        var now = DateTimeOffset.UtcNow;
        item.Status = to;
        item.UpdatedAt = now;
        item.UpdatedByUserId = actorId;

        if (to == ContentStatus.Published)
        {
            item.PublishedAt ??= now;
        }
        else if (to == ContentStatus.Archived)
        {
            item.ArchivedAt ??= now;
        }

        return Task.CompletedTask;
    }
}
