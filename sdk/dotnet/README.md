# Cmsify .NET SDK

The .NET SDK targets .NET 10 and is published by Syntax Circus LLC under two packages:

```powershell
dotnet add package SyntaxCircus.Cmsify.Contracts
dotnet add package SyntaxCircus.Cmsify.Client
```

`SyntaxCircus.Cmsify.Contracts` is the shared, handwritten wire-contract library consumed by the API, admin application, and client. `SyntaxCircus.Cmsify.Client` provides typed service groups over `HttpClient`.

Build and test locally with `dotnet test Cmsify.slnx`. A maintainer can dry-run packaging with `./publish.ps1 -DryRun`; publishing requires `NUGET_API_KEY`. The GitHub Actions workflow runs on a published release or manual dispatch and uses the `NUGET_API_KEY` repository secret.

See [integration guidance](../../docs/integrating.md) for DI and server-side application examples.
