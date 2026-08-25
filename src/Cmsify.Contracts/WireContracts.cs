using System.Text.Json;
using System.ComponentModel.DataAnnotations;
namespace SyntaxCircus.Cmsify.Contracts;

public sealed record PagedResponse<T>(IReadOnlyList<T> Items, int TotalCount, int Page, int PageSize)
{
    public int TotalPages => (int)Math.Ceiling(TotalCount / (double)Math.Max(1, PageSize));
}

public sealed record PaginationQuery(
    int Page = 1,
    int PageSize = 20) : IValidatableObject
{
    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext) => PaginationValidation.Validate(Page, PageSize);
}

internal static class PaginationValidation
{
    public static IEnumerable<ValidationResult> Validate(int page, int pageSize)
    {
        if (page < 1)
        {
            yield return new ValidationResult("Page must be at least 1.", ["Page"]);
        }

        if (pageSize is < 1 or > 100)
        {
            yield return new ValidationResult("PageSize must be between 1 and 100.", ["PageSize"]);
        }
    }
}

public sealed record FileDownloadResponse(string FileName, string ContentType, byte[] Content);

public sealed record UserSummary(Guid Id, string Email, string DisplayName, string Role, bool IsSuperAdmin);

public sealed record LoginResponse(string Token, DateTimeOffset ExpiresAt, bool MustChangePassword, UserSummary User);

public sealed record WorkspaceDto(Guid Id, string Name, string Slug, string? Description, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt, bool CanWrite = false);

public sealed record WorkspaceRequest(string Name, string Slug, string? Description);

public sealed record TemplateSummaryResponse(Guid Id, Guid WorkspaceId, string Name, string Slug, string? Description, Guid? CurrentVersionId);

public sealed record TemplateResponse(Guid Id, Guid WorkspaceId, string Name, string Slug, string? Description, bool IsSystem, TemplateVersionResponse? CurrentVersion);

public sealed record TemplateVersionSummaryResponse(Guid Id, int VersionNumber, TemplateVersionStatus Status, DateTimeOffset? PublishedAt, string? Notes, int FieldCount);

public sealed record TemplateVersionResponse(Guid Id, Guid TemplateId, int VersionNumber, TemplateVersionStatus Status, DateTimeOffset? PublishedAt, string? Notes, IReadOnlyList<TemplateSectionResponse> Sections, IReadOnlyList<TemplateFieldResponse> Fields);

public sealed record TemplateSectionResponse(Guid Id, string Name, string? Description, int Order, bool IsCollapsible);

public sealed record TemplateFieldAllowedTypeResponse(Guid Id, PrimitiveType? PrimitiveType, Guid? AllowedTemplateId);

public sealed record TemplateFieldResponse(Guid Id, Guid? SectionId, string Key, string Label, string? HelpText, int Order, bool IsRequired, int MinOccurrences, int? MaxOccurrences, bool IsOpen, CompositionMode CompositionMode, PrimitiveType? PrimitiveType, Guid? TemplateId, IReadOnlyList<TemplateFieldAllowedTypeResponse> AllowedTypes, JsonElement? FieldConfig, Guid? ComponentId = null);

public sealed record PickListSummaryResponse(Guid Id, string Name, string Slug, string? Description, int OptionCount, Guid? CurrentRevisionId = null, int CurrentVersionNumber = 0);

public sealed record PickListResponse(Guid Id, string Name, string Slug, string? Description, IReadOnlyList<PickListOptionResponse> Options, Guid? CurrentRevisionId = null, int CurrentVersionNumber = 0);

public sealed record PickListOptionResponse(Guid Id, string Label, string Value, int Order);

public sealed record PickListOptionRequest(string Label, string Value, int? Order);

public sealed record PickListRequest(string Name, string Slug, string? Description, IReadOnlyList<PickListOptionRequest> Options);

public sealed record CreateTemplateRequest(string Name, string Slug, string? Description);

public sealed record TemplateSectionRequest(string Name, string? Description, int Order, bool IsCollapsible);

public sealed record TemplateFieldAllowedTypeRequest(PrimitiveType? PrimitiveType, Guid? AllowedTemplateId);

public sealed record TemplateFieldRequest(Guid? SectionId, string Key, string Label, string? HelpText, int Order, bool IsRequired, int MinOccurrences, int? MaxOccurrences, bool IsOpen, CompositionMode CompositionMode, PrimitiveType? PrimitiveType, Guid? TemplateId, IReadOnlyList<TemplateFieldAllowedTypeRequest> AllowedTypes, JsonElement? FieldConfig, Guid? ComponentId = null);

