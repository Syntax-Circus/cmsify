using Cmsify.Core.Domain.Entities;
using Cmsify.Core.Interfaces.Repositories;
using Microsoft.EntityFrameworkCore;

namespace Cmsify.Infrastructure.Persistence.Repositories;

public sealed class UserRepository : IUserRepository
{
    private readonly CmsifyDbContext dbContext;

    public UserRepository(CmsifyDbContext dbContext) => this.dbContext = dbContext;

    public async Task<UserDto?> GetAsync(Guid id, CancellationToken ct = default) =>
        (await dbContext.Users.AsNoTracking().FirstOrDefaultAsync(user => user.Id == id, ct))?.ToDto();

    public async Task<UserDto?> GetByEmailAsync(string email, CancellationToken ct = default) =>
        (await dbContext.Users.AsNoTracking().FirstOrDefaultAsync(user => user.Email == email, ct))?.ToDto();

    public Task<PagedResult<UserDto>> ListAsync(PageRequest page, CancellationToken ct = default) =>
        dbContext.Users.AsNoTracking().OrderBy(user => user.Email).ToPagedResultAsync(page, user => user.ToDto(), ct);

    public async Task<UserDto> CreateAsync(CreateUserCommand command, string passwordHash, CancellationToken ct = default)
    {
        var entity = new User
        {
            Email = command.Email,
            DisplayName = command.DisplayName,
            PasswordHash = passwordHash,
            Role = command.Role,
            TimeZoneId = command.TimeZoneId,
            MustChangePassword = true,
            IsActive = true
        };
        dbContext.Users.Add(entity);
        await dbContext.SaveChangesAsync(ct);
        return entity.ToDto();
    }

    public async Task<UserDto> UpdateAsync(UpdateUserCommand command, CancellationToken ct = default)
    {
        var entity = await dbContext.Users.FirstAsync(user => user.Id == command.Id, ct);
        entity.Email = command.Email;
        entity.DisplayName = command.DisplayName;
        entity.Role = command.Role;
        entity.TimeZoneId = command.TimeZoneId;
        entity.IsActive = command.IsActive;
        entity.UpdatedAt = DateTimeOffset.UtcNow;
        await dbContext.SaveChangesAsync(ct);
        return entity.ToDto();
    }

    public async Task SoftDeleteAsync(Guid id, Guid actorUserId, CancellationToken ct = default)
    {
        var entity = await dbContext.Users.FirstAsync(user => user.Id == id, ct);
        entity.SoftDelete(actorUserId);
        await dbContext.SaveChangesAsync(ct);
    }
}
