# 10 — Query API

## Goal
Define and document the content query surface available to API consumers. This is the primary read interface for external applications consuming Cmsify content.

---

## Scope (MVP)

The MVP query API filters and sorts on **content item metadata** plus a full-text `q` parameter backed by a denormalised Postgres `tsvector` column (`ContentItem.SearchVector`, refreshed on every save — see `03_database_schema.md`). Field-value filtering (e.g. "all posts where author = 'Jane'") is explicitly out of scope and planned as a post-MVP phase.

All consumers must authenticate. There is no anonymous delivery API in MVP — see `00_index.md`.

---

## Primary Query Endpoint

`GET /api/v1/workspaces/{workspaceId}/content` (documented fully in `08_content_api.md`)

This endpoint is the query surface. All filter, sort, and pagination parameters are defined there.

---

## Consumer Patterns

### Pattern 1 — Fetch all published content of a template type
```
GET /api/v1/workspaces/{wsId}/content
  ?templateId={blogPostTemplateId}
  &status=Published
  &sortBy=publishedAt
  &sortDesc=true
  &pageSize=10
  &page=1
```

### Pattern 2 — Fetch a single item by slug
```
GET /api/v1/workspaces/{wsId}/content/by-slug/my-first-post
```

### Pattern 3 — Fetch all locale variants of a content item
```
GET /api/v1/workspaces/{wsId}/content/{id}/translations
```

### Pattern 4 — Fetch content published in a date range
```
GET /api/v1/workspaces/{wsId}/content
  ?status=Published
  &publishedAfter=2025-01-01T00:00:00Z
  &publishedBefore=2025-06-01T00:00:00Z
```

### Pattern 5 — Fetch content by tag
```
GET /api/v1/workspaces/{wsId}/content
  ?tags=featured,blog
  &status=Published
```

### Pattern 6 — Full-text search
```
GET /api/v1/workspaces/{wsId}/content
  ?q=postgres+performance
  &status=Published
  &sortBy=relevance
```

---

## Pagination

MVP uses **offset pagination**:
- Request: `?page=1&pageSize=20`
- Response envelope:
```json
{
  "items": [...],
  "totalCount": 142,
  "page": 1,
  "pageSize": 20,
  "totalPages": 8
}
```

Cursor-based pagination is planned post-MVP for large datasets and real-time consumer use cases.

---

## Post-MVP: Field-Value Search

When implemented, field-value filtering will allow queries like:
```
GET /api/v1/workspaces/{wsId}/content
  ?templateId={blogPostTemplateId}
  &field[author.textValue]=Jane
  &field[featured.boolValue]=true
```

This requires querying into `ContentFieldValues` joined with `TemplateFields` — a non-trivial SQL operation on an EAV structure. The schema is already designed to support this; it is a query-layer concern only.

---

## Tasks

- [x] Ensure all query parameters on `ContentController` list endpoint are implemented
- [x] Verify query performance with realistic data volumes (add indexes as needed — see `03_database_schema.md`)
- [x] Document all consumer patterns in OpenAPI descriptions
- [x] Add query parameter examples to Swagger UI
- [x] Integration test: each filter parameter in isolation and in combination

---

## Deliverables
- [x] All MVP query parameters working and documented in OpenAPI
- [x] Pagination envelope consistent across all list endpoints
- [x] Consumer pattern documentation in OpenAPI descriptions
