# 09 — Media API

## Goal
Implement file/media upload, storage routing, and asset management endpoints.

All media access — including the file-stream endpoint — requires an authenticated caller (`Reader` or higher, or an `ApiClient` token). Cmsify does **not** serve media anonymously to end users; consumers are expected to fetch via their server-side credentials and re-host or proxy media themselves. See the Architecture Decision Register in `00_index.md`.

All endpoints in this document follow the API conventions in `25_cross_cutting.md`: URL prefix `/api/v1/`, RFC 7807 ProblemDetails, and `ETag` / `If-Match` on updates.

---

## MediaAsset Lifecycle
1. Client uploads file via multipart form POST
2. API validates MIME type and file size against config limits
3. File is streamed to `IStorageProvider`; a `StoredFile` record is returned
4. `MediaAsset` row is created in DB with metadata + storage key
5. Client receives the `MediaAsset` ID and a retrieval URL
6. Content field values reference `MediaAsset` by ID (`MediaAssetId` on `ContentFieldValue`)

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
Soft-deletes the asset (`IsDeleted = true`); the file remains in storage until a future retention sweep. Returns `409` (ProblemDetails type `referenced-by-other-entity`) if the asset is referenced by any non-soft-deleted content item, with `extensions.referencedBy` listing the referencing content item IDs.

Requires `Editor` and `If-Match`.

---

## Tasks

- [ ] Implement `MediaController` with all endpoints
- [ ] Implement multipart upload with streaming to `IStorageProvider`
- [ ] Implement MIME type and file size validation (configurable)
- [ ] Implement file stream endpoint with proper headers
- [ ] Implement delete with reference check
- [ ] Add `Media:MaxFileSizeMb` and `Media:AllowedMimeTypes` to `.env.example`
- [ ] Integration test: upload → retrieve → delete flow
- [ ] Integration test: delete blocked by published content reference

---

## Deliverables
- File upload and retrieval working with local storage provider
- S3 provider swap verified via config change
- Reference-guarded delete
