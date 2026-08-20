using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using SyntaxCircus.Cmsify.Contracts;

namespace SyntaxCircus.Cmsify;

public sealed class AuthClient(CmsifyClient client)
{
    public Task<LoginResponse?> LoginAsync(LoginRequest request, CancellationToken ct = default) => client.PostAsync<LoginResponse>("/api/v1/auth/login", request, ct);
    public Task<LoginResponse?> RefreshAsync(CancellationToken ct = default) => client.PostAsync<LoginResponse>("/api/v1/auth/refresh", null, ct);
    public Task<ActorResponse?> MeAsync(CancellationToken ct = default) => client.GetAsync<ActorResponse>("/api/v1/auth/me", ct);
    public Task LogoutAsync(CancellationToken ct = default) => client.PostAsync<object>("/api/v1/auth/logout", null, ct);
    public Task ChangePasswordAsync(ChangePasswordRequest request, CancellationToken ct = default) => client.PostAsync<object>("/api/v1/auth/change-password", request, ct);
}

public sealed class HealthClient(CmsifyClient client)
{
    public Task LiveAsync(CancellationToken ct = default) => client.GetAsync<object>("/health/live", ct);
    public Task ReadyAsync(CancellationToken ct = default) => client.GetAsync<object>("/health/ready", ct);
}

public sealed class WorkspaceClient(CmsifyClient client)
{
    public Task<PagedResult<WorkspaceDto>?> ListAsync(int offset = 0, int limit = 50, CancellationToken ct = default) => client.GetAsync<PagedResult<WorkspaceDto>>($"/api/v1/workspaces?offset={offset}&limit={limit}", ct);
    public Task<WorkspaceDto?> GetAsync(Guid id, CancellationToken ct = default) => client.GetAsync<WorkspaceDto>($"/api/v1/workspaces/{id}", ct);
    public Task<WorkspaceDto?> CreateAsync(WorkspaceRequest request, CancellationToken ct = default) => client.PostAsync<WorkspaceDto>("/api/v1/workspaces", request, ct);
    public Task<WorkspaceDto?> UpdateAsync(Guid id, WorkspaceRequest request, CancellationToken ct = default) => client.PutAsync<WorkspaceDto>($"/api/v1/workspaces/{id}", request, ct);
    public Task DeleteAsync(Guid id, CancellationToken ct = default) => client.DeleteAsync<object>($"/api/v1/workspaces/{id}", ct);
}

public sealed class TemplateClient(CmsifyClient client)
{
    public Task<PagedResult<TemplateSummaryResponse>?> ListAsync(Guid workspaceId, bool? isSystem = null, string? search = null, int page = 1, int pageSize = 20, CancellationToken ct = default) =>
        client.GetAsync<PagedResult<TemplateSummaryResponse>>($"{CmsifyClient.WorkspacePath(workspaceId, "/templates")}?page={page}&pageSize={pageSize}{Query.Optional("isSystem", isSystem)}{Query.Optional("search", search)}", ct);
    public Task<TemplateResponse?> GetAsync(Guid workspaceId, Guid id, CancellationToken ct = default) => client.GetAsync<TemplateResponse>(CmsifyClient.WorkspacePath(workspaceId, $"/templates/{id}"), ct);
    public Task<TemplateResponse?> CreateAsync(Guid workspaceId, CreateTemplateRequest request, CancellationToken ct = default) => client.PostAsync<TemplateResponse>(CmsifyClient.WorkspacePath(workspaceId, "/templates"), request, ct);
    public Task<TemplateResponse?> UpdateAsync(Guid workspaceId, Guid id, UpdateTemplateRequest request, CancellationToken ct = default) => client.PutAsync<TemplateResponse>(CmsifyClient.WorkspacePath(workspaceId, $"/templates/{id}"), request, ct);
    public Task DeleteAsync(Guid workspaceId, Guid id, CancellationToken ct = default) => client.DeleteAsync<object>(CmsifyClient.WorkspacePath(workspaceId, $"/templates/{id}"), ct);
    public Task<IReadOnlyList<TemplateVersionSummaryResponse>?> VersionsAsync(Guid workspaceId, Guid id, CancellationToken ct = default) => client.GetAsync<IReadOnlyList<TemplateVersionSummaryResponse>>(CmsifyClient.WorkspacePath(workspaceId, $"/templates/{id}/versions"), ct);
    public Task<TemplateVersionResponse?> CreateVersionAsync(Guid workspaceId, Guid id, CreateTemplateVersionRequest request, CancellationToken ct = default) => client.PostAsync<TemplateVersionResponse>(CmsifyClient.WorkspacePath(workspaceId, $"/templates/{id}/versions"), request, ct);
    public Task<TemplateVersionResponse?> GetVersionAsync(Guid workspaceId, Guid id, int version, CancellationToken ct = default) => client.GetAsync<TemplateVersionResponse>(CmsifyClient.WorkspacePath(workspaceId, $"/templates/{id}/versions/{version}"), ct);
    public Task<TemplateVersionResponse?> PublishVersionAsync(Guid workspaceId, Guid id, int version, CancellationToken ct = default) => client.PutAsync<TemplateVersionResponse>(CmsifyClient.WorkspacePath(workspaceId, $"/templates/{id}/versions/{version}/publish"), null, ct);
    public Task<TemplateSectionResponse?> AddSectionAsync(Guid workspaceId, Guid id, int version, TemplateSectionRequest request, CancellationToken ct = default) => client.PostAsync<TemplateSectionResponse>(CmsifyClient.WorkspacePath(workspaceId, $"/templates/{id}/versions/{version}/sections"), request, ct);
    public Task<TemplateFieldResponse?> AddFieldAsync(Guid workspaceId, Guid id, int version, TemplateFieldRequest request, CancellationToken ct = default) => client.PostAsync<TemplateFieldResponse>(CmsifyClient.WorkspacePath(workspaceId, $"/templates/{id}/versions/{version}/fields"), request, ct);
}

