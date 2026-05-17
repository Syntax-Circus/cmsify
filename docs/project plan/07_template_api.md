# 07 — Template API

## Goal
Implement full CRUD for templates, template versions, sections, and fields — including cycle detection and version management.

---

## Key Concepts Recap
- A `Template` is a named, workspace-scoped schema
- Each change to structure happens on a `Draft` `TemplateVersion`; publishing freezes it
- `TemplateVersion.Status` is `Draft | Published | Archived` (see `02_core_domain.md`). Only one `Draft` per template at a time. Publishing a draft archives the previously-published current version.
- `TemplateSections` optionally group `TemplateFields`
- Fields are either primitive-typed or reference another Template; primitive fields carry a `FieldConfig` jsonb for per-type settings (see `02_core_domain.md`)
- Constrained fields declare `TemplateFieldAllowedTypes`; open fields accept any type
- All endpoints in this document follow the API conventions in `25_cross_cutting.md`: URL prefix `/api/v1/`, RFC 7807 ProblemDetails error contract, and `ETag` / `If-Match` concurrency on updates.

---

## Endpoints

### Templates

#### `GET /api/v1/workspaces/{workspaceId}/templates`
List templates in workspace. Supports filter: `?isSystem=false&search={name}`. Paged.

#### `POST /api/v1/workspaces/{workspaceId}/templates`
Create a new template (starts at version 1, no fields yet).
Request:
```json
{
  "name": "Blog Post",
  "slug": "blog-post",
  "description": "A standard blog post"
}
```
Requires `TemplateAdmin`.

#### `GET /api/v1/workspaces/{workspaceId}/templates/{id}`
Get template with its current version's sections and fields.

#### `PUT /api/v1/workspaces/{workspaceId}/templates/{id}`
Update template metadata (name, description) only — not structure. Structure changes go through version management.
Requires `TemplateAdmin`.

#### `DELETE /api/v1/workspaces/{workspaceId}/templates/{id}`
Delete template if no content items reference any of its versions. Returns `409 Conflict` with details if content exists.
Requires `TemplateAdmin`.

---

### Template Versions

#### `GET /api/v1/workspaces/{workspaceId}/templates/{id}/versions`
List all versions of a template. Returns version number, created date, notes, field count.

#### `POST /api/v1/workspaces/{workspaceId}/templates/{id}/versions`
Create a new `Draft` version. Fails with `409` (ProblemDetails type `conflict`) if a `Draft` already exists for this template. The new version is a **copy** of the current published version's sections and fields — a starting point to edit. Optionally supply `{ notes: string }`.
Requires `TemplateAdmin`.

#### `GET /api/v1/workspaces/{workspaceId}/templates/{id}/versions/{versionNumber}`
Get a specific version with its full structure (sections + fields + allowed types). Response carries an `ETag` (only meaningful for `Draft` versions, which are mutable).

#### `PUT /api/v1/workspaces/{workspaceId}/templates/{id}/versions/{versionNumber}/publish`
Transition the version from `Draft` → `Published`. Sets it as `Template.CurrentVersionId`. The previously-published current version (if any) transitions to `Archived`. Returns `409` if the version is not currently `Draft`.
Requires `TemplateAdmin`.

---

### Content Migration to a New Version

#### `POST /api/v1/workspaces/{workspaceId}/content/{id}/upgrade-version`
Opt-in admin action that re-pins a `ContentItem` from its current `TemplateVersion` to the template's latest `Published` version.

**Process:**
1. Look up the target latest `Published` version
2. Run `IContentValidator` against the new version's structure
3. If valid: update `TemplateVersionId`, drop any field values whose `FieldId` no longer exists in the new version (audit-logged), and `SaveChanges`
4. If invalid (required fields added, cardinality tightened, etc.): return `422` with a per-field breakdown of what would need to be fixed first

Requires `Editor`. Not performed automatically on publish — content pinning is preserved by default.

---

### Template Sections

All section operations act on an **unpublished** version only. Attempting to modify a published version returns `409 Conflict`.

#### `POST /api/v1/workspaces/{workspaceId}/templates/{id}/versions/{v}/sections`
Add a section.
Request: `{ name, description?, order, isCollapsible }`

#### `PUT /api/v1/workspaces/{workspaceId}/templates/{id}/versions/{v}/sections/{sectionId}`
Update section metadata or reorder.

#### `DELETE /api/v1/workspaces/{workspaceId}/templates/{id}/versions/{v}/sections/{sectionId}`
Delete section. Fields in the section are moved to root level (null sectionId) rather than deleted.

