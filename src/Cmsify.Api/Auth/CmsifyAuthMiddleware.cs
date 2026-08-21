using System.Security.Claims;
using Cmsify.Core.Domain.Enums;
using Cmsify.Core.Interfaces.Services;
using Cmsify.Infrastructure.Persistence;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;

namespace Cmsify.Api.Auth;

public sealed class CmsifyAuthMiddleware
{
    private readonly RequestDelegate next;
    private readonly IConfiguration configuration;

    public CmsifyAuthMiddleware(RequestDelegate next, IConfiguration configuration)
    {
        this.next = next;
        this.configuration = configuration;
    }

    public async Task InvokeAsync(HttpContext context, CmsifyDbContext dbContext)
    {
        var actor = await ResolveActorAsync(context, dbContext);
        context.Items[CurrentActorHttpContextKeys.ItemName] = actor;
        await next(context);
    }

    private async Task<ICurrentActor> ResolveActorAsync(HttpContext context, CmsifyDbContext dbContext)
    {
        var authorization = context.Request.Headers.Authorization.ToString();
        if (!authorization.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            return CurrentActorInfo.Anonymous;
        }

        var rawToken = authorization["Bearer ".Length..].Trim();
        if (string.IsNullOrWhiteSpace(rawToken))
        {
            return CurrentActorInfo.Anonymous;
        }

        if (rawToken.StartsWith("cmsify_", StringComparison.Ordinal))
        {
            return await ResolveApiClientAsync(rawToken, dbContext);
        }

        var sessionActor = await ResolveUserSessionAsync(rawToken, context, dbContext);
        if (sessionActor.IsAuthenticated)
        {
            return sessionActor;
        }

        return configuration.GetValue("Auth:Oidc:Enabled", false)
            ? await ResolveJwtActorAsync(context)
            : CurrentActorInfo.Anonymous;
    }

    private async Task<ICurrentActor> ResolveUserSessionAsync(string rawToken, HttpContext context, CmsifyDbContext dbContext)
    {
        var tokenHash = TokenUtility.Sha256Hash(rawToken);
        var now = DateTimeOffset.UtcNow;
        var session = await dbContext.UserSessions
            .FirstOrDefaultAsync(candidate => candidate.TokenHash == tokenHash && candidate.ExpiresAt > now);

        if (session is null)
        {
            return CurrentActorInfo.Anonymous;
        }

        var user = await dbContext.Users
            .AsNoTracking()
            .FirstOrDefaultAsync(candidate => candidate.Id == session.UserId && candidate.IsActive);

        if (user is null)
        {
            return CurrentActorInfo.Anonymous;
        }

        var touchInterval = TimeSpan.FromSeconds(Math.Clamp(configuration.GetValue("Auth:SessionTouchIntervalSeconds", 300), 1, 3600));
        if (!session.LastSeenAt.HasValue || session.LastSeenAt.Value <= now - touchInterval)
        {
            session.LastSeenAt = now;
            session.ExpiresAt = LocalSessionLifetime.CalculateExpiresAt(configuration, now);
            session.IpAddress = context.Connection.RemoteIpAddress?.ToString();
            await dbContext.SaveChangesAsync(context.RequestAborted);
        }
        context.Response.Headers[LocalSessionLifetime.ExpiresAtHeaderName] = session.ExpiresAt.ToString("O");

        return new CurrentActorInfo(user.Id, null, user.Role, null, true, user.IsSuperAdmin);
    }

    private async Task<ICurrentActor> ResolveApiClientAsync(string rawToken, CmsifyDbContext dbContext)
    {
        var now = DateTimeOffset.UtcNow;
        var tokenIdentifier = TryGetApiTokenIdentifier(rawToken);
        var query = dbContext.ApiClients.Where(client => client.IsActive && !client.IsDeleted && (!client.ExpiresAt.HasValue || client.ExpiresAt > now));
        var clients = tokenIdentifier is not null
            ? await query.Where(client => client.TokenIdentifier == tokenIdentifier).ToListAsync()
            : await query.Where(client => client.TokenIdentifier == null).ToListAsync();

        foreach (var client in clients)
        {
            if (!BCrypt.Net.BCrypt.Verify(rawToken, client.TokenHash))
            {
                continue;
            }

            var touchInterval = TimeSpan.FromSeconds(Math.Clamp(configuration.GetValue("Auth:ApiClientTouchIntervalSeconds", 300), 1, 3600));
            if (!client.LastUsedAt.HasValue || client.LastUsedAt.Value <= now - touchInterval)
            {
                client.LastUsedAt = now;
                await dbContext.SaveChangesAsync();
            }
            return new CurrentActorInfo(null, client.Id, client.Role, client.WorkspaceId, true);
        }

        return CurrentActorInfo.Anonymous;
    }

    private static string? TryGetApiTokenIdentifier(string rawToken)
    {
        var parts = rawToken.Split('_', StringSplitOptions.None);
        return parts.Length == 3 && parts[0] == "cmsify" && parts[1].Length is > 0 and <= 64 && parts[2].Length > 0
            ? parts[1]
            : null;
    }

    private async Task<ICurrentActor> ResolveJwtActorAsync(HttpContext context)
    {
        var result = await context.AuthenticateAsync(JwtBearerDefaults.AuthenticationScheme);
        if (!result.Succeeded || result.Principal is null)
        {
            return CurrentActorInfo.Anonymous;
        }

        var roleClaimName = configuration["Auth:Oidc:ClaimsMapping:Role"] ?? ClaimTypes.Role;
        var workspaceClaimName = configuration["Auth:Oidc:ClaimsMapping:WorkspaceId"] ?? "workspace_id";
        var roleValue = result.Principal.FindFirstValue(roleClaimName);
        var role = Enum.TryParse<UserRole>(roleValue, ignoreCase: true, out var parsedRole) ? parsedRole : UserRole.Reader;
        var workspaceValue = result.Principal.FindFirstValue(workspaceClaimName);
        var workspaceId = Guid.TryParse(workspaceValue, out var parsedWorkspaceId) ? parsedWorkspaceId : (Guid?)null;

        return new CurrentActorInfo(null, null, role, workspaceId, true);
    }
}
