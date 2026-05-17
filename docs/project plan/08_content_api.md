# 08 — Content API

## Goal
Implement the full content item API: CRUD, lifecycle transitions, scheduled publishing, field value storage, slug/tag/locale management, and translation group support.

---

## Content Item Structure Recap

A `ContentItem` is an instance of a `TemplateVersion`. Its data is stored as a list of `ContentFieldValue` rows — one per field occurrence (multi-occurrence fields get multiple rows with ascending `Order`).

For fields whose type is another Template (`ChildContentItemId`), the child is itself a full `ContentItem`. Inline children are owned; referenced children are independent.

All endpoints in this document follow the API conventions in `25_cross_cutting.md`: URL prefix `/api/v1/`, RFC 7807 ProblemDetails error contract, and `ETag` / `If-Match` concurrency on updates. Soft-deleted items are excluded from all list and get responses by default.

---

## Endpoints

### `GET /api/v1/workspaces/{workspaceId}/content`
List content items. Supports rich filtering (see Query section). Paged. Returns summary projection (no field values).

### `POST /api/v1/workspaces/{workspaceId}/content`
Create a new content item in `Draft` status.
Request:
```json
{
  "templateVersionId": "...",
  "slug": "my-first-post",          // optional
  "localeCode": "en",               // optional
  "translationGroupId": null,       // optional; link to existing group
  "tags": ["blog", "featured"],
  "fields": [
    {
      "fieldId": "...",
      "order": 0,
      "valueKind": "Text",
      "textValue": "Hello World"
    },
    {
      "fieldId": "...",
      "order": 0,
      "valueKind": "ChildContent",
      "childContentItemId": "..."    // for Reference fields
    }
  ]
}
```

On save: validate field values against the `TemplateVersion` via `IContentValidator`.
Requires `Editor`.

### `GET /api/v1/workspaces/{workspaceId}/content/{id}`
Get full content item with all field values and nested inline children (recursively resolved).

### `GET /api/v1/workspaces/{workspaceId}/content/by-slug/{slug}`
Look up content item by slug within the workspace.

### `PUT /api/v1/workspaces/{workspaceId}/content/{id}`
Update field values. Only allowed when status is `Draft` or `Review`.
Full replacement of field values — client sends the complete field set.
Requires `Editor`. Requires `If-Match` header (412 on mismatch).

### `DELETE /api/v1/workspaces/{workspaceId}/content/{id}`
Soft-deletes the content item. Cascades to owned inline children (also soft-deleted). Only `Draft` or `Archived` items may be deleted. Returns `409` (ProblemDetails type `conflict`) for `Published` items.

**Reference guard:** if any *other* `ContentItem` references this item via a `CompositionMode = Reference` field, the request fails with `409` (ProblemDetails type `referenced-by-other-entity`) and `extensions.referencedBy` listing the referencing content item IDs. The caller must first remove or update those references.

Requires `Editor` and `If-Match`.

### `POST /api/v1/workspaces/{workspaceId}/content/{id}/upgrade-version`
See `07_template_api.md` — re-pins the content item to the latest published `TemplateVersion` of its template.

---

## Lifecycle Endpoints

### `POST /api/v1/workspaces/{workspaceId}/content/{id}/submit`
Transition `Draft → Review`. Requires `Editor`.

### `POST /api/v1/workspaces/{workspaceId}/content/{id}/approve`
Transition `Review → Approved`. Requires `TemplateAdmin` or `Admin`.

### `POST /api/v1/workspaces/{workspaceId}/content/{id}/reject`
Transition `Review → Draft` (send back). Body: `{ reason: string }`. Reason stored in audit log.
Requires `TemplateAdmin` or `Admin`.

### `POST /api/v1/workspaces/{workspaceId}/content/{id}/publish`
Transition `Approved → Published` immediately.
Optionally schedule: body `{ publishAt: "2025-09-01T09:00:00Z" }` — sets `PublishAt` and leaves in `Approved` status; hosted service handles the actual transition.
Requires `Editor`.

### `POST /api/v1/workspaces/{workspaceId}/content/{id}/archive`
Transition `Published → Archived`. Requires `Editor`.

### `POST /api/v1/workspaces/{workspaceId}/content/{id}/restore`
Transition `Archived → Draft`. Requires `Editor`.

---

## Content Query (MVP Scope)

`GET /api/v1/workspaces/{workspaceId}/content` accepts:

