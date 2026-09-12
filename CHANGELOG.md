# Changelog

All notable changes to Cmsify are documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and this project follows [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

## [0.4.5] - 2026-09-12

### Fixed

- EF Core logged a `MultipleCollectionIncludeWarning` (visible in OTel) for any query that loaded more than one collection navigation without an explicit `QuerySplittingBehavior`, e.g. `ContentController.CreateVersion`/`UpgradeTemplateVersion` loading a template version's fields together with each field's allowed types, and the equivalent patterns in `TemplateVersionRepository`, `TemplatesController`, `PackagesController`, and `ComponentsController`. `QuerySplittingBehavior.SplitQuery` is now the default for the Npgsql provider, which both silences the warning and avoids the cartesian-product row explosion `SingleQuery` produces when joining multiple sibling collections.

## [0.4.4] - 2026-09-12

### Fixed

- `POST .../content/{id}/versions/{versionNumber}/upgrade-template-version` wiped every field value on upgrade, not just genuinely removed ones - it matched a version's existing values against the target template version's fields by field *Id*, but a package re-import always mints brand-new `TemplateField` rows for every field on every template version, even one whose key never changed. In practice this meant upgrading any content onto a newer template version always emptied every field, then immediately failed its own post-upgrade validation the moment any field was required. Values are now remapped onto the target field with the same *key*; only a value whose key genuinely no longer exists in the target is dropped.
- Suppressed chatty `Microsoft`/`System` Serilog categories so `Microsoft.EntityFrameworkCore.Database.Command` stops flooding the OTel collector at Information level.

## [0.4.3] - 2026-09-11

### Fixed

- Package import: importing a package that both introduced a brand-new component and required an explicit "replace" resolution for an already-installed component (e.g. adding a field to an existing component) failed with an opaque 500 (`DbUpdateConcurrencyException`) instead of succeeding. `PackagesController.CreateComponentVersion` only attached the replacement `ComponentVersion` via its parent's `Versions` navigation collection; since `Entity.Id` is assigned client-side at construction (`Guid.CreateVersion7()`), EF Core's change detection saw a non-default key reached only through an already-tracked (unchanged/modified) parent and inferred the row already existed, issuing an `UPDATE` whose optimistic-concurrency check then never matched instead of an `INSERT`. The equivalent PickList "replace" path (`AddRevision`) already explicitly added its new revision to the `DbContext`; `CreateComponentVersion` now does the same.

## [0.4.2] - 2026-09-10

### Fixed

- Release pipeline: `quay.io/skopeo/stable`'s tag digest was being rotated and garbage-collected by quay.io on a roughly 1-3 day cadence, breaking the pinned Skopeo helper used by `artifact-smoke`, `candidate-accessibility`, and `upgrade-rollback` (this exact failure recurred six times). The helper is now pinned against `ghcr.io/syntax-circus/skopeo`, a mirror this org controls.
- `upgrade-rollback`'s assertions (`eng/upgrade-tests/assertions.mjs`) were never updated for 0.4.0's version-centric Content API breaking change, so the job carried `continue-on-error: true` to mask real, permanent failures - which itself broke the release-contract governance test suite on every PR. The assertions are fixed against the current API contract and `continue-on-error` is removed from both `publish-cmsify.yml` and `upgrade-rollback.yml`; the release gate fails closed again.
- v0.4.1's release could not complete due to the Skopeo failure above; this release carries no functional changes beyond it and the previous fix.

## [0.4.1] - 2026-09-09

### Added

- A "View" action in the Admin content list and a new read-only content viewer (`/workspaces/{workspaceId}/content/{id}/view`, optionally `/view/{versionNumber}`) for inspecting a content item's currently-published (or any specific) version without creating a Draft. Previously, opening "Edit" on a Published or Archived item always minted a new Draft via `CreateVersionAsync`, even when the user only wanted to look at the content — there was no way to see it otherwise short of the crude raw-table "Inspect" modal on the Versions page. The viewer reuses the existing `SyntaxCircus.Cmsify.Components` field editors via a new non-interactive `ReadOnly` mode (added to `FieldEditor`, `FieldEditorRenderContext`, `ContentEditForm`, `ContentEditPanel`, `ContentListView`/`ContentListPanel`, and every field-type editor) instead of a second renderer, so formatting, resolved pick-list labels, and asset names render exactly as they do when editing. Reader-role users clicking "Edit" are now redirected into this read-only view instead of landing on a form they can't successfully save.

## [0.4.0] - 2026-09-07

### Added

- Content duplication in the Admin content editor (a new "Duplicate" button) — clones an existing item's fields into a new draft under a fresh slug/translation group, keeping locale and tags. Nothing equivalent existed anywhere before (domain, API, either SDK, or Admin UI).

### Changed

- **Breaking:** `ContentVersion` is now the sole carrier of content fields and workflow lifecycle (Draft → Review → Approved → Published → Archived); `ContentItem` is now a lightweight slug/template/locale identity header. This enables independently-editable, independently-scheduled date-bounded versions of the same content item. The Content API's item-level workflow routes (`/content/{id}/submit`, `/approve`, `/reject`, `/publish`, `/archive`, `/restore`, `/rollback`, `/upgrade-version`) are replaced by version-level equivalents (`/content/{id}/versions/{versionNumber}/submit`, etc.); `GetBySlug` now resolves to a `ContentVersionDetailResponse` instead of `ContentItemDetailResponse`. The TypeScript SDK's `content.bySlug` return type changes accordingly. This is a breaking change to the Content API and its contracts — accepted as acceptable pre-release, since there are no external consumers of the API or the TypeScript SDK yet.
- **Breaking (database):** the accompanying migration (`UnifyContentVersionLifecycle`) is not a pure schema change — it carries a hand-written data backfill. It moves every `ContentItem`'s status, publish schedule and field values onto a `ContentVersion` (materialising a new version row for any item that did not already have a matching one), renames `content_versions.retired_at` to `archived_at`, rewrites the `Retired` status to `Archived`, seeds `created_at`/`updated_at` on pre-existing `content_versions` rows, and then drops the `content_field_values` table along with the moved `content_items` columns. Operators upgrading an existing deployment should back up before applying it: `Down()` restores the dropped structure but does **not** reverse the data movement.
- **Breaking (webhooks):** content webhook event types now distinguish item lifecycle from version lifecycle. Added: `content.version_created`, `content.version_updated`, `content.version_deleted`, `content.version_status_changed`, `content.version_published`, `content.version_template_upgraded`. Removed (nothing emits them any more, and subscription requests naming them are now rejected): `content.published`, `content.status_changed`, `content.archived` — existing subscriptions to these must be repointed, most often `content.published` → `content.version_published`. `content.created`/`content.updated`/`content.deleted` are unchanged and still describe item-level lifecycle. Both publish paths (the API's publish action and the scheduled-publish background worker) now emit the same `content.version_published` event; previously the scheduled worker emitted `content.published`.
- **Breaking (audit log):** audit entries for content status changes now record an `EntityType` of `ContentVersion` instead of `ContentItem`, since the status being changed belongs to the version. Audit queries filtering on `entityType=ContentItem` to find status changes need updating.

### Fixed

- `Cmsify.Admin` (the Blazor admin UI) and the separate `sdk/dotnet` .NET client SDK — both left non-functional by the version-centric Content API change above — are restored. `Cmsify.Admin`'s Content pages (list, editor, version history, publish dialog, workspace dashboard) and `sdk/dotnet`'s `ContentClient` now target the version-scoped API. Note: the Admin publish dialog no longer offers the bounded-window ("publish as bounded override") inputs it previously had — that capability didn't survive the restoration and belongs to a future dated-version-management UI, not this pass; the dialog otherwise still supports scheduling a publish date and, for Admins, overriding the workflow gate. The Content list also no longer shows a definite status/workflow actions for an item with no currently-serving version (e.g. never-published, or Archived) — it shows "—" and a link into the editor instead, since the list's summary data can't distinguish those states; the editor page still resolves and displays the item's real status correctly.
- The Admin content editor no longer mints a new, orphaned Draft version every time it's reopened for a Published or Archived item. It previously re-derived the version to edit from the item's currently-serving version on every page load, ignoring any Draft/Review/Approved version that already existed — so repeat visits (and even Rollback, immediately followed by landing back on the editor) kept spawning throwaway Drafts instead of resuming the one already there. It now reuses the latest already-editable version whenever one exists.
- The Admin content editor no longer shows a false-positive "Content changed while saving" warning on a normal, single-editor save. Saving issues two sequential writes (the version, then the item's slug/locale/tags); the version write bumps the parent item's `UpdatedAt` as a side effect, which invalidated the item write's optimistic-concurrency ETag captured at page load — so the warning fired deterministically on every save, not just real conflicts. The item's ETag is now refreshed between the two writes.
- Creating a template field with `isOpen: true` and an `allowedTypes` entry that also specifies a `primitiveType` is now rejected (`422`) at creation time. Previously this was silently accepted, then produced a confusing "expects a child content value" error the first time any content was submitted against the field.
- Dark-mode contrast across the Admin app: shared `Cmsify.Components` form labels (via `Cmsify.Components.Theme`) rendered near-black on Admin's dark background because the theme's `--cmsify-*` variables were static light-mode values, never wired to Bootstrap's `[data-bs-theme="dark"]` convention — they now track the active theme's `--bs-*` variables instead. Bootstrap's outline button variants (`.btn-outline-secondary` and, once the Content list started using more of them, `-primary/-info/-success/-warning/-danger`) bake their colors to fixed light-mode hex values at compile time with no native dark-mode handling; dark-mode overrides now cover all of them, including disabled-button state (Bootstrap's extra opacity on `:disabled` was making already-muted colors unreadable).
- The Admin sidebar's build-version footer overflowed its container showing the full informational version string; it now shows a short version (without the `+sha` suffix). The About page's Admin/API version-match indicator is unaffected and keeps showing the full version.
- The Content list's Edit button now uses real Bootstrap `btn btn-sm` classes (via a new `EditButtonClass` parameter on the shared `ContentListView`/`ContentListPanel` components) instead of hand-approximated CSS, so it's pixel-identical to its row-action siblings instead of visibly oversized. Row-action buttons are recolored from a uniform, flat `btn-outline-secondary` to distinct, semantically-appropriate variants (Edit=primary, Submit=info, Approve=success, Reject=warning, Publish=success filled, Archive=danger, Restore=secondary), and their spacing no longer silently depends on incidental HTML whitespace between buttons that Razor could trim away.

## [0.3.2] - 2026-09-06

### Added

- New publishable package `SyntaxCircus.Cmsify.Components`: headless, restylable Blazor Server components for editing and managing Cmsify content, for embedding content editing directly in a consuming site instead of sending users to Cmsify Admin. Includes a field editor for every template field type, a `FieldEditor` dispatcher with a per-field-type override hook, composed `ContentEditForm`/`ContentListView` components, SDK-backed `Client.ContentEditPanel`/`Client.ContentListPanel` drop-in panels, and a shared media/reference picker. Structural styling only; every visual property is a `--cmsify-*` CSS custom property.
- New publishable package `SyntaxCircus.Cmsify.Components.Theme`: optional default styling for `SyntaxCircus.Cmsify.Components`, matching Admin's current look. Purely static CSS; omit it to theme the components yourself.

### Changed

- The Admin app's Content Editor and Content List pages now run on the new `SyntaxCircus.Cmsify.Components` package instead of Admin's previous hand-rolled markup, and its bespoke media picker has been replaced by the shared component.

### Fixed

- The Admin sidebar's build-version display (added in 0.3.1) sometimes rendered as literal text (`v@AdminBuildInfo.Version`) instead of the actual version, due to a Razor markup-parsing quirk. It now always shows the resolved version.

## [0.3.1] - 2026-09-05

### Added

- The Admin app now shows its running build version in the sidebar and on a new `/about` page, alongside the API's reported version (read from `/health/ready`) with a match/mismatch indicator — useful for confirming a deploy landed both images in sync.
- Admins can now publish content directly from Draft or Review, skipping Submit/Approve, via a confirmation dialog in the Admin UI (`PublishContentRequest.OverrideWorkflow`, requiring both the Admin role and explicit opt-in — existing callers see no behavior change unless they opt in).
- Reject (Review → Draft) and Restore (Archived → Draft) buttons on the Admin content list — both existed in the API already but had no UI entry point.
- The Content Editor's Lifecycle card now shows the same workflow action buttons (Submit/Approve/Reject/Publish/Archive/Restore) as the content list, so reviewing content no longer requires navigating back to the list.

### Changed

- The .NET SDK's `HealthClient.LiveAsync`/`ReadyAsync` (`client.Health`) now return a typed `HealthCheckResponse` (with `Status` and `Metadata.Version`/`Metadata.GeneratedAt`) instead of an untyped payload.

### Fixed

- Release promotion now also publishes the `:latest` Docker Hub tag alongside the version-numbered tag for stable (non-prerelease) releases. Previously only the versioned tag was pushed, leaving `:latest` stuck on an old release.
- Content workflow buttons (Submit/Approve/Archive) in the Admin UI previously failed silently when clicked on an item in the wrong status. They're now disabled with a tooltip explaining why when not applicable, and any remaining server-side rejection shows an error toast instead of doing nothing.

## [0.3.0] - 2026-09-05

### Added

- Client-side `${{name}}` template rendering (`CmsifyTemplateRenderer.Render` in the .NET client, `renderCmsifyTemplate` in the TypeScript client) for substituting caller-supplied variables into Text/Markdown field values read from Cmsify content. Purely opt-in and client-side; the server has no concept of variables. See "Rendering field templates" in `docs/integrating.md`.

### Fixed

- An expired Admin session no longer redirects to a raw HTTP 400 error page. Signing back in after expiry now always reaches the login form, and any remaining antiforgery-token mismatch on the login/logout endpoints redirects to the login page with a friendly "session expired" message instead.

## [0.2.4] - 2026-09-04

### Fixed

- Release restores now pin the Admin and Admin integration projects to the repository-signed `SyntaxCircus.Http.Resilience` package hash, matching the public .NET client dependency graph.

## [0.2.3] - 2026-09-04

### Added

- Optional OpenTelemetry/SigNoz and Sentry/GlitchTip telemetry is available to the API and Admin hosts through the reusable `SyntaxCircus.Observability` package, without changing the existing configuration sections.

### Fixed

- Creating a workspace in the Admin UI now selects and displays it immediately instead of leaving the workspace state empty until a browser refresh.

## [0.2.2] - 2026-09-04

### Fixed

- `AddCmsifyClient` is now covered by a regression test proving its `AddTypedClient`-based registration (introduced in 0.2.0) avoids the constructor-ambiguity crash (`InvalidOperationException: Multiple constructors accepting all given argument types...`) that the naive `services.AddHttpClient<CmsifyClient>()` pattern still hits. This fix shipped silently in 0.2.0 as part of the HTTP resilience consolidation; this entry makes it discoverable for anyone who hit the crash on 0.1.x.

## [0.2.1] - 2026-09-01

### Changed

- Releases are certified from a reviewed immutable SemVer tag. Branch and pull-request builds validate only and never publish artifacts or create tags.
- The TypeScript SDK uses the owned npm identity `@syntaxcircus/cmsify-client`; trusted publishing leaves token-style registry configuration unset so npm can exchange the GitHub Actions OIDC token.

### Release note

- `v0.2.0` was not completed as a GitHub Release. Its NuGet submissions and OCI images were accepted before npm rejected the unowned `@cmsify/client` scope. That tag remains immutable historical evidence; the next complete same-source release is `v0.2.1`.

## [0.2.0] - 2026-08-31

### Added

- First-party .NET and TypeScript SDK packages, Admin accessibility certification, production-like release smoke tests, and deterministic upgrade/rollback rehearsal.
- OIDC administration support, durable media reconciliation, package import/export, and release provenance/SBOM attestations.

### Changed

- Public SDK packages (`SyntaxCircus.Cmsify.Contracts`, both .NET clients, and `@syntaxcircus/cmsify-client`) are MIT-licensed; the server repository and OCI images remain AGPL-3.0-or-later.
- Workspace responses now include the actor-specific `canWrite` capability for permission-aware clients.
- The Admin app now generates slugs from a new workspace, template, picklist, or component name until the slug is manually edited.
- User-management forms show API validation details inline.
- Workspace management and selection now honor user role and per-workspace grants. The workspace picker is selectable only when multiple workspaces are available.
- Admin navigation now shows only settings available to the current role; webhook management remains an Editor-level, workspace-scoped feature.
- Admin static CSS, scripts, and branding assets use Blazor asset fingerprinting so deployments receive changed assets without stale browser caches.
- API JSON uses Cmsify's shared camel-case, string-enum wire format consistently.

### Fixed

- Unauthorized API requests now return normal `401` or `403` responses instead of requiring an unconfigured authentication scheme.

## [0.1.3] - 2026-08-21

### Fixed

- Corrected workspace permissions and administration authorization behavior.

## [0.1.0] - 2026-08-20

### Added

- Initial Cmsify release with a versioned HTTP API, PostgreSQL persistence, and a Blazor administration UI.
- Versioned templates, inline components, choice sets, content lifecycle, media, API clients, workspaces, audit history, webhooks, and scheduled publishing.
- First-party TypeScript and .NET clients for server-side integrations.
