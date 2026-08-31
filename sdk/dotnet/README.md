# Cmsify .NET SDK

The .NET SDK targets .NET 10 and is published by Syntax Circus LLC under two packages:

```powershell
dotnet add package SyntaxCircus.Cmsify.Contracts
dotnet add package SyntaxCircus.Cmsify.Client
```

`SyntaxCircus.Cmsify.Contracts` is the shared, handwritten wire-contract library consumed by the API, admin application, and client. `SyntaxCircus.Cmsify.Client` provides typed service groups over `HttpClient`.

Collection methods return `PagedResponse<T>` and accept one-based `page` and `pageSize` values; `pageSize` must be between 1 and 100. Service groups that back all-item selectors or histories also expose `ListAll*Async` helpers, which request successive pages without changing the HTTP contract.

`SyntaxCircus.Cmsify.Client` also contains an opt-in `IMemoryCache` content-read facade. Applications that need a shared cache can instead add `SyntaxCircus.Cmsify.Client.DistributedCaching`, which works with their configured `IDistributedCache` provider (such as Redis) and does not bring a Redis dependency.

The SDK targets .NET 10 only for v1 and is licensed under MIT. Local source builds use the deliberately non-publishable `0.0.0-local` version. A reviewed SemVer tag (`vX.Y.Z`, including prereleases such as `v1.0.0-rc.1`) is the sole release input: it supplies one version and source commit to every package, the TypeScript SDK, and both containers. Branch and pull-request builds validate only; they never publish or create tags.

See [integration guidance](../../docs/integrating.md) for DI and server-side application examples.