public sealed class ContentClient(CmsifyClient client)
{
    public Task<PagedResponse<ContentItemSummaryResponse>?> ListAsync(Guid workspaceId, ContentListQuery? query = null, CancellationToken ct = default) => client.GetAsync<PagedResponse<ContentItemSummaryResponse>>(CmsifyClient.WorkspacePath(workspaceId, $"/content{Query.Content(query)}"), ct);
    public IAsyncEnumerable<ContentItemSummaryResponse> ListAllAsync(Guid workspaceId, ContentListQuery? query = null, CancellationToken ct = default) => CmsifyClient.ListAll(page => ListAsync(workspaceId, query is null ? new ContentListQuery(null, null, null, null, null, null, null, null, null, null, null, null, false, null, "createdAt", true, page, 20) : query with { Page = page }, ct), ct);
    public Task<ContentItemDetailResponse?> GetAsync(Guid workspaceId, Guid id, bool resolve = false, DateTimeOffset? asOf = null, CancellationToken ct = default) => client.GetAsync<ContentItemDetailResponse>(CmsifyClient.WorkspacePath(workspaceId, $"/content/{id}?resolve={resolve}{Query.Optional("asOf", asOf)}"), ct);
    public Task<ContentItemDetailResponse?> BySlugAsync(Guid workspaceId, string slug, DateTimeOffset? asOf = null, CancellationToken ct = default) => client.GetAsync<ContentItemDetailResponse>(CmsifyClient.WorkspacePath(workspaceId, $"/content/by-slug/{Uri.EscapeDataString(slug)}{Query.OptionalQuery("asOf", asOf)}"), ct);
    public Task<ContentItemDetailResponse?> CreateAsync(Guid workspaceId, CreateContentItemRequest request, CancellationToken ct = default) => client.PostAsync<ContentItemDetailResponse>(CmsifyClient.WorkspacePath(workspaceId, "/content"), request, ct);
    public Task<ContentItemDetailResponse?> UpdateAsync(Guid workspaceId, Guid id, UpdateContentItemRequest request, CancellationToken ct = default) => client.PutAsync<ContentItemDetailResponse>(CmsifyClient.WorkspacePath(workspaceId, $"/content/{id}"), request, ct);
    public Task DeleteAsync(Guid workspaceId, Guid id, CancellationToken ct = default) => client.DeleteAsync<object>(CmsifyClient.WorkspacePath(workspaceId, $"/content/{id}"), ct);
    public Task<ContentItemDetailResponse?> SubmitAsync(Guid workspaceId, Guid id, CancellationToken ct = default) => client.PostAsync<ContentItemDetailResponse>(CmsifyClient.WorkspacePath(workspaceId, $"/content/{id}/submit"), null, ct);
    public Task<ContentItemDetailResponse?> ApproveAsync(Guid workspaceId, Guid id, CancellationToken ct = default) => client.PostAsync<ContentItemDetailResponse>(CmsifyClient.WorkspacePath(workspaceId, $"/content/{id}/approve"), null, ct);
    public Task<ContentItemDetailResponse?> RejectAsync(Guid workspaceId, Guid id, RejectContentRequest request, CancellationToken ct = default) => client.PostAsync<ContentItemDetailResponse>(CmsifyClient.WorkspacePath(workspaceId, $"/content/{id}/reject"), request, ct);
    public Task<PublishContentResponse?> PublishAsync(Guid workspaceId, Guid id, PublishContentRequest? request = null, CancellationToken ct = default) => client.PostAsync<PublishContentResponse>(CmsifyClient.WorkspacePath(workspaceId, $"/content/{id}/publish"), request, ct);
    public Task<ContentItemDetailResponse?> ArchiveAsync(Guid workspaceId, Guid id, CancellationToken ct = default) => client.PostAsync<ContentItemDetailResponse>(CmsifyClient.WorkspacePath(workspaceId, $"/content/{id}/archive"), null, ct);
    public Task<ContentItemDetailResponse?> RestoreAsync(Guid workspaceId, Guid id, CancellationToken ct = default) => client.PostAsync<ContentItemDetailResponse>(CmsifyClient.WorkspacePath(workspaceId, $"/content/{id}/restore"), null, ct);
    public Task<IReadOnlyList<ContentItemSummaryResponse>?> TranslationsAsync(Guid workspaceId, Guid id, CancellationToken ct = default) => client.GetAsync<IReadOnlyList<ContentItemSummaryResponse>>(CmsifyClient.WorkspacePath(workspaceId, $"/content/{id}/translations"), ct);
    public Task<IReadOnlyList<ContentVersionSummaryResponse>?> VersionsAsync(Guid workspaceId, Guid id, CancellationToken ct = default) => client.GetAsync<IReadOnlyList<ContentVersionSummaryResponse>>(CmsifyClient.WorkspacePath(workspaceId, $"/content/{id}/versions"), ct);
}

