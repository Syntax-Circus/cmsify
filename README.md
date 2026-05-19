# Cmsify

Cmsify is a headless CMS with composable, versioned templates, built with .NET 10, PostgreSQL, EF Core, and a Blazor admin UI.

The project is implemented from the numbered plan in `docs\project plan`.

## Local development with Docker

Copy `.env.example` to `.env` at the repository root and change any local secrets or ports you need. App-specific overrides can also live beside each app as `src\Cmsify.Api\.env.local` or `src\Cmsify.Admin\.env.local`; in development, Cmsify loads repo-level files first and app-level files last so closer settings win. `.env` and `.env.local` files are ignored by Git.

Start the full local stack from the repository root:

```powershell
docker compose up --build
```

The API is exposed at `http://localhost:5000`, Swagger is available at `http://localhost:5000/swagger`, and the admin UI is exposed at `http://localhost:5001`. The API readiness probe is `GET /health/ready`; liveness is `GET /health/live`.
