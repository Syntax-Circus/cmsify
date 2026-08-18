using System.Net.Http.Json;
using Microsoft.AspNetCore.Components.Forms;
using Cmsify.Admin.Auth;
using Cmsify.Admin.State;
using Cmsify.Core.Domain.Enums;

namespace Cmsify.Admin.Services;

public sealed class AuthService
{
    private readonly IHttpClientFactory httpClientFactory;

    public AuthService(IHttpClientFactory httpClientFactory)
    {
        this.httpClientFactory = httpClientFactory;
    }

    public async Task<LoginResponse> LoginAsync(string email, string password, CancellationToken ct = default)
    {
        var response = await httpClientFactory.CreateClient("CmsifyApi").PostAsJsonAsync("/api/v1/auth/login", new { email, password }, ct);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<LoginResponse>(cancellationToken: ct))!;
    }

    public async Task LogoutAsync(string? token, CancellationToken ct = default)
    {
        var http = httpClientFactory.CreateClient("CmsifyApi");
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/auth/logout");
        if (!string.IsNullOrWhiteSpace(token))
        {
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
        }

        using var response = await http.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();
    }

    public async Task ChangePasswordAsync(string? token, string currentPassword, string newPassword, CancellationToken ct = default)
    {
        var http = httpClientFactory.CreateClient("CmsifyApi");
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/auth/change-password")
        {
            Content = JsonContent.Create(new { currentPassword, newPassword })
        };
        if (!string.IsNullOrWhiteSpace(token))
        {
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
        }

        using var response = await http.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();
    }
}

public sealed class WorkspaceApiClient : CmsifyApiClientBase
{
    public WorkspaceApiClient(IHttpClientFactory httpClientFactory, IApiTokenAccessor apiTokenAccessor) : base(httpClientFactory, apiTokenAccessor) { }

    public Task<PagedResult<WorkspaceDto>> ListAsync(CancellationToken ct = default) => RequireAsync(GetAsync<PagedResult<WorkspaceDto>>("/api/v1/workspaces", ct));

    public Task<WorkspaceDto> GetAsync(Guid id, CancellationToken ct = default) => RequireAsync(GetWithETagAsync<WorkspaceDto>($"/api/v1/workspaces/{id}", ct, useConditionalRequest: false));

    public Task<WorkspaceDto> CreateAsync(WorkspaceRequest request, CancellationToken ct = default) => RequireAsync(PostAsync<WorkspaceRequest, WorkspaceDto>("/api/v1/workspaces", request, ct));

    public Task<WorkspaceDto> UpdateAsync(Guid id, WorkspaceRequest request, CancellationToken ct = default) => RequireAsync(PutAsync<WorkspaceRequest, WorkspaceDto>($"/api/v1/workspaces/{id}", request, ct));

    public Task DeleteAsync(Guid id, CancellationToken ct = default) => DeleteAsync($"/api/v1/workspaces/{id}", ct);
}

public sealed class TemplateApiClient : CmsifyApiClientBase
{
    public TemplateApiClient(IHttpClientFactory httpClientFactory, IApiTokenAccessor apiTokenAccessor) : base(httpClientFactory, apiTokenAccessor) { }

    public Task<PagedResult<TemplateSummaryResponse>> ListAsync(Guid workspaceId, string? search = null, CancellationToken ct = default) =>
        RequireAsync(GetAsync<PagedResult<TemplateSummaryResponse>>($"/api/v1/workspaces/{workspaceId}/templates?search={Uri.EscapeDataString(search ?? string.Empty)}", ct));

    public Task<TemplateResponse> GetAsync(Guid workspaceId, Guid templateId, CancellationToken ct = default) =>
        RequireAsync(GetWithETagAsync<TemplateResponse>($"/api/v1/workspaces/{workspaceId}/templates/{templateId}", ct, useConditionalRequest: false));

