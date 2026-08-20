# Cmsify .NET SDK

The .NET SDK targets .NET 10 and is published by Syntax Circus LLC under two packages:

```powershell
dotnet add package SyntaxCircus.Cmsify.Contracts
dotnet add package SyntaxCircus.Cmsify.Client
```

`SyntaxCircus.Cmsify.Contracts` is the shared, handwritten wire-contract library consumed by the API, admin application, and client. `SyntaxCircus.Cmsify.Client` provides typed service groups over `HttpClient`.

`SyntaxCircus.Cmsify.Client` also contains an opt-in `IMemoryCache` content-read facade. Applications that need a shared cache can instead add `SyntaxCircus.Cmsify.Client.DistributedCaching`, which works with their configured `IDistributedCache` provider (such as Redis) and does not bring a Redis dependency.

Package versions are supplied by the repository-level GitVersion configuration. Feature branches produce prerelease versions in the form `0.1.0-<branch>.<commit-count>`; `main` produces the stable version defined by `GitVersion.yml`. Build and test locally with `dotnet test Cmsify.slnx`. A maintainer can package locally with `./publish.ps1`; publication uses GitHub Actions NuGet Trusted Publishing, with the NuGet account configured in the `release` environment and its username stored as `secrets.NUGET_USER`. The workflow checks out full history so GitVersion can calculate the same version, then uses the OIDC token exchange provided by `NuGet/login@v1`.

See [integration guidance](../../docs/integrating.md) for DI and server-side application examples.
