namespace Cmsify.Infrastructure.Extensions;

[Flags]
public enum CmsifyWorkers
{
    None = 0,
    ScheduledPublishing = 1 << 0,
    MediaReconciliation = 1 << 1,
    WebhookDispatch = 1 << 2,
    WebhookRetry = 1 << 3,
    WebhookSecretRotation = 1 << 4,
    WebhookSecretRotationInventoryPreflight = 1 << 5,
    All = ScheduledPublishing | MediaReconciliation | WebhookDispatch | WebhookRetry
        | WebhookSecretRotation | WebhookSecretRotationInventoryPreflight
}

/// <summary>Registration-time host integration choices; PostgreSQL remains the persistence provider.</summary>
public sealed record CmsifyInfrastructureOptions
{
    public bool UseHostCurrentActorForAudit { get; init; }

    public CmsifyWorkers Workers { get; init; } = CmsifyWorkers.All;
}
