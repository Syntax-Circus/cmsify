# Generic embedded document qualification — 2026-10-12

Source `3078a81ea0651f8b5e41c534d2d925314f3cd75e` on
`feature/embedded-document-files` passes full Windows and pinned Linux source
qualification. Each platform passes2039/2039 with zero failed/skipped tests;
locked restore and clean Release build pass with zero warnings/errors. The967
source path/SHA256 pairs match exactly. Windows uses SDK10.0.401 under the
recorded latestPatch ruling; Linux uses10.0.400 with the existing locked-assets
target. Existing provider/photo/standalone regressions and actual crash helpers
are included. [Machine summary](2026-10-12-embedded-document-files.json) records
source, raw log, archive and DLL identities.

Private candidate `0.8.16-documents.1` passes independent package-only Windows
and Linux consumers from the same Core/Infrastructure archives, with empty
caches, generated then locked restores, zero build warnings/errors, PostgreSQL
17.10 and25packaged migrations. Infrastructure declares the same Core version;
both archives declare source3078a81 and retain AGPL license/readmes. Windows
consumer runtime is10.0.12; Linux is10.0.11. The Windows transitive vulnerability
query reports no vulnerable package entries at measurement time; this is not
a permanent dependency-security claim. Owned candidate containers/networks/temp
staging are removed; owner services remain.

Both consumers verify exact PDF digest/length and replay, foreign receipt denial,
explicit anonymous read and mutation denial, streaming revocation, loaded-file
reference authorization, retention denial/release replay and actual child-process
death before storage, after durable storage and after receipt commit. Sources
remain caller-owned; read outcomes do not recover uploads. Source tests separately
cover actual deaths after reference protect/release, malformed/aliased generic
access, protected retention, bounded cleanup starvation and PostgreSQL deadlock
regressions. Source-referenced diagnostic builds are not package evidence.

One independent final review found0Critical and5Important; all five were
reproduced and fixed in one pass before both final full suites. Deferred Minors:
the runner does not automatically reject untracked engine inputs (actual candidate
manually checked clean), and the guide's1–20MiB wording should state positive
bytes up to20MiB plus signature. [Execution ledger](../execution/embedded-document-files.md)
preserves all rulings, failed/zero-test/interrupted runs and their limitations.
The earlier SQLite save failure did not recur in the full final run; root cause
remains unknown, diagnostic assertion retained, no eliminated-flakiness claim.

## Separately authorized public release sequence

1. Review/approve upstream integration and a public version, proposed0.8.16
   subject to maintainer confirmation that the version is unused. Keep the local
   branches until that authorization. Add the dated changelog entry and use the
   exact approved integration commit as release source; candidate evidence binds
   to3078a81, and subsequent evidence-only commits do not alter engine bytes.
2. Follow [release runbook](../release-runbook.md): validate the tag/changelog,
   run exact release-commit full qualification, and push the approved tag. The
   existing pipeline certifies all eight NuGet packages, npm and container images,
   including existing PostgreSQL/SQLite consumers. Before environment promotion,
   also run the generic-file consumer with `CMSIFY_FILE_QUALIFICATION=1` against
   the produced exact release packages. Registry ownership/trusted publisher and
   manual release-environment approval remain maintainer gates.
3. After successful publication, run fresh Windows/Linux generic-file consumers
   using `Verify-EmbeddedFiles.ps1 -PublicPackages` with that public version and
   exact release source SHA, and preserve public archive/DLL identities and logs.
   Do not reuse this private candidate as public-package evidence.
4. Only after public certification and fresh consumers pass, authorize product
   adoption: update Puppies Plus public package pins, rerun its Task0 gate against
   the released generic contracts, then resume dog pedigree/health PDF and HTTPS
   reference Tasks1–4 on `feature/batch-puppy-entry`. Deployment remains separate.

This increment adds no public HTTP file endpoint, PDF rendering/sanitization,
new file SQLite claim, completed-original garbage collection or product runtime
changes. Completed immutable originals remain owned after reference release.
