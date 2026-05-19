# 11 — Webhook API

## Goal
Implement webhook endpoint registration, event subscription, delivery, HMAC signing, and delivery log management.

All endpoints follow the API conventions in `25_cross_cutting.md`: URL prefix `/api/v1/`, RFC 7807 ProblemDetails, `ETag` / `If-Match` on updates.

---

## Event Types

| Event | Fired When |
|-------|-----------|
| `content.created` | Content item created |
| `content.updated` | Content item field values updated |
| `content.status_changed` | Any lifecycle transition |
| `content.published` | Content item transitions to Published |
| `content.archived` | Content item transitions to Archived |
| `content.deleted` | Content item deleted |
| `content.rolled_back` | Content item rolled back to a prior version snapshot |
| `template.version_published` | A template version is published |
| `workspace.updated` | Workspace metadata changed |

---

## Webhook Payload Shape

All events share a common envelope:
```json
{
  "id": "...",
  "eventType": "content.published",
  "workspaceId": "...",
  "occurredAt": "2025-09-01T09:00:00Z",
  "data": {
    // event-specific payload; for content events: ContentItemSummary
  }
}
```

Delivery header: `X-Cmsify-Signature: sha256={hmac_hex}` computed over the raw JSON body using the endpoint's secret.

---

## Endpoints

### `GET /api/v1/workspaces/{workspaceId}/webhooks`
List webhook endpoints for the workspace.

### `POST /api/v1/workspaces/{workspaceId}/webhooks`
Register a new endpoint.
Request:
```json
{
  "name": "My Site Revalidation",
  "url": "https://mysite.com/api/revalidate",
  "secret": "my-signing-secret",
  "events": ["content.published", "content.archived"]
}
```
Secret is stored encrypted at rest (AES-256, key from `Secrets:EncryptionKey` config).
Requires `Editor`.

### `GET /api/v1/workspaces/{workspaceId}/webhooks/{id}`
Get endpoint details (secret is never returned in responses).

### `PUT /api/v1/workspaces/{workspaceId}/webhooks/{id}`
Update URL, name, events, active status. To rotate secret: use dedicated rotate endpoint.
Requires `Editor`.

### `POST /api/v1/workspaces/{workspaceId}/webhooks/{id}/rotate-secret`
Issues a new secret, returns it **once**. Existing in-flight deliveries using the old secret will fail — caller must update their receiver.
Requires `Editor`.

### `DELETE /api/v1/workspaces/{workspaceId}/webhooks/{id}`
Remove endpoint and all associated delivery logs.
Requires `Editor`.

### `GET /api/v1/workspaces/{workspaceId}/webhooks/{id}/deliveries`
List delivery log entries for this endpoint. Filter: `?isDelivered=false&isFailed=true`. Paged.

### `POST /api/v1/workspaces/{workspaceId}/webhooks/{id}/deliveries/{deliveryId}/retry`
Manually re-queue a failed delivery.
Requires `Editor`.

---

## Delivery & Retry

See `04_infrastructure.md` for `WebhookDispatchService` and `WebhookRetryService` implementation details.

**Backoff schedule (default):** 30s, 2m, 10m, 30m, 2h, 6h, 24h — then marked `IsFailed = true`.

---

## Tasks

- [x] Implement `WebhooksController` with all endpoints
- [x] Implement secret encryption at rest (AES-256)
- [x] Implement secret rotation endpoint
- [x] Implement `WebhookEvent` emission from all relevant service methods
- [x] Implement delivery log list and manual retry endpoints
- [x] Add `Secrets:EncryptionKey` to `.env.example`
- [x] Integration test: register endpoint → trigger event → verify delivery log
- [x] Integration test: HMAC signature verification
- [x] Integration test: retry flow (simulate failed delivery, trigger retry)

---

## Deliverables
- [x] Webhook registration and management endpoints
- [x] Event emission wired to all content and template lifecycle transitions
- [x] HMAC-signed delivery with retry and delivery log

---

---

# 12 — Audit API

## Goal
Expose the audit log as a queryable API so admins and template admins can inspect the full change history.

All endpoints follow the API conventions in `25_cross_cutting.md`: URL prefix `/api/v1/`, RFC 7807 ProblemDetails.

---

## Audit Log Recap
- Written by `AuditInterceptor` on every `SaveChangesAsync`
- Records: entity type, entity ID, action, actor (user or API client), timestamp, JSONB change delta
- Append-only — no update or delete operations on audit log rows
- Retention / archival policy is **out of MVP scope**; the audit log grows unbounded until a retention sweep is added post-MVP

---

## Endpoints

### `GET /api/v1/audit`
Query audit log across all workspaces the actor has access to. Requires `TemplateAdmin` or `Admin`.

### `GET /api/v1/workspaces/{workspaceId}/audit`
Query audit log scoped to a workspace. Requires `TemplateAdmin`.

Both support filters:

| Parameter | Description |
|-----------|-------------|
| `entityType` | e.g. `ContentItem`, `Template`, `User` |
| `entityId` | Specific entity GUID |
| `action` | `Created`, `Updated`, `Deleted`, `StatusChanged` |
| `actorUserId` | Filter by user actor |
| `actorApiClientId` | Filter by API client actor |
| `after` | ISO datetime |
| `before` | ISO datetime |
| `page` | Default 1 |
| `pageSize` | Default 50, max 200 |

Response item:
```json
{
  "id": "...",
  "entityType": "ContentItem",
  "entityId": "...",
  "action": "StatusChanged",
  "actor": {
    "type": "User",
    "id": "...",
    "displayName": "Jane Smith"
  },
  "timestamp": "2025-09-01T10:30:00Z",
  "workspaceId": "...",
  "changeDelta": {
    "status": { "from": "Review", "to": "Approved" }
  }
}
```

---

## Tasks

- [x] Implement `AuditController` with workspace-scoped and global endpoints
- [x] Implement `IAuditLogRepository` filtering and pagination
- [x] Verify `AuditInterceptor` capturing all entity types correctly
- [x] Integration test: create content item → verify audit log entry exists with correct delta
- [x] Integration test: lifecycle transition → verify `StatusChanged` entry with before/after status

---

## Deliverables
- [x] Audit log queryable via API with full filter support
- [x] Change delta correctly capturing before/after state for updates
- [x] Access correctly restricted to `TemplateAdmin` and `Admin`