public sealed class MediaClient(CmsifyClient client)
{
    public Task<MediaAssetResponse?> UploadAsync(Guid workspaceId, Stream content, string fileName, string contentType, string? altText = null, CancellationToken ct = default)
    {
        var form = new MultipartFormDataContent();
        var file = new StreamContent(content);
        file.Headers.ContentType = new MediaTypeHeaderValue(contentType);
        form.Add(file, "file", fileName);
        if (altText is not null)
        {
            form.Add(new StringContent(altText), "altText");
        }

        return client.SendMultipartAsync<MediaAssetResponse>(CmsifyClient.WorkspacePath(workspaceId, "/media"), form, ct);
    }
    public Task<PagedResponse<MediaAssetResponse>?> ListAsync(Guid workspaceId, string? mimeType = null, string? search = null, int page = 1, int pageSize = 20, CancellationToken ct = default) => client.GetAsync<PagedResponse<MediaAssetResponse>>(CmsifyClient.WorkspacePath(workspaceId, $"/media?page={page}&pageSize={pageSize}{Query.Optional("mimeType", mimeType)}{Query.Optional("search", search)}"), ct);
    public Task<MediaAssetResponse?> GetAsync(Guid workspaceId, Guid id, CancellationToken ct = default) => client.GetAsync<MediaAssetResponse>(CmsifyClient.WorkspacePath(workspaceId, $"/media/{id}"), ct);
    public Task<byte[]> DownloadAsync(Guid workspaceId, Guid id, CancellationToken ct = default) => client.DownloadAsync(CmsifyClient.WorkspacePath(workspaceId, $"/media/{id}/file"), ct);
    public Task<MediaAssetResponse?> UpdateAsync(Guid workspaceId, Guid id, UpdateMediaAssetRequest request, CancellationToken ct = default) => client.PutAsync<MediaAssetResponse>(CmsifyClient.WorkspacePath(workspaceId, $"/media/{id}"), request, ct);
    public Task DeleteAsync(Guid workspaceId, Guid id, CancellationToken ct = default) => client.DeleteAsync<object>(CmsifyClient.WorkspacePath(workspaceId, $"/media/{id}"), ct);
}

