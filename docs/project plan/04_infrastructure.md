# 04 — Infrastructure

## Goal
Implement all infrastructure concerns in `Cmsify.Infrastructure`: repository implementations, storage abstraction, background hosted services, and the audit interceptor.

---

## Repository Implementations

Each repository implements its corresponding interface from `Cmsify.Core`. All use `CmsifyDbContext` via constructor injection.

### Conventions
- All database access stays inside repository implementations; no caller reaches into `DbContext` or EF entities directly
- Repository methods accept DTO inputs and return DTO outputs only; EF entities never cross the repository boundary
- Read queries use `.AsNoTracking()` by default and only opt into tracking when a write workflow genuinely requires it
- Write operations call `SaveChangesAsync()` within the repository method
- Pagination uses `Skip` / `Take` with a standard `PagedResult<T>` wrapper
- All `Guid` IDs generated in application code via `UUIDNext.Uuid.NewDatabaseFriendly(Database.PostgreSql)` before insert

```csharp
public record PagedResult<T>(IReadOnlyList<T> Items, int TotalCount, int Page, int PageSize);
```

### Key Repository Patterns

**TemplateRepository**
- `GetByIdWithVersionAsync(Guid templateId, int? version = null)` — fetches template + specific version (or latest) with sections and fields eagerly loaded
- `ListAsync(Guid workspaceId, ...)` — workspace-scoped list with filter/sort/page
- `SaveVersionAsync(TemplateVersion version)` — saves a new version row; updates `Template.CurrentVersionId`

**ContentItemRepository**
- `GetByIdAsync(Guid id, bool includeFieldValues = true)` — optionally loads `ContentFieldValues` (can be large)
- `GetBySlugAsync(Guid workspaceId, string slug)` — slug lookup within workspace
- `ListAsync(ContentItemQuery query)` — accepts the query filter model; returns paged results
- `GetPendingScheduledPublishAsync()` — returns `Approved` items where `PublishAt <= UtcNow`

**ContentItemQuery model**
```csharp
public record ContentItemQuery(
    Guid WorkspaceId,
    Guid? TemplateVersionId,
    ContentStatus? Status,
    string? LocaleCode,
    Guid? TranslationGroupId,
    string? Slug,
    IReadOnlyList<string>? Tags,
    DateTimeOffset? CreatedAfter,
    DateTimeOffset? CreatedBefore,
    DateTimeOffset? PublishedAfter,
    DateTimeOffset? PublishedBefore,
    string? SortBy,        // "createdAt" | "updatedAt" | "publishedAt" | "slug"
    bool SortDescending,
    int Page,
    int PageSize
);
```

**WebhookRepository**
- `GetActiveEndpointsForEventAsync(string eventType)` — returns all active endpoints subscribed to a given event type
- `GetPendingDeliveryLogsAsync()` — returns undelivered, non-failed logs where `NextRetryAt <= UtcNow`

---

## Storage Abstraction

### Interface (defined in Core)

```csharp
// Cmsify.Core/Interfaces/Services/IStorageProvider.cs
public interface IStorageProvider
{
    Task<StoredFile> StoreAsync(Stream content, string fileName, string mimeType, CancellationToken ct = default);
    Task<Stream> RetrieveAsync(string storageKey, CancellationToken ct = default);
    Task DeleteAsync(string storageKey, CancellationToken ct = default);
    Task<bool> ExistsAsync(string storageKey, CancellationToken ct = default);
}

public record StoredFile(string StorageKey, string Provider, long SizeBytes);
```

### LocalFileSystemStorageProvider
- Stores files under a configurable base path (e.g. `/var/cmsify/media`)
- `StorageKey` = relative path from base: `{workspaceId}/{year}/{month}/{uuid7}_{filename}`
- Config key: `Storage:Local:BasePath`

### S3BlobStorageProvider
- Uses `AWSSDK.S3` (compatible with MinIO, Backblaze B2, Cloudflare R2 via S3-compatible API)
- Config keys: `Storage:S3:BucketName`, `Storage:S3:Region`, `Storage:S3:AccessKey`, `Storage:S3:SecretKey`, `Storage:S3:ServiceUrl` (for non-AWS endpoints)
- `StorageKey` = S3 object key: `{workspaceId}/{year}/{month}/{uuid7}_{filename}`

### Provider Selection
Config-driven via `Storage:Provider` = `"local"` | `"s3"`. Registered in DI as `IStorageProvider` singleton.

```csharp
services.AddStorageProvider(configuration); // extension method reads config and registers correct impl
```

---

## Audit Interceptor

### AuditInterceptor
Implements `SaveChangesInterceptor`. Fires on every `SaveChangesAsync`.