    public Task<TemplateResponse> CreateAsync(Guid workspaceId, CreateTemplateRequest request, CancellationToken ct = default) =>
        RequireAsync(PostAsync<CreateTemplateRequest, TemplateResponse>($"/api/v1/workspaces/{workspaceId}/templates", request, ct));

    public Task DeleteAsync(Guid workspaceId, Guid templateId, CancellationToken ct = default) =>
        DeleteAsync($"/api/v1/workspaces/{workspaceId}/templates/{templateId}", ct);

    public Task<IReadOnlyList<TemplateVersionSummaryResponse>> ListVersionsAsync(Guid workspaceId, Guid templateId, CancellationToken ct = default) =>
        RequireAsync(GetAsync<IReadOnlyList<TemplateVersionSummaryResponse>>($"/api/v1/workspaces/{workspaceId}/templates/{templateId}/versions", ct));

    public Task<TemplateVersionResponse> GetVersionAsync(Guid workspaceId, Guid templateId, int versionNumber, CancellationToken ct = default) =>
        RequireAsync(GetAsync<TemplateVersionResponse>($"/api/v1/workspaces/{workspaceId}/templates/{templateId}/versions/{versionNumber}", ct));

    public Task<TemplateVersionResponse> CreateDraftAsync(Guid workspaceId, Guid templateId, string? notes, CancellationToken ct = default) =>
        RequireAsync(PostAsync<object, TemplateVersionResponse>($"/api/v1/workspaces/{workspaceId}/templates/{templateId}/versions", new { notes }, ct));

    public Task<TemplateVersionResponse> PublishAsync(Guid workspaceId, Guid templateId, int versionNumber, CancellationToken ct = default) =>
        RequireAsync(PutAsync<object, TemplateVersionResponse>($"/api/v1/workspaces/{workspaceId}/templates/{templateId}/versions/{versionNumber}/publish", new { }, ct));

    public Task<TemplateSectionResponse> AddSectionAsync(Guid workspaceId, Guid templateId, int versionNumber, TemplateSectionRequest request, CancellationToken ct = default) =>
        RequireAsync(PostAsync<TemplateSectionRequest, TemplateSectionResponse>($"/api/v1/workspaces/{workspaceId}/templates/{templateId}/versions/{versionNumber}/sections", request, ct));

    public Task<TemplateSectionResponse> UpdateSectionAsync(Guid workspaceId, Guid templateId, int versionNumber, Guid sectionId, TemplateSectionRequest request, CancellationToken ct = default) =>
        RequireAsync(PutAsync<TemplateSectionRequest, TemplateSectionResponse>($"/api/v1/workspaces/{workspaceId}/templates/{templateId}/versions/{versionNumber}/sections/{sectionId}", request, ct));

    public Task DeleteSectionAsync(Guid workspaceId, Guid templateId, int versionNumber, Guid sectionId, CancellationToken ct = default) =>
        DeleteAsync($"/api/v1/workspaces/{workspaceId}/templates/{templateId}/versions/{versionNumber}/sections/{sectionId}", ct);

    public Task<TemplateFieldResponse> AddFieldAsync(Guid workspaceId, Guid templateId, int versionNumber, TemplateFieldRequest request, CancellationToken ct = default) =>
        RequireAsync(PostAsync<TemplateFieldRequest, TemplateFieldResponse>($"/api/v1/workspaces/{workspaceId}/templates/{templateId}/versions/{versionNumber}/fields", request, ct));

    public Task<TemplateFieldResponse> UpdateFieldAsync(Guid workspaceId, Guid templateId, int versionNumber, Guid fieldId, TemplateFieldRequest request, CancellationToken ct = default) =>
        RequireAsync(PutAsync<TemplateFieldRequest, TemplateFieldResponse>($"/api/v1/workspaces/{workspaceId}/templates/{templateId}/versions/{versionNumber}/fields/{fieldId}", request, ct));

