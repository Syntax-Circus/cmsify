using Cmsify.Api.Controllers;
using Cmsify.Core.Domain.Entities;
using Cmsify.Core.Domain.Enums;
using Cmsify.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using ContentListQuery = SyntaxCircus.Cmsify.Contracts.ContentListQuery;

namespace Cmsify.Api.Queries;

internal interface IResolvedContentListQuery
{
    Task<ResolvedContentListPage> ExecuteAsync(
        Guid workspaceId,
        ContentListQuery query,
        DateTimeOffset asOf,
        CancellationToken ct);
}

internal sealed record ResolvedContentListRow(
    Guid ContentItemId,
    Guid TemplateVersionId,
    string TemplateName,
    string? Slug,
    string? LocaleCode,
    Guid? TranslationGroupId,
    IReadOnlyList<string> Tags,
    DateTimeOffset PublishedAt);

internal sealed record ResolvedContentListPage(
    IReadOnlyList<ResolvedContentListRow> Items,
    int TotalCount);

internal sealed class ResolvedContentListQuery(CmsifyDbContext dbContext) : IResolvedContentListQuery
{
    public async Task<ResolvedContentListPage> ExecuteAsync(
        Guid workspaceId,
        ContentListQuery query,
        DateTimeOffset asOf,
        CancellationToken ct)
    {
        if (query.Status.HasValue && query.Status.Value != SyntaxCircus.Cmsify.Contracts.ContentStatus.Published)
        {
            return new ResolvedContentListPage([], 0);
        }

        var tags = string.IsNullOrWhiteSpace(query.Tags)
            ? []
            : query.Tags
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(NormalizeTag)
                .Where(tag => tag.Length > 0)
                .Distinct()
                .ToArray();
        IQueryable<ContentVersion> candidates = tags.Length == 0
            ? dbContext.ContentVersions.AsNoTracking()
            : dbContext.ContentVersions
                .FromSql($"SELECT * FROM content_versions WHERE tags @> {tags}")
                .AsNoTracking();
        candidates = candidates
            .Where(version => version.WorkspaceId == workspaceId && version.Status == ContentVersionStatus.Published)
            .Where(version =>
                (version.EffectiveStartAt == null && version.EffectiveEndAt == null)
                || (version.EffectiveStartAt <= asOf && asOf < version.EffectiveEndAt))
            .Where(version => dbContext.ContentItems.Any(content => content.Id == version.ContentItemId));

        if (query.TemplateVersionId.HasValue)
        {
            candidates = candidates.Where(version => version.TemplateVersionId == query.TemplateVersionId.Value);
        }

        if (query.TemplateId.HasValue)
        {
            candidates = candidates.Where(version => dbContext.TemplateVersions.Any(templateVersion =>
                templateVersion.Id == version.TemplateVersionId
                && templateVersion.TemplateId == query.TemplateId.Value));
        }

        if (!string.IsNullOrWhiteSpace(query.LocaleCode))
        {
            candidates = candidates.Where(version => version.LocaleCode == query.LocaleCode);
        }

        if (query.TranslationGroupId.HasValue)
        {
            candidates = candidates.Where(version => version.TranslationGroupId == query.TranslationGroupId.Value);
        }

        if (!string.IsNullOrWhiteSpace(query.Slug))
        {
            candidates = candidates.Where(version => version.Slug == query.Slug);
        }

        if (query.PublishedAfter.HasValue)
        {
            candidates = candidates.Where(version => version.PublishedAt >= query.PublishedAfter.Value);
        }

        if (query.PublishedBefore.HasValue)
        {
            candidates = candidates.Where(version => version.PublishedAt <= query.PublishedBefore.Value);
        }

        var winners = candidates.Where(version => version.Id == candidates
            .Where(candidate => candidate.ContentItemId == version.ContentItemId)
            .OrderBy(candidate => candidate.EffectiveStartAt.HasValue && candidate.EffectiveEndAt.HasValue ? 0 : 1)
            .ThenBy(candidate => candidate.EffectiveStartAt.HasValue && candidate.EffectiveEndAt.HasValue
                ? candidate.EffectiveEndAt!.Value - candidate.EffectiveStartAt!.Value
                : TimeSpan.MaxValue)
            .ThenByDescending(candidate => candidate.PublishedAt)
            .ThenByDescending(candidate => candidate.VersionNumber)
            .Select(candidate => candidate.Id)
            .First());

        if (!string.IsNullOrWhiteSpace(query.Q))
        {
            winners = winners.Where(version => EF.Functions.ILike(version.Slug ?? string.Empty, $"%{query.Q}%"));
        }

        var totalCount = await winners.CountAsync(ct);
        if (!ControllerHelpers.TryOffset(query.Page, query.PageSize, out var offset))
        {
            return new ResolvedContentListPage([], totalCount);
        }

        var pageRows =
            from version in winners
            join templateVersion in dbContext.TemplateVersions.AsNoTracking()
                on version.TemplateVersionId equals templateVersion.Id
            join template in dbContext.Templates.AsNoTracking()
                on templateVersion.TemplateId equals template.Id
            select new
            {
                Version = version,
                TemplateName = template.Name
            };

        var orderedRows = query.SortBy switch
        {
            "slug" when query.SortDesc => pageRows
                .OrderBy(row => row.Version.Slug == null ? 1 : 0)
                .ThenByDescending(row => row.Version.Slug)
                .ThenBy(row => row.Version.ContentItemId),
            "slug" => pageRows
                .OrderBy(row => row.Version.Slug == null ? 0 : 1)
                .ThenBy(row => row.Version.Slug)
                .ThenBy(row => row.Version.ContentItemId),
            _ when query.SortDesc => pageRows
                .OrderByDescending(row => row.Version.PublishedAt)
                .ThenBy(row => row.Version.ContentItemId),
            _ => pageRows
                .OrderBy(row => row.Version.PublishedAt)
                .ThenBy(row => row.Version.ContentItemId)
        };

        var projections = await orderedRows
            .Skip(offset)
            .Take(ControllerHelpers.Limit(query.PageSize))
            .Select(row => new ResolvedContentListProjection(
                row.Version.ContentItemId,
                row.Version.TemplateVersionId,
                row.TemplateName,
                row.Version.Slug,
                row.Version.LocaleCode,
                row.Version.TranslationGroupId,
                row.Version.Tags,
                row.Version.PublishedAt))
            .ToListAsync(ct);
        var items = projections
            .Select(row => new ResolvedContentListRow(
                row.ContentItemId,
                row.TemplateVersionId,
                row.TemplateName,
                row.Slug,
                row.LocaleCode,
                row.TranslationGroupId,
                row.Tags.ToList(),
                row.PublishedAt))
            .ToList();

        return new ResolvedContentListPage(items, totalCount);
    }

    private static string NormalizeTag(string tag) => tag.Trim().ToLowerInvariant();

    private sealed record ResolvedContentListProjection(
        Guid ContentItemId,
        Guid TemplateVersionId,
        string TemplateName,
        string? Slug,
        string? LocaleCode,
        Guid? TranslationGroupId,
        IList<string> Tags,
        DateTimeOffset PublishedAt);
}
