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

            if (priorDefaults.Count > 0)
            {
                // ix_content_versions_content_item_id is a partial unique index on ContentItemId, filtered to
                // default-published rows (status = 'Published' AND effective_start_at/effective_end_at IS NULL).
                // Postgres cannot make a partial index DEFERRABLE, so it is enforced per-statement, not at
                // commit. Content version ids are UUIDv7 (time-ordered), and EF Core's SaveChanges batches all
                // pending UPDATEs together in an order that is not guaranteed to match assignment order below -
                // it can be keyed off the entities' ids. If this version's id sorts before a prior default's id
                // (an older in-flight version being published after a newer version already holds the default
                // Published slot - the common real-world case), batching both UPDATEs in one SaveChanges could
                // send this version's UPDATE-to-Published before the prior's UPDATE-to-Archived, and the index
                // would momentarily see two default-published rows for the same content item and reject it.
                // Flushing the archival here - before this version's Status is set to Published below -
                // guarantees the prior is archived in Postgres first, regardless of id ordering. Callers must
                // run PublishAsync inside their own transaction (see ContentController.Publish and
                // ScheduledPublishingRepository.CompleteClaimAsync) so that a later failure rolls this archival
                // back too, rather than leaving the prior stranded as Archived with no Published successor.
                await dbContext.SaveChangesAsync(ct);
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
