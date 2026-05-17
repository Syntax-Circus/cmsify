using System.Text.Json;
using Cmsify.Core.Domain.Entities;
using Cmsify.Core.Domain.Enums;
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

public interface ICurrentActor
{
    Guid? UserId { get; }

    Guid? ApiClientId { get; }

    UserRole Role { get; }

    Guid? WorkspaceId { get; }

    bool IsAuthenticated { get; }
}

public sealed record CurrentActorInfo(Guid? UserId, Guid? ApiClientId, UserRole Role, Guid? WorkspaceId, bool IsAuthenticated) : ICurrentActor
{
    public static CurrentActorInfo Anonymous { get; } = new(null, null, UserRole.Reader, null, false);
}

public static class CurrentActorHttpContextKeys
{
    public const string ItemName = "Cmsify.CurrentActor";
}

public interface IWebhookQueue
{
    ValueTask EnqueueAsync(WebhookEvent evt, CancellationToken ct = default);

    IAsyncEnumerable<WebhookEvent> DequeueAllAsync(CancellationToken ct = default);
}

public interface IScheduledPublishingDispatcher
{
    Task RunOnceAsync(CancellationToken ct = default);
}

public interface IStorageProvider
{
    Task<StoredFile> StoreAsync(Stream content, string fileName, string mimeType, CancellationToken ct = default);

    Task<Stream> RetrieveAsync(string storageKey, CancellationToken ct = default);

    Task DeleteAsync(string storageKey, CancellationToken ct = default);

    Task<bool> ExistsAsync(string storageKey, CancellationToken ct = default);
}

public sealed record StoredFile(string StorageKey, string Provider, long SizeBytes);
