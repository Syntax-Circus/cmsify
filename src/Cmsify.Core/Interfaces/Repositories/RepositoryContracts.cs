using System.Text.Json;
using Cmsify.Core.Domain.Enums;

namespace Cmsify.Core.Interfaces.Repositories;

public sealed record PageRequest(int Offset = 0, int Limit = 50);

public sealed record PagedResult<T>(IReadOnlyList<T> Items, int TotalCount, int Offset, int Limit);

public sealed record WorkspaceDto(Guid Id, string Name, string Slug, string? Description, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt, bool CanWrite = false);

public sealed record CreateWorkspaceCommand(string Name, string Slug, string? Description);

public sealed record UpdateWorkspaceCommand(Guid Id, string Name, string Slug, string? Description);

public sealed record TemplateDto(
    Guid Id,
    Guid WorkspaceId,
    string Name,
    string Slug,
    string? Description,
    string? PackageNamespace,
    string? PackageId,
    string? PackageVersion,
    string? TitleFieldKey,
    Guid? CurrentVersionId);

public sealed record CreateTemplateCommand(Guid WorkspaceId, string Name, string Slug, string? Description, string? TitleFieldKey);

public sealed record UpdateTemplateCommand(Guid Id, string Name, string Slug, string? Description, string? TitleFieldKey);

public sealed record TemplateVersionDto(Guid Id, Guid TemplateId, int VersionNumber, TemplateVersionStatus Status, DateTimeOffset? PublishedAt, string? Notes);

public sealed record TemplateSectionDto(Guid Id, Guid TemplateVersionId, string Name, string? Description, int Order, bool IsCollapsible);

public sealed record TemplateFieldDto(
    Guid Id,
    Guid TemplateVersionId,
    Guid? SectionId,
    string Key,
    string Label,
    string? HelpText,
    int Order,
    bool IsRequired,
    int MinOccurrences,
    int? MaxOccurrences,
    bool IsOpen,
    CompositionMode CompositionMode,
    PrimitiveType? PrimitiveType,
    Guid? TemplateId,
    JsonElement? FieldConfig);

public sealed record CreateTemplateVersionCommand(Guid TemplateId, string? Notes);

public sealed record SaveTemplateVersionStructureCommand(
    Guid TemplateVersionId,
    IReadOnlyList<TemplateSectionInput> Sections,
    IReadOnlyList<TemplateFieldInput> Fields);

public sealed record TemplateSectionInput(string Name, string? Description, int Order, bool IsCollapsible);

public sealed record TemplateFieldInput(
    Guid? SectionId,
    string Key,
    string Label,
    string? HelpText,
    int Order,
    bool IsRequired,
    int MinOccurrences,
    int? MaxOccurrences,
    bool IsOpen,
    CompositionMode CompositionMode,
    PrimitiveType? PrimitiveType,
    Guid? TemplateId,
    JsonElement? FieldConfig,
    IReadOnlyList<TemplateFieldAllowedTypeInput> AllowedTypes,
    Guid? ComponentId = null);

public sealed record TemplateFieldAllowedTypeInput(PrimitiveType? PrimitiveType, Guid? AllowedTemplateId);

