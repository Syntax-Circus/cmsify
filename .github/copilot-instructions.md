# Copilot instructions for Cmsify

The repository-wide agent and contributor guidance is maintained in [`AGENTS.md`](../AGENTS.md). Read it first for architecture, commands, API conventions, generated-file rules, and change hygiene.

## GitHub-specific checks

- The .NET workflow runs `dotnet test Cmsify.slnx --configuration Release --no-restore --verbosity minimal`.
- The TypeScript workflow runs `npm run generate:check`, `npm run typecheck`, `npm test`, and `npm run build` from `sdk/typescript`.
- The accessibility workflow covers the admin application with axe-core checks.
- Pull requests that change API contracts should include regenerated SDK output and updated consumer documentation/examples.