public sealed class PickListClient(CmsifyClient client)
{
    public Task<IReadOnlyList<PickListSummaryResponse>?> ListAsync(Guid workspaceId, string? search = null, CancellationToken ct = default) => client.GetAsync<IReadOnlyList<PickListSummaryResponse>>(CmsifyClient.WorkspacePath(workspaceId, $"/picklists{Query.OptionalQuery("search", search)}"), ct);
    public Task<PickListResponse?> GetAsync(Guid workspaceId, Guid id, CancellationToken ct = default) => client.GetAsync<PickListResponse>(CmsifyClient.WorkspacePath(workspaceId, $"/picklists/{id}"), ct);
    public Task<PickListResponse?> CreateAsync(Guid workspaceId, PickListRequest request, CancellationToken ct = default) => client.PostAsync<PickListResponse>(CmsifyClient.WorkspacePath(workspaceId, "/picklists"), request, ct);
    public Task<PickListResponse?> UpdateAsync(Guid workspaceId, Guid id, PickListRequest request, CancellationToken ct = default) => client.PutAsync<PickListResponse>(CmsifyClient.WorkspacePath(workspaceId, $"/picklists/{id}"), request, ct);
}

public sealed class ComponentClient(CmsifyClient client)
{
    public Task<IReadOnlyList<ComponentSummaryResponse>?> ListAsync(Guid workspaceId, CancellationToken ct = default) => client.GetAsync<IReadOnlyList<ComponentSummaryResponse>>(CmsifyClient.WorkspacePath(workspaceId, "/components"), ct);
    public Task<ComponentResponse?> GetAsync(Guid workspaceId, Guid id, CancellationToken ct = default) => client.GetAsync<ComponentResponse>(CmsifyClient.WorkspacePath(workspaceId, $"/components/{id}"), ct);
    public Task<ComponentResponse?> CreateAsync(Guid workspaceId, ComponentRequest request, CancellationToken ct = default) => client.PostAsync<ComponentResponse>(CmsifyClient.WorkspacePath(workspaceId, "/components"), request, ct);
    public Task<ComponentResponse?> UpdateAsync(Guid workspaceId, Guid id, ComponentRequest request, CancellationToken ct = default) => client.PutAsync<ComponentResponse>(CmsifyClient.WorkspacePath(workspaceId, $"/components/{id}"), request, ct);
    public Task<ComponentVersionResponse?> CreateDraftAsync(Guid workspaceId, Guid id, ComponentVersionRequest request, CancellationToken ct = default) => client.PostAsync<ComponentVersionResponse>(CmsifyClient.WorkspacePath(workspaceId, $"/components/{id}/versions"), request, ct);
    public Task<ComponentVersionResponse?> SaveFieldsAsync(Guid workspaceId, Guid id, int version, IReadOnlyList<ComponentFieldRequest> fields, CancellationToken ct = default) => client.PutAsync<ComponentVersionResponse>(CmsifyClient.WorkspacePath(workspaceId, $"/components/{id}/versions/{version}/fields"), fields, ct);
    public Task<ComponentResponse?> PublishAsync(Guid workspaceId, Guid id, int version, CancellationToken ct = default) => client.PostAsync<ComponentResponse>(CmsifyClient.WorkspacePath(workspaceId, $"/components/{id}/versions/{version}/publish"), null, ct);
}

public sealed class TagClient(CmsifyClient client)
{
    public Task<IReadOnlyList<TagResponse>?> ListAsync(Guid workspaceId, CancellationToken ct = default) => client.GetAsync<IReadOnlyList<TagResponse>>(CmsifyClient.WorkspacePath(workspaceId, "/tags"), ct);
    public Task<TagResponse?> UpsertAsync(Guid workspaceId, string name, CancellationToken ct = default) => client.PostAsync<TagResponse>(CmsifyClient.WorkspacePath(workspaceId, "/tags"), new { name }, ct);
    public Task DeleteAsync(Guid workspaceId, Guid id, CancellationToken ct = default) => client.DeleteAsync<object>(CmsifyClient.WorkspacePath(workspaceId, $"/tags/{id}"), ct);
}

