# Changelog

All notable changes to Cmsify are documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and this project follows [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

### Added

- Client-side `${{name}}` template rendering (`CmsifyTemplateRenderer.Render` in the .NET client, `renderCmsifyTemplate` in the TypeScript client) for substituting caller-supplied variables into Text/Markdown field values read from Cmsify content. Purely opt-in and client-side; the server has no concept of variables. See "Rendering field templates" in `docs/integrating.md`.

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
