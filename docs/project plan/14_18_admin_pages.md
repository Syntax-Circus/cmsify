# 14 — Admin Auth Flow

## Goal
Implement the login UI, session management, and optional OIDC wiring in the Blazor admin app.

---

## Login Page (`/login`)

- Email + password form
- "Remember me" checkbox (controls localStorage vs sessionStorage token persistence)
- Calls `POST /api/v1/auth/login` via `AuthService`
- On success: stores token in `AuthState`. If the response `mustChangePassword` flag is true, redirect to `/account/change-password`. Otherwise redirect to `/workspaces` (or `returnUrl`).
- On failure: inline error message ("Invalid credentials")
- No self-service registration UI — accounts are created by an Admin in Settings (an admin sets a temporary password which is communicated out-of-band; the user is forced to change it on first login). MVP has no SMTP/email integration.

## Change Password Page (`/account/change-password`)

- Three fields: current password, new password, confirm new password
- Posts to `POST /api/v1/auth/change-password`
- Accessible from the user menu at any time
- **Forced** when `AuthState.MustChangePassword == true` — a routing guard redirects to this page and disables the nav menu until the change succeeds
- On success: clears `MustChangePassword`, navigates to `/workspaces` (or `returnUrl` if one was captured)

## Session Management
- `AuthState` tracks the token's **absolute** expiry (8h, non-sliding)
- On every navigation, checks expiry; if expired clears state and redirects to `/login?returnUrl={currentPath}`
- Logout button in nav calls `POST /api/v1/auth/logout`, clears `AuthState`, redirects to `/login`

## Optional OIDC Wiring
- If `Auth:Oidc:Enabled = true` in API config, the login page shows an additional "Sign in with {OidcProviderName}" button
- Clicking initiates standard OIDC authorization code flow via the Blazor app's configured OIDC client
- On callback: exchange code for token, store in `AuthState` same as local login
- `OidcProviderName` configurable via `Admin:OidcProviderName` (e.g. "Authentik")

## Tasks
- [ ] Implement `Login.razor` with email/password form
- [ ] Implement `ChangePassword.razor` and wire the `MustChangePassword` routing guard
- [ ] Implement `AuthService` login/logout/changePassword methods
- [ ] Implement token persistence (sessionStorage/localStorage toggle)
- [ ] Implement absolute-expiry check and auto-redirect on navigation
- [ ] Implement optional OIDC button and callback handling
- [ ] Add `Admin:OidcProviderName` to `.env.example`

## Deliverables
- Working login/logout/change-password flow
- Forced password change on first login
- Auth guard redirecting unauthenticated users
- Optional OIDC button rendered when configured

---

# 15 — Admin: Workspaces & Template Builder

## Goal
Implement workspace management UI and the template builder — the most complex UI surface in the admin app.

---

## Workspace Pages

### WorkspaceList (`/workspaces`)
- Lists all accessible workspaces (cards or table)
- Admin: shows "New Workspace" button
- Click workspace → sets `WorkspaceState`, navigates to workspace dashboard

### WorkspaceDetail (`/workspaces/{id}`)
- Shows workspace summary (name, slug, content count, template count)
- Quick-nav links to Templates, Content, Media

---

## Template Builder (`/workspaces/{id}/templates/{tid}`)

The most complex page in the admin. Allows visual editing of a template's structure.

### Layout
- Left panel: section list + field list (tree view)
- Right panel: selected field/section detail form
- Top bar: template name, version badge, Save Draft / Publish Version buttons

### Capabilities
- Add/remove/reorder sections (drag handles)
- Add/remove/reorder fields within sections (drag handles)
- For each field: edit label, key (auto-generated from label, editable), helpText, required, min/max occurrences, IsOpen, CompositionMode, type picker
- Type picker: shows all primitives + all user-defined templates in the workspace (excluding self to avoid obvious direct cycles)
- For constrained fields (IsOpen=false): allowed types multi-select
- Real-time cycle detection feedback — if a type selection would cause a cycle (detected client-side via a local graph traversal, confirmed server-side on save), show inline warning

