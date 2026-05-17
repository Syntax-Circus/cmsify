using Cmsify.Core.Domain.Entities;
using Cmsify.Core.Interfaces.Repositories;
using Cmsify.Core.Interfaces.Services;
using Microsoft.EntityFrameworkCore;

namespace Cmsify.Infrastructure.Persistence.Repositories;

public sealed class TemplateRepository : ITemplateRepository
{
    private readonly CmsifyDbContext dbContext;
    private readonly ICurrentActor currentActor;

    public TemplateRepository(CmsifyDbContext dbContext, ICurrentActor currentActor)
    {
        this.dbContext = dbContext;
        this.currentActor = currentActor;
    }

    public async Task<TemplateDto?> GetAsync(Guid id, CancellationToken ct = default) =>
        (await dbContext.Templates.AsNoTracking().ScopeToActorWorkspace(currentActor).FirstOrDefaultAsync(template => template.Id == id, ct))?.ToDto();

    public Task<PagedResult<TemplateDto>> ListByWorkspaceAsync(Guid workspaceId, PageRequest page, CancellationToken ct = default) =>
        dbContext.Templates.AsNoTracking()
            .Where(template => template.WorkspaceId == workspaceId)
            .ScopeToActorWorkspace(currentActor)
            .OrderBy(template => template.Name)
            .ToPagedResultAsync(page, template => template.ToDto(), ct);

    public async Task<TemplateDto> CreateAsync(CreateTemplateCommand command, CancellationToken ct = default)
    {
        var entity = new Template
        {
            WorkspaceId = command.WorkspaceId,
            Name = command.Name,
            Slug = command.Slug,
            Description = command.Description,
            TitleFieldKey = command.TitleFieldKey
        };
        dbContext.Templates.Add(entity);
        await dbContext.SaveChangesAsync(ct);
        return entity.ToDto();
    }

    public async Task<TemplateDto> UpdateAsync(UpdateTemplateCommand command, CancellationToken ct = default)
    {
        var entity = await dbContext.Templates.ScopeToActorWorkspace(currentActor).FirstAsync(template => template.Id == command.Id, ct);
        entity.Name = command.Name;
        entity.Slug = command.Slug;
        entity.Description = command.Description;
        entity.TitleFieldKey = command.TitleFieldKey;
        entity.UpdatedAt = DateTimeOffset.UtcNow;
        await dbContext.SaveChangesAsync(ct);
        return entity.ToDto();
    }

    public async Task SoftDeleteAsync(Guid id, Guid actorUserId, CancellationToken ct = default)
    {
        var entity = await dbContext.Templates.ScopeToActorWorkspace(currentActor).FirstAsync(template => template.Id == id, ct);
        entity.SoftDelete(actorUserId);
        await dbContext.SaveChangesAsync(ct);
    }
}
