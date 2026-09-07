using Cmsify.Core.Domain.Entities;
using Cmsify.Core.Domain.Enums;
using Cmsify.Core.Interfaces.Services;
using Microsoft.EntityFrameworkCore;

namespace Cmsify.Infrastructure.Persistence;

public sealed class ContentPublishingService : IContentPublishingService
{
    private readonly CmsifyDbContext dbContext;
    private readonly ICurrentActor currentActor;

    public ContentPublishingService(CmsifyDbContext dbContext, ICurrentActor currentActor)
    {
        this.dbContext = dbContext;
        this.currentActor = currentActor;
    }

    public async Task<ContentPublishResult> PublishAsync(
        ContentVersion version,
        Guid? actorUserId = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(version);

        var now = DateTimeOffset.UtcNow;
        var isDefault = version.EffectiveStartAt is null && version.EffectiveEndAt is null;

        if (isDefault)
        {
            var priorDefaults = await dbContext.ContentVersions
                .Where(candidate =>
                    candidate.ContentItemId == version.ContentItemId
                    && candidate.Id != version.Id
                    && candidate.Status == ContentStatus.Published
                    && candidate.EffectiveStartAt == null
                    && candidate.EffectiveEndAt == null)
                .ToListAsync(ct);
            foreach (var prior in priorDefaults)
            {
                prior.Status = ContentStatus.Archived;
                prior.ArchivedAt = now;
                prior.UpdatedAt = now;
            }
        }

        var warnings = isDefault
            ? []
            : await FindEqualSpecificityWarningsAsync(version, ct);

        var actor = actorUserId ?? currentActor.UserId;
        version.Status = ContentStatus.Published;
        version.PublishedAt ??= now;
        version.PublishedByUserId = actor;
        version.UpdatedAt = now;
        version.UpdatedByUserId = actor;

        return new ContentPublishResult(version, warnings);
    }

    private async Task<IReadOnlyList<string>> FindEqualSpecificityWarningsAsync(ContentVersion version, CancellationToken ct)
    {
        var start = version.EffectiveStartAt!.Value;
        var end = version.EffectiveEndAt!.Value;
        var duration = end - start;
        var overlappingRanges = await dbContext.ContentVersions.AsNoTracking()
            .Where(candidate =>
                candidate.ContentItemId == version.ContentItemId
                && candidate.Id != version.Id
                && candidate.Status == ContentStatus.Published
                && candidate.EffectiveStartAt.HasValue
                && candidate.EffectiveEndAt.HasValue
                && candidate.EffectiveStartAt < end
                && start < candidate.EffectiveEndAt)
            .Select(candidate => new { candidate.EffectiveStartAt, candidate.EffectiveEndAt })
            .ToListAsync(ct);
        var hasEqualSpecificityOverlap = overlappingRanges.Any(candidate => candidate.EffectiveEndAt!.Value - candidate.EffectiveStartAt!.Value == duration);

        return hasEqualSpecificityOverlap
            ? ["Another published override with the same duration overlaps this range. The most recently published matching version will win."]
            : [];
    }
}
