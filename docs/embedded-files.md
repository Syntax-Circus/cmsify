# Embedded PDF files

Embedded files provide generic original PDF bytes, durable upload intents,
immutable receipts, scoped reads and consumer reference protection on PostgreSQL.
Photo processing and native decoder qualification are independent.

Register Cmsify infrastructure first, a host `IEmbeddedFileAuthorizationService`,
and one `RegisteredEmbeddedFileRetentionGuard` for each reference consumer. Then
call `AddCmsifyEmbeddedFiles(options => options.Enabled = true)`. Files default
disabled. Conflicting registrations fail. Host guards own workspace, dog assignment
and publication policy; mutation requires an authenticated actor. An explicit host
guard may authorize an anonymous read of an exact immutable receipt.

Use the named handlers in `Cmsify.Core.EmbeddedFiles`. Begin binds workspace,
asset, consumer, operation, actor, normalized display name, length, SHA256 and
`pdf-v1`. Uploads require1–20MiB, `%PDF-`, exact declared length/digest and observed
EOF; this is bounded byte identification, not a PDF parser or malware scanner.
Begin replay returns the same session; a changed intent conflicts. Write replay
requires the original write operation. Completed replay never opens its source.
Caller-provided source streams remain caller-owned.

Each write attempt has a distinct private key. Only fenced completion commits a
receipt. Outcome lookup is read-only: expired active leases report Interrupted;
explicit write replay recovers an interrupted attempt. Once stale intake is retired
by opted-in reconciliation, it reports Expired and cannot restart. Storage failures
and cancellation propagate without committing a receipt.

Reads bind workspace, consumer and exact asset/receipt/length/digest. Authorization
is checked before storage opens and around each stream read. Revocation prevents
future reads; already delivered bytes cannot be revoked. The sink must consume EOF
to receive success. Storage paths never enter public descriptors.

Protect immutable ordered sets of1–20distinct files before saving host assignments
or published snapshots. Retain historical host versions too. The registered host
guard must allow release of the exact protection revision; unknown, denied or
throwing guards retain protection. Repeated protect/release operations preserve
their original intent. Released reference keys cannot be reused for a changed set.

Standalone media routes and generic repositories exclude embedded file scope,
including case/malformed keys and independently recorded upload sessions. Generic
creation and deletion cannot adopt these files. Existing opted-in reconciliation
retires incomplete/losing attempts after its cutoff and preserves live/completed
winners and protected originals. Completed receipts remain durable and owned after
reference release; completed-file garbage collection is a separate future policy.

Run `scripts/qualification/Invoke-EmbeddedFileSourceQualification.ps1` for source
checks and `scripts/qualification/Verify-EmbeddedFiles.ps1` for private candidate
package consumers. Qualification evidence records exact SDK, source/package hashes,
real process-death boundaries and limitations. No new file SQLite support or public
HTTP file endpoint is introduced. Public release and downstream adoption follow
the existing separately approved release certification.
