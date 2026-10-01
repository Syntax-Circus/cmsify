using Cmsify.Api.Auth;
using Cmsify.Core.Domain.Entities;
using Cmsify.Core.Interfaces.Services;
using Cmsify.Core.Validation;
using Cmsify.Infrastructure.Persistence;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SyntaxCircus.Cmsify.Contracts;
using Microsoft.EntityFrameworkCore;

namespace Cmsify.Api.Controllers;

[ApiController]
[Route("api/v1/auth")]
public sealed class AuthController : ControllerBase
{
    private readonly CmsifyDbContext dbContext;
    private readonly IConfiguration configuration;
    private readonly ICurrentActor currentActor;
    private readonly ILogger<AuthController> logger;

    private const int MaxLoggedEmailLength = 100;

    // Verified against when no usable account exists so failed logins cost roughly the same as real ones.
    private static readonly Lazy<string> DummyPasswordHash = new(() => BCrypt.Net.BCrypt.HashPassword(Guid.NewGuid().ToString("N"), 12));

    public AuthController(CmsifyDbContext dbContext, IConfiguration configuration, ICurrentActor currentActor, ILogger<AuthController> logger)
    {
        this.logger = logger;
        this.dbContext = dbContext;
        this.configuration = configuration;
        this.currentActor = currentActor;
    }

    [HttpPost("login")]
    [AllowAnonymous]
    public async Task<ActionResult<LoginResponse>> Login(LoginRequest request, CancellationToken ct)
    {
        var remoteIp = HttpContext.Connection.RemoteIpAddress?.ToString();
        var email = request.Email ?? string.Empty;

        // Email is only unique among non-deleted users, so prefer the live row; fall back to a deleted one purely to
        // classify the failure in the log. The response is identical for every reason.
        var candidate = await dbContext.Users.FirstOrDefaultAsync(user => user.Email == email, ct)
            ?? await dbContext.Users.IgnoreQueryFilters().FirstOrDefaultAsync(user => user.Email == email && user.IsDeleted, ct);
        string? failureReason = null;
        if (candidate is null)
        {
            failureReason = "UserNotFound";
        }
        else if (candidate.IsDeleted)
        {
            failureReason = "UserDeleted";
        }
        else if (!candidate.IsActive)
        {
            failureReason = "UserInactive";
        }

        var passwordValid = BCrypt.Net.BCrypt.Verify(request.Password ?? string.Empty, failureReason is null ? candidate!.PasswordHash : DummyPasswordHash.Value);
        if (failureReason is null && !passwordValid)
        {
            failureReason = "BadPassword";
        }

        if (failureReason is not null)
        {
            logger.LogWarning(
                "Login failed. Reason={Reason} Email={Email} UserId={UserId} RemoteIp={RemoteIp}",
                failureReason,
                SanitizeForLog(email),
                candidate?.Id,
                remoteIp);
            return Unauthorized();
        }

        var user = candidate!;
        logger.LogInformation("Login succeeded. UserId={UserId} RemoteIp={RemoteIp}", user.Id, remoteIp);

        var now = DateTimeOffset.UtcNow;
        var rawToken = TokenUtility.GenerateSessionToken();
        var expiresAt = LocalSessionLifetime.CalculateExpiresAt(configuration, now);
        dbContext.UserSessions.Add(new UserSession
        {
            UserId = user.Id,
            TokenHash = TokenUtility.Sha256Hash(rawToken),
            ExpiresAt = expiresAt,
            IpAddress = HttpContext.Connection.RemoteIpAddress?.ToString()
        });
        user.LastLoginAt = now;
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
            return this.Error(StatusCodes.Status400BadRequest, CmsifyError.BadRequest, "Only user sessions can be refreshed.");
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

        var now = DateTimeOffset.UtcNow;
        var newToken = TokenUtility.GenerateSessionToken();
        var expiresAt = LocalSessionLifetime.CalculateExpiresAt(configuration, now);
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
            return this.Error(StatusCodes.Status400BadRequest, CmsifyError.BadRequest, "Only local users can change passwords.");
        }

        if (!PasswordRules.IsValid(request.NewPassword))
        {
            return this.InvalidPasswordError();
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

    private static string SanitizeForLog(string value)
    {
        var trimmed = value.Trim();
        if (trimmed.Length > MaxLoggedEmailLength)
        {
            trimmed = trimmed[..MaxLoggedEmailLength];
        }

        return string.Concat(trimmed.Select(character => char.IsControl(character) ? '?' : character));
    }

    private string? GetBearerToken()
    {
        var authorization = Request.Headers.Authorization.ToString();
        return authorization.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)
            ? authorization["Bearer ".Length..].Trim()
            : null;
    }
}
