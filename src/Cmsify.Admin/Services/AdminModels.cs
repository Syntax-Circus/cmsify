using System.Text.Json;
using Cmsify.Core.Domain.Enums;

namespace Cmsify.Admin.Services;

public sealed record PagedResponse<T>(IReadOnlyList<T> Items, int TotalCount, int Page, int PageSize);

public sealed record PagedResult<T>(IReadOnlyList<T> Items, int TotalCount, int Offset, int Limit);

public sealed class ProblemDetailsException : Exception
{
    public ProblemDetailsException(int statusCode, string title, string? detail, IReadOnlyDictionary<string, string[]>? errors)
        : base(detail ?? title)
    {
        StatusCode = statusCode;
        Title = title;
        Detail = detail;
        Errors = errors;
    }

    public int StatusCode { get; }
    public string Title { get; }
    public string? Detail { get; }
    public IReadOnlyDictionary<string, string[]>? Errors { get; }
}

public sealed record UserSummary(Guid Id, string Email, string DisplayName, string Role, bool IsSuperAdmin);

public sealed record LoginResponse(string Token, DateTimeOffset ExpiresAt, bool MustChangePassword, UserSummary User);

public sealed record WorkspaceDto(Guid Id, string Name, string Slug, string? Description, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt);

public sealed record WorkspaceRequest(string Name, string Slug, string? Description);

public sealed record TemplateSummaryResponse(Guid Id, Guid WorkspaceId, string Name, string Slug, string? Description, Guid? CurrentVersionId);

public sealed record TemplateResponse(Guid Id, Guid WorkspaceId, string Name, string Slug, string? Description, bool IsSystem, TemplateVersionResponse? CurrentVersion);

public sealed record TemplateVersionSummaryResponse(Guid Id, int VersionNumber, TemplateVersionStatus Status, DateTimeOffset? PublishedAt, string? Notes, int FieldCount);

public sealed record TemplateVersionResponse(Guid Id, Guid TemplateId, int VersionNumber, TemplateVersionStatus Status, DateTimeOffset? PublishedAt, string? Notes, IReadOnlyList<TemplateSectionResponse> Sections, IReadOnlyList<TemplateFieldResponse> Fields);

public sealed record TemplateSectionResponse(Guid Id, string Name, string? Description, int Order, bool IsCollapsible);

public sealed record TemplateFieldAllowedTypeResponse(Guid Id, PrimitiveType? PrimitiveType, Guid? AllowedTemplateId);

public sealed record TemplateFieldResponse(Guid Id, Guid? SectionId, string Key, string Label, string? HelpText, int Order, bool IsRequired, int MinOccurrences, int? MaxOccurrences, bool IsOpen, CompositionMode CompositionMode, PrimitiveType? PrimitiveType, Guid? TemplateId, IReadOnlyList<TemplateFieldAllowedTypeResponse> AllowedTypes, JsonElement? FieldConfig);

public sealed record CreateTemplateRequest(string Name, string Slug, string? Description);

public sealed record TemplateSectionRequest(string Name, string? Description, int Order, bool IsCollapsible);

public sealed record TemplateFieldAllowedTypeRequest(PrimitiveType? PrimitiveType, Guid? AllowedTemplateId);

public sealed record TemplateFieldRequest(Guid? SectionId, string Key, string Label, string? HelpText, int Order, bool IsRequired, int MinOccurrences, int? MaxOccurrences, bool IsOpen, CompositionMode CompositionMode, PrimitiveType? PrimitiveType, Guid? TemplateId, IReadOnlyList<TemplateFieldAllowedTypeRequest> AllowedTypes, JsonElement? FieldConfig);

public sealed record ReorderFieldRequest(Guid FieldId, int Order);

public sealed record ContentItemSummaryResponse(Guid Id, Guid TemplateVersionId, string TemplateName, ContentStatus Status, string? Slug, string? LocaleCode, Guid? TranslationGroupId, IReadOnlyList<string> Tags, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt, DateTimeOffset? PublishedAt);

public sealed record ContentItemDetailResponse(Guid Id, Guid TemplateVersionId, string TemplateName, ContentStatus Status, string? Slug, string? LocaleCode, Guid? TranslationGroupId, IReadOnlyList<string> Tags, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt, DateTimeOffset? PublishedAt, IReadOnlyList<ContentFieldValueResponse> Fields);

public sealed record ContentFieldValueRequest(Guid FieldId, int Order, ValueKind ValueKind, string? TextValue, bool? BoolValue, Guid? MediaAssetId, Guid? FileAssetId, Guid? ChildContentItemId, JsonElement? JsonValue);

public sealed record ContentFieldValueResponse(Guid FieldId, string? Key, string? Label, int Order, ValueKind ValueKind, string? TextValue, bool? BoolValue, Guid? MediaAssetId, Guid? FileAssetId, Guid? ChildContentItemId, ContentItemDetailResponse? Child, JsonElement? JsonValue);

