# Embedded file execution

Plan: `E:/dev/SyntaxCircus/puppies-plus/docs/superpowers/plans/2026-10-12-cmsify-embedded-documents.md`.
Approved amendment and plan, inline execution. Base9f11f086; branch
`feature/embedded-document-files`. Product branch remains unchanged.

Task1 checkpoint: Core4/4 and registration2/2 focused checks pass. Missing Core
namespace and registration method were observed compile RED before implementation,
then GREEN. Task2 uploads/read persistence and Tasks3–4 remain pending.
No file storage/runtime qualification is inferred from these contract checks.
No public release authorization.

Task2 checkpoint: owned PostgreSQL upload3/3, read2/2, schema1/1,
real subprocess death/replay2/2 and registration2/2 pass on Windows.
Read repository absence was observed compile RED; crash-helper absence produced
two assertion failures before wiring the helper into Release solution/test builds.
Actual child processes were killed before storage and after durable storage, before
receipt commit. Read-only lookup reported Interrupted after forced lease expiry;
explicit replay produced one receipt and two separately keyed attempts.
Exact digest/length and denied/foreign receipt/no-byte reads, mid-stream revocation,
concurrent writers, repeat migration and standalone/photo coexistence pass.
New concurrency/revocation/schema checks are supplementary coverage without
observed behavioral RED. These measurements do not qualify Linux or packages.
Task3 access/reference/cleanup fences and Task4 full qualification remain pending.

Rulings: keep the small checked-stream helper with its storage adapter rather
than a separate file (cost if wrong: move the internal type). Use installed EF
tool10.0.4 against runtime10.0.11 without changing pins; generated migration was
applied twice and measured (cost if wrong: regenerate with matching tools).
The combined MTP middle-wildcard filter selected zero tests; individual class
filters were used and actual counts inspected. No zero-test success claimed.

Ruling: use available SDK10.0.401, explicitly recording the AGENTS10.0.400
mismatch — global.json permits latestPatch and10.0.400 is not installed — cost
if wrong: qualification must be repeated with10.0.400. No SDK/pin file changes.

Preflight: Task1 defines ports consumed by Task2 uploads/reads and Task3 references;
Task2 produces exact receipts/fences consumed by Task3; Task4 measures their same
source/candidate archives. PostgreSQL-only entities must be ignored by SQLite.
No interface conflict found. Tracked Windows ledger replaces POSIX bookkeeping.
