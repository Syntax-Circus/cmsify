using Cmsify.Core.Domain.Entities;
using Cmsify.Core.Interfaces.Repositories;
using Microsoft.EntityFrameworkCore;

namespace Cmsify.Infrastructure.Persistence.Repositories;

public sealed class WorkspaceRepository : IWorkspaceRepository
{
    private readonly CmsifyDbContext dbContext;

    public WorkspaceRepository(CmsifyDbContext dbContext) => this.dbContext = dbContext;

    public async Task<WorkspaceDto?> GetAsync(Guid id, CancellationToken ct = default) =>
        (await dbContext.Workspaces.AsNoTracking().FirstOrDefaultAsync(workspace => workspace.Id == id, ct))?.ToDto();

    public Task<PagedResult<WorkspaceDto>> ListAsync(PageRequest page, CancellationToken ct = default) =>
        dbContext.Workspaces.AsNoTracking().OrderBy(workspace => workspace.Name).ToPagedResultAsync(page, workspace => workspace.ToDto(), ct);

    public async Task<WorkspaceDto> CreateAsync(CreateWorkspaceCommand command, CancellationToken ct = default)
    {
        var entity = new Workspace { Name = command.Name, Slug = command.Slug, Description = command.Description };
        dbContext.Workspaces.Add(entity);
        await dbContext.SaveChangesAsync(ct);
        return entity.ToDto();
    }

    public async Task<WorkspaceDto> UpdateAsync(UpdateWorkspaceCommand command, CancellationToken ct = default)
    {
        var entity = await dbContext.Workspaces.FirstAsync(workspace => workspace.Id == command.Id, ct);
        entity.Name = command.Name;
        entity.Slug = command.Slug;
        entity.Description = command.Description;
        entity.UpdatedAt = DateTimeOffset.UtcNow;
        await dbContext.SaveChangesAsync(ct);
        return entity.ToDto();
    }

    public async Task SoftDeleteAsync(Guid id, Guid actorUserId, CancellationToken ct = default)
    {
        var entity = await dbContext.Workspaces.FirstAsync(workspace => workspace.Id == id, ct);
        entity.SoftDelete(actorUserId);
        await dbContext.SaveChangesAsync(ct);
    }
}
