# SyntaxCircus.Cmsify.Core

Cmsify's existing `Cmsify.Core` assembly packaged for .NET 10 hosts. Namespaces remain `Cmsify.Core.*`.

Contains domain entities and enums, validation, repository/service contracts, direct workspace request handlers and `Cmsify.Core.ContentQueries.IListContentRequestHandler` for ordinary/resolved content lists. Use the matching `SyntaxCircus.Cmsify.Infrastructure` version for Cmsify's PostgreSQL implementations and DI registration. The content-query contract is qualified in local candidate `0.8.9-query.1`; released 0.8.8 does not contain it.

The engine is licensed **AGPL-3.0-or-later**, as specified by the included `LICENSE`. The MIT license of Cmsify's separate SDK and component packages does not apply to the engine.

Packaging does not establish complete application embedding or SQLite support. See [engine package qualification](https://github.com/Syntax-Circus/cmsify/blob/main/docs/engine-packages.md) for the measured scope and remaining limitations.
