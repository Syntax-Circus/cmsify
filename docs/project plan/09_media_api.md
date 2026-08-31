# 09 — Media API

## Goal
Implement file/media upload, storage routing, and asset management endpoints.

All media access — including the file-stream endpoint — requires an authenticated caller (`Reader` or higher, or an `ApiClient` token). Cmsify does **not** serve media anonymously to end users; consumers are expected to fetch via their server-side credentials and re-host or proxy media themselves. See the Architecture Decision Register in `00_index.md`.

All endpoints in this document follow the API conventions in `25_cross_cutting.md`: URL prefix `/api/v1/`, RFC 7807 ProblemDetails, and `ETag` / `If-Match` on updates.

---

## MediaAsset lifecycle

1. The API validates the multipart upload and allocates the final asset ID and deterministic key: `cmsify/media/{workspaceId}/{yyyy}/{MM}/{assetId}_{safeFileName}`.
2. It commits a `PendingUpload` row before sending bytes to storage. Only `Available` rows are visible through list, metadata, and file endpoints.
3. Storage receives that exact key. A successful write transitions the row to `Available`; a failed or canceled write best-effort transitions it to `UploadFailed` and queues immediate idempotent cleanup.
4. Reconciliation marks a `PendingUpload` older than 30 minutes failed, verifies bounded batches of `Available`/`Missing` objects through metadata, and scans only `cmsify/media/` and legacy `default/` for orphans older than 24 hours.
5. Delete performs reference and `If-Match` checks, then atomically soft-deletes the row, transitions it to `DeletePending`, and creates a durable deletion intent due after 30 days. Until purge, an operator can recover the matched database/blob state using the operations runbook.
6. Replicas claim deletion and scan work with fenced PostgreSQL leases. Failed deletes use capped exponential retry; stale owners cannot complete reclaimed work.
7. Content field values reference `MediaAsset` by ID (`MediaAssetId` on `ContentFieldValue`).

---

## Endpoints

### `POST /api/v1/workspaces/{workspaceId}/media`
Upload a file. Multipart form data: `file` (binary) + optional `altText`.

**Validation:**
- Max file size: configurable via `Media:MaxFileSizeMb` (default 50MB)
- Allowed MIME types: configurable via `Media:AllowedMimeTypes` (default: common image/audio/video/doc types)

Response:
```json
{
  "id": "...",
  "fileName": "hero.jpg",
  "mimeType": "image/jpeg",
  "sizeBytes": 204800,
  "altText": "A hero image",
  "url": "/api/v1/workspaces/{workspaceId}/media/{id}/file",
  "createdAt": "..."
}
```

### `GET /api/v1/workspaces/{workspaceId}/media`
List media assets. Filter: `?mimeType=image/&search={filename}`. Paged.

### `GET /api/v1/workspaces/{workspaceId}/media/{id}`
Get asset metadata.

### `GET /api/v1/workspaces/{workspaceId}/media/{id}/file`
Stream the file bytes from the storage provider. Sets `Content-Type` and `Content-Disposition` headers.

### `PUT /api/v1/workspaces/{workspaceId}/media/{id}`
Update `altText` only.

### `DELETE /api/v1/workspaces/{workspaceId}/media/{id}`
Atomically soft-deletes the asset (`IsDeleted = true`), marks it `DeletePending`, and creates a durable deletion intent due after the configured 30-day recovery window. Returns `409` (ProblemDetails type `referenced-by-other-entity`) if the asset is referenced by any non-soft-deleted content item, with `extensions.referencedBy` listing the referencing content item IDs.

Requires `Editor` and `If-Match`.

---

## Tasks

- [x] Implement `MediaController` with all endpoints
- [x] Implement multipart upload with streaming to `IStorageProvider`
- [x] Implement MIME type and file size validation (configurable)
- [x] Implement file stream endpoint with proper headers
- [x] Implement delete with reference check
- [x] Add `Media:MaxFileSizeMb` and `Media:AllowedMimeTypes` to `.env.example`
- [x] Integration test: upload → retrieve → delete flow
- [x] Integration test: delete blocked by published content reference
- [x] Persist database-first upload state and deterministic storage keys
- [x] Add durable retention tombstones, leased deletion retries, missing-blob verification, and managed-prefix orphan reconciliation
- [x] Cover database/storage failures, visibility states, disposal, authorization, concurrency, restart recovery, and replica-safe claims

---

## Deliverables
- [x] File upload and retrieval working with local storage provider
- [x] S3 provider swap verified via config change
- [x] Reference-guarded delete
- [x] Local and S3-compatible metadata/list/disposal support through `SyntaxCircus.Storage`
- [x] Operator recovery window and reconciliation runbook
