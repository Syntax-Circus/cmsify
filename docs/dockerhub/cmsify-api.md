# Cmsify API

The Cmsify API is the headless CMS service for composable, versioned content models. It provides a versioned HTTP API for workspaces, templates, components, choice sets, content, media, webhooks, audit history, and API clients.

## Deploy with Cmsify Admin

Deploy this image together with `syntaxcircus/cmsify-admin` by using the production Compose template. The template configures PostgreSQL, persistent media storage, Admin Data Protection keys, loopback-bound service ports, and the API/Admin connection.

1. Follow the [production deployment guide](https://docs.cmsify.dev/operations/) to prepare configuration and secrets.
2. Copy [`docker-compose.prod.env.example`](https://github.com/Syntax-Circus/cmsify/blob/main/docker-compose.prod.env.example) to a private environment file and set the exact `CMSIFY_VERSION`, `CMSIFY_API_IMAGE_DIGEST`, and `CMSIFY_ADMIN_IMAGE_DIGEST` values published for that release.
3. Start the paired services with the [production Compose template](https://github.com/Syntax-Circus/cmsify/blob/main/docker-compose.prod.yml).

Use a versioned tag in production. The moving `latest` tag is provided for evaluation and development only.

## API operations

- OpenAPI/Swagger is available at `/swagger` when enabled with `Api__SwaggerEnabled=true`.
- `/health/live` verifies that the process is running; `/health/ready` verifies database and storage dependencies.
- Use least-privilege API clients and keep `cmsify_...` tokens in server-side secret storage.

## Learn more

- [Cmsify documentation](https://docs.cmsify.dev/)
- [API integration guide](https://docs.cmsify.dev/integrating/)
- [Configuration reference](https://docs.cmsify.dev/configuration/)
- [Source code](https://github.com/Syntax-Circus/cmsify)
- [License: AGPL-3.0-or-later](https://github.com/Syntax-Circus/cmsify/blob/main/LICENSE)

## Support

Cmsify is community-maintained and published as-is. Support is best effort and no SLA is provided. Report bugs or propose improvements through [GitHub Issues](https://github.com/Syntax-Circus/cmsify/issues).