    public Task DeleteFieldAsync(Guid workspaceId, Guid templateId, int versionNumber, Guid fieldId, CancellationToken ct = default) =>
        DeleteAsync($"/api/v1/workspaces/{workspaceId}/templates/{templateId}/versions/{versionNumber}/fields/{fieldId}", ct);

    public Task ReorderFieldsAsync(Guid workspaceId, Guid templateId, int versionNumber, IReadOnlyList<ReorderFieldRequest> request, CancellationToken ct = default) =>
        PutAsync($"/api/v1/workspaces/{workspaceId}/templates/{templateId}/versions/{versionNumber}/fields/reorder", request, ct);
}

public sealed class PickListApiClient : CmsifyApiClientBase
{
    public PickListApiClient(IHttpClientFactory httpClientFactory, IApiTokenAccessor apiTokenAccessor) : base(httpClientFactory, apiTokenAccessor) { }

    public Task<IReadOnlyList<PickListSummaryResponse>> ListAsync(Guid workspaceId, string? search = null, CancellationToken ct = default) =>
        RequireAsync(GetAsync<IReadOnlyList<PickListSummaryResponse>>($"/api/v1/workspaces/{workspaceId}/picklists?search={Uri.EscapeDataString(search ?? string.Empty)}", ct));

    public Task<PickListResponse> GetAsync(Guid workspaceId, Guid id, CancellationToken ct = default) =>
        RequireAsync(GetWithETagAsync<PickListResponse>($"/api/v1/workspaces/{workspaceId}/picklists/{id}", ct, useConditionalRequest: false));

    public Task<PickListResponse> CreateAsync(Guid workspaceId, PickListRequest request, CancellationToken ct = default) =>
        RequireAsync(PostAsync<PickListRequest, PickListResponse>($"/api/v1/workspaces/{workspaceId}/picklists", request, ct));

    public Task<PickListResponse> UpdateAsync(Guid workspaceId, Guid id, PickListRequest request, CancellationToken ct = default) =>
        RequireAsync(PutAsync<PickListRequest, PickListResponse>($"/api/v1/workspaces/{workspaceId}/picklists/{id}", request, ct));

    public Task DeleteAsync(Guid workspaceId, Guid id, CancellationToken ct = default) =>
        DeleteAsync($"/api/v1/workspaces/{workspaceId}/picklists/{id}", ct);
}

public sealed class ContentApiClient : CmsifyApiClientBase
{
    public ContentApiClient(IHttpClientFactory httpClientFactory, IApiTokenAccessor apiTokenAccessor) : base(httpClientFactory, apiTokenAccessor) { }

    public Task<PagedResponse<ContentItemSummaryResponse>> ListAsync(Guid workspaceId, ContentStatus? status, Guid? templateId, string? locale, string? tags, string? q, CancellationToken ct = default)
    {
        var query = $"status={status}&templateId={templateId}&localeCode={Uri.EscapeDataString(locale ?? string.Empty)}&tags={Uri.EscapeDataString(tags ?? string.Empty)}&q={Uri.EscapeDataString(q ?? string.Empty)}&sortBy=updatedAt&sortDesc=true";
        return RequireAsync(GetAsync<PagedResponse<ContentItemSummaryResponse>>($"/api/v1/workspaces/{workspaceId}/content?{query}", ct));
    }

    public Task<ContentItemDetailResponse> GetAsync(Guid workspaceId, Guid contentId, CancellationToken ct = default) =>
        RequireAsync(GetWithETagAsync<ContentItemDetailResponse>($"/api/v1/workspaces/{workspaceId}/content/{contentId}", ct, useConditionalRequest: false));

    public Task<ContentItemDetailResponse> CreateAsync(Guid workspaceId, CreateContentItemRequest request, CancellationToken ct = default) =>
        RequireAsync(PostAsync<CreateContentItemRequest, ContentItemDetailResponse>($"/api/v1/workspaces/{workspaceId}/content", request, ct));

