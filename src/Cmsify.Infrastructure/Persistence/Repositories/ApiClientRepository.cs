using Cmsify.Core.Domain.Entities;
using Cmsify.Core.Interfaces.Repositories;
using Microsoft.EntityFrameworkCore;

namespace Cmsify.Infrastructure.Persistence.Repositories;

public sealed class ApiClientRepository : IApiClientRepository
{
    private readonly CmsifyDbContext dbContext;

    public ApiClientRepository(CmsifyDbContext dbContext) => this.dbContext = dbContext;

    public async Task<ApiClientDto?> GetAsync(Guid id, CancellationToken ct = default) =>
        (await dbContext.ApiClients.AsNoTracking().FirstOrDefaultAsync(client => client.Id == id, ct))?.ToDto();

    public Task<PagedResult<ApiClientDto>> ListAsync(PageRequest page, CancellationToken ct = default) =>
        dbContext.ApiClients.AsNoTracking().OrderBy(client => client.Name).ToPagedResultAsync(page, client => client.ToDto(), ct);

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
        var entity = await dbContext.ApiClients.FirstAsync(client => client.Id == command.Id, ct);
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
        var entity = await dbContext.ApiClients.FirstAsync(client => client.Id == id, ct);
        entity.SoftDelete(actorUserId);
        await dbContext.SaveChangesAsync(ct);
    }
}
