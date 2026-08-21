using Cmsify.Core.Domain.Entities;
using Cmsify.Core.Domain.Enums;
using Cmsify.Core.Interfaces.Services;
using Microsoft.EntityFrameworkCore;
using System.Text.Json;

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

        var fields = await dbContext.TemplateFields.AsNoTracking()
            .Where(field => field.TemplateVersionId == content.TemplateVersionId && field.PrimitiveType == PrimitiveType.PickList)
            .ToListAsync(ct);
        var pickListIds = fields.Select(GetPickListId).Where(id => id.HasValue).Select(id => id!.Value).Distinct().ToArray();
        var currentLabels = await dbContext.PickLists.AsNoTracking().Include(list => list.Options)
            .Where(list => pickListIds.Contains(list.Id)).ToDictionaryAsync(list => list.Id, list => list.Options.ToDictionary(option => option.Value, option => option.Label, StringComparer.OrdinalIgnoreCase), ct);
        var revisionIds = fields.Select(GetPickListRevisionId).Where(id => id.HasValue).Select(id => id!.Value).Distinct().ToArray();
        var revisionLabels = await dbContext.PickListRevisions.AsNoTracking().Include(revision => revision.Options)
            .Where(revision => revisionIds.Contains(revision.Id)).ToDictionaryAsync(revision => revision.Id, revision => revision.Options.ToDictionary(option => option.Value, option => option.Label, StringComparer.OrdinalIgnoreCase), ct);
        var fieldPickLists = fields.Select(field => (field.Id, PickListId: GetPickListId(field), RevisionId: GetPickListRevisionId(field))).Where(x => x.PickListId.HasValue).ToDictionary(x => x.Id);

        foreach (var value in content.FieldValues)
        {
            snapshot.FieldValues.Add(new ContentVersionFieldValue
            {
                ContentVersionId = snapshot.Id,
                FieldId = value.FieldId,
                Order = value.Order,
                ValueKind = value.ValueKind,
                TextValue = value.TextValue,
                DisplayLabel = value.ValueKind == ValueKind.PickList && value.TextValue is not null && fieldPickLists.TryGetValue(value.FieldId, out var binding)
                    ? (binding.RevisionId.HasValue && revisionLabels.TryGetValue(binding.RevisionId.Value, out var versionedOptions) ? versionedOptions : currentLabels.GetValueOrDefault(binding.PickListId!.Value))?.GetValueOrDefault(value.TextValue)
                    : null,
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

    private static Guid? GetPickListId(TemplateField field)
    {
        if (field.FieldConfig is not { ValueKind: JsonValueKind.Object } config || !config.TryGetProperty("picklistId", out var id) || id.ValueKind != JsonValueKind.String)
        {
            return null;
        }
        return Guid.TryParse(id.GetString(), out var parsed) ? parsed : null;
    }

    private static Guid? GetPickListRevisionId(TemplateField field)
    {
        if (field.FieldConfig is not { ValueKind: JsonValueKind.Object } config || !config.TryGetProperty("picklistRevisionId", out var id) || id.ValueKind != JsonValueKind.String)
        {
            return null;
        }
        return Guid.TryParse(id.GetString(), out var parsed) ? parsed : null;
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
