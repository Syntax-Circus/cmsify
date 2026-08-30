# Rollback runbook

Use this runbook only with authorized operators. It is a recovery procedure, not permission to rebuild, publish, promote, tag, or release artifacts.

## Abort and retain evidence

Abort a rollout when readiness, authenticated reads, representative media downloads, migration behavior, backup verification, or candidate digest verification fails. Preserve the workflow/run output, exact source SHA, failing immutable digest, and bounded diagnostics. Do not rebuild or replace a failed candidate, and do not continue with a tag or digest that is not the recorded certified identity.

## Verify the rollback point

1. Stop or drain application traffic and identify the deployed and prior API/Admin image immutable digest values.
2. Re-verify the matched PostgreSQL, media, and Admin Data Protection-key backup manifest, including its timestamp and checksums. If either database or media backup member cannot be verified, rollback is not proved; preserve the remaining state and escalate.
3. Confirm the retained prior images are the recorded immutable digests. Do not pull a mutable replacement or rebuild an image during recovery.

## Restore and validate

Restore PostgreSQL and media from the same pre-upgrade backup generation; never mix generations. Restore the retained Data Protection keys when preserving Admin sessions matters. Start only the recorded prior image digests, wait for `/health/live` and `/health/ready`, then verify Admin sign-in, representative authenticated content, and byte-for-byte representative media downloads before returning traffic.

Keep the failed candidate, prior digest, backup manifest, and restore result together as incident evidence. A public restore or package-replacement gate remains separate: do not claim the local `SyntaxCircus.Http.Resilience` prerequisite is publicly restored until exact public package bytes and clean consumer restore evidence are available.
