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
