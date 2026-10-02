namespace Cmsify.Core.Interfaces.Services;

/// <summary>Audit attribution only; this identity does not confer permissions.</summary>
public readonly record struct AuditActor(Guid? UserId, Guid? ApiClientId);

public interface IAuditActorAccessor
{
    AuditActor GetActor();
}
