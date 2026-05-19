using Cmsify.Core.Domain.Entities;
using Cmsify.Core.Interfaces.Repositories;
using Cmsify.Core.Interfaces.Services;
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

    public static IQueryable<TEntity> ScopeToActorWorkspace<TEntity>(this IQueryable<TEntity> query, ICurrentActor actor)
    {
        return actor.WorkspaceId.HasValue
            ? query.Where(entity => EF.Property<Guid>(entity!, "WorkspaceId") == actor.WorkspaceId.Value)
            : query;
    }

    public static IQueryable<Workspace> ScopeWorkspacesToReadableActor(this IQueryable<Workspace> query, CmsifyDbContext dbContext, ICurrentActor actor)
    {
        if (actor.IsSuperAdmin)
        {
            return query;
        }

        if (actor.WorkspaceId.HasValue)
        {
            return query.Where(workspace => workspace.Id == actor.WorkspaceId.Value);
        }

        if (actor.UserId.HasValue)
        {
            var userId = actor.UserId.Value;
            return query.Where(workspace => dbContext.UserWorkspaceAccesses.Any(access => access.UserId == userId && access.WorkspaceId == workspace.Id));
        }

        return query.Where(_ => false);
    }

    public static IQueryable<TEntity> ScopeWorkspaceEntitiesToReadableActor<TEntity>(this IQueryable<TEntity> query, CmsifyDbContext dbContext, ICurrentActor actor)
        where TEntity : class
    {
        if (actor.IsSuperAdmin)
        {
            return query;
        }

        if (actor.WorkspaceId.HasValue)
        {
            return query.Where(entity => EF.Property<Guid>(entity, "WorkspaceId") == actor.WorkspaceId.Value);
        }

        if (actor.UserId.HasValue)
        {
            var userId = actor.UserId.Value;
            return query.Where(entity => dbContext.UserWorkspaceAccesses.Any(access => access.UserId == userId && access.WorkspaceId == EF.Property<Guid>(entity, "WorkspaceId")));
        }

        return query.Where(_ => false);
    }
}
