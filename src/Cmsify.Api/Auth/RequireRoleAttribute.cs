using Cmsify.Core.Domain.Enums;
using Cmsify.Core.Interfaces.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace Cmsify.Api.Auth;

[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method)]
public sealed class RequireRoleAttribute : Attribute, IAsyncAuthorizationFilter
{
    private readonly UserRole requiredRole;

    public RequireRoleAttribute(UserRole requiredRole) => this.requiredRole = requiredRole;

    public Task OnAuthorizationAsync(AuthorizationFilterContext context)
    {
        var actor = context.HttpContext.RequestServices.GetRequiredService<ICurrentActor>();
        if (!actor.IsAuthenticated)
        {
            context.Result = new UnauthorizedResult();
            return Task.CompletedTask;
        }

        if (actor.Role < requiredRole)
        {
            context.Result = new ForbidResult();
        }

        return Task.CompletedTask;
    }
}
