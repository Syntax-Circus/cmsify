using Cmsify.Api.Auth;
using Cmsify.Core.Domain.Enums;
using Cmsify.Core.Interfaces.Repositories;
using Cmsify.Core.Interfaces.Services;
using Cmsify.Infrastructure.Persistence;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

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
    public async Task<ActionResult<PagedResponse<UserDto>>> List([FromQuery] int page = 1, [FromQuery] int pageSize = 50, CancellationToken ct = default)
    {
        var limit = Math.Clamp(pageSize, 1, 200);
        var result = await userRepository.ListAsync(new PageRequest((Math.Max(1, page) - 1) * limit, limit), ct);
        return Ok(new PagedResponse<UserDto>(result.Items, result.TotalCount, Math.Max(1, page), limit));
    }

    [HttpPost]
    public async Task<ActionResult<TempPasswordResponse>> Create(CreateUserRequest request, CancellationToken ct)
    {
        var hash = BCrypt.Net.BCrypt.HashPassword(request.TemporaryPassword, configuration.GetValue("Auth:BcryptCost", 12));
        var user = await userRepository.CreateAsync(new CreateUserCommand(request.Email, request.DisplayName, request.TemporaryPassword, request.Role, request.TimeZoneId), hash, ct);
        return CreatedAtAction(nameof(Get), new { id = user.Id }, new TempPasswordResponse(user.Id, request.TemporaryPassword, "Copy this temporary password now. It will not be shown again."));
    }

    [HttpGet("{id:guid}")]
    public async Task<ActionResult<UserDto>> Get(Guid id, CancellationToken ct)
    {
        var user = await userRepository.GetAsync(id, ct);
        return user is null ? NotFound() : Ok(user);
    }

    [HttpPut("{id:guid}")]
    public async Task<ActionResult<UserDto>> Update(Guid id, UpdateUserRequest request, CancellationToken ct)
    {
        if (currentActor.UserId == id && !request.IsActive)
        {
            return this.Error(StatusCodes.Status409Conflict, "conflict", "Admins cannot deactivate themselves.");
        }

        var existing = await userRepository.GetAsync(id, ct);
        if (existing is null)
        {
            return NotFound();
        }

        return Ok(await userRepository.UpdateAsync(new UpdateUserCommand(id, request.Email, request.DisplayName, request.Role, request.TimeZoneId, request.IsActive), ct));
    }

    [HttpPost("{id:guid}/reset-password")]
    public async Task<ActionResult<TempPasswordResponse>> ResetPassword(Guid id, ResetPasswordRequest request, CancellationToken ct)
    {
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
}

public sealed record CreateUserRequest(string Email, string DisplayName, UserRole Role, string TemporaryPassword, string? TimeZoneId);
public sealed record UpdateUserRequest(string Email, string DisplayName, UserRole Role, string? TimeZoneId, bool IsActive);
public sealed record ResetPasswordRequest(string TemporaryPassword);
public sealed record TempPasswordResponse(Guid UserId, string TemporaryPassword, string Warning);