### Version Management
- "New Version" button: enabled only when no `Draft` exists; calls `POST .../versions`, navigates to the new draft
- "Version History" tab: shows all versions with their `TemplateVersionStatus` (`Draft` / `Published` / `Archived`); click to view (read-only for non-`Draft`)
- `Draft` versions: fully editable
- `Published` and `Archived` versions: all field editors are disabled and the version is clearly labeled with its status
- "Publish" button on the `Draft` confirms: archives the prior `Published` and promotes the draft

---

## Tasks
- [ ] Implement `WorkspaceList.razor` and `WorkspaceDetail.razor`
- [ ] Implement `TemplateList.razor` with search and new template button
- [ ] Implement `TemplateBuilder.razor` with section/field tree and detail panel
- [ ] Implement drag-and-drop reordering (use `SortableJS` via JS interop or a Blazor-compatible library)
- [ ] Implement field type picker with primitive and template options
- [ ] Implement allowed types multi-select for constrained fields
- [ ] Implement client-side cycle detection hint (visual warning before save)
- [ ] Implement `VersionHistory.razor`
- [ ] Implement publish version confirmation dialog

## Deliverables
- Workspace switcher working
- Template list and creation working
- Template builder functional (add/edit/remove sections and fields, publish versions)

---

# 16 — Admin: Content Editor

## Goal
Implement the content list and content editor pages — the primary day-to-day UI for editors.

---

## Content List (`/workspaces/{id}/content`)

- Table/card view of content items
- Columns: title (sourced from `Template.TitleFieldKey`), template, status (badge), locale, updated date
- Filters: status dropdown, template picker, locale, tag filter, date range
- Full-text search box (`?q=`) and slug-exact search
- "New Content" button → picks a template → navigates to `/content/new?templateId={id}`
- Lifecycle actions inline: submit for review, approve, publish, archive

## Content Editor (`/workspaces/{id}/content/{cid}`)

Renders a dynamic form based on the content item's `TemplateVersion` structure.

### Template Version Banner
If the content item is pinned to a `TemplateVersion` that is not the template's latest `Published` version, a banner offers an "Upgrade to latest version" action. This calls `POST .../content/{id}/upgrade-version` and, on success, reloads the editor against the new schema. If the upgrade would fail validation (e.g. new required fields), the API returns `422` and the banner expands to show what must be fixed first.

### Field Rendering

Each field type renders a distinct input component:

| Field Type | Component |
|------------|-----------|
| Text | Single-line text input |
| RichText | Rich text editor (e.g. Quill or TipTap via JS interop) |
| Markdown | Textarea with preview toggle |
| Boolean | Toggle switch |
| PickList | Select dropdown (options from field config) |
| Media | Media picker (opens MediaLibrary modal) |
| File | File picker (opens MediaLibrary modal filtered to non-image) |
| Link | URL input + link text input |
| Quote | Blockquote text + attribution inputs |
| Separator | Non-editable visual divider |
| Template (Inline) | Embedded sub-form (recursive) |
| Template (Reference) | Search/pick existing content item of the correct type |

Multi-occurrence fields: rendered as a list with +/- controls and drag-to-reorder.

### Sections
If the template has sections, fields are grouped under collapsible section headings.

### Lifecycle Panel (sidebar)
- Current status badge
- Available transition buttons based on current status and actor role
- Slug input
- Locale code input + "Link Translation" button
- Tags input (tokenized)
- `PublishAt` datetime picker (shown when Approved)
- "Save Draft" button (auto-save every 60s optional)

---

## Tasks
- [ ] Implement `ContentList.razor` with filters and inline lifecycle actions
- [ ] Implement `ContentEditor.razor` with dynamic field rendering
- [ ] Implement all primitive field input components
- [ ] Implement rich text editor via JS interop
- [ ] Implement markdown textarea with preview
- [ ] Implement media picker modal (integrates with MediaLibrary)
- [ ] Implement inline sub-form for Inline Template fields (recursive)
- [ ] Implement reference picker for Reference Template fields
- [ ] Implement multi-occurrence field list with drag reorder
- [ ] Implement lifecycle sidebar with transition buttons
- [ ] Implement translation link UI

## Deliverables
- Content list with filtering working
- Content editor rendering all field types correctly
- Lifecycle transitions available in editor sidebar
- Save and publish flows working end-to-end

---

# 17 — Admin: Media Library

## Goal
Implement the media management UI including upload, browse, preview, and selection modal.

---

