# Getting started

This guide takes a new operator from a checkout to a usable local Cmsify instance and a token for an integration.

## Prerequisites

- Docker Desktop with Compose support
- Git
- PowerShell, Bash, or an equivalent shell

## Run the local stack

From the repository root:

```powershell
Copy-Item .env.example .env
docker compose up --build
```

The API listens on `http://localhost:5000` and the admin UI on `http://localhost:5001`. Swagger is at `http://localhost:5000/swagger`.

Cmsify applies migrations on API startup. It also creates the default workspace and the first admin user when the database is empty. The seeded values come from `Seed:DefaultWorkspace:*` and `Seed:Admin:*` configuration keys; the sample password is for local development only.

## Sign in and create an API client

1. Open the admin UI and sign in with `Seed:Admin:Email` and `Seed:Admin:Password`.
2. Change the temporary password when prompted.
3. Create an API client from the API client/settings area.
4. Give the client the smallest role and workspace scope it needs. A server-side content reader normally needs the `Reader` role and one workspace.
5. Copy the returned `cmsify_...` token immediately. Cmsify stores only a hash and never shows the raw token again.

Use the token only from a server-side process or secret store. Do not put it in browser JavaScript, a public environment variable, a committed `.env` file, or a client-side bundle.

## Verify the API

Set the URL, token, and workspace ID/slug in your application, then call a read endpoint. The TypeScript example is the shortest path:

```powershell
Set-Location sdk/typescript
npm ci
npm test
```

For an application example, see [`examples/nextjs-app-router/cmsify.ts`](../examples/nextjs-app-router/cmsify.ts) and the [integration guide](integrating.md).

## Stop and reset local data

Stop the stack with `Ctrl+C`. The development database and media are stored under `local/`, which is ignored by Git. Remove those directories only when you intentionally want to recreate the local database and uploaded files.
