# Cmsify

[![.NET tests](https://github.com/Syntax-Circus/cmsify/actions/workflows/dotnet-test.yml/badge.svg)](https://github.com/Syntax-Circus/cmsify/actions/workflows/dotnet-test.yml)
[![NuGet: SyntaxCircus.Cmsify.Client](https://img.shields.io/nuget/v/SyntaxCircus.Cmsify.Client.svg?label=NuGet%20Client)](https://www.nuget.org/packages/SyntaxCircus.Cmsify.Client)
[![NuGet: SyntaxCircus.Cmsify.Client.DistributedCaching](https://img.shields.io/nuget/v/SyntaxCircus.Cmsify.Client.DistributedCaching.svg?label=NuGet%20Distributed%20Caching)](https://www.nuget.org/packages/SyntaxCircus.Cmsify.Client.DistributedCaching)
[![Docker: syntaxcircus/cmsify-api](https://img.shields.io/docker/v/syntaxcircus/cmsify-api?label=Docker%20API&sort=semver)](https://hub.docker.com/r/syntaxcircus/cmsify-api)
[![Docker: syntaxcircus/cmsify-admin](https://img.shields.io/docker/v/syntaxcircus/cmsify-admin?label=Docker%20Admin&sort=semver)](https://hub.docker.com/r/syntaxcircus/cmsify-admin)
[![License: AGPL-3.0-or-later](https://img.shields.io/badge/license-AGPL--3.0--or--later-blue.svg)](LICENSE)

Cmsify is a headless CMS with composable, versioned templates, built with .NET 10, PostgreSQL, EF Core, and a Blazor admin UI. It exposes a versioned HTTP API and a first-party TypeScript client for server-side applications.

> **No support guaranteed.** Cmsify is published as-is and maintained on a best-effort basis. Issues and pull requests are welcome, but there is no SLA or guaranteed support response.

## Published artifacts

- **NuGet SDK packages:** [`SyntaxCircus.Cmsify.Client`](https://www.nuget.org/packages/SyntaxCircus.Cmsify.Client) and [`SyntaxCircus.Cmsify.Client.DistributedCaching`](https://www.nuget.org/packages/SyntaxCircus.Cmsify.Client.DistributedCaching)
- **Docker Hub images:** [`syntaxcircus/cmsify-api`](https://hub.docker.com/r/syntaxcircus/cmsify-api) and [`syntaxcircus/cmsify-admin`](https://hub.docker.com/r/syntaxcircus/cmsify-admin)

## Start locally

The fastest path is Docker Desktop and a repository checkout:

```powershell
Copy-Item .env.example .env
docker compose up --build
```

The local stack provides:

- Admin UI: <http://localhost:5001>
- API: <http://localhost:5000>
- Swagger/OpenAPI UI: <http://localhost:5000/swagger>
- Liveness: <http://localhost:5000/health/live>
- Readiness (database and storage): <http://localhost:5000/health/ready>
- Optional operator dashboard: <http://localhost:5000/health/dashboard> (enable `Api__HealthDashboardEnabled=true`; restrict it at the reverse proxy)

The first API start applies database migrations and seeds the configured admin user and default workspace. Change the development password in `.env` before sharing a running instance. Never commit `.env` or an API token.

## Configuration

Cmsify loads the base `appsettings.json` for each application, then normal environment variables. In Development it also loads dotenv files: repository-level `.env` values load first, and app-level `.env` or `.env.local` files under `src/Cmsify.Api` and `src/Cmsify.Admin` override them. Copy the root [`.env.example`](.env.example) for the standard local setup, or use the app-specific templates when running an app directly.

Environment-variable names replace `:` with `__`. Comma-separated values are accepted for `Cors__AllowedOrigins` and `Media__AllowedMimeTypes`; use indexed names such as `TrustedProxy__TrustedProxies__0` for configuration arrays. Commented indexed values in the templates are optional examples, not active configuration.

### Shared hosting

| Setting | Default/example | Description |
| --- | --- | --- |
| `ASPNETCORE_ENVIRONMENT` | `Development` | Selects the ASP.NET Core environment; dotenv files load only in Development. |
| `AllowedHosts` | `*` | Host-header allow-list for the API or Admin host. Restrict this in production when appropriate. |

### API: connectivity, diagnostics, and ingress

| Setting | Default/example | Description |
| --- | --- | --- |
| `ConnectionStrings__Cmsify` | local PostgreSQL connection string | Required PostgreSQL connection for the API. Treat its password as a secret. |
| `Cors__AllowedOrigins` | `http://localhost:5001,https://localhost:7002` | Browser origins permitted to call the API. Use explicit HTTPS origins in production. |
| `Api__SwaggerEnabled` | `false` | Enables Swagger outside Development. |
| `Api__HealthDashboardEnabled` | `false` | Enables the internal `/health/dashboard` HTML operator view. Restrict this endpoint at the reverse proxy; it is not a public status page. |
| `Serilog__MinimumLevel__Default` | `Information` | Default API log level. |
| `Serilog__MinimumLevel__Override__Microsoft.AspNetCore` | `Warning` | Log level for ASP.NET Core framework events. |
| `Serilog__File__Enabled` | `false` | Enables the rolling API log file sink. |
| `Serilog__File__Path` | empty | Rolling file path when file logging is enabled. |
| `Serilog__File__RetainedFileCountLimit` | `14` | Number of rolled log files to retain. |
| `SecurityHeaders__PathOverrides__0__PathPrefix` | `/swagger` | Path prefix for the first security-header override. Add further overrides with the next numeric index. |
| `SecurityHeaders__PathOverrides__0__ReferrerPolicy` | `no-referrer` | Referrer policy for that path override. |
| `TrustedProxy__RequireTrustedProxiesInProduction` | `true` | Requires explicit trusted proxy configuration in production before forwarded headers are accepted. |
| `TrustedProxy__TrustedProxies__0` | optional IP address | First trusted reverse-proxy address; add indexes for additional proxies. |
| `TrustedProxy__TrustedNetworks__0` | optional CIDR network | First trusted reverse-proxy network; add indexes for additional networks. |
| `RateLimit__PerActor__PermitPerMinute` | `600` | Per authenticated actor/anonymous actor request limit per minute. |
| `RateLimit__PerIp__PermitPerMinute` | `60` | Per-client-IP request limit per minute. |

### API: authentication and first-run data

| Setting | Default/example | Description |
| --- | --- | --- |
| `Auth__BcryptCost` | `12` | BCrypt work factor for passwords and API client tokens. Higher values increase CPU cost. |
| `Auth__SessionAbsoluteExpiryHours` | `8` | Maximum API local-session lifetime when sliding expiry is disabled. |
| `Auth__SessionSlidingExpiryMinutes` | `480` | Renewed API local-session lifetime; set to `0` to use only the absolute expiry. |
| `Auth__SessionTouchIntervalSeconds` | `300` | Minimum interval between persistence updates for an active user session. |
| `Auth__ApiClientTouchIntervalSeconds` | `300` | Minimum interval between persistence updates for an active API client. |
| `Auth__Oidc__Enabled` | `false` | Enables API JWT bearer authentication and the Admin OIDC sign-in option. |
| `Auth__Oidc__Authority` | empty | OIDC issuer/authority used to validate JWT bearer tokens. Required when OIDC is enabled. |
| `Auth__Oidc__Audience` | `cmsify` | Expected JWT audience. |
| `Auth__Oidc__Audiences__0` | `cmsify` | First accepted JWT audience for the reusable API bearer registration; set this for every accepted audience. |
| `Auth__Oidc__ClientId` | empty | Admin OIDC client ID. Required for the interactive Admin sign-in option. |
| `Auth__Oidc__ClientSecret` | empty | Admin OIDC client secret. Store only in a secret manager or environment configuration. |
| `Auth__Oidc__RequireHttpsMetadata` | production default | Require HTTPS OIDC discovery metadata; keep enabled outside controlled development. |
| `Auth__Oidc__ClaimsMapping__Role` | `cmsify_role` | Claim name mapped to the Cmsify role. |
| `Auth__Oidc__ClaimsMapping__WorkspaceId` | `cmsify_workspace` | Claim name mapped to the optional workspace ID. |
| `Seed__DefaultWorkspace__Name` | `Default` | Name used only when creating the first workspace. |
| `Seed__DefaultWorkspace__Slug` | `default` | Slug used only when creating the first workspace. |
| `Seed__Admin__Email` | `admin@localhost` | Email for the first admin user. |
| `Seed__Admin__DisplayName` | `Cmsify Admin` | Display name for the first admin user. |
| `Seed__Admin__Password` | replace before use | Plaintext password for the first admin; use this or `PasswordHash`, never commit either. |
| `Seed__Admin__PasswordHash` | empty | Precomputed BCrypt hash alternative to `Password` for the first admin. |
| `Secrets__ActiveKeyId` | `development` locally | ID of the sole key used for new signing-secret writes. Production requires it to name a configured key. |
| `Secrets__EncryptionKeys__<keyId>` | development fixture locally | Canonical Base64 for exactly 32 bytes. Retain entries for every `v2` ciphertext that may still exist; use a secret manager in production. |
| `Secrets__EncryptionKey` | migration input only | Legacy `v1` read key. Configure only while existing `v1` ciphertext remains; it is never used for new writes. |
| `Secrets__Rotation__Enabled` | `false` | Enables the opt-in, bounded PostgreSQL signing-secret re-encryption worker. Start disabled and enable only for an observed window. |
| `Secrets__Rotation__BatchSize` | `100` | Maximum endpoint rows claimed per key-rotation cycle (1–500). |
| `Secrets__Rotation__DelaySeconds` | `5` | Delay between key-rotation cycles (1–3600 seconds). |

### API: media and background processing

| Setting | Default/example | Description |
| --- | --- | --- |
| `Storage__Provider` | `local` | Media storage provider: `local` or `s3`. |
| `Storage__Local__BasePath` | `.local/storage` | Local media root directory. |
| `Storage__Local__RootPath` | `.local/storage` | Backward-compatible local media root fallback; normally keep it equal to `BasePath`. |
| `Storage__S3__BucketName` | empty | S3-compatible bucket; required when `Storage__Provider=s3`. |
| `Storage__S3__Region` | `us-east-1` | S3 region. |
| `Storage__S3__AccessKey` | empty | S3 access key; treat as a secret. |
| `Storage__S3__SecretKey` | empty | S3 secret key; treat as a secret. |
| `Storage__S3__ServiceUrl` | empty | Optional S3-compatible endpoint URL. |
| `Storage__S3__ForcePathStyle` | automatic | Uses path-style addressing when `ServiceUrl` is set unless explicitly `false`; otherwise uses the provider default. |
| `Media__MaxFileSizeMb` | `50` | Maximum uploaded media-file size in MiB. |
| `Media__AllowedMimeTypes` | documented default list | Comma-separated MIME types or prefixes allowed for uploads. |
| `Media__Operations__ReconciliationIntervalSeconds` | `300` | Delay between bounded reconciliation cycles. |
| `Media__Operations__LeaseDurationSeconds` | `300` | Fenced deletion/checkpoint claim lease. Expired claims can be recovered by another replica. |
| `Media__Operations__BatchSize` | `100` | Maximum deletion, stale-upload, verification, or listing work per operation (1–1,000). |
| `Media__Operations__RetryBaseSeconds` | `30` | First failed-deletion retry delay. |
| `Media__Operations__RetryCapSeconds` | `3600` | Maximum exponential retry delay. |
| `Media__Operations__RetentionDays` | `30` | Recovery window before a user-deleted blob becomes eligible for purge. |
| `Media__Operations__OrphanGraceHours` | `24` | Minimum object age before an unowned managed-prefix object can be queued for deletion. |
| `Media__Operations__AbandonedUploadMinutes` | `30` | Age at which an incomplete database-first upload becomes failed cleanup work. |
| `Media__Operations__ManagedPrefixes__0/1` | `cmsify/media/`, `default/` | Fixed prefixes eligible for orphan scans; foreign prefixes are rejected by validation. |
| `Webhook__OutboxPollIntervalSeconds` | `30` | Durable outbox polling interval (1–3600 seconds). |
| `Webhook__OutboxLeaseDurationSeconds` | `300` | Outbox claim lease (1–1800 seconds). |
| `Webhook__OutboxBatchSize` | `100` | Maximum outbox rows claimed per cycle (1–500). |
| `Webhook__RetryIntervalSeconds` | `30` | Interval for polling webhook deliveries due for retry. |
| `Webhook__DeliveryLeaseDurationSeconds` | `300` | Delivery claim lease (1–1800 seconds). |
| `Webhook__DeliveryBatchSize` | `100` | Maximum due delivery rows claimed per cycle (1–500). |
| `Webhook__MaxAttempts` | `10` | Maximum webhook delivery attempts before failure. |
| `Webhook__RequestTimeoutSeconds` | `15` | Outbound webhook HTTP timeout (1–120 seconds). |
| `Webhook__AllowHttp` | `false` | Allows non-TLS webhook endpoints. Keep `false`; opt in only for controlled development. Webhook egress remains direct-only and does not provide a proxy mode. |
| `Webhook__RetentionDays` | `30` | Retention for processed outbox rows and successful delivery logs; retry and dead-letter diagnostics are retained. |
| `Webhook__CleanupBatchSize` | `100` | Per-table retention deletion limit per cleanup cycle (1–500). |
| `Webhook__CleanupIntervalSeconds` | `3600` | Durable-worker cleanup cadence (1–86400 seconds). |
| `Scheduler__PublishingIntervalSeconds` | `60` | Interval for processing scheduled content publication. |
| `Scheduler__PublishingLeaseDurationSeconds` | `300` | Durable scheduled-publication lease (1–1800 seconds). |
| `Scheduler__PublishingBatchSize` | `100` | Maximum due scheduled rows claimed per cycle (1–500). |

### Admin

| Setting | Default/example | Description |
| --- | --- | --- |
| `Admin__ApiBaseUrl` | `https://localhost:61241` | Required base URL used by the server-rendered Admin app to call the API. |
| `Admin__OidcProviderName` | `Authentik` | Provider name displayed on the Admin OIDC sign-in option. |
| `Admin__Auth__Session__SlidingWindowMinutes` | `60` | Sliding Admin cookie lifetime. Keep it no longer than the API session lifetime. |
| `Admin__Auth__Session__MaxLifetimeHours` | `24` | Absolute Admin cookie lifetime. |
| `Admin__DataProtection__KeysPath` | `.local/keys/admin` | Directory used to persist Admin Data Protection keys. Persist this path across production restarts. |
| `Auth__Oidc__TokenCache__Redis__Enabled` | `false` | Use the distributed OIDC token cache for multi-instance Admin deployments. |
| `Auth__Oidc__TokenCache__Redis__ConnectionString` | empty | Redis connection string required when the distributed token cache is enabled. |

## Run in production with Docker

The included [`docker-compose.prod.yml`](docker-compose.prod.yml) is a complete single-host deployment template with PostgreSQL and persistent database, media, and Admin session-key volumes. A reviewed SemVer tag publishes matching versioned `syntaxcircus/cmsify-api` and `syntaxcircus/cmsify-admin` images from one immutable commit; branch builds never publish. The template keeps literal API/Admin manifest digests beside the selected version, so upgrades are deliberate and reversible; `CMSIFY_IMAGE_PREFIX` can point it at a private registry instead.

On the production host, copy the environment template and replace every placeholder with a real value. Keep the resulting file outside source control.

```bash
cp docker-compose.prod.env.example .env.prod
cp docker-compose.prod.keyring.env.example .env.prod.keyring
# Edit .env.prod: set CMSIFY_VERSION, CMSIFY_IMAGE_PREFIX, database/admin passwords, keyring-file path (`./.env.prod.keyring` after copying), and CORS origin. Update the literal API/Admin manifest digests in docker-compose.prod.yml together with a version change.
# Edit the untracked keyring file: set its active key and retain every referenced older v2 key.
# Pull the exact published CMSIFY_VERSION from Docker Hub (or the configured registry).
docker compose --env-file .env.prod -f docker-compose.prod.yml pull
docker compose --env-file .env.prod -f docker-compose.prod.yml up -d
```

When using locally built images, run `./Build-CmsifyDocker.ps1 -ImageTag local`, update the literal API/Admin image references and digests in `docker-compose.prod.yml` from `docker image inspect`, set `CMSIFY_VERSION=local`, and run only `up -d`; see [Operating Cmsify](docs/operations.md#container-deployment) for private-registry publishing commands.

The Compose sample binds the Admin and API ports to `127.0.0.1` (`5001` and `5000` by default). Put a TLS-terminating reverse proxy such as Caddy or Nginx on the host in front of those ports; configure its public Admin origin as `CORS_ALLOWED_ORIGIN`. To expose the containers directly instead, remove `127.0.0.1:` from the two `ports` mappings and provide TLS outside this sample. Do not expose the PostgreSQL service publicly.

The template defaults to the named local-media volume. Set `Storage__Provider=s3` and its `Storage__S3__*` values to use S3-compatible object storage instead. To upgrade, update `CMSIFY_VERSION` and the literal matching image digests in `docker-compose.prod.yml` to the next published release, back up PostgreSQL and the media volume, then rerun `pull` and `up -d`. The API applies database migrations at startup. Check `/health/live` for process health and `/health/ready` for database and storage readiness.

The production Compose template reads signing-secret configuration only into the API container from `CMSIFY_API_KEYRING_ENV_FILE`; it does not pass the main Compose interpolation file through to the container. Copy [`docker-compose.prod.keyring.env.example`](docker-compose.prod.keyring.env.example) to an untracked deployment-only file, set `CMSIFY_API_KEYRING_ENV_FILE` to that path, and replace its active-key placeholder with a stable ID and canonical Base64 32-byte CSPRNG key. Add and retain every older `Secrets__EncryptionKeys__<keyId>` entry that may still be referenced by `v2` ciphertext. Missing, malformed, or placeholder keyring configuration fails API startup; never commit the deployment keyring file.

## Documentation

For a guide organized by task and audience, see the [documentation index](docs/README.md).

**Start here:**

- [Getting started](docs/getting-started.md) — local setup, first login, workspace, and API client token.
- [Authentication and authorization](docs/authentication-and-authorization.md) — Admin user sessions, API-client tokens, SDK bearer handling, roles, scopes, and token lifecycle.
- [Operating Cmsify](docs/operations.md) — configuration, production essentials, persistence, health checks, and security.
- [API compatibility](docs/api-compatibility.md) — `/api/v1` compatibility and deprecation policy.
- [Release runbook](docs/release-runbook.md) and [rollback runbook](docs/rollback-runbook.md) — authorized-release evidence and recovery boundaries.
- [Security policy](SECURITY.md) and [support policy](SUPPORT.md) — private vulnerability reporting and public support channels.

**Build integrations and content models:**

- [Integrating with the API](docs/integrating.md) — authentication, workspace-scoped requests, REST conventions, and server-side consumers.
- [Content modeling](docs/content-modeling.md) — how templates, components, and publishable content items fit together.
- [Components and choice sets](docs/content-components-and-choice-sets.md) — reusable inline schemas, nested values, and immutable pick-list revisions.
- [Reusable model packages](docs/packages.md) — portable template, component, and picklist bundles.

**SDKs and project work:**

- [TypeScript client](sdk/typescript/README.md) — `@cmsify/client` usage, framework examples, pagination, errors, and regeneration.
- [.NET client](sdk/dotnet/README.md) — NuGet packages, dependency injection, authentication, and service examples.
- [Changelog](CHANGELOG.md) — released and upcoming changes.
- [Project roadmap](docs/roadmap.md) — committed future work, when available.

## Repository layout

| Path | Purpose |
| --- | --- |
| `src/Cmsify.Core` | Domain entities, contracts, validation, and business rules |
| `src/Cmsify.Infrastructure` | EF Core/PostgreSQL, storage, authentication, audit, and background services |
| `src/Cmsify.Api` | Versioned HTTP API, OpenAPI, middleware, and health endpoints |
| `src/Cmsify.Admin` | Blazor administration UI; all data access goes through the API |
| `sdk/typescript` | Generated OpenAPI types and the ergonomic TypeScript client |
| `sdk/dotnet` | Shared wire contracts and the `SyntaxCircus.Cmsify.Client` NuGet client |
| `examples` | Next.js, Astro, and SvelteKit server-side integration examples |
| `tests` | Unit and HTTP/integration test projects |

## Development commands

Cmsify pins .NET SDK `10.0.400` and checks in a NuGet lock beside every solution project. Verify the SDK and use locked restore before building:

```powershell
dotnet --version
dotnet restore Cmsify.slnx --locked-mode
dotnet build Cmsify.slnx --configuration Release --no-restore --no-incremental
dotnet test Cmsify.slnx --configuration Release --no-build --verbosity minimal
```

The ordinary public restore is currently gated by the unpublished `SyntaxCircus.Http.Resilience` `0.2.0-cmsify.1` package. Maintainers with the approved ignored local feed must use the local restore command in [Quality and capacity operations](docs/performance.md); hosted/public restore is not certified until the user publishes those exact bytes or approves and pins a stable replacement.

For the TypeScript client:

```powershell
Set-Location sdk/typescript
npm ci
npm run typecheck
npm test
npm run build
```

See [Quality and capacity operations](docs/performance.md) for lock regeneration, warning enforcement, deterministic capacity filters, coverage summaries, scheduled timing reports, and the single-MSBuild-node final command. See [`AGENTS.md`](AGENTS.md) for generated-file rules and architecture conventions.

## Contributing

Issues and pull requests are welcome. See the [contributing guide](docs/contributing.md) for local setup, validation, API/SDK compatibility, documentation, and pull-request expectations. Keep changes focused, add observable tests, and describe any breaking public-contract change.

## License

Cmsify is licensed under the [GNU Affero General Public License v3.0 or later](LICENSE).
