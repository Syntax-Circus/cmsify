using Cmsify.Core.ContentQueries;
using System.Text.Json;
using SyntaxCircus.Cmsify.Contracts;

namespace Cmsify.Api.Controllers;

internal static class ContentListMappings
{
    public static ListContentRequest ToRequest(this ContentListQuery query, Guid workspaceId) => new(
        workspaceId, query.Q, query.TemplateVersionId, query.TemplateId, query.Status?.ToCore(), query.LocaleCode,
        query.TranslationGroupId, query.Slug, query.Tags, query.CreatedAfter, query.CreatedBefore,
        query.PublishedAfter, query.PublishedBefore, query.Resolve, query.AsOf, query.SortBy, query.SortDesc,
        query.Page, query.PageSize, query.Fields);

    public static PagedResponse<ContentItemSummaryResponse> ToResponse(this ContentListOutput page) => new(
        page.Items.Select(item => new ContentItemSummaryResponse(item.Id, item.TemplateVersionId, item.TemplateName,
            item.Slug, item.LocaleCode, item.TranslationGroupId, item.Tags, item.CreatedAt, item.UpdatedAt,
            item.VersionCount, item.CurrentlyServingVersion is { } version ? ToResponse(version) : null,
            item.TemplateSlug, item.Fields?.Select(ToResponse).ToArray())).ToArray(), page.TotalCount, page.Page, page.PageSize);

    private static ContentVersionFieldValueResponse ToResponse(ContentListFieldOutput field) => new(
        field.FieldId, field.Key, field.Label, field.Order, field.ValueKind.ToContract(), field.TextValue, field.BoolValue,
        field.MediaAssetId, field.FileAssetId, field.ChildContentItemId, null,
        field.JsonValue is null ? null : JsonDocument.Parse(field.JsonValue).RootElement.Clone(), field.DisplayLabel);

    private static ContentVersionSummaryResponse ToResponse(ContentListVersionOutput version) => new(
        version.Id, version.ContentItemId, version.VersionNumber, version.Status.ToContract(), version.TemplateVersionId,
        version.Slug, version.LocaleCode, version.EffectiveStartAt, version.EffectiveEndAt, version.PublishAt,
        version.PublishedAt, version.ArchivedAt, version.PublishedByUserId, version.RolledBackFromVersionNumber,
        version.Tags, version.CreatedAt, version.UpdatedAt);
}