public sealed class WebhookClient(CmsifyClient client)
{
    public Task<PagedResponse<WebhookEndpointResponse>?> ListAsync(Guid workspaceId, int page = 1, int pageSize = 20, CancellationToken ct = default) => client.GetAsync<PagedResponse<WebhookEndpointResponse>>(CmsifyClient.WorkspacePath(workspaceId, $"/webhooks?page={page}&pageSize={pageSize}"), ct);
    public Task<WebhookEndpointResponse?> GetAsync(Guid workspaceId, Guid id, CancellationToken ct = default) => client.GetAsync<WebhookEndpointResponse>(CmsifyClient.WorkspacePath(workspaceId, $"/webhooks/{id}"), ct);
    public Task<CreateWebhookEndpointResponse?> CreateAsync(Guid workspaceId, CreateWebhookEndpointRequest request, CancellationToken ct = default) => client.PostAsync<CreateWebhookEndpointResponse>(CmsifyClient.WorkspacePath(workspaceId, "/webhooks"), request, ct);
    public Task<WebhookEndpointResponse?> UpdateAsync(Guid workspaceId, Guid id, UpdateWebhookEndpointRequest request, CancellationToken ct = default) => client.PutAsync<WebhookEndpointResponse>(CmsifyClient.WorkspacePath(workspaceId, $"/webhooks/{id}"), request, ct);
    public Task<RotateWebhookSecretResponse?> RotateSecretAsync(Guid workspaceId, Guid id, CancellationToken ct = default) => client.PostAsync<RotateWebhookSecretResponse>(CmsifyClient.WorkspacePath(workspaceId, $"/webhooks/{id}/rotate-secret"), null, ct);
    public Task DeleteAsync(Guid workspaceId, Guid id, CancellationToken ct = default) => client.DeleteAsync<object>(CmsifyClient.WorkspacePath(workspaceId, $"/webhooks/{id}"), ct);
    public Task<PagedResponse<WebhookDeliveryResponse>?> DeliveriesAsync(Guid workspaceId, Guid id, int page = 1, int pageSize = 50, CancellationToken ct = default) => client.GetAsync<PagedResponse<WebhookDeliveryResponse>>(CmsifyClient.WorkspacePath(workspaceId, $"/webhooks/{id}/deliveries?page={page}&pageSize={pageSize}"), ct);
}

public sealed class AuditClient(CmsifyClient client)
{
    public Task<PagedResponse<AuditLogResponse>?> QueryAsync(AuditQueryRequest? query = null, CancellationToken ct = default) => client.GetAsync<PagedResponse<AuditLogResponse>>($"/api/v1/audit{Query.Audit(query)}", ct);
    public Task<PagedResponse<AuditLogResponse>?> QueryWorkspaceAsync(Guid workspaceId, AuditQueryRequest? query = null, CancellationToken ct = default) => client.GetAsync<PagedResponse<AuditLogResponse>>(CmsifyClient.WorkspacePath(workspaceId, $"/audit{Query.Audit(query)}"), ct);
}

public sealed class UserClient(CmsifyClient client)
{
    public Task<PagedResponse<UserDto>?> ListAsync(int page = 1, int pageSize = 50, CancellationToken ct = default) => client.GetAsync<PagedResponse<UserDto>>($"/api/v1/users?page={page}&pageSize={pageSize}", ct);
    public Task<UserDto?> GetAsync(Guid id, CancellationToken ct = default) => client.GetAsync<UserDto>($"/api/v1/users/{id}", ct);
    public Task<TempPasswordResponse?> CreateAsync(CreateUserRequest request, CancellationToken ct = default) => client.PostAsync<TempPasswordResponse>("/api/v1/users", request, ct);
    public Task<UserDto?> UpdateAsync(Guid id, UpdateUserRequest request, CancellationToken ct = default) => client.PutAsync<UserDto>($"/api/v1/users/{id}", request, ct);
    public Task<TempPasswordResponse?> ResetPasswordAsync(Guid id, ResetPasswordRequest request, CancellationToken ct = default) => client.PostAsync<TempPasswordResponse>($"/api/v1/users/{id}/reset-password", request, ct);
}

