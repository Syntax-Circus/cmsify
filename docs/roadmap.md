# Roadmap

This file records follow-up work that is intentionally separate from the current documentation and runtime implementation.

## Planned

- [ ] Publish a first-party .NET API client as a NuGet package. Define the package name, supported .NET versions, generated-vs-handwritten layering, authentication/token abstractions, workspace scoping, ProblemDetails and ETag handling, release/version compatibility with `/api/v1`, tests, samples, and publishing workflow. Keep the public client server-safe and aligned with the integration guarantees documented in [`integrating.md`](integrating.md).
