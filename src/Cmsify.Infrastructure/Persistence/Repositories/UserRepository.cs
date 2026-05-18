using Cmsify.Core.Domain.Entities;
using Cmsify.Core.Domain.Enums;
using Cmsify.Core.Interfaces.Repositories;
using Microsoft.EntityFrameworkCore;

namespace Cmsify.Infrastructure.Persistence.Repositories;

public sealed class UserRepository : IUserRepository
{
    private readonly CmsifyDbContext dbContext;

    public UserRepository(CmsifyDbContext dbContext) => this.dbContext = dbContext;

    public async Task<UserDto?> GetAsync(Guid id, CancellationToken ct = default) =>
        (await dbContext.Users.AsNoTracking().Include(user => user.WorkspaceAccesses).FirstOrDefaultAsync(user => user.Id == id, ct))?.ToDto();

    public async Task<UserDto?> GetByEmailAsync(string email, CancellationToken ct = default) =>
        (await dbContext.Users.AsNoTracking().Include(user => user.WorkspaceAccesses).FirstOrDefaultAsync(user => user.Email == email, ct))?.ToDto();

    public Task<PagedResult<UserDto>> ListAsync(PageRequest page, CancellationToken ct = default) =>
        dbContext.Users.AsNoTracking().Include(user => user.WorkspaceAccesses).OrderBy(user => user.Email).ToPagedResultAsync(page, user => user.ToDto(), ct);

    public async Task<UserDto> CreateAsync(CreateUserCommand command, string passwordHash, CancellationToken ct = default)
    {
        var entity = new User
        {
            Email = command.Email,
            DisplayName = command.DisplayName,
            PasswordHash = passwordHash,
            Role = command.Role,
            IsSuperAdmin = command.IsSuperAdmin,
            TimeZoneId = command.TimeZoneId,
            MustChangePassword = true,
            IsActive = true
        };
        ApplyWorkspaceAccesses(entity, command.WorkspaceAccesses);
        dbContext.Users.Add(entity);
        await dbContext.SaveChangesAsync(ct);
        return entity.ToDto();
    }

    public async Task<UserDto> UpdateAsync(UpdateUserCommand command, CancellationToken ct = default)
    {
        var entity = await dbContext.Users.Include(user => user.WorkspaceAccesses).FirstAsync(user => user.Id == command.Id, ct);
        entity.Email = command.Email;
        entity.DisplayName = command.DisplayName;
        entity.Role = command.Role;
        entity.IsSuperAdmin = command.IsSuperAdmin;
        entity.TimeZoneId = command.TimeZoneId;
        entity.IsActive = command.IsActive;
        entity.UpdatedAt = DateTimeOffset.UtcNow;
        dbContext.UserWorkspaceAccesses.RemoveRange(entity.WorkspaceAccesses);
        entity.WorkspaceAccesses.Clear();
        ApplyWorkspaceAccesses(entity, command.WorkspaceAccesses);
        await dbContext.SaveChangesAsync(ct);
        return entity.ToDto();
    }

    public async Task SoftDeleteAsync(Guid id, Guid actorUserId, CancellationToken ct = default)
    {
        var entity = await dbContext.Users.FirstAsync(user => user.Id == id, ct);
        entity.SoftDelete(actorUserId);
        await dbContext.SaveChangesAsync(ct);
    }

    private static void ApplyWorkspaceAccesses(User entity, IReadOnlyList<UserWorkspaceAccessDto> workspaceAccesses)
    {
        foreach (var access in workspaceAccesses
            .GroupBy(access => access.WorkspaceId)
            .Select(group => group.Any(access => access.AccessLevel == WorkspaceAccessLevel.Write)
                ? new UserWorkspaceAccessDto(group.Key, WorkspaceAccessLevel.Write)
                : new UserWorkspaceAccessDto(group.Key, WorkspaceAccessLevel.Read)))
        {
            entity.WorkspaceAccesses.Add(new UserWorkspaceAccess
            {
                UserId = entity.Id,
                WorkspaceId = access.WorkspaceId,
                AccessLevel = access.AccessLevel
            });
        }
    }
}
