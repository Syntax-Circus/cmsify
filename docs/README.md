# Cmsify documentation

Use the guide that matches the job you are doing. The current API behavior is defined by the running API, its checked-in OpenAPI contract, and tests; the project-plan archive records design history.

## Operate Cmsify

- [Getting started](getting-started.md) — run a local stack, sign in, and create the first integration token.
- [Configuration](../README.md#configuration) — every supported API and Admin setting, defaults, and production guidance.
- [Operations](operations.md) — deployment, persistence, backup, restore, upgrades, health checks, and incident response.
- [Release runbook](release-runbook.md) and [rollback runbook](rollback-runbook.md) — release evidence, abort criteria, and recovery.
- [API compatibility](api-compatibility.md) — `/api/v1` compatibility and deprecation policy.

## Build with Cmsify

- [Published artifacts](../README.md#published-artifacts) — NuGet SDK packages and Docker Hub images.
- [Authentication and authorization](authentication-and-authorization.md) — credentials, roles, scopes, and token lifecycle.
- [Integrating with the API](integrating.md) — workspace-scoped HTTP use, errors, pagination, retries, and SDK integration.
- [Content modeling](content-modeling.md) — templates, components, and content lifecycle.
- [Components and choice sets](content-components-and-choice-sets.md) — inline schemas, nested values, and immutable choice revisions.
- [Reusable model packages](packages.md) — `.ctp` import/export and built-in starter packs.
- [TypeScript client](../sdk/typescript/README.md) and [.NET client](../sdk/dotnet/README.md) — first-party SDK usage.

## Contribute or maintain

- [Contributing](contributing.md) — local setup, validation, and pull-request expectations.
- [Agent and contributor guidance](../AGENTS.md) — architecture boundaries, change-scoped validation, generated-file rules, and hygiene.
- [Changelog](../CHANGELOG.md) — released and upcoming changes.
- [Roadmap](roadmap.md) — committed future work, when available.
- [v1 release-readiness audit](v1-release-readiness.md) — maintainer release gates, prioritized remediation backlog, and shared-package decisions.
- [Project-plan archive](project%20plan/00_index.md) — design decisions and historical implementation plans, not a current API reference.
