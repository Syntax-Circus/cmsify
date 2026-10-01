using Cmsify.Core.Interfaces.Services;

namespace Cmsify.Infrastructure.Auth;

public sealed class HostCurrentActorAuditAccessor(ICurrentActor currentActor) : IAuditActorAccessor
{
    public AuditActor GetActor() => currentActor.IsAuthenticated
        ? new(currentActor.UserId, currentActor.ApiClientId)
        : new(null, null);
}