public sealed record ContentItemDto(
    Guid Id,
    Guid WorkspaceId,
    Guid TemplateVersionId,
    ContentStatus Status,
    string? Slug,
    string? LocaleCode,
    Guid? TranslationGroupId,
    DateTimeOffset? PublishAt,
    DateTimeOffset? PublishedAt,
    DateTimeOffset? ArchivedAt,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

public sealed record ContentFieldValueDto(
    Guid Id,
    Guid ContentItemId,
    Guid FieldId,
    int Order,
    ValueKind ValueKind,
    string? TextValue,
    bool? BoolValue,
    Guid? MediaAssetId,
    Guid? FileAssetId,
    Guid? ChildContentItemId,
    JsonElement? JsonValue);

public sealed record CreateContentItemCommand(
    Guid WorkspaceId,
    Guid TemplateVersionId,
    string? Slug,
    string? LocaleCode,
    Guid? TranslationGroupId,
    DateTimeOffset? PublishAt,
    IReadOnlyList<ContentFieldValueInput> FieldValues,
    IReadOnlyList<Guid> TagIds);

public sealed record UpdateContentItemCommand(
    Guid Id,
    string? Slug,
    string? LocaleCode,
    Guid? TranslationGroupId,
    DateTimeOffset? PublishAt,
    IReadOnlyList<ContentFieldValueInput> FieldValues,
    IReadOnlyList<Guid> TagIds);

public sealed record ContentFieldValueInput(
    Guid FieldId,
    int Order,
    ValueKind ValueKind,
    string? TextValue,
    bool? BoolValue,
    Guid? MediaAssetId,
    Guid? FileAssetId,
    Guid? ChildContentItemId,
    JsonElement? JsonValue);

public sealed record ContentQuery(
    Guid? WorkspaceId,
    Guid? TemplateId,
    ContentStatus? Status,
    string? LocaleCode,
    string? Slug,
    IReadOnlyList<string> Tags,
    DateTimeOffset? CreatedFrom,
    DateTimeOffset? CreatedTo,
    DateTimeOffset? PublishedFrom,
    DateTimeOffset? PublishedTo,
    string? Search,
    string? SortBy,
    bool SortDescending,
    PageRequest Page);

public sealed record MediaAssetDto(Guid Id, Guid WorkspaceId, string FileName, string MimeType, long SizeBytes, string StorageKey, string StorageProvider, string? AltText);

public sealed record CreateMediaAssetCommand(Guid WorkspaceId, string FileName, string MimeType, long SizeBytes, string StorageKey, string StorageProvider, string? AltText);

public sealed record UpdateMediaAssetCommand(Guid Id, string FileName, string AltText);

public sealed record TagDto(Guid Id, Guid WorkspaceId, string Name);

public sealed record UpsertTagCommand(Guid WorkspaceId, string Name);

public sealed record UserWorkspaceAccessDto(Guid WorkspaceId, WorkspaceAccessLevel AccessLevel);

public sealed record UserDto(Guid Id, string Email, string DisplayName, UserRole Role, bool IsSuperAdmin, bool MustChangePassword, string? TimeZoneId, bool IsActive, DateTimeOffset CreatedAt, DateTimeOffset? LastLoginAt, IReadOnlyList<UserWorkspaceAccessDto> WorkspaceAccesses);

public sealed record CreateUserCommand(string Email, string DisplayName, string TemporaryPassword, UserRole Role, bool IsSuperAdmin, string? TimeZoneId, IReadOnlyList<UserWorkspaceAccessDto> WorkspaceAccesses);

public sealed record UpdateUserCommand(Guid Id, string Email, string DisplayName, UserRole Role, bool IsSuperAdmin, string? TimeZoneId, bool IsActive, IReadOnlyList<UserWorkspaceAccessDto> WorkspaceAccesses);

public sealed record ApiClientDto(Guid Id, string Name, string? Description, UserRole Role, Guid? WorkspaceId, bool IsActive, DateTimeOffset? ExpiresAt, DateTimeOffset CreatedAt, DateTimeOffset? LastUsedAt);

public sealed record CreateApiClientCommand(string Name, string? Description, UserRole Role, Guid? WorkspaceId, DateTimeOffset? ExpiresAt, Guid CreatedByUserId);

public sealed record UpdateApiClientCommand(Guid Id, string Name, string? Description, UserRole Role, Guid? WorkspaceId, bool IsActive, DateTimeOffset? ExpiresAt);

public sealed record WebhookEndpointDto(Guid Id, Guid WorkspaceId, string Name, string Url, bool IsActive, DateTimeOffset CreatedAt, IReadOnlyList<string> EventTypes);

public sealed record CreateWebhookEndpointCommand(Guid WorkspaceId, string Name, string Url, IReadOnlyList<string> EventTypes, Guid CreatedByUserId);

public sealed record UpdateWebhookEndpointCommand(Guid Id, string Name, string Url, bool IsActive, IReadOnlyList<string> EventTypes);

public sealed record WebhookDeliveryLogDto(
    Guid Id,
    Guid WebhookEndpointId,
    string EventType,
    JsonElement Payload,
    int AttemptCount,
    DateTimeOffset? LastAttemptAt,
    DateTimeOffset? NextRetryAt,
    int? StatusCode,
    bool IsDelivered,
    bool IsFailed,
    DateTimeOffset CreatedAt);

public sealed record WebhookDispatchTargetDto(Guid Id, Guid WorkspaceId, string Url, string Secret);

public sealed record PendingWebhookDeliveryDto(
    Guid Id,
    Guid WebhookEndpointId,
    Guid WebhookEventId,
    Guid WorkspaceId,
    string EventType,
    string Url,
    string Secret,
    JsonElement Payload,
    int AttemptCount,
    DateTimeOffset? NextRetryAt,
    string LeaseOwner,
    Guid LeaseToken,
    bool WasReclaimed = false);

public sealed record WebhookDeliveryCompletionDto(Guid Id, string LeaseOwner, Guid LeaseToken, DateTimeOffset AttemptedAt);

public sealed record ClaimedWebhookOutboxEventDto(
    Guid Id,
    string EventType,
    Guid? WorkspaceId,
    Guid EntityId,
    JsonElement Payload,
    DateTimeOffset OccurredAt,
    string LeaseOwner,
    Guid LeaseToken,
    bool WasReclaimed = false);

public sealed record ScheduledContentClaimDto(Guid ContentItemId, string LeaseOwner, Guid LeaseToken, bool WasReclaimed = false);

public sealed record WebhookRetentionCleanupResult(int ProcessedOutboxEventsDeleted, int DeliveredLogsDeleted);

public sealed record AuditLogDto(
    Guid Id,
    string EntityType,
    Guid EntityId,
    AuditAction Action,
    Guid? ActorUserId,
    Guid? ActorApiClientId,
    DateTimeOffset Timestamp,
    JsonElement? ChangeDelta,
    Guid? WorkspaceId);

public sealed record AuditLogQuery(Guid? WorkspaceId, string? EntityType, Guid? EntityId, Guid? ActorUserId, Guid? ActorApiClientId, PageRequest Page);
