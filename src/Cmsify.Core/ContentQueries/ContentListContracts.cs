using Cmsify.Core.Domain.Enums;

namespace Cmsify.Core.ContentQueries;

public sealed record ListContentRequest(
    Guid WorkspaceId, string? Q = null, Guid? TemplateVersionId = null,
    Guid? TemplateId = null, ContentStatus? Status = null, string? LocaleCode = null,
    Guid? TranslationGroupId = null, string? Slug = null, string? Tags = null,
    DateTimeOffset? CreatedAfter = null, DateTimeOffset? CreatedBefore = null,
    DateTimeOffset? PublishedAfter = null, DateTimeOffset? PublishedBefore = null,
    bool Resolve = false, DateTimeOffset? AsOf = null, string? SortBy = ContentListRules.CreatedAtSort,
    bool SortDesc = true, int Page = 1, int PageSize = ContentListRules.DefaultPageSize);

public sealed record ContentListOutput(IReadOnlyList<ContentListItemOutput> Items, int TotalCount, int Page, int PageSize);
public sealed record ContentListPage(IReadOnlyList<ContentListItemOutput> Items, int TotalCount);

/// <summary>Trusted handler output. A null offset requests a count-only, empty page.</summary>
public sealed record ContentListCriteria(ListContentRequest Request, DateTimeOffset EvaluationTime, int? Offset);

public sealed record ContentListItemOutput(
    Guid Id, Guid TemplateVersionId, string TemplateName, string? Slug, string? LocaleCode,
    Guid? TranslationGroupId, IReadOnlyList<string> Tags, DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt, int VersionCount, ContentListVersionOutput? CurrentlyServingVersion,
    string TemplateSlug = "");

public sealed record ContentListVersionOutput(
    Guid Id, Guid ContentItemId, int VersionNumber, ContentStatus Status, Guid TemplateVersionId,
    string? Slug, string? LocaleCode, DateTimeOffset? EffectiveStartAt, DateTimeOffset? EffectiveEndAt,
    DateTimeOffset? PublishAt, DateTimeOffset? PublishedAt, DateTimeOffset? ArchivedAt,
    Guid? PublishedByUserId, int? RolledBackFromVersionNumber, IReadOnlyList<string> Tags,
    DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt);
