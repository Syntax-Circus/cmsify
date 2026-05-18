using Cmsify.Core.Domain.Enums;
using Cmsify.Core.Interfaces.Services;
using Microsoft.AspNetCore.Http;

namespace Cmsify.Infrastructure.Auth;

public sealed class HttpContextCurrentActor : ICurrentActor
{
    private readonly IHttpContextAccessor httpContextAccessor;

    public HttpContextCurrentActor(IHttpContextAccessor httpContextAccessor) => this.httpContextAccessor = httpContextAccessor;

    private ICurrentActor Current =>
        httpContextAccessor.HttpContext?.Items[CurrentActorHttpContextKeys.ItemName] as ICurrentActor
        ?? CurrentActorInfo.Anonymous;

    public Guid? UserId => Current.UserId;

    public Guid? ApiClientId => Current.ApiClientId;

    public UserRole Role => Current.Role;

    public Guid? WorkspaceId => Current.WorkspaceId;

    public bool IsAuthenticated => Current.IsAuthenticated;

    public bool IsSuperAdmin => Current.IsSuperAdmin;
}
