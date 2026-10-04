using System.Runtime.CompilerServices;
using Cmsify.Core.ContentQueries;
using Cmsify.Core.Domain.Entities;
using Microsoft.EntityFrameworkCore;

[assembly: InternalsVisibleTo("Cmsify.Infrastructure.Sqlite")]

namespace Cmsify.Infrastructure.Persistence.ContentQueries;

/// <summary>Identical, bounded DTO projection for both provider query shapes.</summary>
internal static class ContentListProjection
{
    internal static async Task<ContentListPage> Items(CmsifyDbContext db, IQueryable<ContentItem> items,
        ContentListCriteria criteria, Func<Guid[], IQueryable<ContentVersion>> serving, CancellationToken ct)
    {
        var total = await items.CountAsync(ct);
        if (criteria.Offset is not { } offset) return new([], total);
        var request = criteria.Request;
        var ordered = request.SortBy switch
        {
            ContentListRules.UpdatedAtSort => request.SortDesc ? items.OrderByDescending(x => x.UpdatedAt) : items.OrderBy(x => x.UpdatedAt),
            ContentListRules.SlugSort => request.SortDesc ? items.OrderByDescending(x => x.Slug) : items.OrderBy(x => x.Slug),
            _ => request.SortDesc ? items.OrderByDescending(x => x.CreatedAt) : items.OrderBy(x => x.CreatedAt)
        };
        // Left joins deliberately retain owners with unavailable templates; the released
        // ordinary operation fails while resolved count/page retain their separate shape.
        var page = await (from item in ordered.Skip(offset).Take(request.PageSize)
            join tv in db.TemplateVersions.AsNoTracking() on item.TemplateVersionId equals tv.Id into versions
            from tv in versions.DefaultIfEmpty()
            join template in db.Templates.AsNoTracking() on tv.TemplateId equals template.Id into templates
            from template in templates.DefaultIfEmpty()
            select new ItemRow(item.Id, item.TemplateVersionId, template == null ? null : template.Name,
                template == null ? null : template.Slug, item.Slug, item.LocaleCode, item.TranslationGroupId,
                item.CreatedAt, item.UpdatedAt, db.ContentVersions.Count(v => v.ContentItemId == item.Id)))
            .ToListAsync(ct);
        if (page.Any(row => row.TemplateName is null || row.TemplateSlug is null))
            throw new InvalidOperationException("Sequence contains no elements.");
        var ids = page.Select(row => row.Id).ToArray();
        var tags = await (from link in db.ContentItemTags.AsNoTracking()
            join tag in db.Tags.AsNoTracking() on link.TagId equals tag.Id
            where ids.Contains(link.ContentItemId)
            orderby tag.Name
            select new TagRow(link.ContentItemId, tag.Name)).ToListAsync(ct);
        var winners = await serving(ids).Select(v => new ContentListVersionOutput(v.Id, v.ContentItemId,
            v.VersionNumber, v.Status, v.TemplateVersionId, v.Slug, v.LocaleCode, v.EffectiveStartAt, v.EffectiveEndAt,
            v.PublishAt, v.PublishedAt, v.ArchivedAt, v.PublishedByUserId, v.RolledBackFromVersionNumber,
            v.Tags.ToArray(), v.CreatedAt, v.UpdatedAt)).ToListAsync(ct);
        var tagsByOwner = tags.ToLookup(row => row.Owner, row => row.Name);
        var winnerByOwner = winners.ToDictionary(row => row.ContentItemId);
        return new(page.Select(row => new ContentListItemOutput(row.Id, row.TemplateVersionId, row.TemplateName!,
            row.Slug, row.LocaleCode, row.TranslationGroupId, tagsByOwner[row.Id].ToArray(), row.CreatedAt,
            row.UpdatedAt, row.VersionCount, winnerByOwner.GetValueOrDefault(row.Id), row.TemplateSlug!)).ToArray(), total);
    }

    internal static async Task<ContentListPage> Resolved(CmsifyDbContext db, IQueryable<ContentVersion> winners,
        ContentListCriteria criteria, CancellationToken ct)
    {
        var total = await winners.CountAsync(ct);
        if (criteria.Offset is not { } offset) return new([], total);
        var joined = from v in winners
            join tv in db.TemplateVersions.AsNoTracking() on v.TemplateVersionId equals tv.Id
            join template in db.Templates.AsNoTracking() on tv.TemplateId equals template.Id
            select new { Version = v, template.Name, template.Slug };
        var request = criteria.Request;
        var ordered = request.SortBy switch
        {
            ContentListRules.SlugSort when request.SortDesc => joined.OrderBy(x => x.Version.Slug == null ? 1 : 0)
                .ThenByDescending(x => x.Version.Slug).ThenBy(x => x.Version.ContentItemId),
            ContentListRules.SlugSort => joined.OrderBy(x => x.Version.Slug == null ? 0 : 1)
                .ThenBy(x => x.Version.Slug).ThenBy(x => x.Version.ContentItemId),
            _ when request.SortDesc => joined.OrderByDescending(x => x.Version.PublishedAt).ThenBy(x => x.Version.ContentItemId),
            _ => joined.OrderBy(x => x.Version.PublishedAt).ThenBy(x => x.Version.ContentItemId)
        };
        var rows = await ordered.Skip(offset).Take(request.PageSize).Select(x => new ContentListItemOutput(
            x.Version.ContentItemId, x.Version.TemplateVersionId, x.Name, x.Version.Slug, x.Version.LocaleCode,
            x.Version.TranslationGroupId, x.Version.Tags.ToArray(), x.Version.PublishedAt.GetValueOrDefault(),
            x.Version.PublishedAt.GetValueOrDefault(), 1, null, x.Slug)).ToListAsync(ct);
        return new(rows, total);
    }

    private sealed record ItemRow(Guid Id, Guid TemplateVersionId, string? TemplateName, string? TemplateSlug,
        string? Slug, string? LocaleCode, Guid? TranslationGroupId, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt, int VersionCount);
    private sealed record TagRow(Guid Owner, string Name);
}
