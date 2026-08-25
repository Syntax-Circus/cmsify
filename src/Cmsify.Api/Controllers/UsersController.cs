using Cmsify.Api.Auth;
using Cmsify.Core.Domain.Enums;
using Cmsify.Core.Interfaces.Repositories;
using Cmsify.Core.Interfaces.Services;
using Cmsify.Infrastructure.Persistence;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using PaginationQuery = SyntaxCircus.Cmsify.Contracts.PaginationQuery;

namespace Cmsify.Api.Controllers;

[ApiController]
[Route("api/v1/users")]
[RequireRole(UserRole.Admin)]
public sealed class UsersController : ControllerBase
{
    private readonly IUserRepository userRepository;
    private readonly ICurrentActor currentActor;
    private readonly IConfiguration configuration;
    private readonly CmsifyDbContext dbContext;

    public UsersController(IUserRepository userRepository, ICurrentActor currentActor, IConfiguration configuration, CmsifyDbContext dbContext)
    {
        this.userRepository = userRepository;
        this.currentActor = currentActor;
        this.configuration = configuration;
        this.dbContext = dbContext;
    }

    [HttpGet]
    public async Task<ActionResult<SyntaxCircus.Cmsify.Contracts.PagedResponse<UserDto>>> List([FromQuery] PaginationQuery pagination, CancellationToken ct = default)
    {
        if (!currentActor.IsSuperAdmin)
        {
            return StatusCode(StatusCodes.Status403Forbidden);
        }

        var result = await userRepository.ListAsync(new PageRequest(ControllerHelpers.Offset(pagination.Page, pagination.PageSize), pagination.PageSize), ct);
        return Ok(new SyntaxCircus.Cmsify.Contracts.PagedResponse<UserDto>(result.Items, result.TotalCount, pagination.Page, pagination.PageSize));
    }

    [HttpPost]
    public async Task<ActionResult<TempPasswordResponse>> Create(CreateUserRequest request, CancellationToken ct)
    {
        if (!currentActor.IsSuperAdmin)
        {
            return StatusCode(StatusCodes.Status403Forbidden);
        }

        var workspaceAccesses = NormalizeWorkspaceAccesses(request.WorkspaceAccesses);
        var validation = await ValidateWorkspaceAccessesAsync(workspaceAccesses, ct);
        if (validation is not null)
        {
            return validation;
        }

        var hash = BCrypt.Net.BCrypt.HashPassword(request.TemporaryPassword, configuration.GetValue("Auth:BcryptCost", 12));
        var user = await userRepository.CreateAsync(new CreateUserCommand(request.Email, request.DisplayName, request.TemporaryPassword, request.Role, request.IsSuperAdmin, request.TimeZoneId, workspaceAccesses), hash, ct);
        return CreatedAtAction(nameof(Get), new { id = user.Id }, new TempPasswordResponse(user.Id, request.TemporaryPassword, "Copy this temporary password now. It will not be shown again."));
    }

    [HttpGet("{id:guid}")]
    public async Task<ActionResult<UserDto>> Get(Guid id, CancellationToken ct)
    {
        if (!currentActor.IsSuperAdmin)
        {
            return StatusCode(StatusCodes.Status403Forbidden);
        }

        var user = await userRepository.GetAsync(id, ct);
        return user is null ? NotFound() : Ok(user);
    }

    [HttpPut("{id:guid}")]
    public async Task<ActionResult<UserDto>> Update(Guid id, UpdateUserRequest request, CancellationToken ct)
    {
        if (!currentActor.IsSuperAdmin)
        {
            return StatusCode(StatusCodes.Status403Forbidden);
        }

        if (currentActor.UserId == id && !request.IsActive)
        {
            return this.Error(StatusCodes.Status409Conflict, "conflict", "Admins cannot deactivate themselves.");
        }

        var existing = await userRepository.GetAsync(id, ct);
        if (existing is null)
        {
            return NotFound();
        }

        if (existing.IsSuperAdmin && (!request.IsSuperAdmin || !request.IsActive || request.Role != UserRole.Admin) && await IsLastSuperAdminAsync(id, ct))
        {
            return this.Error(StatusCodes.Status409Conflict, "conflict", "The last superadmin cannot be disabled, demoted, or converted to a regular user.");
        }

        var workspaceAccesses = NormalizeWorkspaceAccesses(request.WorkspaceAccesses);
        var validation = await ValidateWorkspaceAccessesAsync(workspaceAccesses, ct);
        if (validation is not null)
        {
            return validation;
        }

        return Ok(await userRepository.UpdateAsync(new UpdateUserCommand(id, request.Email, request.DisplayName, request.Role, request.IsSuperAdmin, request.TimeZoneId, request.IsActive, workspaceAccesses), ct));
    }

