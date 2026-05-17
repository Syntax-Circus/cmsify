# 23 — Template Packages (Post-MVP)

## Goal
Implement the Cmsify Template Package (`.ctp`) system: a portable, vendor-namespaced format for sharing and importing template definitions. Enables an OSS community template library and a first-run preset picker.

---

## Overview

A `.ctp` file is a self-contained JSON manifest describing one or more templates and all their dependencies. It can be exported from any Cmsify instance and imported into any other.

This phase builds on top of the `PackageNamespace`, `PackageId`, and `PackageVersion` columns already present on the `Template` entity from MVP (those fields are nullable for user-created templates; populated for imported ones).

---

## `.ctp` File Format

A `.ctp` file is a JSON document with the extension `.ctp`.

```json
{
  "cmsifyPackage": "1.0",
  "namespace": "cmsify.official",
  "id": "blog",
  "version": "1.2.0",
  "name": "Blog Starter Pack",
  "description": "A complete blog template set including BlogPost, AuthorBio, and Category.",
  "author": "Cmsify Team",
  "license": "MIT",
  "homepage": "https://github.com/cmsify/packages",
  "templates": [
    {
      "slug": "blog-post",
      "name": "Blog Post",
      "description": "A standard blog post.",
      "sections": [
        {
          "name": "Header",
          "order": 0,
          "fields": [
            {
              "key": "title",
              "label": "Title",
              "order": 0,
              "isRequired": true,
              "minOccurrences": 1,
              "maxOccurrences": 1,
              "isOpen": false,
              "compositionMode": "Inline",
              "primitiveType": "Text"
            },
            {
              "key": "author",
              "label": "Author",
              "order": 1,
              "isRequired": true,
              "minOccurrences": 1,
              "maxOccurrences": 1,
              "isOpen": false,
              "compositionMode": "Reference",
              "templateRef": "author-bio"   // references another template in this package by slug
            }
          ]
        }
      ]
    },
    {
      "slug": "author-bio",
      "name": "Author Bio",
      "description": "An author profile.",
      "sections": [],
      "fields": [
        {
          "key": "name",
          "label": "Name",
          "order": 0,
          "isRequired": true,
          "primitiveType": "Text"
        },
        {
          "key": "avatar",
          "label": "Avatar",
          "order": 1,
          "isRequired": false,
          "primitiveType": "Media"
        }
      ]
    }
  ]
}
```

### Key Design Points
- `templateRef` (within the package) uses slug, not ID — IDs are resolved at import time
- All templates in the package travel together — no cross-package references in MVP of this feature
- `namespace/id@version` is the globally unique identity of a package

---

## Import Endpoint

### `POST /api/v1/workspaces/{workspaceId}/packages/import`

**Request:** multipart form upload of a `.ctp` file, or JSON body of the parsed manifest.

**Process:**
1. Parse and validate the `.ctp` manifest against its JSON schema
2. Check for conflicts: does this `namespace/id` already exist in the workspace?
   - If yes and `version` is higher: offer upgrade (non-destructive — creates new `TemplateVersion` for each template)
   - If yes and `version` is same or lower: return `409 Conflict` with details
   - If no: proceed with fresh import
3. Resolve internal `templateRef` cross-references to build an import order (topological sort)
4. Run cycle detection across the full graph including existing workspace templates
5. Import each template in dependency order:
   - Create `Template` record with `PackageNamespace`, `PackageId`, `PackageVersion` populated
   - Create `TemplateVersion` with all sections and fields
   - Publish the version immediately (package templates start as published)
6. Return summary: `{ imported: [...], skipped: [...], errors: [...] }`

**Requires:** `TemplateAdmin`

---

## Export Endpoint

### `GET /api/v1/workspaces/{workspaceId}/packages/export`

Query params: `?templateIds={id1},{id2}` — one or more template IDs to include.

**Process:**
1. Resolve all dependencies (templates referenced by fields of the selected templates, recursively)
2. Include all resolved templates in the manifest
3. Set `namespace` from the requesting user/workspace config (or prompt for it)
4. Set `version` from query param `?version=1.0.0`
5. Return a `.ctp` JSON file as a download (`Content-Disposition: attachment; filename=export.ctp`)

