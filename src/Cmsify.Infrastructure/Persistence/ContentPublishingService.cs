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

    public async Task<ContentPublishResult> PublishSnapshotAsync(
        ContentItem content,
        ContentEffectiveRange effectiveRange,
        int? rolledBackFromVersionNumber = null,
        Guid? actorUserId = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(content);
        ValidateRange(effectiveRange);

        var now = DateTimeOffset.UtcNow;
        if (effectiveRange.IsDefault)
        {
            var priorDefaults = await dbContext.ContentVersions
                .Where(version =>
                    version.ContentItemId == content.Id
                    && version.Status == ContentVersionStatus.Published
                    && version.EffectiveStartAt == null
                    && version.EffectiveEndAt == null)
                .ToListAsync(ct);
            foreach (var prior in priorDefaults)
            {
                prior.Status = ContentVersionStatus.Retired;
                prior.RetiredAt = now;
            }
        }

        var warnings = effectiveRange.IsDefault
            ? []
            : await FindEqualSpecificityWarningsAsync(content.Id, effectiveRange, ct);

        var nextNumber = await dbContext.ContentVersions
            .Where(version => version.ContentItemId == content.Id)
            .Select(version => (int?)version.VersionNumber)
            .MaxAsync(ct) ?? 0;
        nextNumber += 1;

        var tagNames = await dbContext.ContentItemTags.AsNoTracking()
            .Where(join => join.ContentItemId == content.Id)
            .Join(dbContext.Tags.AsNoTracking(), join => join.TagId, tag => tag.Id, (_, tag) => tag.Name)
            .OrderBy(name => name)
            .ToListAsync(ct);

        var snapshot = new ContentVersion
        {
            ContentItemId = content.Id,
            WorkspaceId = content.WorkspaceId,
            VersionNumber = nextNumber,
            Status = ContentVersionStatus.Published,
            TemplateVersionId = content.TemplateVersionId,
            Slug = content.Slug,
            LocaleCode = content.LocaleCode,
            TranslationGroupId = content.TranslationGroupId,
            Tags = tagNames.ToList(),
            EffectiveStartAt = effectiveRange.StartAt,
            EffectiveEndAt = effectiveRange.EndAt,
            PublishedAt = now,
            PublishedByUserId = actorUserId ?? currentActor.UserId,
            RolledBackFromVersionNumber = rolledBackFromVersionNumber
        };

        foreach (var value in content.FieldValues)
        {
            snapshot.FieldValues.Add(new ContentVersionFieldValue
            {
                ContentVersionId = snapshot.Id,
                FieldId = value.FieldId,
                Order = value.Order,
                ValueKind = value.ValueKind,
                TextValue = value.TextValue,
                BoolValue = value.BoolValue,
                MediaAssetId = value.MediaAssetId,
                FileAssetId = value.FileAssetId,
                ChildContentItemId = value.ChildContentItemId,
                JsonValue = value.JsonValue?.Clone()
            });
        }

        dbContext.ContentVersions.Add(snapshot);
        return new ContentPublishResult(snapshot, warnings);
    }

    private static void ValidateRange(ContentEffectiveRange range)
    {
        if (range.StartAt.HasValue != range.EndAt.HasValue)
        {
            throw new ArgumentException("Effective range must provide both start and end, or neither.", nameof(range));
        }

        if (range.StartAt.HasValue && range.StartAt.Value >= range.EndAt!.Value)
        {
            throw new ArgumentException("Effective range start must be before end.", nameof(range));
        }
    }

    private async Task<IReadOnlyList<string>> FindEqualSpecificityWarningsAsync(Guid contentItemId, ContentEffectiveRange range, CancellationToken ct)
    {
        var start = range.StartAt!.Value;
        var end = range.EndAt!.Value;
        var duration = end - start;
        var overlappingRanges = await dbContext.ContentVersions.AsNoTracking()
            .Where(version =>
                version.ContentItemId == contentItemId
                && version.Status == ContentVersionStatus.Published
                && version.EffectiveStartAt.HasValue
                && version.EffectiveEndAt.HasValue
                && version.EffectiveStartAt < end
                && start < version.EffectiveEndAt)
            .Select(version => new { version.EffectiveStartAt, version.EffectiveEndAt })
            .ToListAsync(ct);
        var hasEqualSpecificityOverlap = overlappingRanges.Any(version => version.EffectiveEndAt!.Value - version.EffectiveStartAt!.Value == duration);

        return hasEqualSpecificityOverlap
            ? ["Another published override with the same duration overlaps this range. The most recently published matching version will win."]
            : [];
    }
}
