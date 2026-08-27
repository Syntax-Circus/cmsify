using System.Net;
using System.Text.Json;
using Cmsify.Core.Domain.Entities;
using Cmsify.Core.Domain.Enums;
using Cmsify.Core.Interfaces.Repositories;
using FluentValidation.Results;

namespace Cmsify.Core.Interfaces.Services;

public interface ITemplateGraphValidator
{
    ValidationResult ValidateCycles(TemplateVersion version);
}

public interface IContentValidator
{
    ValidationResult Validate(ContentItem item, TemplateVersion version);
}

public interface IFieldConfigValidator
{
    ValidationResult Validate(PrimitiveType type, JsonElement? config);
}

public interface IContentLifecycleService
{
    bool CanTransition(ContentStatus from, ContentStatus to);

    Task TransitionAsync(ContentItem item, ContentStatus to, Guid actorId);
}

public interface IContentSearchVectorBuilder
{
    string Build(ContentItem item, TemplateVersion version);
}

public sealed record ContentEffectiveRange(DateTimeOffset? StartAt, DateTimeOffset? EndAt)
{
    public bool IsDefault => !StartAt.HasValue && !EndAt.HasValue;
}

public sealed record ContentPublishResult(ContentVersion Version, IReadOnlyList<string> Warnings);

public interface IContentPublishingService
{
    Task<ContentPublishResult> PublishSnapshotAsync(
        ContentItem content,
        ContentEffectiveRange effectiveRange,
        int? rolledBackFromVersionNumber = null,
        Guid? actorUserId = null,
        CancellationToken ct = default);
}

public interface ICurrentActor
{
    Guid? UserId { get; }

    Guid? ApiClientId { get; }

    UserRole Role { get; }

    Guid? WorkspaceId { get; }

    bool IsAuthenticated { get; }

    bool IsSuperAdmin { get; }
}

public sealed record CurrentActorInfo(Guid? UserId, Guid? ApiClientId, UserRole Role, Guid? WorkspaceId, bool IsAuthenticated, bool IsSuperAdmin = false) : ICurrentActor
{
    public static CurrentActorInfo Anonymous { get; } = new(null, null, UserRole.Reader, null, false);
}

public interface IWorkspaceAuthorizationService
{
    Task<bool> CanReadWorkspaceAsync(Guid workspaceId, CancellationToken ct = default);

    Task<bool> CanWriteWorkspaceAsync(Guid workspaceId, CancellationToken ct = default);
}

public static class CurrentActorHttpContextKeys
{
    public const string ItemName = "Cmsify.CurrentActor";
}

/// <summary>
/// Adds a webhook event to the current persistence unit of work. Implementations must not save or deliver it.
/// </summary>
public interface IWebhookOutbox
{
    void Enqueue(string eventType, Guid? workspaceId, Guid entityId, JsonElement payload, DateTimeOffset occurredAt);
}

public interface IWebhookDestinationValidator
{
    Task<WebhookDestinationValidationResult> ValidateAsync(string url, CancellationToken ct = default);
}

public sealed record WebhookDestinationValidationResult(
    bool IsValid, Uri? DestinationUri, IReadOnlyList<IPAddress> Addresses, string? Error)
{
    public string? NormalizedUrl => DestinationUri?.AbsoluteUri;

    public static WebhookDestinationValidationResult Valid(Uri uri, IReadOnlyList<IPAddress> addresses) =>
        new(true, uri, addresses.ToArray(), null);

    public static WebhookDestinationValidationResult Invalid(string error) => new(false, null, [], error);
}

public interface IScheduledPublishingDispatcher
{
    Task<IReadOnlyList<ScheduledContentClaimDto>> ClaimDueAsync(string workerId, DateTimeOffset now, TimeSpan leaseDuration, int limit, CancellationToken ct = default);

    Task<bool> CompleteClaimAsync(ScheduledContentClaimDto claim, DateTimeOffset now, CancellationToken ct = default);
}

public interface ISecretProtector
{
    string Protect(string secret);

    string Unprotect(string protectedSecret);
}
