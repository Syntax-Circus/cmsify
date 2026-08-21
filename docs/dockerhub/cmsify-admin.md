# Cmsify Admin

Cmsify Admin is the server-rendered Blazor administration UI for the Cmsify headless CMS. It manages workspaces, templates, components, choice sets, content, media, users, API clients, webhooks, packages, and audit history through the paired Cmsify API.

## Deploy with Cmsify API

Deploy this image together with `syntaxcircus/cmsify-api` by using the production Compose template. The template configures the Admin-to-API connection, persistent Data Protection keys, and the PostgreSQL and media services required by the API.

1. Follow the [production deployment guide](https://docs.cmsify.dev/operations/) to prepare configuration and secrets.
2. Copy [`docker-compose.prod.env.example`](https://github.com/Syntax-Circus/cmsify/blob/main/docker-compose.prod.env.example) to a private environment file and set an exact `CMSIFY_VERSION`.
3. Start the paired services with the [production Compose template](https://github.com/Syntax-Circus/cmsify/blob/main/docker-compose.prod.yml).

Use a versioned tag in production. The moving `latest` tag is provided for evaluation and development only. Persist the Admin Data Protection key volume across restarts so active sessions remain valid.

## Learn more

- [Cmsify documentation](https://docs.cmsify.dev/)
- [Getting started](https://docs.cmsify.dev/getting-started/)
- [Authentication and authorization](https://docs.cmsify.dev/authentication-and-authorization/)
- [Source code](https://github.com/Syntax-Circus/cmsify)
- [License: AGPL-3.0-or-later](https://github.com/Syntax-Circus/cmsify/blob/main/LICENSE)

## Support

Cmsify is community-maintained and published as-is. Support is best effort and no SLA is provided. Report bugs or propose improvements through [GitHub Issues](https://github.com/Syntax-Circus/cmsify/issues).
