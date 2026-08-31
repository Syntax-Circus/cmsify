using System.Security.Claims;
using Cmsify.Core.Domain.Enums;
using Cmsify.Core.Interfaces.Services;
using Microsoft.AspNetCore.Authentication;

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

    public async Task InvokeAsync(HttpContext context)
    {
        var result = await context.AuthenticateAsync();
        context.Items[CurrentActorHttpContextKeys.ItemName] = MapActor(result.Succeeded ? result.Principal : null);
        await next(context);
    }

    private ICurrentActor MapActor(ClaimsPrincipal? principal)
    {
        if (principal is null)
        {
            return CurrentActorInfo.Anonymous;
        }

        var isOpaque = principal.Identity?.AuthenticationType == CmsifyOpaqueBearerAuthenticationHandler.SchemeName;
        var roleClaimName = isOpaque ? CmsifyOpaqueBearerClaims.Role : configuration["Auth:Oidc:ClaimsMapping:Role"] ?? ClaimTypes.Role;
        var roleValue = principal.FindFirstValue(roleClaimName);
        var role = Enum.TryParse<UserRole>(roleValue, ignoreCase: true, out var parsedRole) ? parsedRole : UserRole.Reader;
        var workspaceClaimName = isOpaque ? CmsifyOpaqueBearerClaims.WorkspaceId : configuration["Auth:Oidc:ClaimsMapping:WorkspaceId"] ?? "workspace_id";
        var workspaceValue = principal.FindFirstValue(workspaceClaimName);
        var workspaceId = Guid.TryParse(workspaceValue, out var parsedWorkspaceId) ? parsedWorkspaceId : (Guid?)null;
        var userId = Guid.TryParse(principal.FindFirstValue(ClaimTypes.NameIdentifier), out var parsedUserId) ? parsedUserId : (Guid?)null;
        var apiClientId = Guid.TryParse(principal.FindFirstValue(CmsifyOpaqueBearerClaims.ApiClientId), out var parsedApiClientId) ? parsedApiClientId : (Guid?)null;
        var isSuperAdmin = isOpaque && string.Equals(principal.FindFirstValue(CmsifyOpaqueBearerClaims.SuperAdmin), "true", StringComparison.OrdinalIgnoreCase);
        return new CurrentActorInfo(userId, apiClientId, role, workspaceId, true, isSuperAdmin);
    }
}
