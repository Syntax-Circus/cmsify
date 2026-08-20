# Cmsify .NET SDK

The .NET SDK targets .NET 10 and is published by Syntax Circus LLC under two packages:

```powershell
dotnet add package SyntaxCircus.Cmsify.Contracts
dotnet add package SyntaxCircus.Cmsify.Client
```

`SyntaxCircus.Cmsify.Contracts` is the shared, handwritten wire-contract library consumed by the API, admin application, and client. `SyntaxCircus.Cmsify.Client` provides typed service groups over `HttpClient`.

Package versions are supplied by the repository-level GitVersion configuration. Feature branches produce prerelease versions in the form `0.1.0-<branch>.<commit-count>`; `main` produces the stable version defined by `GitVersion.yml`. Build and test locally with `dotnet test Cmsify.slnx`. A maintainer can dry-run packaging with `./publish.ps1 -DryRun`; publishing requires `NUGET_API_KEY`. The GitHub Actions workflow checks out full history so GitVersion can calculate the same version and runs on a published release or manual dispatch.

See [integration guidance](../../docs/integrating.md) for DI and server-side application examples.