public sealed record ComponentSummaryResponse(Guid Id, string Name, string Slug, string? Description, Guid? CurrentVersionId);
public sealed record ComponentResponse(Guid Id, Guid WorkspaceId, string Name, string Slug, string? Description, ComponentVersionResponse? CurrentVersion);
public sealed record ComponentVersionResponse(Guid Id, Guid ComponentId, int VersionNumber, TemplateVersionStatus Status, DateTimeOffset? PublishedAt, string? Notes, IReadOnlyList<ComponentFieldResponse> Fields);
public sealed record ComponentFieldResponse(Guid Id, string Key, string Label, string? HelpText, int Order, bool IsRequired, int MinOccurrences, int? MaxOccurrences, PrimitiveType? PrimitiveType, Guid? NestedComponentId, JsonElement? FieldConfig);
public sealed record ComponentRequest(string Name, string Slug, string? Description);
public sealed record ComponentVersionRequest(string? Notes);
public sealed record ComponentFieldRequest(string Key, string Label, string? HelpText, int Order, bool IsRequired, int MinOccurrences, int? MaxOccurrences, PrimitiveType? PrimitiveType, Guid? NestedComponentId, JsonElement? FieldConfig);

public sealed record ReorderFieldRequest(Guid FieldId, int Order);

public sealed record ContentItemSummaryResponse(Guid Id, Guid TemplateVersionId, string TemplateName, ContentStatus Status, string? Slug, string? LocaleCode, Guid? TranslationGroupId, IReadOnlyList<string> Tags, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt, DateTimeOffset? PublishedAt);

public sealed record ContentItemDetailResponse(Guid Id, Guid TemplateVersionId, string TemplateName, ContentStatus Status, string? Slug, string? LocaleCode, Guid? TranslationGroupId, IReadOnlyList<string> Tags, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt, DateTimeOffset? PublishedAt, IReadOnlyList<ContentFieldValueResponse> Fields);

public sealed record ContentFieldValueRequest(Guid FieldId, int Order, ValueKind ValueKind, string? TextValue, bool? BoolValue, Guid? MediaAssetId, Guid? FileAssetId, Guid? ChildContentItemId, JsonElement? JsonValue);

public sealed record ContentFieldValueResponse(Guid FieldId, string? Key, string? Label, int Order, ValueKind ValueKind, string? TextValue, bool? BoolValue, Guid? MediaAssetId, Guid? FileAssetId, Guid? ChildContentItemId, ContentItemDetailResponse? Child, JsonElement? JsonValue, string? DisplayLabel = null);

public sealed record CreateContentItemRequest(Guid TemplateVersionId, string? Slug, string? LocaleCode, Guid? TranslationGroupId, IReadOnlyList<string> Tags, IReadOnlyList<ContentFieldValueRequest> Fields);

public sealed record UpdateContentItemRequest(string? Slug, string? LocaleCode, Guid? TranslationGroupId, DateTimeOffset? PublishAt, IReadOnlyList<string> Tags, IReadOnlyList<ContentFieldValueRequest> Fields);

public sealed record PublishContentRequest(DateTimeOffset? PublishAt, DateTimeOffset? EffectiveStartAt, DateTimeOffset? EffectiveEndAt);
public sealed record PublishContentResponse(ContentItemDetailResponse Content, IReadOnlyList<string> Warnings);

public sealed record LinkTranslationRequest(Guid TargetContentItemId);

public sealed record ContentVersionSummaryResponse(Guid Id, Guid ContentItemId, int VersionNumber, ContentVersionStatus Status, Guid TemplateVersionId, string? Slug, string? LocaleCode, DateTimeOffset? EffectiveStartAt, DateTimeOffset? EffectiveEndAt, DateTimeOffset PublishedAt, DateTimeOffset? RetiredAt, Guid? PublishedByUserId, int? RolledBackFromVersionNumber, IReadOnlyList<string> Tags);

public sealed record ContentVersionFieldValueResponse(Guid FieldId, string? Key, string? Label, int Order, ValueKind ValueKind, string? TextValue, bool? BoolValue, Guid? MediaAssetId, Guid? FileAssetId, Guid? ChildContentItemId, JsonElement? JsonValue, string? DisplayLabel = null);

public sealed record ContentVersionDetailResponse(Guid Id, Guid ContentItemId, int VersionNumber, ContentVersionStatus Status, Guid TemplateVersionId, string TemplateName, string? Slug, string? LocaleCode, Guid? TranslationGroupId, DateTimeOffset? EffectiveStartAt, DateTimeOffset? EffectiveEndAt, DateTimeOffset PublishedAt, DateTimeOffset? RetiredAt, Guid? PublishedByUserId, int? RolledBackFromVersionNumber, IReadOnlyList<string> Tags, IReadOnlyList<ContentVersionFieldValueResponse> Fields);

public sealed record MediaAssetResponse(Guid Id, string FileName, string MimeType, long SizeBytes, string? AltText, string Url, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt);

public sealed record UpdateMediaAssetRequest(string? AltText);

public sealed record UserWorkspaceAccessDto(Guid WorkspaceId, WorkspaceAccessLevel AccessLevel);

