using Cmsify.Core.ContentWrites;
using Cmsify.Core.Domain.Entities;
using Cmsify.Core.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace Cmsify.Infrastructure.Persistence.ContentWrites;

public sealed class ContentVersionDetailProjector(CmsifyDbContext dbContext)
{
    public static ContentVersion SelectMostSpecific(IEnumerable<ContentVersion> versions, DateTimeOffset asOf) =>
        versions
            .OrderBy(version => version.EffectiveStartAt.HasValue && version.EffectiveEndAt.HasValue ? 0 : 1)
            .ThenBy(version => version.EffectiveStartAt.HasValue && version.EffectiveEndAt.HasValue ? version.EffectiveEndAt!.Value - version.EffectiveStartAt!.Value : TimeSpan.MaxValue)
            .ThenByDescending(version => version.PublishedAt)
            .ThenByDescending(version => version.VersionNumber)
            .First();

    // Resolves a whole set of content item ids in ONE query instead of one query per id, used to
    // expand a whole layer of a version's field-value tree at once. Filtering semantics are
    // preserve the API's published/effective filtering and existing soft-delete subquery;
    // only the shape differs, batched with an IN-clause and grouped by content item id before
    // SelectMostSpecific picks the winner for each one.
    private async Task<Dictionary<Guid, ContentVersion>> ResolvePublishedVersionsAsync(Guid workspaceId, IReadOnlyCollection<Guid> contentItemIds, DateTimeOffset asOf, CancellationToken ct)
    {
        if (contentItemIds.Count == 0)
        {
            return [];
        }

        var candidates = await dbContext.ContentVersions.AsNoTracking()
            .Include(version => version.FieldValues)
            .Where(version => version.WorkspaceId == workspaceId && version.Status == ContentStatus.Published)
            .Where(version => contentItemIds.Contains(version.ContentItemId))
            .Where(version =>
                (version.EffectiveStartAt == null && version.EffectiveEndAt == null)
                || (version.EffectiveStartAt <= asOf && asOf < version.EffectiveEndAt))
            .Where(version => !dbContext.ContentItems.Any(content => content.Id == version.ContentItemId && content.IsDeleted))
            .ToListAsync(ct);

        return candidates
            .GroupBy(version => version.ContentItemId)
            .ToDictionary(group => group.Key, group => SelectMostSpecific(group, asOf));
    }

