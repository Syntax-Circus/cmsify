# Webhook Egress and Secret Rotation Design

**Date:** 2026-08-26
**Status:** Approved in chat; pending written-spec review
**Remediation scope:** Task 7; findings F-08 and F-18

## Summary

Cmsify will close the time-of-check/time-of-use gap in webhook destination validation by resolving each destination once per delivery attempt and connecting only to the resulting validated public addresses. The request retains the original hostname for HTTP and TLS identity checks. Redirects and ambient proxies remain disabled, and every durable retry performs a fresh resolution and validation.

Webhook signing secrets will be protected by a versioned encryption keyring. New ciphertext identifies the key used to create it, retained keys can read older ciphertext, and the existing `v1` format remains readable during migration. An explicitly enabled, bounded PostgreSQL worker will re-encrypt stored secrets without overwriting concurrent changes.

No public API shape, OpenAPI contract, or webhook signing contract changes.

## Goals

- Prevent DNS rebinding or a second resolver lookup from changing the destination after validation.
- Reject any destination whose resolved set contains a non-global or special-use address.
- Preserve the original destination hostname for the `Host` header, TLS SNI, and certificate validation.
- Ensure durable retry attempts repeat DNS validation and use only that attempt's approved addresses.
- Require production encryption keys with an exact, testable format and reject obvious low-entropy or development values.
- Support versioned encryption keys, backward reads, safe online re-encryption, restart recovery, and multiple application replicas.
- Provide actionable metrics, logs, configuration examples, and an operator runbook.

## Non-goals

- A general-purpose outbound proxy or configurable webhook proxy mode.
- Following redirects, including redirects to another validated host.
- DNS caching across delivery attempts.
- A new public endpoint or Admin UI for encryption-key rotation.
- Automatic retirement or deletion of old keys.
- Changing endpoint signing secrets as part of encryption-key rotation.

## Constraints and assumptions

- Webhook work is already durable and each retry is represented by a distinct delivery attempt.
- PostgreSQL is the authoritative store and can provide row locking with `SKIP LOCKED`.
- Destination validation and transport construction are internal contracts, so they can change without altering the public API.
- Direct outbound HTTPS is available in the supported v1 deployment model. Installations that require an explicit egress proxy are outside this release's supported webhook-delivery topology.
- Key identifiers are operational metadata, not secrets. Key material, plaintext secrets, ciphertext, and credential-bearing URLs are secrets.

## Considered approaches

### Webhook egress

1. **Direct pinned connections — selected.** Resolve and validate in-process, then connect directly to an approved address while preserving the original TLS host. This closes the current gap with the smallest operational footprint.
2. **Trusted egress proxy.** Send all webhooks through a controlled proxy that owns DNS and address policy. This centralizes policy but adds another production service, availability dependency, and configuration surface that v1 does not otherwise need.
3. **Validate and then use the normal `HttpClient` resolver.** This is the current pattern and is rejected because the connection-time DNS answer can differ from the validated answer.

Direct mode explicitly sets `UseProxy = false`. Silently honoring machine or environment proxy settings would delegate DNS resolution to an unvalidated component and reintroduce the vulnerability.

### Secret rotation

1. **Opt-in hosted rotation worker — selected.** Bounded batches, database concurrency controls, metrics, and restart safety support online deployments without expanding the public API.
2. **One migration that rewrites every secret.** Rejected because a long data migration increases deployment and rollback risk, cannot safely depend on runtime secret configuration, and is awkward for large installations.
3. **Administrative rotation endpoint.** Rejected because it creates a new privileged public operation and still requires orchestration, concurrency handling, and observability.

## Architecture

```text
durable delivery attempt
        |
        v
destination parser -> DNS resolver -> global-address policy
        |                                  |
        | invalid                          | approved URI + address set
        v                                  v
record failed attempt              pinned HTTP transport
                                           |
                              direct socket to approved IP
                                           |
                         HTTP Host + TLS SNI use original host

stored endpoint secret -> version-aware protector -> plaintext in memory
        ^                         |
        |                         v
PostgreSQL rotation worker <- active-key v2 ciphertext
```

