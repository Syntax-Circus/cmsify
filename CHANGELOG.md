# Changelog

All notable changes to Cmsify are documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and this project follows [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

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