    public Task<ContentItemDetailResponse> UpdateAsync(Guid workspaceId, Guid contentId, UpdateContentItemRequest request, CancellationToken ct = default) =>
        RequireAsync(PutAsync<UpdateContentItemRequest, ContentItemDetailResponse>($"/api/v1/workspaces/{workspaceId}/content/{contentId}", request, ct));

    public Task<ContentItemDetailResponse> TransitionAsync(Guid workspaceId, Guid contentId, string action, object? request = null, CancellationToken ct = default) =>
        RequireAsync(PostAsync<object?, ContentItemDetailResponse>($"/api/v1/workspaces/{workspaceId}/content/{contentId}/{action}", request, ct));

    public Task<PublishContentResponse> PublishAsync(Guid workspaceId, Guid contentId, PublishContentRequest request, CancellationToken ct = default) =>
        RequireAsync(PostAsync<PublishContentRequest, PublishContentResponse>($"/api/v1/workspaces/{workspaceId}/content/{contentId}/publish", request, ct));

    public Task<IReadOnlyList<ContentItemSummaryResponse>> LinkTranslationAsync(Guid workspaceId, Guid contentId, Guid targetId, CancellationToken ct = default) =>
        RequireAsync(PostAsync<LinkTranslationRequest, IReadOnlyList<ContentItemSummaryResponse>>($"/api/v1/workspaces/{workspaceId}/content/{contentId}/link-translation", new LinkTranslationRequest(targetId), ct));

    public Task<IReadOnlyList<ContentVersionSummaryResponse>> ListVersionsAsync(Guid workspaceId, Guid contentId, CancellationToken ct = default) =>
        RequireAsync(GetAsync<IReadOnlyList<ContentVersionSummaryResponse>>($"/api/v1/workspaces/{workspaceId}/content/{contentId}/versions", ct));

    public Task<ContentVersionDetailResponse> GetVersionAsync(Guid workspaceId, Guid contentId, int versionNumber, CancellationToken ct = default) =>
        RequireAsync(GetAsync<ContentVersionDetailResponse>($"/api/v1/workspaces/{workspaceId}/content/{contentId}/versions/{versionNumber}", ct));

    public Task<ContentItemDetailResponse> RollbackAsync(Guid workspaceId, Guid contentId, int versionNumber, CancellationToken ct = default) =>
        RequireAsync(PostAsync<object, ContentItemDetailResponse>($"/api/v1/workspaces/{workspaceId}/content/{contentId}/versions/{versionNumber}/rollback", new { }, ct));
}

public sealed class MediaApiClient : CmsifyApiClientBase
{
    public MediaApiClient(IHttpClientFactory httpClientFactory, IApiTokenAccessor apiTokenAccessor) : base(httpClientFactory, apiTokenAccessor) { }

    public Task<PagedResponse<MediaAssetResponse>> ListAsync(Guid workspaceId, string? mimeType, string? search, CancellationToken ct = default) =>
        RequireAsync(GetAsync<PagedResponse<MediaAssetResponse>>($"/api/v1/workspaces/{workspaceId}/media?mimeType={Uri.EscapeDataString(mimeType ?? string.Empty)}&search={Uri.EscapeDataString(search ?? string.Empty)}", ct));

    public Task<MediaAssetResponse> UpdateAsync(Guid workspaceId, Guid id, string? altText, CancellationToken ct = default) =>
        RequireAsync(PutAsync<UpdateMediaAssetRequest, MediaAssetResponse>($"/api/v1/workspaces/{workspaceId}/media/{id}", new UpdateMediaAssetRequest(altText), ct));

    public Task DeleteAsync(Guid workspaceId, Guid id, CancellationToken ct = default) =>
        DeleteAsync($"/api/v1/workspaces/{workspaceId}/media/{id}", ct);