public sealed record UserDto(Guid Id, string Email, string DisplayName, UserRole Role, bool IsSuperAdmin, bool MustChangePassword, string? TimeZoneId, bool IsActive, DateTimeOffset CreatedAt, DateTimeOffset? LastLoginAt, IReadOnlyList<UserWorkspaceAccessDto> WorkspaceAccesses);

public sealed record CreateUserRequest(string Email, string DisplayName, UserRole Role, string TemporaryPassword, bool IsSuperAdmin, string? TimeZoneId, IReadOnlyList<UserWorkspaceAccessRequest>? WorkspaceAccesses);

public sealed record UpdateUserRequest(string Email, string DisplayName, UserRole Role, bool IsSuperAdmin, string? TimeZoneId, bool IsActive, IReadOnlyList<UserWorkspaceAccessRequest>? WorkspaceAccesses);

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

public sealed record OfficialPackageResponse(string PackageNamespace, string Id, string Version, string Name, string? Description, string? Author, string? License, string? Homepage, int TemplateCount, IReadOnlyList<OfficialPackageTemplateResponse> Templates, int PickListCount = 0, int ComponentCount = 0);

public sealed record OfficialPackageTemplateResponse(string Slug, string Name, string? Description);

public sealed record PackageImportResponse(
    string PackageNamespace,
    string Id,
    string Version,
    IReadOnlyList<PackageTemplateImportResult> Imported,
    IReadOnlyList<string> Skipped,
    IReadOnlyList<string> Errors,
    IReadOnlyList<PackagePickListImportResult>? PickLists = null,
    IReadOnlyList<PackageComponentImportResult>? Components = null);

public sealed record PackageTemplateImportResult(Guid TemplateId, string Slug, string Name, Guid TemplateVersionId, int VersionNumber);

public sealed record PackagePickListImportResult(string Slug, string ResolvedSlug, Guid PickListId, string Action);

public sealed record PackageComponentImportResult(string Slug, string ResolvedSlug, Guid ComponentId, string Action, Guid? ComponentVersionId, int? VersionNumber);

public sealed record PackageImportPreviewResponse(
    string PackageNamespace,
    string Id,
    string Version,
    IReadOnlyList<PackagePickListPreview> PickLists,
    IReadOnlyList<PackageTemplatePreview> Templates,
    IReadOnlyList<PackageComponentPreview>? Components = null);

public sealed record PackagePickListPreview(
    string Slug,
    string Name,
    string? Description,
    IReadOnlyList<PackagePickListOptionPreview> Options,
    string Status,
    Guid? ExistingId,
    string? ExistingName,
    string? ExistingDescription,
    IReadOnlyList<PackagePickListOptionPreview>? ExistingOptions,
    string SuggestedAction);

public sealed record PackagePickListOptionPreview(string Label, string Value, int Order);

public sealed record PackageTemplatePreview(string Slug, string Name, string Status);

public sealed record PackageComponentPreview(string Slug, string Name, string? Description, int FieldCount, string Status, Guid? ExistingId, string? ExistingName, int? ExistingFieldCount, string SuggestedAction);

public sealed record PackageImportResolutionsRequest(IReadOnlyDictionary<string, string>? PickLists, IReadOnlyDictionary<string, string>? Components = null);


public sealed record LoginRequest(string Email, string Password);
public sealed record ActorResponse(Guid? UserId, Guid? ApiClientId, string Role, Guid? WorkspaceId, bool IsSuperAdmin);
public sealed record ChangePasswordRequest(string CurrentPassword, string NewPassword);
public sealed record ContentListQuery(string? Q, Guid? TemplateVersionId, Guid? TemplateId, ContentStatus? Status, string? LocaleCode, Guid? TranslationGroupId, string? Slug, string? Tags, DateTimeOffset? CreatedAfter, DateTimeOffset? CreatedBefore, DateTimeOffset? PublishedAfter, DateTimeOffset? PublishedBefore, bool Resolve = false, DateTimeOffset? AsOf = null, string? SortBy = "createdAt", bool SortDesc = true, int Page = 1, int PageSize = 20) : IValidatableObject
{
    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext) => PaginationValidation.Validate(Page, PageSize);
}
public sealed record RejectContentRequest(string Reason);
public sealed record UpdateTemplateRequest(string Name, string? Description);
public sealed record CreateTemplateVersionRequest(string? Notes);
public sealed record RotateWebhookSecretResponse(Guid Id, string Secret, string Warning);
public sealed record TagResponse(Guid Id, string Name, int UsageCount);
public sealed record AuditQueryRequest(string? EntityType, Guid? EntityId, AuditAction? Action, Guid? ActorUserId, Guid? ActorApiClientId, DateTimeOffset? After, DateTimeOffset? Before, int Page = 1, int PageSize = 50) : IValidatableObject
{
    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext) => PaginationValidation.Validate(Page, PageSize);
}
public sealed record UserWorkspaceAccessRequest(Guid WorkspaceId, WorkspaceAccessLevel AccessLevel);