**Requires:** `TemplateAdmin`

---

## Bundled Official Packages

Cmsify ships with a set of official `.ctp` files in `src/Cmsify.Api/Packages/`:
- `cmsify.official/blog@1.0.0.ctp` — BlogPost, AuthorBio, Category
- `cmsify.official/portfolio@1.0.0.ctp` — Project, CaseStudy, Testimonial
- `cmsify.official/docs@1.0.0.ctp` — DocPage, DocSection, Changelog
- `cmsify.official/product@1.0.0.ctp` — Product, ProductVariant, Review

These are embedded resources loaded by the API at runtime.

---

## Onboarding Flow (First Run)

After first-run admin setup and workspace creation, the admin UI redirects to `/onboarding/templates`:

1. Page title: "Get started with template presets"
2. Grid of official package cards (name, description, template count, preview of included templates)
3. Multi-select: user picks which packs to install
4. "Install Selected" calls `POST /packages/import` for each selected pack
5. On completion: redirect to Templates page with installed templates visible
6. "Skip" option available — can always import later from Settings

### `GET /api/v1/packages/official`
Returns the list of bundled official packages (metadata only, no full manifest). Used by onboarding UI.

---

## Admin UI: Import Package

### `Settings → Packages` page (new settings section, post-MVP)
- List of installed packages (namespace/id, version, template count, installed date)
- "Import Package" button:
  - Tab 1: Upload `.ctp` file
  - Tab 2: Import from URL (fetches the `.ctp` file server-side, validates, imports)
- Per installed package: "Check for updates" (future — requires registry), "Export", view included templates

---

## Package Registry (Future, Post-Post-MVP)

A community package registry (separate service) where authors can publish `.ctp` files. The admin UI would gain a "Browse Registry" tab in the Import Package screen. The format and import endpoint are forward-compatible — adding registry support is a UI and discovery concern, not a schema change.

---

## Schema (already in MVP schema, documented here for completeness)

```sql
-- On Templates table (from MVP):
package_namespace   VARCHAR(200)    NULL
package_id          VARCHAR(200)    NULL
package_version     VARCHAR(50)     NULL
```

No additional tables required for MVP of this feature. A future `InstalledPackage` tracking table may be added when registry/update-check functionality is implemented.

---

## Tasks

- [ ] Define `.ctp` JSON schema (publish to `/schema/ctp-1.0.json` from the API)
- [ ] Implement `POST /api/v1/workspaces/{workspaceId}/packages/import`
  - [ ] Parse + validate `.ctp` manifest
  - [ ] Conflict detection (same namespace/id/version)
  - [ ] Topological sort for dependency import order
  - [ ] Cycle detection including existing workspace templates
  - [ ] Template + version creation with package provenance fields
- [ ] Implement `GET /api/v1/workspaces/{workspaceId}/packages/export`
  - [ ] Dependency resolution (recursive)
  - [ ] `.ctp` manifest generation
  - [ ] File download response
- [ ] Implement `GET /api/v1/packages/official` (bundled packages list)
- [ ] Embed official `.ctp` files as assembly resources
- [ ] Author all official packages (`blog`, `portfolio`, `docs`, `product`)
- [ ] Implement onboarding flow in Admin (`/onboarding/templates`)
- [ ] Implement `Settings → Packages` page in Admin
  - [ ] File upload import
  - [ ] URL import
  - [ ] Installed package list
- [ ] Unit test: `.ctp` manifest validation
- [ ] Unit test: topological sort for import ordering
- [ ] Unit test: cycle detection with package templates
- [ ] Integration test: import official blog package → verify templates created
- [ ] Integration test: export templates → re-import → verify idempotent
- [ ] Integration test: conflict detection (same version → 409, higher version → upgrade)

---

## Deliverables
- `.ctp` format spec and JSON schema published
- Import and export endpoints working
- Official packages bundled with Cmsify
- Onboarding template picker on first run
- Settings → Packages management page
- Community can share `.ctp` files via GitHub immediately (no registry required)
