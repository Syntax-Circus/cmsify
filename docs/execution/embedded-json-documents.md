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
4. Full Windows/Linux suites and both package-only consumers pass. One independent
   read-only review ofac196e9..0e51b7e finds no Critical/Important/Minor issues.
   The review left external verification to the executor; the measured results
   below satisfy those outstanding gates. [PR133](https://github.com/Syntax-Circus/cmsify/pull/133)
   is prepared for owner review/merge. No tag or public release is published.

Toolchain: the checked-in latestPatch roll-forward resolves installed Windows
SDK10.0.401 although AGENTS.md names10.0.400; the qualification wrapper explicitly
supports both. Record the actual SDK in evidence; do not claim an exact400 run
from the Windows401 execution. Linux source qualification uses a pinned image.

## Final measured verification

- Windows public locked restore and nonincremental Release build: passed,
  zero warnings/errors. Full solution2064/2064, zero failures/skips,25m18.059s.
- Linux public locked restore and nonincremental Release build: passed,
  zero warnings/errors. Full solution2064/2064, zero failures/skips,28m07.712s.
- Candidate0.8.18-site.1, source0e51b7e929e02eea50f05f2f18c6186c3c8a399a:
  Windows/Linux isolated package consumers pass all three exact256KiB JSON flows,
  with no engine project references. Existing embedded-content/media regressions
  pass in that consumer; native decoding/phone support is not newly qualified.
- Initial GitHub accessibility check failed at locked restore: patch roll-forward
  selected internal ASP.NET assets10.0.13 versus the unchanged10.0.12 lock.
  Reusing the existing locked-assets target (already used by test/release jobs)
  fixes restore/build/run; actual GitHub accessibility check then passed.
  [Passing job](https://github.com/Syntax-Circus/cmsify/actions/runs/38635785166/job/116098467607).
- Dated0.8.18 changelog/release-tag gate: observed missing-entry RED, then GREEN.

Raw evidence: `artifacts/embedded-json-{restore,build,tests}.log`;
`artifacts/file-source/linux-cd6753686fa642b88b9968e4291d617c/`;
`artifacts/photo-qualification/0.8.18-site.1-fa14f069d7b2416eadde11dec671525d/`.
The [checked evidence summary](../evidence/2026-10-14-embedded-json-documents.json)
records candidate digests and source identities. Linux used979 frozen source
files. Runtime source, tests and consumer fixtures matched after execution;
the separately verified accessibility workflow and release changelog changed
after that snapshot. This reporting update follows verification. The qualified
engine code remains0e51b7e, not a claim that those later metadata bytes were
part of the earlier snapshot. CI's full .NET job remains an independent gate.

Public publication and downstream adoption are not complete. After owner merge,
release0.8.18 through the normal protected workflow and qualify the public
packages before changing Puppies Plus pins. No migration, SQLite embedded-content
support, generic plaintext search fix or complete Beseme-site acceptance is claimed.