    public async Task<MediaAssetResponse> UploadAsync(Guid workspaceId, IBrowserFile file, string? altText, IProgress<long>? progress = null, CancellationToken ct = default)
    {
        using var content = new MultipartFormDataContent();
        var stream = file.OpenReadStream(1_073_741_824, ct);
        var fileContent = new StreamContent(new ProgressStream(stream, bytes => progress?.Report(bytes)));
        fileContent.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(file.ContentType);
        content.Add(fileContent, "file", file.Name);
        if (!string.IsNullOrWhiteSpace(altText))
        {
            content.Add(new StringContent(altText), "altText");
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/workspaces/{workspaceId}/media") { Content = content };
        using var response = await SendAsync(request, ct);
        return (await ReadJsonAsync<MediaAssetResponse>(response, ct))!;
    }

    private sealed class ProgressStream : Stream
    {
        private readonly Stream inner;
        private readonly Action<long> report;
        private long total;

        public ProgressStream(Stream inner, Action<long> report)
        {
            this.inner = inner;
            this.report = report;
        }

        public override bool CanRead => inner.CanRead;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => inner.Length;
        public override long Position { get => total; set => throw new NotSupportedException(); }
        public override void Flush() => inner.Flush();
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            var read = await inner.ReadAsync(buffer, cancellationToken);
            total += read;
            report(total);
            return read;
        }
    }
}

public sealed class WebhookApiClient : CmsifyApiClientBase
{
    public WebhookApiClient(IHttpClientFactory httpClientFactory, IApiTokenAccessor apiTokenAccessor) : base(httpClientFactory, apiTokenAccessor) { }

    public Task<PagedResponse<WebhookEndpointResponse>> ListAsync(Guid workspaceId, CancellationToken ct = default) =>
        RequireAsync(GetAsync<PagedResponse<WebhookEndpointResponse>>($"/api/v1/workspaces/{workspaceId}/webhooks", ct));

    public Task<CreateWebhookEndpointResponse> CreateAsync(Guid workspaceId, CreateWebhookEndpointRequest request, CancellationToken ct = default) =>
        RequireAsync(PostAsync<CreateWebhookEndpointRequest, CreateWebhookEndpointResponse>($"/api/v1/workspaces/{workspaceId}/webhooks", request, ct));

    public Task<WebhookEndpointResponse> UpdateAsync(Guid workspaceId, Guid id, UpdateWebhookEndpointRequest request, CancellationToken ct = default) =>
        RequireAsync(PutAsync<UpdateWebhookEndpointRequest, WebhookEndpointResponse>($"/api/v1/workspaces/{workspaceId}/webhooks/{id}", request, ct));

    public Task<PagedResponse<WebhookDeliveryResponse>> ListDeliveriesAsync(Guid workspaceId, Guid id, CancellationToken ct = default) =>
        RequireAsync(GetAsync<PagedResponse<WebhookDeliveryResponse>>($"/api/v1/workspaces/{workspaceId}/webhooks/{id}/deliveries", ct));

    public Task RetryAsync(Guid workspaceId, Guid id, Guid deliveryId, CancellationToken ct = default) =>
        PostAsync($"/api/v1/workspaces/{workspaceId}/webhooks/{id}/deliveries/{deliveryId}/retry", new { }, ct);
}

public sealed class AuditApiClient : CmsifyApiClientBase
{
    public AuditApiClient(IHttpClientFactory httpClientFactory, IApiTokenAccessor apiTokenAccessor) : base(httpClientFactory, apiTokenAccessor) { }

    public Task<PagedResponse<AuditLogResponse>> QueryAsync(Guid? workspaceId, string? entityType, string? action, CancellationToken ct = default)
    {
        var root = workspaceId.HasValue ? $"/api/v1/workspaces/{workspaceId}/audit" : "/api/v1/audit";
        return RequireAsync(GetAsync<PagedResponse<AuditLogResponse>>($"{root}?entityType={Uri.EscapeDataString(entityType ?? string.Empty)}&action={Uri.EscapeDataString(action ?? string.Empty)}", ct));
    }
}

