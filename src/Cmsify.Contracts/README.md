# SyntaxCircus.Cmsify.Contracts

Shared wire contracts for the Cmsify API and first-party .NET clients. Install this package when an application needs the public request, response, pagination, enum, or ProblemDetails models without the HTTP client implementation.

List responses use `PagedResponse<T>` with `items`, `totalCount`, `page`, `pageSize`, and `totalPages`. List queries use one-based `page` and `pageSize` from 1 through 100; invalid values are rejected with RFC 7807 ProblemDetails rather than normalized.

The package targets .NET 10 and is licensed under AGPL-3.0-only. See the repository documentation for API and client usage.