public sealed record CreateContentItemRequest(Guid TemplateVersionId, string? Slug, string? LocaleCode, Guid? TranslationGroupId, IReadOnlyList<string> Tags, IReadOnlyList<ContentFieldValueRequest> Fields);

public sealed record UpdateContentItemRequest(string? Slug, string? LocaleCode, Guid? TranslationGroupId, DateTimeOffset? PublishAt, IReadOnlyList<string> Tags, IReadOnlyList<ContentFieldValueRequest> Fields);

public sealed record PublishContentRequest(DateTimeOffset? PublishAt);

public sealed record LinkTranslationRequest(Guid TargetContentItemId);

public sealed record MediaAssetResponse(Guid Id, string FileName, string MimeType, long SizeBytes, string? AltText, string Url, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt);

public sealed record UpdateMediaAssetRequest(string? AltText);

public sealed record UserWorkspaceAccessDto(Guid WorkspaceId, WorkspaceAccessLevel AccessLevel);

public sealed record UserDto(Guid Id, string Email, string DisplayName, UserRole Role, bool IsSuperAdmin, bool MustChangePassword, string? TimeZoneId, bool IsActive, DateTimeOffset CreatedAt, DateTimeOffset? LastLoginAt, IReadOnlyList<UserWorkspaceAccessDto> WorkspaceAccesses);

public sealed record CreateUserRequest(string Email, string DisplayName, UserRole Role, string TemporaryPassword, bool IsSuperAdmin, string? TimeZoneId, IReadOnlyList<UserWorkspaceAccessDto> WorkspaceAccesses);

public sealed record UpdateUserRequest(string Email, string DisplayName, UserRole Role, bool IsSuperAdmin, string? TimeZoneId, bool IsActive, IReadOnlyList<UserWorkspaceAccessDto> WorkspaceAccesses);

public sealed record ResetPasswordRequest(string TemporaryPassword);

public sealed record TempPasswordResponse(Guid UserId, string TemporaryPassword, string Warning);

public sealed record AccountPreferencesResponse(Guid UserId, string DisplayName, string Email, string? TimeZoneId, string Theme);

public sealed record UpdateAccountPreferencesRequest(string? TimeZoneId, string Theme);

public sealed record ApiClientDto(Guid Id, string Name, string? Description, UserRole Role, Guid? WorkspaceId, bool IsActive, DateTimeOffset? ExpiresAt, DateTimeOffset CreatedAt, DateTimeOffset? LastUsedAt);

public sealed record CreateApiClientRequest(string Name, string? Description, UserRole Role, Guid? WorkspaceId, DateTimeOffset? ExpiresAt);

public sealed record CreateApiClientResponse(ApiClientDto Client, string Token, string Warning);

public sealed record WebhookEndpointResponse(Guid Id, Guid WorkspaceId, string Name, string Url, bool IsActive, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt, IReadOnlyList<string> Events);

public sealed record CreateWebhookEndpointRequest(string Name, string Url, string? Secret, IReadOnlyList<string> Events);

public sealed record UpdateWebhookEndpointRequest(string Name, string Url, bool IsActive, IReadOnlyList<string> Events);

public sealed record CreateWebhookEndpointResponse(WebhookEndpointResponse Endpoint, string Secret);

public sealed record WebhookDeliveryResponse(Guid Id, Guid WebhookEndpointId, string EventType, JsonElement Payload, int AttemptCount, DateTimeOffset? LastAttemptAt, DateTimeOffset? NextRetryAt, int? StatusCode, bool IsDelivered, bool IsFailed, DateTimeOffset CreatedAt);

public sealed record AuditActorResponse(string Type, Guid Id, string? DisplayName);

public sealed record AuditLogResponse(Guid Id, string EntityType, Guid EntityId, AuditAction Action, AuditActorResponse? Actor, DateTimeOffset Timestamp, Guid? WorkspaceId, JsonElement? ChangeDelta);

public sealed record StorageConfigResponse(string Provider, bool IsConfigured);

public sealed record StorageTestResponse(string Provider, bool Success, string Message);

public sealed record OfficialPackageResponse(string PackageNamespace, string Id, string Version, string Name, string? Description, string? Author, string? License, string? Homepage, int TemplateCount, IReadOnlyList<OfficialPackageTemplateResponse> Templates);

public sealed record OfficialPackageTemplateResponse(string Slug, string Name, string? Description);

public sealed record PackageImportResponse(string PackageNamespace, string Id, string Version, IReadOnlyList<PackageTemplateImportResult> Imported, IReadOnlyList<string> Skipped, IReadOnlyList<string> Errors);

public sealed record PackageTemplateImportResult(Guid TemplateId, string Slug, string Name, Guid TemplateVersionId, int VersionNumber);
