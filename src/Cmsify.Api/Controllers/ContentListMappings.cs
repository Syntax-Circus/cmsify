using Cmsify.Core.ContentQueries;
using SyntaxCircus.Cmsify.Contracts;

namespace Cmsify.Api.Controllers;

internal static class ContentListMappings
{
    public static ListContentRequest ToRequest(this ContentListQuery query, Guid workspaceId) => new(
        workspaceId, query.Q, query.TemplateVersionId, query.TemplateId, query.Status?.ToCore(), query.LocaleCode,
        query.TranslationGroupId, query.Slug, query.Tags, query.CreatedAfter, query.CreatedBefore,
        query.PublishedAfter, query.PublishedBefore, query.Resolve, query.AsOf, query.SortBy, query.SortDesc,
        query.Page, query.PageSize);

    public static PagedResponse<ContentItemSummaryResponse> ToResponse(this ContentListOutput page) => new(
        page.Items.Select(item => new ContentItemSummaryResponse(item.Id, item.TemplateVersionId, item.TemplateName,
            item.Slug, item.LocaleCode, item.TranslationGroupId, item.Tags, item.CreatedAt, item.UpdatedAt,
            item.VersionCount, item.CurrentlyServingVersion is { } version ? ToResponse(version) : null,
            item.TemplateSlug)).ToArray(), page.TotalCount, page.Page, page.PageSize);

    private static ContentVersionSummaryResponse ToResponse(ContentListVersionOutput version) => new(
        version.Id, version.ContentItemId, version.VersionNumber, version.Status.ToContract(), version.TemplateVersionId,
        version.Slug, version.LocaleCode, version.EffectiveStartAt, version.EffectiveEndAt, version.PublishAt,
        version.PublishedAt, version.ArchivedAt, version.PublishedByUserId, version.RolledBackFromVersionNumber,
        version.Tags, version.CreatedAt, version.UpdatedAt);
}
