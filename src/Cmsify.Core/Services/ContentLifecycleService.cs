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

    private static readonly IReadOnlySet<(ContentStatus From, ContentStatus To)> AllowedOverrideTransitions = new HashSet<(ContentStatus, ContentStatus)>
    {
        (ContentStatus.Draft, ContentStatus.Published),
        (ContentStatus.Review, ContentStatus.Published)
    };

    public bool CanTransition(ContentStatus from, ContentStatus to, bool allowOverride = false)
    {
        return from == to
            || AllowedTransitions.Contains((from, to))
            || (allowOverride && AllowedOverrideTransitions.Contains((from, to)));
    }

    public Task TransitionAsync(ContentVersion version, ContentStatus to, Guid actorId, bool allowOverride = false)
    {
        ArgumentNullException.ThrowIfNull(version);

        if (!CanTransition(version.Status, to, allowOverride))
        {
            throw new DomainException($"Content version cannot transition from {version.Status} to {to}.");
        }

        var now = DateTimeOffset.UtcNow;
        version.Status = to;
        version.UpdatedAt = now;
        version.UpdatedByUserId = actorId;

        if (to == ContentStatus.Published)
        {
            version.PublishedAt ??= now;
        }
        else if (to == ContentStatus.Archived)
        {
            version.ArchivedAt ??= now;
        }

        return Task.CompletedTask;
    }
}
