using Cmsify.Core.Domain.Entities;
using Cmsify.Core.Interfaces.Repositories;
using Cmsify.Core.Interfaces.Services;
using Cmsify.Core.Workspaces;
using Cmsify.Infrastructure.Auth;
using Microsoft.EntityFrameworkCore;

namespace Cmsify.Infrastructure.Persistence.Repositories;

public sealed class WorkspaceRepository : IWorkspaceRepository
{
    private readonly CmsifyDbContext dbContext;
    private readonly ICurrentActor currentActor;
    private readonly IWorkspaceVisibilityScopeProvider _visibility;

    public WorkspaceRepository(CmsifyDbContext dbContext, ICurrentActor currentActor)
        : this(dbContext, currentActor, new CmsManagedWorkspaceVisibilityScopeProvider())
    {
    }

    public WorkspaceRepository(CmsifyDbContext dbContext, ICurrentActor currentActor, IWorkspaceVisibilityScopeProvider visibility)
    {
        this.dbContext = dbContext;
        this.currentActor = currentActor;
        _visibility = visibility;
    }

    public async Task<WorkspaceDto?> GetAsync(Guid id, CancellationToken ct = default) =>
        (await (await ScopeAsync(dbContext.Workspaces.AsNoTracking(), ct)).FirstOrDefaultAsync(workspace => workspace.Id == id, ct))?.ToDto();

    public async Task<PagedResult<WorkspaceDto>> ListAsync(PageRequest page, CancellationToken ct = default) =>
        await (await ScopeAsync(dbContext.Workspaces.AsNoTracking(), ct)).OrderBy(workspace => workspace.Name).ToPagedResultAsync(page, workspace => workspace.ToDto(), ct);

    public async Task<WorkspaceDto> CreateAsync(CreateWorkspaceCommand command, CancellationToken ct = default)
    {
        var entity = new Workspace { Name = command.Name, Slug = command.Slug, Description = command.Description };
        entity.CreatedAt = WorkspaceRevision.Normalize(entity.CreatedAt);
        entity.UpdatedAt = WorkspaceRevision.Normalize(entity.UpdatedAt);
        dbContext.Workspaces.Add(entity);
        await dbContext.SaveChangesAsync(ct);
        return entity.ToDto();
    }

    public async Task<WorkspaceDto> UpdateAsync(UpdateWorkspaceCommand command, CancellationToken ct = default)
    {
        var entity = await (await ScopeAsync(dbContext.Workspaces, ct)).FirstAsync(workspace => workspace.Id == command.Id, ct);
        entity.Name = command.Name;
        entity.Slug = command.Slug;
        entity.Description = command.Description;
        entity.UpdatedAt = DateTimeOffset.UtcNow;
        await dbContext.SaveChangesAsync(ct);
        return entity.ToDto();
    }

    public async Task SoftDeleteAsync(Guid id, Guid actorUserId, CancellationToken ct = default)
    {
        var entity = await (await ScopeAsync(dbContext.Workspaces, ct)).FirstAsync(workspace => workspace.Id == id, ct);
        entity.SoftDelete(actorUserId);
        await dbContext.SaveChangesAsync(ct);
    }

    private async Task<IQueryable<Workspace>> ScopeAsync(IQueryable<Workspace> query, CancellationToken ct) =>
        query.ApplyWorkspaceVisibility(dbContext, currentActor, await _visibility.ResolveAsync(ct));
}