## Media Library Page (`/workspaces/{id}/media`)

- Grid/list toggle view of all media assets
- Filter by MIME type category (Images, Video, Audio, Documents)
- Search by filename
- Upload button: drag-and-drop or file picker → calls `POST /api/v1/.../media`
- Upload progress indicator
- Click asset: opens detail panel (filename, size, MIME, alt text, copy URL button)
- Delete button with confirmation (warns if referenced by published content)

## Media Picker Modal (used by Content Editor)
- Same browsable grid as the library page
- Optional MIME type filter (e.g. images only for an Image field)
- Search
- Click to select → returns `MediaAsset` to calling field component
- "Upload new" tab within the modal

---

## Tasks
- [ ] Implement `MediaLibrary.razor` (standalone page)
- [ ] Implement media grid/list with filter and search
- [ ] Implement drag-and-drop upload with progress
- [ ] Implement asset detail panel with alt text edit
- [ ] Implement delete with published-content warning
- [ ] Implement `MediaPickerModal.razor` for use in content editor
- [ ] Wire media picker into Media and File field components

## Deliverables
- Media library page working with upload, browse, and delete
- Media picker modal working from within content editor

---

# 18 — Admin: Settings

## Goal
Implement all settings pages: user management, API clients, webhooks, storage config, and audit log viewer.

---

## Users (`/settings/users`) — Admin only
- List users (email, display name, role, last login, active)
- **Create user:** admin enters email, display name, role, and a temporary password (or clicks "generate"). The new user is persisted with `MustChangePassword = true`. The temp password is shown **once** in a copy-friendly dialog — the admin is responsible for communicating it to the user out-of-band (Slack, in person, etc.). MVP has no SMTP integration, so there is no automatic email invite or password-reset link.
- Edit role
- Reset password: admin sets a new temporary password; flips `MustChangePassword = true` on the target user; new password shown once
- Deactivate/reactivate (soft delete — sets `IsDeleted`)
- Admin cannot deactivate themselves

## Account Preferences (`/account/preferences`) — any signed-in user
- Display name (read-only — change requires admin in MVP)
- Time zone picker (IANA TZ database, defaults to browser detection) — persisted as `User.TimeZoneId` via `PUT /api/v1/account/preferences`; drives all timestamp rendering in the UI through `LocalTimeDisplay`
- Theme override (light / dark / auto)
- Change-password link (routes to `/account/change-password`)

## API Clients (`/settings/api-clients`) — Admin only
- List clients (name, role, workspace scope, created, last used, expiry)
- Create client: name, role, optional workspace scope, optional expiry
- On create: show generated token **once** with copy button and "Store this securely" warning
- Revoke client (sets `IsActive = false`)
- Rotate token: shows new token once

## Webhooks (`/settings/webhooks`) — Editor+
- List endpoints with delivery success rate indicator
- Create/edit endpoint: name, URL, secret, event subscriptions
- View delivery log per endpoint: status, timestamp, response code, retry count
- Manual retry for failed deliveries

## Storage Config (`/settings/storage`) — Admin only
- Display current provider type (read from API)
- Links to documentation for switching providers (actual config is env/appsettings — not editable in UI)
- Test connection button: calls a `POST /api/v1/settings/storage/test` endpoint

## Audit Log (`/settings/audit`) — TemplateAdmin+
- Paginated audit log table
- Filters: entity type, action, actor, date range
- Click row: expand change delta JSON

---

## Tasks
- [ ] Implement `Users.razor` with list, create (temp password reveal), edit role, reset password, deactivate
- [ ] Implement `Preferences.razor` with time zone, theme, change-password link
- [ ] Implement `ApiClients.razor` with list, create (token reveal), revoke, rotate
- [ ] Implement `Webhooks.razor` with list, create/edit, delivery log, manual retry
- [ ] Implement `StorageConfig.razor` with provider display and test connection
- [ ] Implement `AuditLog.razor` with filter and delta expand
- [ ] Add `POST /api/v1/settings/storage/test` endpoint to API
- [ ] Add `GET`/`PUT /api/v1/account/preferences` endpoints to API

## Deliverables
- All settings pages functional
- Token / temp-password reveal pattern working (single display + copy)
- Per-user preferences applied across the UI (time zone)
- Audit log viewer with delta expand working
