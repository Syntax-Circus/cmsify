# 06 — Workspaces

## Goal
Implement workspace management — the top-level scoping container for all templates and content.

---

## Workspace Rules
- All templates and content items belong to exactly one workspace
- API clients may be scoped to a single workspace or granted access to all (`WorkspaceId = null`)
- Users have a single global role; workspace-level role overrides are post-MVP
- Workspace slugs are globally unique
- Soft delete (`IsDeleted` + `DeletedAt`) — see `02_core_domain.md`. The previously-documented `IsActive` flag is removed in favour of soft delete.

## Endpoints

### `GET /api/v1/workspaces`
Returns all workspaces the current actor has access to. `Admin` sees all; others see workspaces they're scoped to.

### `POST /api/v1/workspaces`
Create workspace. Requires `Admin` role.
Request: `{ name, slug, description? }`

### `GET /api/v1/workspaces/{id}`
Get single workspace. Response carries an `ETag` header (see `25_cross_cutting.md`).

### `PUT /api/v1/workspaces/{id}`
Update name/description. Requires `Admin`. Requires `If-Match` header.

### `DELETE /api/v1/workspaces/{id}`
Soft-deletes the workspace (sets `IsDeleted = true`, populates `DeletedAt` and `DeletedByUserId`). Requires `Admin` and `If-Match`. All workspace-scoped queries exclude soft-deleted workspaces by default. A future endpoint may permit hard deletion after a retention window.

---

## Tasks

- [ ] Implement `WorkspacesController` with all endpoints
- [ ] Implement `IWorkspaceRepository` and `WorkspaceRepository`
- [ ] Add workspace scoping checks to all other repositories (ensure queries filter by `WorkspaceId` from `ICurrentActor`)
- [ ] Integration test: workspace CRUD, access scoping by role
- [ ] Integration test: cross-workspace data isolation (actor scoped to workspace A cannot see workspace B content)

---

## Deliverables
- Workspace CRUD endpoints working
- All downstream repositories enforce workspace scoping
