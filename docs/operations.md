# Operating Cmsify

## Configuration

Cmsify reads `appsettings.json`, environment variables, and development dotenv files. Copy `.env.example` for local development. Repository-level `.env` values load before app-level files such as `src/Cmsify.Api/.env.local`, so the closer app-level file wins.

Important production settings include:

- `ConnectionStrings__Cmsify` — PostgreSQL connection string.
- `Seed__Admin__Password` or `Seed__Admin__PasswordHash` — required when the initial admin is seeded.
- `Secrets__EncryptionKey` — required by the production Compose configuration.
- `Storage__Provider` and the corresponding local/S3 settings — media persistence.
- `Cors__AllowedOrigins` — explicit browser origins; do not use a wildcard.
- `Api__SwaggerEnabled` — keep Swagger disabled unless it is intentionally exposed.
- `Auth__Oidc__Enabled` and its authority/audience/claim mappings — optional OIDC/JWT authentication.
- `RateLimit__PerActor__PermitPerMinute` and `RateLimit__PerIp__PermitPerMinute` — rate-limit ceilings.

Use environment-specific secret management in production. Do not commit credentials, connection strings containing passwords, encryption keys, or bearer tokens.

## Persistence and upgrades

The API runs EF Core migrations at startup. Back up PostgreSQL and the configured media storage before upgrades. Treat migrations and media as a matched deployment: restoring only the database or only the media volume can leave content references unusable.

The development Compose file persists PostgreSQL under `local/postgres` and local media under `local/media`. The production Compose file uses named volumes (`postgres_data` and `media_data`). Choose an external PostgreSQL and object-storage backup policy for production rather than relying on container lifecycle.

## Network and TLS

Place the API and admin UI behind a trusted reverse proxy that terminates TLS and forwards the required host/proto headers. Set `Admin__ApiBaseUrl` to the API address reachable by the admin service. Allow only known frontend origins through `Cors__AllowedOrigins`.

API clients use `Authorization: Bearer cmsify_...`. Keep those tokens server-side, assign an expiry where possible, and rotate or revoke them when a service changes ownership. A rotated token is returned once and cannot be recovered later.

## Health and diagnostics

- `GET /health/live` confirms that the process is running.
- `GET /health/ready` confirms that the database and storage dependencies are reachable.
- Swagger/OpenAPI is served at `/swagger` when enabled.
- Responses include RFC 7807 ProblemDetails for failures. The API and SDK use correlation IDs; retain the `X-Correlation-Id` and `traceId` values when escalating an error.

Monitor readiness separately from liveness. A failed readiness check should remove the instance from traffic while leaving the process available for diagnosis.

## Incident basics

When an integration fails, record the HTTP status, ProblemDetails `type`, `detail`, `traceId`, correlation ID, workspace, and client identity (never the token). Check token expiry/revocation, workspace scope, role, CORS/proxy configuration, rate-limit responses, and database/storage readiness in that order.
