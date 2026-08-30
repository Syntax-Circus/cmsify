# API compatibility policy

Cmsify treats the live OpenAPI document for `/api/v1` as its public compatibility contract. The checked-in snapshot and TypeScript output are derived artifacts, not alternative authorities.

Within `/api/v1`, additive optional fields, endpoints, and optional query parameters are permitted. Existing routes, required request fields, response fields, field meanings, status/error semantics, and authentication/authorization behavior remain compatible. Consumers must tolerate unknown JSON fields. Response enum additions require compatibility review and may be breaking unless that wire type is explicitly documented as extensible.

Deprecated stable `/api/v1` operations and fields remain available for at least 12 months and through at least one subsequent stable minor release. Each deprecation record identifies its owner, announcement date, earliest removal date/version, and replacement/migration path. Where HTTP applies, responses send `Deprecation: true` and an absolute-date `Sunset` header. No endpoint receives those headers unless a documented deprecation decision exists.

Removing or materially changing a public `/api/v1` contract requires `/api/v2`, except for an explicitly reviewed emergency exception. Such an exception must be approved through the protected GitHub environment named `api-breaking-change-approved`, with non-empty `API_BREAKING_CHANGE_EVIDENCE` configured as an environment secret. Labels, commit messages, workflow inputs, tool failures, and ordinary review never waive the protected emergency/breaking approval gate.

Pull requests compare the live document built from the PR head against the exact target-branch commit (`pull_request.base.sha`). Pushes compare the live document at the pushed commit against the event's exact previous tip (`before`); a newly-created branch falls back to the pushed commit's first parent. The breaking comparison uses oasdiff `1.28.0` and is scoped to `/api/v1`.
