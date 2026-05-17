using Cmsify.Core.Domain.Entities;
using Cmsify.Core.Interfaces.Repositories;

namespace Cmsify.Infrastructure.Persistence.Repositories;

internal static class RepositoryMapping
{
    public static WorkspaceDto ToDto(this Workspace entity) =>
        new(entity.Id, entity.Name, entity.Slug, entity.Description, entity.CreatedAt, entity.UpdatedAt);

    public static TemplateDto ToDto(this Template entity) =>
        new(entity.Id, entity.WorkspaceId, entity.Name, entity.Slug, entity.Description, entity.PackageNamespace, entity.PackageId, entity.PackageVersion, entity.TitleFieldKey, entity.CurrentVersionId);

    public static TemplateVersionDto ToDto(this TemplateVersion entity) =>
        new(entity.Id, entity.TemplateId, entity.VersionNumber, entity.Status, entity.PublishedAt, entity.Notes);

    public static ContentItemDto ToDto(this ContentItem entity) =>
        new(entity.Id, entity.WorkspaceId, entity.TemplateVersionId, entity.Status, entity.Slug, entity.LocaleCode, entity.TranslationGroupId, entity.PublishAt, entity.PublishedAt, entity.ArchivedAt, entity.CreatedAt, entity.UpdatedAt);

    public static MediaAssetDto ToDto(this MediaAsset entity) =>
        new(entity.Id, entity.WorkspaceId, entity.FileName, entity.MimeType, entity.SizeBytes, entity.StorageKey, entity.StorageProvider, entity.AltText);

    public static TagDto ToDto(this Tag entity) =>
        new(entity.Id, entity.WorkspaceId, entity.Name);

    public static UserDto ToDto(this User entity) =>
        new(entity.Id, entity.Email, entity.DisplayName, entity.Role, entity.MustChangePassword, entity.TimeZoneId, entity.IsActive, entity.CreatedAt, entity.LastLoginAt);

    public static ApiClientDto ToDto(this ApiClient entity) =>
        new(entity.Id, entity.Name, entity.Description, entity.Role, entity.WorkspaceId, entity.IsActive, entity.ExpiresAt, entity.CreatedAt, entity.LastUsedAt);

    public static WebhookEndpointDto ToDto(this WebhookEndpoint entity) =>
        new(entity.Id, entity.WorkspaceId, entity.Name, entity.Url, entity.IsActive, entity.CreatedAt, entity.Subscriptions.Select(subscription => subscription.EventType).ToArray());

    public static WebhookDeliveryLogDto ToDto(this WebhookDeliveryLog entity) =>
        new(entity.Id, entity.WebhookEndpointId, entity.EventType, entity.Payload, entity.AttemptCount, entity.LastAttemptAt, entity.NextRetryAt, entity.StatusCode, entity.IsDelivered, entity.IsFailed, entity.CreatedAt);

    public static AuditLogDto ToDto(this AuditLog entity) =>
        new(entity.Id, entity.EntityType, entity.EntityId, entity.Action, entity.ActorUserId, entity.ActorApiClientId, entity.Timestamp, entity.ChangeDelta, entity.WorkspaceId);
}
