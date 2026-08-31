using Cmsify.Core.Domain.Enums;

namespace Cmsify.Core.Interfaces.Repositories;

public interface IWorkspaceRepository
{
    Task<WorkspaceDto?> GetAsync(Guid id, CancellationToken ct = default);

    Task<PagedResult<WorkspaceDto>> ListAsync(PageRequest page, CancellationToken ct = default);

    Task<WorkspaceDto> CreateAsync(CreateWorkspaceCommand command, CancellationToken ct = default);

    Task<WorkspaceDto> UpdateAsync(UpdateWorkspaceCommand command, CancellationToken ct = default);

    Task SoftDeleteAsync(Guid id, Guid actorUserId, CancellationToken ct = default);
}

public interface ITemplateRepository
{
    Task<TemplateDto?> GetAsync(Guid id, CancellationToken ct = default);

    Task<PagedResult<TemplateDto>> ListByWorkspaceAsync(Guid workspaceId, PageRequest page, CancellationToken ct = default);

    Task<TemplateDto> CreateAsync(CreateTemplateCommand command, CancellationToken ct = default);

    Task<TemplateDto> UpdateAsync(UpdateTemplateCommand command, CancellationToken ct = default);

    Task SoftDeleteAsync(Guid id, Guid actorUserId, CancellationToken ct = default);
}

public interface ITemplateVersionRepository
{
    Task<TemplateVersionDto?> GetAsync(Guid id, CancellationToken ct = default);

    Task<IReadOnlyList<TemplateVersionDto>> ListByTemplateAsync(Guid templateId, CancellationToken ct = default);

    Task<TemplateVersionDto> CreateDraftAsync(CreateTemplateVersionCommand command, CancellationToken ct = default);

    Task SaveStructureAsync(SaveTemplateVersionStructureCommand command, CancellationToken ct = default);

    Task<TemplateVersionDto> PublishAsync(Guid id, Guid actorUserId, CancellationToken ct = default);
}

public interface IContentItemRepository
{
    Task<ContentItemDto?> GetAsync(Guid id, CancellationToken ct = default);

    Task<PagedResult<ContentItemDto>> QueryAsync(ContentQuery query, CancellationToken ct = default);

    Task<ContentItemDto> CreateAsync(CreateContentItemCommand command, CancellationToken ct = default);

    Task<ContentItemDto> UpdateAsync(UpdateContentItemCommand command, CancellationToken ct = default);

    Task<ContentItemDto> SetStatusAsync(Guid id, ContentStatus status, Guid actorUserId, CancellationToken ct = default);

    Task<IReadOnlyList<ContentItemDto>> GetPendingScheduledPublishAsync(DateTimeOffset now, int limit = 100, CancellationToken ct = default);

    Task SoftDeleteAsync(Guid id, Guid actorUserId, CancellationToken ct = default);
}

public interface IMediaAssetRepository
{
    Task<MediaAssetDto?> GetAsync(Guid id, CancellationToken ct = default);

    Task<PagedResult<MediaAssetDto>> ListByWorkspaceAsync(Guid workspaceId, PageRequest page, CancellationToken ct = default);

    Task<MediaAssetDto> CreateAsync(CreateMediaAssetCommand command, CancellationToken ct = default);

    Task<MediaAssetDto> UpdateAsync(UpdateMediaAssetCommand command, CancellationToken ct = default);

    Task SoftDeleteAsync(Guid id, Guid actorUserId, CancellationToken ct = default);
}

public interface ITagRepository
{
    Task<IReadOnlyList<TagDto>> ListByWorkspaceAsync(Guid workspaceId, CancellationToken ct = default);

    Task<TagDto> UpsertAsync(UpsertTagCommand command, CancellationToken ct = default);

    Task DeleteAsync(Guid id, CancellationToken ct = default);
}

public interface IUserRepository
{
    Task<UserDto?> GetAsync(Guid id, CancellationToken ct = default);