| Parameter | Type | Description |
|-----------|------|-------------|
| `q` | string | Full-text search across title field and searchable primitive values (uses `ContentItem.SearchVector` Postgres tsvector) |
| `templateVersionId` | Guid | Filter by specific version |
| `templateId` | Guid | Filter by any version of a template |
| `status` | string | `Draft`, `Review`, `Approved`, `Published`, `Archived` |
| `localeCode` | string | e.g. `en`, `fr-CA` |
| `translationGroupId` | Guid | All locale variants of one logical item |
| `slug` | string | Exact slug match |
| `tags` | string (comma-separated) | Items must have ALL listed tags |
| `createdAfter` | ISO datetime | |
| `createdBefore` | ISO datetime | |
| `publishedAfter` | ISO datetime | |
| `publishedBefore` | ISO datetime | |
| `sortBy` | string | `createdAt` \| `updatedAt` \| `publishedAt` \| `slug` \| `relevance` (only valid with `q`) |
| `sortDesc` | bool | Default `true` |
| `page` | int | Default 1 |
| `pageSize` | int | Default 20, max 100 |

**Field-value filtering is explicitly out of MVP scope.**

---

## Content Validation

Implemented in `Cmsify.Core` as `IContentValidator`. Runs on create and update.

**Checks:**
1. All `IsRequired` fields have at least `MinOccurrences` values
2. No field exceeds `MaxOccurrences` values
3. Each `ContentFieldValue.ValueKind` matches the field's declared type
4. For `ChildContent` values: if `IsOpen = false`, the child content item's template must be in the field's `AllowedTypes`
5. For `Reference` fields: the referenced `ContentItem` must exist
6. For `Inline` fields: the provided inline data must itself pass validation recursively

---

## Tags

Tags are workspace-scoped. Creating a content item with a tag that doesn't exist auto-creates the tag. Tags are case-insensitive and stored lowercase.

### `GET /api/v1/workspaces/{workspaceId}/tags`
List all tags in workspace. Includes usage count.

### `DELETE /api/v1/workspaces/{workspaceId}/tags/{id}`
Delete tag (and all `ContentItemTag` associations). Requires `Admin`.

---

## Locale & Translation Groups

- `localeCode` is a free-form BCP-47 string on the content item (no enforcement of valid locales in MVP — let the operator decide)
- `translationGroupId` is a `Guid` that links locale variants; operators generate this themselves or use the API helper:

### `POST /api/v1/workspaces/{workspaceId}/content/{id}/link-translation`
Body: `{ targetContentItemId: "..." }` — creates a shared `TranslationGroupId` between the two items (or adds the target to the source's existing group).

### `GET /api/v1/workspaces/{workspaceId}/content/{id}/translations`
Returns all content items in the same `TranslationGroupId`.

---

## Response Models

### ContentItemSummary (list response)
```json
{
  "id": "...",
  "templateVersionId": "...",
  "templateName": "Blog Post",
  "status": "Published",
  "slug": "my-first-post",
  "localeCode": "en",
  "translationGroupId": "...",
  "tags": ["blog", "featured"],
  "createdAt": "...",
  "updatedAt": "...",
  "publishedAt": "..."
}
```

### ContentItemDetail (single-item response)
Extends summary with:
```json
{
  "fields": [
    {
      "fieldId": "...",
      "key": "title",
      "label": "Title",
      "order": 0,
      "valueKind": "Text",
      "textValue": "Hello World"
    },
    {
      "fieldId": "...",
      "key": "author",
      "order": 0,
      "valueKind": "ChildContent",
      "child": { /* nested ContentItemDetail */ }
    }
  ]
}
```

---

## Tasks

- [ ] Implement `ContentController` with all CRUD endpoints
- [ ] Implement all lifecycle transition endpoints
- [ ] Implement `IContentValidator` in Core
- [ ] Implement `ContentItemQuery` filtering in `ContentItemRepository`
- [ ] Implement tag auto-create on content save
- [ ] Implement `TagsController` (list + delete)
- [ ] Implement translation group link endpoint and translations query
- [ ] Implement slug uniqueness enforcement (per workspace + template type)
- [ ] Implement recursive inline child resolution for detail response
- [ ] Implement `ScheduledPublishingService` hook (fires after lifecycle transition to Published)
- [ ] Implement webhook event emission on `content.published`, `content.updated`, `content.deleted`, `content.archived`
- [ ] Define all request/response DTOs
- [ ] Unit test: `IContentValidator` (all validation rule cases)
- [ ] Unit test: lifecycle transition guard (all valid/invalid paths)
- [ ] Integration test: full content lifecycle (create → submit → approve → publish → archive)
- [ ] Integration test: scheduled publish (set `PublishAt` in the past, trigger hosted service, verify status)
- [ ] Integration test: content query filtering (each filter parameter)
- [ ] Integration test: translation group linking and locale query

---

## Deliverables
- Full content item CRUD and lifecycle API
- Content validation enforcing template field constraints
- Scheduled publishing via hosted service
- Tag management endpoints
- Locale and translation group support
- Webhook events emitted on lifecycle transitions
- All endpoints covered by integration tests