    // expandChildren controls whether ChildContentItemId fields are resolved into a nested `Child`
    // ContentVersionDetailOutput (as this always did historically) or left as just the id -
    // editing clients (ContentEditPanel/ContentEditSupport) only ever read ChildContentItemId, never
    // Child, so they pass false to skip the whole expansion below. The API facade defaults to true.
    //
    // Expansion, when enabled, is batched per recursion layer instead of recursing field-by-field:
    // layer 0 is just `version` itself, and each subsequent layer resolves EVERY ChildContentItemId
    // referenced anywhere in the previous layer with one query (see ResolvePublishedVersionsAsync)
    // instead of one query per child. The original shape here did one sequential DB round trip per
    // child, at every depth down to 8 - for a deeply-nested Inline content tree that's dozens of
    // round trips for what a single batched query per layer now covers. Template name/field lookups
    // are also memoized per TemplateVersionId (templateCache) for the lifetime of one response
    // build, since the same template is commonly reused by many sibling/descendant children.
    public async Task<ContentVersionDetailOutput> ProjectAsync(ContentVersion version, DateTimeOffset asOf, bool expandChildren, CancellationToken ct)
    {
        var templateCache = new Dictionary<Guid, (string Name, string Slug, Dictionary<Guid, TemplateField> Fields)>();
        var layers = new List<IReadOnlyList<ContentVersion>> { new List<ContentVersion> { version } };
        var fieldValuesByVersionId = new Dictionary<Guid, IReadOnlyList<ContentVersionFieldValue>>();
        // Index i holds the content-item-id -> resolved-version map produced FROM layer i's field
        // values, i.e. it's the map that layer i+1 was built from - kept around so the bottom-up
        // build below can look up which already-built child response belongs to which field.
        var resolvedChildrenPerLayer = new List<Dictionary<Guid, ContentVersion>>();

        for (var depth = 0; expandChildren && depth < 8 && layers[depth].Count > 0; depth++)
        {
            var childIds = new HashSet<Guid>();
            foreach (var layerVersion in layers[depth])
            {
                var fieldValues = await GetFieldValuesAsync(layerVersion, ct);
                fieldValuesByVersionId[layerVersion.Id] = fieldValues;
                foreach (var value in fieldValues)
                {
                    if (value.ChildContentItemId is { } childId)
                    {
                        childIds.Add(childId);
                    }
                }
            }

            var resolvedChildren = await ResolvePublishedVersionsAsync(version.WorkspaceId, childIds, asOf, ct);
            resolvedChildrenPerLayer.Add(resolvedChildren);
            layers.Add(resolvedChildren.Count == 0 ? [] : resolvedChildren.Values.ToList());
        }

        // The last layer's own field values are only loaded above when the loop actually inspects
        // it to discover the NEXT layer - a layer stopped by the depth-8 cap (rather than simply
        // having no children) never gets that turn, so make sure it's loaded here too before the
        // bottom-up build below.
        foreach (var layerVersion in layers[^1])
        {
            fieldValuesByVersionId.TryAdd(layerVersion.Id, await GetFieldValuesAsync(layerVersion, ct));
        }

        // Build bottom-up: the deepest layer has nothing further to attach (either genuinely
        // childless or past the depth cap), then each shallower layer attaches the already-built
        // response for whichever child ContentVersion was resolved for its ChildContentItemId.
        var builtByVersionId = new Dictionary<Guid, ContentVersionDetailOutput>();
        for (var depth = layers.Count - 1; depth >= 0; depth--)
        {
            var childrenByContentItemId = depth < resolvedChildrenPerLayer.Count ? resolvedChildrenPerLayer[depth] : null;
            foreach (var layerVersion in layers[depth])
            {
                var (templateName, templateSlug, templateFields) = await GetTemplateVersionInfoAsync(layerVersion.TemplateVersionId, templateCache, ct);
                var fieldValues = fieldValuesByVersionId[layerVersion.Id];

                var fields = new List<ContentVersionFieldOutput>();
                foreach (var value in fieldValues.OrderBy(value => templateFields.GetValueOrDefault(value.FieldId)?.Order ?? 0).ThenBy(value => value.Order).ThenBy(value => value.Id))
                {
                    templateFields.TryGetValue(value.FieldId, out var field);
                    ContentVersionDetailOutput? child = null;
                    if (childrenByContentItemId is not null
                        && value.ChildContentItemId is { } childContentItemId
                        && childrenByContentItemId.TryGetValue(childContentItemId, out var childVersion))
                    {
                        child = builtByVersionId[childVersion.Id];
                    }

                    fields.Add(new ContentVersionFieldOutput(value.FieldId, field?.Key, field?.Label, value.Order, value.ValueKind, value.TextValue, value.BoolValue, value.MediaAssetId, value.FileAssetId, value.ChildContentItemId, child, value.JsonValue?.Clone(), value.DisplayLabel));
                }

                builtByVersionId[layerVersion.Id] = new ContentVersionDetailOutput(
                    layerVersion.Id, layerVersion.ContentItemId, layerVersion.VersionNumber, layerVersion.Status, layerVersion.TemplateVersionId, templateName,
                    layerVersion.Slug, layerVersion.LocaleCode, layerVersion.TranslationGroupId, layerVersion.EffectiveStartAt, layerVersion.EffectiveEndAt,
                    layerVersion.PublishAt, layerVersion.PublishedAt, layerVersion.ArchivedAt, layerVersion.PublishedByUserId, layerVersion.RolledBackFromVersionNumber,
                    layerVersion.Tags.ToList(), layerVersion.CreatedAt, layerVersion.UpdatedAt, fields, templateSlug);
            }
        }

        return builtByVersionId[version.Id];
    }

    private async Task<IReadOnlyList<ContentVersionFieldValue>> GetFieldValuesAsync(ContentVersion version, CancellationToken ct) =>
        version.FieldValues.Count > 0
            ? [.. version.FieldValues]
            : await dbContext.ContentVersionFieldValues.AsNoTracking().Where(value => value.ContentVersionId == version.Id).ToListAsync(ct);

    // Memoizes template name + field lookups per TemplateVersionId for the lifetime of a single
    // projection - the same template is commonly reused by many
    // sibling/descendant children in an expanded content tree, and this avoids re-querying it once
    // per occurrence.
    private async Task<(string Name, string Slug, Dictionary<Guid, TemplateField> Fields)> GetTemplateVersionInfoAsync(Guid templateVersionId, Dictionary<Guid, (string Name, string Slug, Dictionary<Guid, TemplateField> Fields)> cache, CancellationToken ct)
    {
        if (cache.TryGetValue(templateVersionId, out var cached))
        {
            return cached;
        }

        var template = await dbContext.TemplateVersions.AsNoTracking()
            .Where(tv => tv.Id == templateVersionId)
            .Select(tv => dbContext.Templates.Where(t => t.Id == tv.TemplateId).Select(t => new { t.Name, t.Slug }).First())
            .FirstOrDefaultAsync(ct);
        var templateFields = await dbContext.TemplateFields.AsNoTracking()
            .Where(field => field.TemplateVersionId == templateVersionId)
            .ToDictionaryAsync(field => field.Id, ct);

        var info = (template?.Name ?? string.Empty, template?.Slug ?? string.Empty, templateFields);
        cache[templateVersionId] = info;
        return info;
    }

}