---

### Template Fields

#### `POST /api/v1/workspaces/{workspaceId}/templates/{id}/versions/{v}/fields`
Add a field to a version (optionally in a section). The version must be in `Draft` status.
Request:
```json
{
  "sectionId": null,
  "key": "title",
  "label": "Title",
  "helpText": "The blog post title",
  "order": 0,
  "isRequired": true,
  "minOccurrences": 1,
  "maxOccurrences": 1,
  "isOpen": false,
  "compositionMode": "Inline",
  "primitiveType": "Text",
  "templateId": null,
  "allowedTypes": [],
  "fieldConfig": { "maxLength": 200, "multiline": false }
}
```

On save:
- Run **cycle detection** across the full template graph; return `422` (ProblemDetails type `circular-template-reference`) with `extensions.cycle` if a cycle is detected
- Run `IFieldConfigValidator` against the supplied `fieldConfig` for the chosen primitive; return `422` (ProblemDetails type `validation-failed`) with `errors.fieldConfig[...]` if invalid

#### `PUT /api/v1/workspaces/{workspaceId}/templates/{id}/versions/{v}/fields/{fieldId}`
Update field. Re-run cycle detection if `templateId` or `allowedTypes` changed.

#### `DELETE /api/v1/workspaces/{workspaceId}/templates/{id}/versions/{v}/fields/{fieldId}`
Remove field from version.

#### `PUT /api/v1/workspaces/{workspaceId}/templates/{id}/versions/{v}/fields/reorder`
Bulk reorder. Body: `[{ fieldId, order }]`

---

## Cycle Detection

Implemented in `Cmsify.Core` as `ITemplateGraphValidator`.

**Algorithm:** DFS from the template being saved. At each node, follow all field references to other templates (via `TemplateId` on fields and `AllowedTemplateId` on allowed types). If any traversal arrives back at the origin template, a cycle exists.

**Input:** the full set of current `TemplateField` and `TemplateFieldAllowedType` records for all templates in the workspace, plus the proposed changes.

**Error response:** uses the standard ProblemDetails contract (see `25_cross_cutting.md`):

```json
{
  "type": "https://cmsify.dev/errors/circular-template-reference",
  "title": "Circular template reference",
  "status": 422,
  "detail": "Saving this field would create a circular reference: BlogPost → AuthorBio → BlogPost",
  "instance": "/api/v1/workspaces/.../fields",
  "traceId": "...",
  "extensions": {
    "cycle": ["BlogPost", "AuthorBio", "BlogPost"]
  }
}
```

---

## Request/Response Models

### TemplateResponse
```json
{
  "id": "...",
  "workspaceId": "...",
  "name": "Blog Post",
  "slug": "blog-post",
  "description": "...",
  "isSystem": false,
  "currentVersion": {
    "versionNumber": 2,
    "publishedAt": "...",
    "sections": [...],
    "fields": [...]
  }
}
```

### TemplateFieldResponse
```json
{
  "id": "...",
  "key": "title",
  "label": "Title",
  "order": 0,
  "isRequired": true,
  "minOccurrences": 1,
  "maxOccurrences": 1,
  "isOpen": false,
  "compositionMode": "Inline",
  "primitiveType": "Text",
  "referencedTemplate": null,
  "allowedTypes": []
}
```

---

## Tasks

- [ ] Implement `TemplatesController` with all template CRUD endpoints
- [ ] Implement version management endpoints (list, create, publish)
- [ ] Implement section CRUD endpoints
- [ ] Implement field CRUD + reorder endpoints
- [ ] Implement `ITemplateGraphValidator` (DFS cycle detection) in Core
- [ ] Wire cycle detection into field save/update operations
- [ ] Enforce immutability of published template versions (return 409 on structure modification attempts)
- [ ] Implement "copy structure on new version" logic
- [ ] Define all request/response DTOs in `Cmsify.Api/Models/`
- [ ] Implement request/response mapping layer
- [ ] Unit test: cycle detection (direct, transitive, no-cycle cases)
- [ ] Unit test: version immutability guard
- [ ] Integration test: full template lifecycle (create → add fields → publish → new version → modify → publish)
- [ ] Integration test: delete template blocked by content references

---

## Deliverables
- Full template and version management API
- Section and field CRUD with ordering
- Cycle detection implemented and returning descriptive errors
- Published version immutability enforced
- All endpoints covered by integration tests