public sealed class UserApiClient : CmsifyApiClientBase
{
    public UserApiClient(IHttpClientFactory httpClientFactory, IApiTokenAccessor apiTokenAccessor) : base(httpClientFactory, apiTokenAccessor) { }

    public Task<PagedResponse<UserDto>> ListAsync(CancellationToken ct = default) => RequireAsync(GetAsync<PagedResponse<UserDto>>("/api/v1/users", ct));

    public Task<TempPasswordResponse> CreateAsync(CreateUserRequest request, CancellationToken ct = default) => RequireAsync(PostAsync<CreateUserRequest, TempPasswordResponse>("/api/v1/users", request, ct));

    public Task<UserDto> UpdateAsync(Guid id, UpdateUserRequest request, CancellationToken ct = default) => RequireAsync(PutAsync<UpdateUserRequest, UserDto>($"/api/v1/users/{id}", request, ct));

    public Task<TempPasswordResponse> ResetPasswordAsync(Guid id, string temporaryPassword, CancellationToken ct = default) =>
        RequireAsync(PostAsync<ResetPasswordRequest, TempPasswordResponse>($"/api/v1/users/{id}/reset-password", new ResetPasswordRequest(temporaryPassword), ct));
}

public sealed class ApiClientsApiClient : CmsifyApiClientBase
{
    public ApiClientsApiClient(IHttpClientFactory httpClientFactory, IApiTokenAccessor apiTokenAccessor) : base(httpClientFactory, apiTokenAccessor) { }

    public Task<PagedResponse<ApiClientDto>> ListAsync(CancellationToken ct = default) => RequireAsync(GetAsync<PagedResponse<ApiClientDto>>("/api/v1/clients", ct));

    public Task<CreateApiClientResponse> CreateAsync(CreateApiClientRequest request, CancellationToken ct = default) => RequireAsync(PostAsync<CreateApiClientRequest, CreateApiClientResponse>("/api/v1/clients", request, ct));

    public Task<ApiClientDto> RevokeAsync(Guid id, CancellationToken ct = default) => RequireAsync(PostAsync<object, ApiClientDto>($"/api/v1/clients/{id}/revoke", new { }, ct));

    public Task<CreateApiClientResponse> RotateAsync(Guid id, CancellationToken ct = default) => RequireAsync(PostAsync<object, CreateApiClientResponse>($"/api/v1/clients/{id}/rotate", new { }, ct));
}

public sealed class SettingsApiClient : CmsifyApiClientBase
{
    public SettingsApiClient(IHttpClientFactory httpClientFactory, IApiTokenAccessor apiTokenAccessor) : base(httpClientFactory, apiTokenAccessor) { }

    public Task<AccountPreferencesResponse> GetPreferencesAsync(CancellationToken ct = default) => RequireAsync(GetAsync<AccountPreferencesResponse>("/api/v1/account/preferences", ct));

    public Task<AccountPreferencesResponse> UpdatePreferencesAsync(UpdateAccountPreferencesRequest request, CancellationToken ct = default) => RequireAsync(PutAsync<UpdateAccountPreferencesRequest, AccountPreferencesResponse>("/api/v1/account/preferences", request, ct));

    public Task<StorageConfigResponse> GetStorageAsync(CancellationToken ct = default) => RequireAsync(GetAsync<StorageConfigResponse>("/api/v1/settings/storage", ct));

    public Task<StorageTestResponse> TestStorageAsync(CancellationToken ct = default) => RequireAsync(PostAsync<object, StorageTestResponse>("/api/v1/settings/storage/test", new { }, ct));
}