**Logic:**
1. Before save: capture `ChangeTracker.Entries()` for all `Added`, `Modified`, `Deleted` entities
2. For `Modified`: compute before/after diff as JSONB (serialize original values vs current values for changed properties only)
3. After save: insert `AuditLog` records for all captured changes
4. Actor resolution: `IHttpContextAccessor` to pull current user/API client from the request context; falls back to null for background service operations

**Excluded from audit:** `AuditLog` itself, `WebhookDeliveryLog` (too noisy), `UserSession` tokens.

```csharp
public class AuditInterceptor : SaveChangesInterceptor
{
    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(...) { ... }
    public override ValueTask<int> SavedChangesAsync(...) { ... }
}
```

---

## Background Hosted Services

All implemented as `BackgroundService` subclasses registered in `IHostedService`. Durable work is claimed from PostgreSQL with bounded owner/token leases so replicas can process independent rows and recover expired claims.

### ScheduledPublishingService
- Polls every 60 seconds (configurable via `Scheduler:PublishingIntervalSeconds`)
- Claims due approved content through `IScheduledPublishingDispatcher`, then conditionally completes the claim in a transaction.
- Completion writes the immutable snapshot and durable `content.published` outbox event together.

### WebhookDispatchService
- Claims durable outbox events with PostgreSQL `FOR UPDATE SKIP LOCKED` and materializes one idempotent delivery intent per subscribed endpoint.
- Never sends HTTP; the retry worker sends only already-persisted, claimed intents.

### WebhookRetryService
- Polls every 30 seconds (configurable)
- Fetches `WebhookDeliveryLog` records where `IsDelivered = false`, `IsFailed = false`, `NextRetryAt <= UtcNow`
- Claims due delivery intents with leases, signs the exact payload bytes, and includes stable `X-Cmsify-Event-Id` for consumer deduplication.
- Uses exponential backoff; after `MaxAttempts` (default 10), preserves a terminal dead-letter diagnostic until manual retry.

---

## Service Registration

```csharp
// Cmsify.Infrastructure/Extensions/ServiceCollectionExtensions.cs
public static IServiceCollection AddCmsifyInfrastructure(
    this IServiceCollection services,
    IConfiguration configuration)
{
    services.AddDbContext<CmsifyDbContext>(...);
    services.AddScoped<IWorkspaceRepository, WorkspaceRepository>();
    services.AddScoped<ITemplateRepository, TemplateRepository>();
    services.AddScoped<ITemplateVersionRepository, TemplateVersionRepository>();
    services.AddScoped<IContentItemRepository, ContentItemRepository>();
    services.AddScoped<IMediaAssetRepository, MediaAssetRepository>();
    services.AddScoped<ITagRepository, TagRepository>();
    services.AddScoped<IUserRepository, UserRepository>();
    services.AddScoped<IApiClientRepository, ApiClientRepository>();
    services.AddScoped<IWebhookRepository, WebhookRepository>();
    services.AddScoped<IAuditLogRepository, AuditLogRepository>();
    services.AddStorageProvider(configuration);
    services.AddScoped<IWebhookOutbox, EfWebhookOutbox>();
    services.AddScoped<IScheduledPublishingDispatcher, ScheduledPublishingDispatcher>();
    services.AddHostedService<ScheduledPublishingService>();
    services.AddHostedService<WebhookDispatchService>();
    services.AddHostedService<WebhookRetryService>();
    return services;
}
```

---

## Tasks

- [x] Implement all repository classes in `Cmsify.Infrastructure/Persistence/Repositories/`
- [x] Implement DTO mapping at every repository boundary so EF entities remain internal
- [x] Implement `PagedResult<T>` and `ContentItemQuery` models
- [x] Implement `IStorageProvider` interface (in Core)
- [x] Implement `LocalFileSystemStorageProvider`
- [x] Implement `S3BlobStorageProvider`
- [x] Implement `AddStorageProvider` extension method (config-driven selection)
- [x] Implement `AuditInterceptor` with before/after JSONB diff
- [x] Implement `WebhookEvent` channel and `WebhookDispatchService`
- [x] Implement `WebhookRetryService`
- [x] Implement `ScheduledPublishingService`
- [x] Implement `AddCmsifyInfrastructure` service registration extension
- [x] Unit test: `LocalFileSystemStorageProvider` store/retrieve/delete
- [x] Unit test: HMAC signing in webhook dispatcher
- [x] Unit test: exponential backoff calculation
- [x] Unit test: `AuditInterceptor` delta computation

---

## Deliverables
- [x] All repositories implemented and registered with DTO-only boundaries and `AsNoTracking()`-first reads
- [x] Storage abstraction working with local filesystem; S3 provider implemented and config-switchable
- [x] Audit interceptor firing on all entity changes
- [x] All three background services running and tested
- [x] `AddCmsifyInfrastructure` one-line registration for consuming projects