### Destination resolution and validation

An injectable DNS resolver performs the only hostname lookup for a delivery attempt. The destination validator returns an immutable validated destination containing:

- the normalized original URI;
- the original hostname and port used for HTTP and TLS identity;
- the complete set of approved IP addresses from that lookup.

The validator accepts absolute `https` URIs without embedded credentials. It accepts `http` only when the existing `Webhook:AllowHttp` option is explicitly enabled; the default remains HTTPS-only. An empty answer, resolver exception, cancellation, malformed URI, unsupported scheme, or prohibited address fails closed. If even one answer is prohibited, the complete result is rejected; the validator never filters out a private member and proceeds with the public subset.

The address policy allows only globally routable unicast destinations. Its explicit IPv4 and IPv6 prefix tables are based on the IANA special-purpose address registries and are covered by table-driven boundary tests. They include unspecified, loopback, private/unique-local, link-local, shared-address, documentation, benchmarking, protocol-assignment, discard, multicast, reserved, and other non-global ranges. IPv4-mapped IPv6 values are normalized and evaluated under the IPv4 policy. Policy-table provenance and the review date are recorded next to the implementation so registry changes can be audited.

### Pinned HTTP transport

The validated destination is attached to the request through an internal typed request option. A dedicated `SocketsHttpHandler.ConnectCallback` reads that option, verifies that the connection context still names the validated original host and port, and opens a socket only to an address in the immutable approved set. It tries approved candidates with the request cancellation token and bounded connect timeout. It never calls DNS and never falls back to the hostname when candidates fail.

The request URI remains the normalized original URI. `SocketsHttpHandler` therefore performs TLS SNI and certificate validation against the original hostname even though the callback connects the socket to a numeric address. The HTTP `Host` header also remains the original authority.

Transport configuration is fail closed:

- `AllowAutoRedirect = false`;
- `UseProxy = false`;
- no cross-attempt connection reuse, so a retry cannot reuse a connection selected by an earlier DNS answer;
- missing or inconsistent pin metadata aborts before any connection;
- normal TLS certificate validation remains enabled.

The durable processor obtains a new validated destination for every attempt, including retries. A DNS rejection, connect failure, timeout, TLS failure, or non-success HTTP response is recorded using the existing attempt and retry/dead-letter state machine. A 3xx response is a failed response, not an instruction to resolve or connect elsewhere.

## Versioned secret protection

### Configuration

The effective configuration is:

```text
Secrets:ActiveKeyId
Secrets:EncryptionKeys:<keyId>
Secrets:EncryptionKey                 # legacy v1 reads only
Secrets:Rotation:Enabled              # default false
Secrets:Rotation:BatchSize            # bounded and range-validated
Secrets:Rotation:Delay                # bounded idle/error backoff
```

Key IDs are 1–64 characters from `[A-Za-z0-9_-]` and cannot contain the ciphertext separator. Each configured key value must be canonical Base64 that decodes to exactly 32 bytes. Re-encoding the bytes must reproduce the configured value exactly.

All environments require usable active-key configuration for new writes. Production additionally rejects:

- known checked-in development values;
- fewer than 16 distinct byte values;
- repeated fixed-size blocks or a single repeating byte pattern;
- a measured byte-distribution entropy below 3.5 bits per byte.

These checks detect obvious mistakes; they do not claim to prove how the key was generated. Operations documentation therefore requires a cryptographically secure random-number generator and provides PowerShell and platform-neutral generation examples. Key validation occurs during startup and fails before the application becomes ready.

### Ciphertext formats

Existing ciphertext remains:

```text
v1.<nonce>.<tag>.<ciphertext>
```

New writes use:

```text
v2.<keyId>.<nonce>.<tag>.<ciphertext>
```

