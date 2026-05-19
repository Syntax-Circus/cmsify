using Cmsify.Api.Auth;
using Cmsify.Core.Domain.Entities;
using Cmsify.Core.Interfaces.Services;
using Cmsify.Infrastructure.Persistence;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Cmsify.Api.Controllers;

[ApiController]
[Route("api/v1/auth")]
public sealed class AuthController : ControllerBase
{
    private readonly CmsifyDbContext dbContext;
    private readonly IConfiguration configuration;
    private readonly ICurrentActor currentActor;

    public AuthController(CmsifyDbContext dbContext, IConfiguration configuration, ICurrentActor currentActor)
    {
        this.dbContext = dbContext;
        this.configuration = configuration;
        this.currentActor = currentActor;
    }

    [HttpPost("login")]
    public async Task<ActionResult<LoginResponse>> Login(LoginRequest request, CancellationToken ct)
    {
        var user = await dbContext.Users.FirstOrDefaultAsync(candidate => candidate.Email == request.Email && candidate.IsActive, ct);
        if (user is null || !BCrypt.Net.BCrypt.Verify(request.Password, user.PasswordHash))
        {
            return Unauthorized();
        }

        var rawToken = TokenUtility.GenerateSessionToken();
        var expiresAt = DateTimeOffset.UtcNow.AddHours(configuration.GetValue("Auth:SessionAbsoluteExpiryHours", 8));
        dbContext.UserSessions.Add(new UserSession
        {
            UserId = user.Id,
            TokenHash = TokenUtility.Sha256Hash(rawToken),
            ExpiresAt = expiresAt,
            IpAddress = HttpContext.Connection.RemoteIpAddress?.ToString()
        });
        user.LastLoginAt = DateTimeOffset.UtcNow;
        await dbContext.SaveChangesAsync(ct);

        return Ok(new LoginResponse(rawToken, expiresAt, user.MustChangePassword, new UserSummary(user.Id, user.Email, user.DisplayName, user.Role.ToString(), user.IsSuperAdmin)));
    }

    [HttpPost("logout")]
    [RequireRole(Core.Domain.Enums.UserRole.Reader)]
    public async Task<IActionResult> Logout(CancellationToken ct)
    {
        var rawToken = GetBearerToken();
        if (rawToken is not null)
        {
            var tokenHash = TokenUtility.Sha256Hash(rawToken);
            var session = await dbContext.UserSessions.FirstOrDefaultAsync(candidate => candidate.TokenHash == tokenHash, ct);
            if (session is not null)
            {
                dbContext.UserSessions.Remove(session);
                await dbContext.SaveChangesAsync(ct);
            }
        }

        return NoContent();
    }

    [HttpGet("me")]
    [RequireRole(Core.Domain.Enums.UserRole.Reader)]
    public IActionResult Me() => Ok(new ActorResponse(currentActor.UserId, currentActor.ApiClientId, currentActor.Role.ToString(), currentActor.WorkspaceId, currentActor.IsSuperAdmin));

    [HttpPost("refresh")]
    [RequireRole(Core.Domain.Enums.UserRole.Reader)]
    public async Task<ActionResult<LoginResponse>> Refresh(CancellationToken ct)
    {
        if (!currentActor.UserId.HasValue)
        {
            return BadRequest("Only user sessions can be refreshed.");
        }

        var rawToken = GetBearerToken();
        if (rawToken is null)
        {
            return Unauthorized();
        }

        var oldHash = TokenUtility.Sha256Hash(rawToken);
        var oldSession = await dbContext.UserSessions.FirstOrDefaultAsync(candidate => candidate.TokenHash == oldHash, ct);
        var user = await dbContext.Users.FirstAsync(candidate => candidate.Id == currentActor.UserId.Value, ct);
        if (oldSession is not null)
        {
            dbContext.UserSessions.Remove(oldSession);
        }

        var newToken = TokenUtility.GenerateSessionToken();
        var expiresAt = DateTimeOffset.UtcNow.AddHours(configuration.GetValue("Auth:SessionAbsoluteExpiryHours", 8));
        dbContext.UserSessions.Add(new UserSession
        {
            UserId = user.Id,
            TokenHash = TokenUtility.Sha256Hash(newToken),
            ExpiresAt = expiresAt,
            IpAddress = HttpContext.Connection.RemoteIpAddress?.ToString()
        });
        await dbContext.SaveChangesAsync(ct);

        return Ok(new LoginResponse(newToken, expiresAt, user.MustChangePassword, new UserSummary(user.Id, user.Email, user.DisplayName, user.Role.ToString(), user.IsSuperAdmin)));
    }

    [HttpPost("change-password")]
    [RequireRole(Core.Domain.Enums.UserRole.Reader)]
    public async Task<IActionResult> ChangePassword(ChangePasswordRequest request, CancellationToken ct)
    {
        if (!currentActor.UserId.HasValue)
        {
            return BadRequest("Only local users can change passwords.");
        }

        var user = await dbContext.Users.FirstAsync(candidate => candidate.Id == currentActor.UserId.Value, ct);
        if (!BCrypt.Net.BCrypt.Verify(request.CurrentPassword, user.PasswordHash))
        {
            return Unauthorized();
        }

        user.PasswordHash = BCrypt.Net.BCrypt.HashPassword(request.NewPassword, configuration.GetValue("Auth:BcryptCost", 12));
        user.MustChangePassword = false;
        user.UpdatedAt = DateTimeOffset.UtcNow;
        await dbContext.SaveChangesAsync(ct);
        return NoContent();
    }

    private string? GetBearerToken()
    {
        var authorization = Request.Headers.Authorization.ToString();
        return authorization.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)
            ? authorization["Bearer ".Length..].Trim()
            : null;
    }
}

public sealed record LoginRequest(string Email, string Password);

public sealed record LoginResponse(string Token, DateTimeOffset ExpiresAt, bool MustChangePassword, UserSummary User);

public sealed record UserSummary(Guid Id, string Email, string DisplayName, string Role, bool IsSuperAdmin);

public sealed record ActorResponse(Guid? UserId, Guid? ApiClientId, string Role, Guid? WorkspaceId, bool IsSuperAdmin);

public sealed record ChangePasswordRequest(string CurrentPassword, string NewPassword);
