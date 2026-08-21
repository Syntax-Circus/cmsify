# Operating Cmsify

## Configuration

Cmsify reads `appsettings.json`, environment variables, and development dotenv files. Copy `.env.example` for local development. Repository-level `.env` values load before app-level files such as `src/Cmsify.Api/.env.local`, so the closer app-level file wins. See the root [configuration inventory](../README.md#configuration) for every supported API and Admin setting, its default, and its production guidance.

Use environment-specific secret management in production. Do not commit credentials, connection strings containing passwords, encryption keys, or bearer tokens.

## Container deployment

The repository's [`docker-compose.prod.yml`](../docker-compose.prod.yml) is a complete single-host deployment template with PostgreSQL and named volumes for database data, local media, and Admin Data Protection keys. It defaults to versioned `syntaxcircus/cmsify-api` and `syntaxcircus/cmsify-admin` images. Use `CMSIFY_IMAGE_PREFIX` to pull from a private registry/repository instead.

```powershell
# Build both local images (including amd64 and arm64 tags).
./Build-CmsifyDocker.ps1 -ImageTag local

# Authenticate separately, then publish a multi-architecture test build.
docker login registry.example.internal
./Build-CmsifyDocker.ps1 -ImageTag 0.1.0-test -Registry registry.example.internal/cmsify -Push
```

Copy [`docker-compose.prod.env.example`](../docker-compose.prod.env.example) to a private environment file, set `CMSIFY_VERSION=local` (or an exact published image tag), set `CMSIFY_IMAGE_PREFIX` to the registry prefix when applicable, replace all placeholder secrets, then run Compose with `--env-file`. The script requires `-Registry` whenever `-Push` is used, and it never accepts registry credentials; authenticate with `docker login` first.

Bind the API and Admin ports to loopback and terminate TLS at a host reverse proxy. If ports are published publicly instead, secure them with TLS and configure the proxy/trusted-forwarded-header settings for that deployment.

## Persistence and upgrades

The API runs EF Core migrations at startup. Back up PostgreSQL and the configured media storage before upgrades. Treat migrations and media as a matched deployment: restoring only the database or only the media volume can leave content references unusable.

The development Compose file persists PostgreSQL under `local/postgres` and local media under `local/media`. The production Compose file uses named volumes (`postgres_data`, `media_data`, and `admin_data_protection_keys`). Choose an external PostgreSQL and object-storage backup policy for production rather than relying on container lifecycle.

### Backup, restore, and upgrade checklist

Use this checklist before each production upgrade:

1. Confirm `/health/ready` is healthy and identify the current image tag.
2. Back up PostgreSQL and media together. For the supplied Compose deployment, back up the `postgres_data` and `media_data` volumes; for S3, back up the bucket or confirm its versioning and recovery policy.
3. Record the Admin Data Protection key volume (`admin_data_protection_keys`) as part of the deployment backup. Losing it signs out existing Admin sessions.
4. Update `CMSIFY_VERSION`, then run `docker compose --env-file .env.prod -f docker-compose.prod.yml pull` when using a registry image, followed by `up -d`.
5. Verify `/health/live`, `/health/ready`, Admin sign-in, and a representative media download.

To restore, stop application traffic, restore the database and its matching media snapshot, restore Data Protection keys when retaining sessions matters, then start the stack and wait for readiness. Do not restore only one of the database and media snapshots: content can otherwise point at missing or mismatched files. If the upgraded stack does not become ready, return to the prior image tag and restore the matched backup before accepting traffic.

## Network and TLS

Place the API and admin UI behind a trusted reverse proxy that terminates TLS and forwards the required host/proto headers. Set `Admin__ApiBaseUrl` to the API address reachable by the admin service. Allow only known frontend origins through `Cors__AllowedOrigins`.

API clients use `Authorization: Bearer cmsify_...`. Keep those tokens server-side, assign an expiry where possible, and rotate or revoke them when a service changes ownership. A rotated token is returned once and cannot be recovered later.

## Health and diagnostics

- `GET /health/live` confirms that the process is running.
- `GET /health/ready` confirms that the database and storage dependencies are reachable.
- Swagger/OpenAPI is served at `/swagger` when enabled.
- Responses include RFC 7807 ProblemDetails for failures. The API and SDK use correlation IDs; retain the `X-Correlation-Id` and `traceId` values when escalating an error.

Monitor readiness separately from liveness. A failed readiness check should remove the instance from traffic while leaving the process available for diagnosis.

## Incident response checklist

When an integration fails, record the HTTP status, ProblemDetails `type`, `detail`, `traceId`, correlation ID, workspace, and client identity (never the token). Then check token expiry/revocation, workspace scope, role, CORS/proxy configuration, rate-limit responses, and database/storage readiness in that order. Escalate with those details plus the deployment image tag and the affected time range.