`v2` uses AES-256-GCM with a fresh 12-byte nonce and 16-byte authentication tag. The active key ID is included as authenticated associated data so the identifier cannot be changed without authentication failure. All binary fields use canonical Base64. The protector selects exactly the key named by `keyId`; an unknown key, malformed payload, unsupported version, or authentication failure raises a safe cryptographic error and never tries arbitrary fallback keys.

The legacy `Secrets:EncryptionKey` is used only to decrypt `v1`. To preserve every existing value, its key derivation remains byte-for-byte compatible with the current implementation: an exact Base64-encoded 32-byte value is used directly, and any other legacy string is SHA-256 hashed as UTF-8. This compatibility exception never makes the legacy value eligible for new writes and never weakens validation of keyring entries. Retained entries under `Secrets:EncryptionKeys` decrypt older `v2` values, while `Secrets:ActiveKeyId` selects the sole key used for new protection.

## Rotation worker

Rotation is disabled by default and requires an explicit deployment-time setting. It is an internal hosted service and does not expose a public command surface.

Each cycle:

1. Opens a short PostgreSQL transaction.
2. Selects at most `BatchSize` webhook endpoints whose ciphertext is not current, using keyset ordering, row locks, and `FOR UPDATE SKIP LOCKED`.
3. Decrypts each value with its recorded `v1` legacy key or `v2` key ID.
4. Protects the unchanged signing secret with the active key.
5. Updates only when the stored ciphertext still equals the value selected by the worker.
6. Commits the batch, reports metrics, and yields before claiming more work.

Row locking lets replicas partition work. The conditional update is a second guard against overwriting a signing-secret rotation that races with the worker. A changed row is skipped and becomes eligible for a later batch only if it still needs re-encryption. Already-current rows are idempotent no-ops.

A row that cannot be decrypted is not changed. The worker records a bounded diagnostic containing the endpoint identifier, ciphertext version, and key ID where available, increments a failure metric, and continues with other rows. Its in-memory keyset cursor advances past every selected row, including failures, until it completes the scan; it then resets only after a bounded delay. This prevents an undecryptable early row from starving later rows while keeping failures eligible for a future pass and after restart. Cancellation rolls back the active database transaction and respects the batch boundary.

The worker publishes a database-derived count of remaining non-active ciphertext. Operators do not infer completion only from a lack of recent updates.

## Concurrency invariants

- A delivery attempt connects only to addresses returned and approved by that same attempt's single DNS lookup.
- No request can use ambient proxy resolution, redirects, unpinned fallback, or a connection pooled from another attempt.
- Any prohibited member makes the entire DNS result unusable.
- A signing-secret change and an encryption-key rewrap cannot silently overwrite each other.
- Reprocessing a completed rotation batch does not alter plaintext or create an invalid value.
- Multiple replicas can rotate concurrently without processing the same locked row.
- Removing an old key is always a separate operator action after verified completion.

## Error handling and sensitive-data policy

Destination failures retain categories such as parse, resolution, prohibited address, missing pin, connection, TLS, timeout, redirect/non-success response, and cancellation. The existing durable delivery state machine decides retry and dead-letter transitions; the security layer never bypasses persistence to retry inline against an unvalidated target.

Secret failures retain categories such as configuration, unknown version, unknown key, malformed ciphertext, authentication, concurrency skip, and rotation database failure. They must not emit key bytes, plaintext secrets, ciphertext, authorization headers, signatures, or credential-bearing URL components. Hostnames and endpoint identifiers follow the repository's existing structured-logging policy; URL user information is prohibited at validation time.

## Observability

Metrics use bounded dimensions and follow existing Cmsify naming conventions:

- destination validation rejections by category;
- pinned connection failures by category;
- secret decrypt failures by ciphertext version and configured key ID;
- rotation rows succeeded, skipped, and failed;
- rotation cycles and cycle duration;
- database-derived remaining ciphertext by version and configured key ID.

Key IDs are drawn only from configuration or parsed ciphertext and are normalized to a bounded `unknown` value when invalid, preventing attacker-controlled metric cardinality. Logs include correlation/delivery identifiers needed for support but no secret material.

## Test strategy

