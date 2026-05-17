using Cmsify.Core.Domain.Entities;
using Cmsify.Core.Interfaces.Repositories;
using Cmsify.Core.Interfaces.Services;
using Microsoft.EntityFrameworkCore;

namespace Cmsify.Infrastructure.Persistence.Repositories;

public sealed class TagRepository : ITagRepository
{
    private readonly CmsifyDbContext dbContext;
    private readonly ICurrentActor currentActor;

    public TagRepository(CmsifyDbContext dbContext, ICurrentActor currentActor)
    {
        this.dbContext = dbContext;
        this.currentActor = currentActor;
    }

    public async Task<IReadOnlyList<TagDto>> ListByWorkspaceAsync(Guid workspaceId, CancellationToken ct = default)
    {
        var tags = await dbContext.Tags.AsNoTracking()
            .Where(tag => tag.WorkspaceId == workspaceId)
            .ScopeToActorWorkspace(currentActor)
            .OrderBy(tag => tag.Name)
            .ToListAsync(ct);
        return tags.Select(tag => tag.ToDto()).ToArray();
    }

    public async Task<TagDto> UpsertAsync(UpsertTagCommand command, CancellationToken ct = default)
    {
        var entity = await dbContext.Tags.ScopeToActorWorkspace(currentActor).FirstOrDefaultAsync(tag => tag.WorkspaceId == command.WorkspaceId && tag.Name == command.Name, ct);
        if (entity is null)
        {
            entity = new Tag { WorkspaceId = command.WorkspaceId, Name = command.Name };
            dbContext.Tags.Add(entity);
        }

        await dbContext.SaveChangesAsync(ct);
        return entity.ToDto();
    }

    public async Task DeleteAsync(Guid id, CancellationToken ct = default)
    {
        var entity = await dbContext.Tags.ScopeToActorWorkspace(currentActor).FirstAsync(tag => tag.Id == id, ct);
        entity.IsDeleted = true;
        entity.DeletedAt = DateTimeOffset.UtcNow;
        await dbContext.SaveChangesAsync(ct);
    }
}
