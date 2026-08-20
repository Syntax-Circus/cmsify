# SyntaxCircus.Cmsify.Client

Typed .NET client for connecting to and managing Cmsify. The package uses the shared `SyntaxCircus.Cmsify.Contracts` wire models and supports both direct construction and Microsoft dependency injection.

```powershell
dotnet add package SyntaxCircus.Cmsify.Client
```

```csharp
using Microsoft.Extensions.DependencyInjection;
using SyntaxCircus.Cmsify;

services.AddCmsifyClient(options =>
{
    options.BaseUrl = new Uri("https://cms.example.com");
    options.ApiToken = configuration["Cmsify:ApiToken"];
});

var posts = await cms.Content.ListAsync(
    workspaceId,
    new ContentListQuery(null, null, null, ContentStatus.Published, null, null, null, "featured", null, null, null, null, false, null, "publishedAt", true, 1, 10),
    cancellationToken);
```

The client also exposes templates, media, picklists, tags, webhooks, audit, users, API clients, settings, packages, authentication, and health services. Requests attach bearer authentication and correlation IDs, map RFC 7807 failures to `CmsifyApiException`, retry transient `429`/`5xx` responses, and preserve ETags for mutation concurrency.

Use `CmsifyClientOptions.TokenProvider` for rotating or request-time credentials. Keep tokens in server-side secret storage and never send them to browser code.

The shared `SyntaxCircus.Cmsify.Contracts` package contains the public request, response, enum, pagination, and dynamic JSON models without the HTTP client implementation.