Development follows strict red-green-refactor sequencing. Tests assert observable behavior rather than merely inspecting handler properties.

### Destination and transport tests

- A resolver that would return public and then private answers is called once in an attempt, and the connector receives only the first approved address.
- A retry performs a new lookup and uses only the new approved set.
- Mixed public/prohibited answers reject the complete destination.
- Table-driven IPv4 and IPv6 cases cover every prohibited prefix, prefix boundaries, IPv4-mapped IPv6, and representative global addresses.
- A controlled TLS fixture confirms connection to the pinned address while SNI and certificate validation use the original hostname.
- Missing or host/port-mismatched pin metadata fails before connection.
- Multiple approved addresses can be attempted, but no hostname fallback occurs.
- Redirect fixtures confirm no redirected request is sent.
- A controlled ambient proxy is not contacted.
- Cancellation and connect timeout terminate the attempt and preserve durable retry behavior.

### Key and rotation tests

- Production startup rejects missing active IDs, missing key entries, malformed/noncanonical Base64, wrong lengths, known development keys, and each low-entropy pattern rule.
- Valid generated keys start successfully.
- Active-key `v2` round trips, older retained `v2` reads, legacy `v1` reads using both historical derivation branches, and unknown/removed keys fail safely.
- Key IDs are authenticated and ciphertext tampering fails.
- New writes always use the configured active key.
- PostgreSQL integration tests cover bounded batches, restart/idempotency, multiple replicas, `SKIP LOCKED`, a concurrent signing-secret update, undecryptable-row starvation prevention, and the remaining-count metric.

### Regression validation

After focused tests pass, run the complete Infrastructure, API, Core, Admin, .NET SDK, and TypeScript checks required by the repository and remediation plan. The public API is unchanged, so OpenAPI regeneration should produce no diff; any unexpected contract diff is a blocking failure to investigate.

## Rollout and rollback

1. Generate a new production key with a CSPRNG and assign a stable operational key ID.
2. Deploy the compatible reader/writer with the active key, all retained `v2` keys, and the legacy `v1` key; leave rotation disabled.
3. Verify readiness, normal webhook delivery, key-version metrics, and absence of decrypt failures.
4. Enable rotation for a bounded deployment window and observe throughput, failures, and remaining counts.
5. Investigate every undecryptable row. Do not remove any key while a row still references it.
6. Require the database-derived remaining count to reach zero and remain zero on an independent verification cycle.
7. Disable rotation, then remove retired keys in a later configuration change.

Once any `v2` value has been written, rolling binaries back to a pre-`v2` reader is unsafe. Rollback must use a build that retains `v2` read support and all keys that may still be referenced. Key removal and binary rollback are never combined in one deployment. Database backup and restore procedures remain the final recovery path for operator misconfiguration.

## Documentation changes

- Update repository and operations configuration references with the keyring shape and secure generation commands.
- Update `.env.example` and application settings examples with development-only placeholders that production validation rejects.
- Document direct-only webhook egress, disabled proxy behavior, prohibited destination classes, and retry revalidation.
- Add the rotation enable-observe-disable runbook, metrics, rollback restriction, and key-retirement checklist.

## Acceptance criteria

- All specified security tests demonstrate a failing state before their production change and pass afterward.
- No connection-time DNS lookup or ambient proxy can bypass the validated address set.
- Original-host TLS identity checks remain enabled and are proven by an integration test.
- Production rejects invalid or obviously weak encryption configuration before readiness.
- Existing encrypted secrets remain readable during migration, and new secrets identify their active key.
- Rotation is bounded, idempotent, multi-replica safe, concurrency safe, observable, and disabled by default.
- Focused and full repository validation passes with no unexplained generated or OpenAPI diff.

## Future reconsiderations

Revisit a separately configured trusted egress-proxy mode only when a supported deployment requires it. That design must define who owns DNS validation, how proxy trust is established, and how proxy redirects and private-network reachability are controlled. Revisit automated key retirement only after rotation history and key inventory have durable first-class storage; it is intentionally an operator-controlled action for v1.
