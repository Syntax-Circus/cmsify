using Cmsify.Core.Domain.Entities;
using Cmsify.Core.Interfaces.Repositories;
using Cmsify.Core.Interfaces.Services;
using Microsoft.EntityFrameworkCore;

namespace Cmsify.Infrastructure.Persistence.Repositories;

public sealed class ApiClientRepository : IApiClientRepository
{
    private readonly CmsifyDbContext dbContext;
    private readonly ICurrentActor currentActor;

    public ApiClientRepository(CmsifyDbContext dbContext, ICurrentActor currentActor)
    {
        this.dbContext = dbContext;
        this.currentActor = currentActor;
    }

    public async Task<ApiClientDto?> GetAsync(Guid id, CancellationToken ct = default) =>
        (await Scope(dbContext.ApiClients.AsNoTracking()).FirstOrDefaultAsync(client => client.Id == id, ct))?.ToDto();

    public Task<PagedResult<ApiClientDto>> ListAsync(PageRequest page, CancellationToken ct = default) =>
        Scope(dbContext.ApiClients.AsNoTracking()).OrderBy(client => client.Name).ToPagedResultAsync(page, client => client.ToDto(), ct);

    public async Task<ApiClientDto> CreateAsync(CreateApiClientCommand command, string tokenHash, CancellationToken ct = default)
    {
        var entity = new ApiClient
        {
            Name = command.Name,
            Description = command.Description,
            TokenHash = tokenHash,
            Role = command.Role,
            WorkspaceId = command.WorkspaceId,
            ExpiresAt = command.ExpiresAt,
            CreatedByUserId = command.CreatedByUserId,
            IsActive = true
        };
        dbContext.ApiClients.Add(entity);
        await dbContext.SaveChangesAsync(ct);
        return entity.ToDto();
    }

    public async Task<ApiClientDto> UpdateAsync(UpdateApiClientCommand command, CancellationToken ct = default)
    {
        var entity = await Scope(dbContext.ApiClients).FirstAsync(client => client.Id == command.Id, ct);
        entity.Name = command.Name;
        entity.Description = command.Description;
        entity.Role = command.Role;
        entity.WorkspaceId = command.WorkspaceId;
        entity.IsActive = command.IsActive;
        entity.ExpiresAt = command.ExpiresAt;
        entity.UpdatedAt = DateTimeOffset.UtcNow;
        await dbContext.SaveChangesAsync(ct);
        return entity.ToDto();
    }

    public async Task SoftDeleteAsync(Guid id, Guid actorUserId, CancellationToken ct = default)
    {
        var entity = await Scope(dbContext.ApiClients).FirstAsync(client => client.Id == id, ct);
        entity.SoftDelete(actorUserId);
        await dbContext.SaveChangesAsync(ct);
    }

    private IQueryable<ApiClient> Scope(IQueryable<ApiClient> query)
    {
        if (currentActor.IsSuperAdmin)
        {
            return query;
        }

        if (currentActor.WorkspaceId.HasValue)
        {
            return query.Where(client => client.WorkspaceId == currentActor.WorkspaceId.Value);
        }

        if (currentActor.UserId.HasValue)
        {
            var userId = currentActor.UserId.Value;
            return query.Where(client =>
                client.WorkspaceId.HasValue
                && dbContext.UserWorkspaceAccesses.Any(access => access.UserId == userId && access.WorkspaceId == client.WorkspaceId.Value));
        }

        return query.Where(_ => false);
    }
}