    [HttpPost("{id:guid}/reset-password")]
    public async Task<ActionResult<TempPasswordResponse>> ResetPassword(Guid id, ResetPasswordRequest request, CancellationToken ct)
    {
        if (!currentActor.IsSuperAdmin)
        {
            return StatusCode(StatusCodes.Status403Forbidden);
        }

        var existing = await userRepository.GetAsync(id, ct);
        if (existing is null)
        {
            return NotFound();
        }

        var user = await dbContext.Users.FirstAsync(candidate => candidate.Id == id, ct);
        user.PasswordHash = BCrypt.Net.BCrypt.HashPassword(request.TemporaryPassword, configuration.GetValue("Auth:BcryptCost", 12));
        user.MustChangePassword = true;
        user.UpdatedAt = DateTimeOffset.UtcNow;
        await dbContext.SaveChangesAsync(ct);
        return Ok(new TempPasswordResponse(id, request.TemporaryPassword, "Copy this temporary password now. It will not be shown again."));
    }

    private async Task<bool> IsLastSuperAdminAsync(Guid userId, CancellationToken ct) =>
        !await dbContext.Users.AsNoTracking().AnyAsync(user => user.Id != userId && user.IsSuperAdmin && user.IsActive, ct);

    private async Task<ActionResult?> ValidateWorkspaceAccessesAsync(IReadOnlyList<UserWorkspaceAccessDto> workspaceAccesses, CancellationToken ct)
    {
        if (workspaceAccesses.Count == 0)
        {
            return null;
        }

        var workspaceIds = workspaceAccesses.Select(access => access.WorkspaceId).Distinct().ToArray();
        var existingCount = await dbContext.Workspaces.AsNoTracking().CountAsync(workspace => workspaceIds.Contains(workspace.Id), ct);
        return existingCount == workspaceIds.Length
            ? null
            : this.Error(StatusCodes.Status422UnprocessableEntity, "validation-failed", "One or more workspace grants reference a workspace that does not exist.");
    }

    private static IReadOnlyList<UserWorkspaceAccessDto> NormalizeWorkspaceAccesses(IReadOnlyList<UserWorkspaceAccessRequest>? workspaceAccesses) =>
        (workspaceAccesses ?? [])
        .Where(access => access.WorkspaceId != Guid.Empty)
        .GroupBy(access => access.WorkspaceId)
        .Select(group => new UserWorkspaceAccessDto(
            group.Key,
            group.Any(access => access.AccessLevel == WorkspaceAccessLevel.Write) ? WorkspaceAccessLevel.Write : WorkspaceAccessLevel.Read))
        .ToArray();
}

public sealed record UserWorkspaceAccessRequest(Guid WorkspaceId, WorkspaceAccessLevel AccessLevel);
public sealed record CreateUserRequest(string Email, string DisplayName, UserRole Role, string TemporaryPassword, bool IsSuperAdmin, string? TimeZoneId, IReadOnlyList<UserWorkspaceAccessRequest>? WorkspaceAccesses);
public sealed record UpdateUserRequest(string Email, string DisplayName, UserRole Role, bool IsSuperAdmin, string? TimeZoneId, bool IsActive, IReadOnlyList<UserWorkspaceAccessRequest>? WorkspaceAccesses);
public sealed record ResetPasswordRequest(string TemporaryPassword);
public sealed record TempPasswordResponse(Guid UserId, string TemporaryPassword, string Warning);
