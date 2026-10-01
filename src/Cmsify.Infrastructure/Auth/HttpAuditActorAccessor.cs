using System.Security.Claims;
using Cmsify.Core.Interfaces.Services;
using Microsoft.AspNetCore.Http;

namespace Cmsify.Infrastructure.Auth;

/// <summary>Preserves standalone HTTP audit item and claims precedence.</summary>
public sealed class HttpAuditActorAccessor(IHttpContextAccessor? httpContextAccessor = null) : IAuditActorAccessor
{
    public AuditActor GetActor()
    {
        var context = httpContextAccessor?.HttpContext;
        var user = context?.User;
        if (context?.Items.TryGetValue(CurrentActorHttpContextKeys.ItemName, out var actorItem) == true
            && actorItem is ICurrentActor currentActor && currentActor.IsAuthenticated)
            return new(currentActor.UserId, currentActor.ApiClientId);

        if (user?.Identity?.IsAuthenticated != true)
            return new(null, null);

        if (Guid.TryParse(user.FindFirst("cmsify_api_client_id")?.Value, out var apiClientId))
            return new(null, apiClientId);

        var userClaim = user.FindFirst(ClaimTypes.NameIdentifier)?.Value
            ?? user.FindFirst("sub")?.Value ?? user.FindFirst("cmsify_user_id")?.Value;
        return Guid.TryParse(userClaim, out var userId) ? new(userId, null) : new(null, null);
    }
}