public sealed class PackagesApiClient : CmsifyApiClientBase
{
    public PackagesApiClient(IHttpClientFactory httpClientFactory, IApiTokenAccessor apiTokenAccessor) : base(httpClientFactory, apiTokenAccessor) { }

    public Task<IReadOnlyList<OfficialPackageResponse>> ListOfficialAsync(CancellationToken ct = default) =>
        RequireAsync(GetAsync<IReadOnlyList<OfficialPackageResponse>>("/api/v1/packages/official", ct));

    public Task<PackageImportResponse> ImportOfficialAsync(Guid workspaceId, string packageId, PackageImportResolutionsRequest? resolutions = null, CancellationToken ct = default) =>
        RequireAsync(PostAsync<PackageImportResolutionsRequest?, PackageImportResponse>($"/api/v1/workspaces/{workspaceId}/packages/import/official/{Uri.EscapeDataString(packageId)}", resolutions, ct));

    public Task<PackageImportPreviewResponse> PreviewOfficialAsync(Guid workspaceId, string packageId, CancellationToken ct = default) =>
        RequireAsync(PostAsync<object, PackageImportPreviewResponse>($"/api/v1/workspaces/{workspaceId}/packages/import/official/{Uri.EscapeDataString(packageId)}/preview", new { }, ct));

    public async Task<PackageImportPreviewResponse> PreviewCustomAsync(Guid workspaceId, IBrowserFile file, CancellationToken ct = default)
    {
        using var content = BuildImportForm(file, null);
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/workspaces/{workspaceId}/packages/import/preview")
        {
            Content = content
        };
        using var response = await SendAsync(request, ct);
        return (await ReadJsonAsync<PackageImportPreviewResponse>(response, ct))!;
    }

    public async Task<PackageImportResponse> ImportAsync(Guid workspaceId, IBrowserFile file, PackageImportResolutionsRequest? resolutions = null, CancellationToken ct = default)
    {
        using var content = BuildImportForm(file, resolutions);
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/workspaces/{workspaceId}/packages/import")
        {
            Content = content
        };
        using var response = await SendAsync(request, ct);
        return (await ReadJsonAsync<PackageImportResponse>(response, ct))!;
    }

    private static MultipartFormDataContent BuildImportForm(IBrowserFile file, PackageImportResolutionsRequest? resolutions)
    {
        var content = new MultipartFormDataContent();
        var stream = file.OpenReadStream(1_073_741_824);
        var fileContent = new StreamContent(stream);
        fileContent.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(string.IsNullOrWhiteSpace(file.ContentType) ? "application/json" : file.ContentType);
        content.Add(fileContent, "file", file.Name);
        if (resolutions is not null)
        {
            content.Add(new StringContent(System.Text.Json.JsonSerializer.Serialize(resolutions, new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web))), "resolutions");
        }

        return content;
    }

    public async Task<FileDownloadResponse> ExportAsync(Guid workspaceId, Guid templateId, string packageNamespace, string id, string version, CancellationToken ct = default)
    {
        var url = $"/api/v1/workspaces/{workspaceId}/packages/export?templateIds={Uri.EscapeDataString(templateId.ToString())}&packageNamespace={Uri.EscapeDataString(packageNamespace)}&id={Uri.EscapeDataString(id)}&version={Uri.EscapeDataString(version)}";
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        // SendAsync already runs EnsureSuccessAsync (throwing ProblemDetailsException on non-success), so no
        // separate success check is needed before reading the raw byte content below.
        using var response = await SendAsync(request, ct);

        var content = await response.Content.ReadAsByteArrayAsync(ct);
        var disposition = response.Content.Headers.ContentDisposition;
        var fileName = disposition?.FileNameStar ?? disposition?.FileName?.Trim('"') ?? $"{packageNamespace}.{id}@{version}.ctp";
        var contentType = response.Content.Headers.ContentType?.MediaType ?? "application/octet-stream";
        return new FileDownloadResponse(fileName, contentType, content);
    }
}