    Task<UserDto?> GetByEmailAsync(string email, CancellationToken ct = default);

    Task<PagedResult<UserDto>> ListAsync(PageRequest page, CancellationToken ct = default);

    Task<UserDto> CreateAsync(CreateUserCommand command, string passwordHash, CancellationToken ct = default);

    Task<UserDto> UpdateAsync(UpdateUserCommand command, CancellationToken ct = default);

    Task SoftDeleteAsync(Guid id, Guid actorUserId, CancellationToken ct = default);
}

public interface IApiClientRepository
{
    Task<ApiClientDto?> GetAsync(Guid id, CancellationToken ct = default);

    Task<PagedResult<ApiClientDto>> ListAsync(PageRequest page, CancellationToken ct = default);

    Task<ApiClientDto> CreateAsync(CreateApiClientCommand command, string tokenHash, string tokenIdentifier, CancellationToken ct = default);

    Task<ApiClientDto> UpdateAsync(UpdateApiClientCommand command, CancellationToken ct = default);

    Task SoftDeleteAsync(Guid id, Guid actorUserId, CancellationToken ct = default);
}

public interface IWebhookRepository
{
    Task<WebhookEndpointDto?> GetEndpointAsync(Guid id, CancellationToken ct = default);

    Task<PagedResult<WebhookEndpointDto>> ListEndpointsAsync(Guid workspaceId, PageRequest page, CancellationToken ct = default);

    Task<WebhookEndpointDto> CreateEndpointAsync(CreateWebhookEndpointCommand command, string encryptedSecret, CancellationToken ct = default);

    Task<WebhookEndpointDto> UpdateEndpointAsync(UpdateWebhookEndpointCommand command, CancellationToken ct = default);

    Task AddDeliveryLogAsync(WebhookDeliveryLogDto log, CancellationToken ct = default);

    Task<PagedResult<WebhookDeliveryLogDto>> ListDeliveryLogsAsync(Guid endpointId, PageRequest page, CancellationToken ct = default);

    Task<IReadOnlyList<WebhookDispatchTargetDto>> GetActiveEndpointsForEventAsync(string eventType, Guid? workspaceId, CancellationToken ct = default);

    Task<IReadOnlyList<PendingWebhookDeliveryDto>> ClaimPendingDeliveryLogsAsync(string workerId, DateTimeOffset now, TimeSpan leaseDuration, int limit, CancellationToken ct = default);

    Task<IReadOnlyList<ClaimedWebhookOutboxEventDto>> ClaimOutboxEventsAsync(string workerId, DateTimeOffset now, TimeSpan leaseDuration, int limit, CancellationToken ct = default);

    Task<bool> MaterializeOutboxEventAsync(ClaimedWebhookOutboxEventDto claim, DateTimeOffset now, CancellationToken ct = default);

    Task<WebhookRetentionCleanupResult> CleanupRetentionAsync(DateTimeOffset olderThan, int batchSize, CancellationToken ct = default);

    Task<bool> CompleteDeliverySucceededAsync(WebhookDeliveryCompletionDto completion, int statusCode, CancellationToken ct = default);

    Task<bool> CompleteDeliveryFailedAsync(WebhookDeliveryCompletionDto completion, int? statusCode, string? error, DateTimeOffset? nextRetryAt, bool isDeadLetter, CancellationToken ct = default);
}

public interface IAuditLogRepository
{
    Task<PagedResult<AuditLogDto>> QueryAsync(AuditLogQuery query, CancellationToken ct = default);

    Task AppendAsync(AuditLogDto log, CancellationToken ct = default);
}

public interface IScheduledPublishingRepository
{
    Task<IReadOnlyList<ScheduledContentClaimDto>> ClaimDueContentAsync(string workerId, DateTimeOffset now, TimeSpan leaseDuration, int limit, CancellationToken ct = default);

    Task<bool> CompleteClaimAsync(ScheduledContentClaimDto claim, DateTimeOffset now, CancellationToken ct = default);
}
