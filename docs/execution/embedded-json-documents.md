# Embedded JSON document amendment

Owner approved this prerequisite to the Beseme site-content delivery on2026-10-14.
Work uses the existing checkout on feature/embedded-json-documents, based on
ac196e9. No worktree, new service, migration, HTTP route or package pin change.

Approved scope: expose the existing non-searchable JSON text hint through the
closed embedded-template contract. CMS continues to own all document values and
history. Hosts validate typed structure/UTF-8 limits; unchanged CMS character
length, authority, fingerprint and receipt checks remain in force. Generic
plaintext search hardening is separate.

Execution:
1. Core regression tests observed RED: JSON setup and fingerprint rejected.
2. Three PostgreSQL provider cases observed RED at JSON template setup, then
   GREEN after the one-line allow-list amendment. Exact256KiB repeated-word,
   long-Unicode and multi-section documents preserve text across writes,
   version receipts/replay and source history; title alone is indexed.
3. Package-only consumer adds the same three positive flows using actual public
   handlers and scoped host authority. This is candidate evidence until release.
4. Full suites, Windows/Linux package consumer, review and PR remain pending.

Toolchain: the checked-in latestPatch roll-forward resolves installed Windows
SDK10.0.401 although AGENTS.md names10.0.400; the qualification wrapper explicitly
supports both. Record the actual SDK in evidence; do not claim an exact400 run
from the Windows401 execution. Linux source qualification uses a pinned image.
