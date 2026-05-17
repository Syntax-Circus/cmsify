using Cmsify.Core.Domain.Entities;
using Cmsify.Core.Domain.Enums;
using Cmsify.Core.Interfaces.Repositories;
using Microsoft.EntityFrameworkCore;

namespace Cmsify.Infrastructure.Persistence.Repositories;

public sealed class ContentItemRepository : IContentItemRepository
{
    private readonly CmsifyDbContext dbContext;

    public ContentItemRepository(CmsifyDbContext dbContext) => this.dbContext = dbContext;

    public async Task<ContentItemDto?> GetAsync(Guid id, CancellationToken ct = default) =>
        (await dbContext.ContentItems.AsNoTracking().FirstOrDefaultAsync(content => content.Id == id, ct))?.ToDto();

    public async Task<PagedResult<ContentItemDto>> QueryAsync(ContentQuery query, CancellationToken ct = default)
    {
        var items = dbContext.ContentItems.AsNoTracking().AsQueryable();

        if (query.WorkspaceId.HasValue)
        {
            items = items.Where(content => content.WorkspaceId == query.WorkspaceId.Value);
        }

        if (query.TemplateId.HasValue)
        {
            items = items.Where(content => content.TemplateVersionId == query.TemplateId.Value);
        }

        if (query.Status.HasValue)
        {
            items = items.Where(content => content.Status == query.Status.Value);
        }

        if (!string.IsNullOrWhiteSpace(query.LocaleCode))
        {
            items = items.Where(content => content.LocaleCode == query.LocaleCode);
        }

        if (!string.IsNullOrWhiteSpace(query.Slug))
        {
            items = items.Where(content => content.Slug == query.Slug);
        }

        if (query.CreatedFrom.HasValue)
        {
            items = items.Where(content => content.CreatedAt >= query.CreatedFrom.Value);
        }

        if (query.CreatedTo.HasValue)
        {
            items = items.Where(content => content.CreatedAt <= query.CreatedTo.Value);
        }

        if (query.PublishedFrom.HasValue)
        {
            items = items.Where(content => content.PublishedAt >= query.PublishedFrom.Value);
        }

        if (query.PublishedTo.HasValue)
        {
            items = items.Where(content => content.PublishedAt <= query.PublishedTo.Value);
        }

        items = query.SortBy switch
        {
            "updatedAt" => query.SortDescending ? items.OrderByDescending(content => content.UpdatedAt) : items.OrderBy(content => content.UpdatedAt),
            "publishedAt" => query.SortDescending ? items.OrderByDescending(content => content.PublishedAt) : items.OrderBy(content => content.PublishedAt),
            "slug" => query.SortDescending ? items.OrderByDescending(content => content.Slug) : items.OrderBy(content => content.Slug),
            _ => query.SortDescending ? items.OrderByDescending(content => content.CreatedAt) : items.OrderBy(content => content.CreatedAt)
        };

        return await items.ToPagedResultAsync(query.Page, content => content.ToDto(), ct);
    }

    public async Task<ContentItemDto> CreateAsync(CreateContentItemCommand command, CancellationToken ct = default)
    {
        var entity = new ContentItem
        {
            WorkspaceId = command.WorkspaceId,
            TemplateVersionId = command.TemplateVersionId,
            Slug = command.Slug,
            LocaleCode = command.LocaleCode,
            TranslationGroupId = command.TranslationGroupId,
            PublishAt = command.PublishAt
        };

        foreach (var input in command.FieldValues)
        {
            entity.FieldValues.Add(CreateFieldValue(entity.Id, input));
        }

        foreach (var tagId in command.TagIds.Distinct())
        {
            entity.Tags.Add(new ContentItemTag { ContentItemId = entity.Id, TagId = tagId });
        }

        dbContext.ContentItems.Add(entity);
        await dbContext.SaveChangesAsync(ct);
        return entity.ToDto();
    }

    public async Task<ContentItemDto> UpdateAsync(UpdateContentItemCommand command, CancellationToken ct = default)
    {
        var entity = await dbContext.ContentItems
            .Include(content => content.FieldValues)
            .Include(content => content.Tags)
            .FirstAsync(content => content.Id == command.Id, ct);

        entity.Slug = command.Slug;
        entity.LocaleCode = command.LocaleCode;
        entity.TranslationGroupId = command.TranslationGroupId;
        entity.PublishAt = command.PublishAt;
        entity.UpdatedAt = DateTimeOffset.UtcNow;

        dbContext.ContentFieldValues.RemoveRange(entity.FieldValues);
        dbContext.ContentItemTags.RemoveRange(entity.Tags);

        foreach (var input in command.FieldValues)
        {
            entity.FieldValues.Add(CreateFieldValue(entity.Id, input));
        }

        foreach (var tagId in command.TagIds.Distinct())
        {
            entity.Tags.Add(new ContentItemTag { ContentItemId = entity.Id, TagId = tagId });
        }

        await dbContext.SaveChangesAsync(ct);
        return entity.ToDto();
    }

    public async Task<ContentItemDto> SetStatusAsync(Guid id, ContentStatus status, Guid actorUserId, CancellationToken ct = default)
    {
        var entity = await dbContext.ContentItems.FirstAsync(content => content.Id == id, ct);
        entity.Status = status;
        entity.UpdatedAt = DateTimeOffset.UtcNow;
        entity.UpdatedByUserId = actorUserId;

        if (status == ContentStatus.Published)
        {
            entity.PublishedAt ??= DateTimeOffset.UtcNow;
        }

        if (status == ContentStatus.Archived)
        {
            entity.ArchivedAt ??= DateTimeOffset.UtcNow;
        }

        await dbContext.SaveChangesAsync(ct);
        return entity.ToDto();
    }

    public async Task<IReadOnlyList<ContentItemDto>> GetPendingScheduledPublishAsync(DateTimeOffset now, int limit = 100, CancellationToken ct = default)
    {
        var items = await dbContext.ContentItems.AsNoTracking()
            .Where(content => content.Status == ContentStatus.Approved && content.PublishAt <= now)
            .OrderBy(content => content.PublishAt)
            .Take(Math.Clamp(limit, 1, 500))
            .ToListAsync(ct);
        return items.Select(content => content.ToDto()).ToArray();
    }

    public async Task SoftDeleteAsync(Guid id, Guid actorUserId, CancellationToken ct = default)
    {
        var entity = await dbContext.ContentItems.FirstAsync(content => content.Id == id, ct);
        entity.SoftDelete(actorUserId);
        await dbContext.SaveChangesAsync(ct);
    }

    private static ContentFieldValue CreateFieldValue(Guid contentItemId, ContentFieldValueInput input) =>
        new()
        {
            ContentItemId = contentItemId,
            FieldId = input.FieldId,
            Order = input.Order,
            ValueKind = input.ValueKind,
            TextValue = input.TextValue,
            BoolValue = input.BoolValue,
            MediaAssetId = input.MediaAssetId,
            FileAssetId = input.FileAssetId,
            ChildContentItemId = input.ChildContentItemId,
            JsonValue = input.JsonValue
        };
}