public sealed class ApiClientManagementClient(CmsifyClient client)
{
    public Task<PagedResponse<ApiClientDto>?> ListAsync(int page = 1, int pageSize = 50, CancellationToken ct = default) => client.GetAsync<PagedResponse<ApiClientDto>>($"/api/v1/clients?page={page}&pageSize={pageSize}", ct);
    public Task<ApiClientDto?> GetAsync(Guid id, CancellationToken ct = default) => client.GetAsync<ApiClientDto>($"/api/v1/clients/{id}", ct);
    public Task<CreateApiClientResponse?> CreateAsync(CreateApiClientRequest request, CancellationToken ct = default) => client.PostAsync<CreateApiClientResponse>("/api/v1/clients", request, ct);
    public Task<ApiClientDto?> RevokeAsync(Guid id, CancellationToken ct = default) => client.PostAsync<ApiClientDto>($"/api/v1/clients/{id}/revoke", null, ct);
    public Task<CreateApiClientResponse?> RotateAsync(Guid id, CancellationToken ct = default) => client.PostAsync<CreateApiClientResponse>($"/api/v1/clients/{id}/rotate", null, ct);
}

public sealed class SettingsClient(CmsifyClient client)
{
    public Task<AccountPreferencesResponse?> PreferencesAsync(CancellationToken ct = default) => client.GetAsync<AccountPreferencesResponse>("/api/v1/account/preferences", ct);
    public Task<AccountPreferencesResponse?> UpdatePreferencesAsync(UpdateAccountPreferencesRequest request, CancellationToken ct = default) => client.PutAsync<AccountPreferencesResponse>("/api/v1/account/preferences", request, ct);
    public Task<StorageConfigResponse?> StorageAsync(CancellationToken ct = default) => client.GetAsync<StorageConfigResponse>("/api/v1/settings/storage", ct);
    public Task<StorageTestResponse?> TestStorageAsync(CancellationToken ct = default) => client.PostAsync<StorageTestResponse>("/api/v1/settings/storage/test", null, ct);
}

public sealed class PackageClient(CmsifyClient client)
{
    public Task<IReadOnlyList<OfficialPackageResponse>?> OfficialAsync(CancellationToken ct = default) => client.GetAsync<IReadOnlyList<OfficialPackageResponse>>("/api/v1/packages/official", ct);
    public Task<PackageImportPreviewResponse?> PreviewOfficialAsync(Guid workspaceId, string packageId, CancellationToken ct = default) => client.PostAsync<PackageImportPreviewResponse>(CmsifyClient.WorkspacePath(workspaceId, $"/packages/import/official/{Uri.EscapeDataString(packageId)}/preview"), null, ct);
    public Task<PackageImportResponse?> ImportOfficialAsync(Guid workspaceId, string packageId, PackageImportResolutionsRequest? request = null, CancellationToken ct = default) => client.PostAsync<PackageImportResponse>(CmsifyClient.WorkspacePath(workspaceId, $"/packages/import/official/{Uri.EscapeDataString(packageId)}"), request, ct);
}

internal static class Query
{
    public static string Optional(string name, object? value) => value is null ? string.Empty : $"&{name}={Uri.EscapeDataString(value is DateTimeOffset date ? date.ToString("O") : value.ToString()!)}";
    public static string OptionalQuery(string name, object? value) => value is null ? string.Empty : $"?{name}={Uri.EscapeDataString(value is DateTimeOffset date ? date.ToString("O") : value.ToString()!)}";
    public static string Content(ContentListQuery? q) => q is null ? "?resolve=true" : $"?q={Uri.EscapeDataString(q.Q ?? string.Empty)}&page={q.Page}&pageSize={q.PageSize}&resolve={q.Resolve}{Optional("templateVersionId", q.TemplateVersionId)}{Optional("templateId", q.TemplateId)}{Optional("status", q.Status)}{Optional("localeCode", q.LocaleCode)}{Optional("translationGroupId", q.TranslationGroupId)}{Optional("slug", q.Slug)}{Optional("tags", q.Tags)}{Optional("createdAfter", q.CreatedAfter)}{Optional("createdBefore", q.CreatedBefore)}{Optional("publishedAfter", q.PublishedAfter)}{Optional("publishedBefore", q.PublishedBefore)}{Optional("asOf", q.AsOf)}{Optional("sortBy", q.SortBy)}&sortDesc={q.SortDesc}";
    public static string Audit(AuditQueryRequest? q) => q is null ? "?page=1&pageSize=50" : $"?page={q.Page}&pageSize={q.PageSize}{Optional("entityType", q.EntityType)}{Optional("entityId", q.EntityId)}{Optional("action", q.Action)}{Optional("actorUserId", q.ActorUserId)}{Optional("actorApiClientId", q.ActorApiClientId)}{Optional("after", q.After)}{Optional("before", q.Before)}";
}
