using Cmsify.Core.Domain.Entities;
using Cmsify.Core.Interfaces.Repositories;
using Microsoft.EntityFrameworkCore;

namespace Cmsify.Infrastructure.Persistence.Repositories;

internal static class RepositoryHelpers
{
    public static async Task<PagedResult<TDto>> ToPagedResultAsync<TEntity, TDto>(
        this IQueryable<TEntity> query,
        PageRequest page,
        Func<TEntity, TDto> mapper,
        CancellationToken ct)
    {
        var offset = Math.Max(0, page.Offset);
        var limit = Math.Clamp(page.Limit, 1, 500);
        var total = await query.CountAsync(ct);
        var items = await query.Skip(offset).Take(limit).ToListAsync(ct);
        return new PagedResult<TDto>(items.Select(mapper).ToArray(), total, offset, limit);
    }

    public static void SoftDelete(this SoftDeletableEntity entity, Guid actorUserId)
    {
        entity.IsDeleted = true;
        entity.DeletedAt = DateTimeOffset.UtcNow;
        entity.DeletedByUserId = actorUserId;
        entity.UpdatedAt = DateTimeOffset.UtcNow;
    }
}
