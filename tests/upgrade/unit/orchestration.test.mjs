import assert from "node:assert/strict";
import { mkdirSync, mkdtempSync, readdirSync, readFileSync, rmSync, writeFileSync } from "node:fs";
import { rename, rm } from "node:fs/promises";
import { tmpdir } from "node:os";
import { basename, resolve } from "node:path";
import test from "node:test";

import { writeFixtureChecksums } from "../../../eng/upgrade-tests/checksums.mjs";
import { installGeneratedFixture, prepareGenerationDirectory } from "../../../eng/upgrade-tests/cli.mjs";
import { compareFixtureTrees, runWithCleanup } from "../../../eng/upgrade-tests/fixture.mjs";
import { validExpectedDocument, validManifestDocument } from "./fixture-documents.mjs";

function temporaryDirectory(prefix = "cmsify-upgrade-orchestration-") {
  const root = mkdtempSync(resolve(tmpdir(), prefix));
  test.after(() => rmSync(root, { force: true, recursive: true }));
  return root;
}

async function materializeValidFixture(root) {
  const manifest = validManifestDocument();
  mkdirSync(resolve(root, "media"), { recursive: true });
  writeFileSync(resolve(root, "database.sql"), "SELECT 1;\n");
  writeFileSync(resolve(root, "expected.json"), JSON.stringify(validExpectedDocument()));
  writeFileSync(resolve(root, "manifest.json"), JSON.stringify(manifest));
  writeFileSync(resolve(root, "media", "a.txt"), "a");
  writeFileSync(resolve(root, "media", "z.txt"), "z");
  await writeFixtureChecksums(root, manifest.requiredFiles);
}

test("removes a preparation directory when an initial fixture copy fails", async () => {
  const repositoryRoot = temporaryDirectory();
  const fixtureDirectory = resolve(repositoryRoot, "source-fixture");
  mkdirSync(fixtureDirectory, { recursive: true });
  writeFileSync(resolve(fixtureDirectory, "manifest.json"), "{}");

  await assert.rejects(
    () => prepareGenerationDirectory(repositoryRoot, fixtureDirectory, "fixture-broken-"),
    /ENOENT|cannot find/i,
  );

  const runDirectory = resolve(repositoryRoot, "tests", "upgrade", ".runs");
  assert.deepEqual(readdirSync(runDirectory), []);
});

test("preserves a preparation copy failure before its cleanup failure", async () => {
  const repositoryRoot = temporaryDirectory();
  const fixtureDirectory = resolve(repositoryRoot, "source-fixture");
  const cleanup = new Error("injected preparation cleanup failure");
  mkdirSync(fixtureDirectory, { recursive: true });
  writeFileSync(resolve(fixtureDirectory, "manifest.json"), "{}");

  await assert.rejects(
    () => prepareGenerationDirectory(repositoryRoot, fixtureDirectory, "fixture-broken-", {
      remove: async () => { throw cleanup; },
    }),
    (error) => {
      assert.equal(error instanceof AggregateError, true);
      assert.match(error.errors[0].message, /ENOENT|cannot find/i);
      assert.equal(error.errors[1], cleanup);
      assert.equal(error.cause, error.errors[0]);
      return true;
    },
  );
});

test("installs a verified complete fixture tree without retaining stale live files", async () => {
  const parent = temporaryDirectory();
  const generated = resolve(parent, "generated");
  const live = resolve(parent, "v0.1.3");
  mkdirSync(generated);
  mkdirSync(live);
  writeFileSync(resolve(live, "stale.txt"), "must disappear");
  await materializeValidFixture(generated);

  await installGeneratedFixture(generated, live);

  await compareFixtureTrees(generated, live);
});

test("leaves the live fixture untouched when the replacement tree fails verification", async () => {
  const parent = temporaryDirectory();
  const generated = resolve(parent, "generated");
  const live = resolve(parent, "v0.1.3");
  mkdirSync(generated);
  mkdirSync(live);
  writeFileSync(resolve(live, "sentinel.txt"), "original fixture");
  await materializeValidFixture(generated);
  writeFileSync(resolve(generated, "database.sql"), "tampered after checksumming\n");

  await assert.rejects(
    () => installGeneratedFixture(generated, live),
    /checksum mismatch for database\.sql/i,
  );

  assert.equal(readFileSync(resolve(live, "sentinel.txt"), "utf8"), "original fixture");
  assert.deepEqual(readdirSync(parent).sort(), ["generated", "v0.1.3"]);
});

test("preserves replacement validation failure before replacement cleanup failure", async () => {
  const parent = temporaryDirectory();
  const generated = resolve(parent, "generated");
  const live = resolve(parent, "v0.1.3");
  const cleanup = new Error("injected replacement cleanup failure");
  mkdirSync(generated);
  mkdirSync(live);
  writeFileSync(resolve(live, "sentinel.txt"), "original fixture");
  await materializeValidFixture(generated);
  writeFileSync(resolve(generated, "database.sql"), "tampered after checksumming\n");
  const failReplacementCleanup = async (path, options) => {
    if (basename(path).includes(".replacement-")) throw cleanup;
    await rm(path, options);
  };

  await assert.rejects(
    () => installGeneratedFixture(generated, live, { remove: failReplacementCleanup }),
    (error) => {
      assert.equal(error instanceof AggregateError, true);
      assert.match(error.errors[0].message, /checksum mismatch for database\.sql/i);
      assert.equal(error.errors[1], cleanup);
      assert.equal(error.cause, error.errors[0]);
      return true;
    },
  );

  assert.equal(readFileSync(resolve(live, "sentinel.txt"), "utf8"), "original fixture");
});

test("preserves initial swap failure before backup cleanup failure", async () => {
  const parent = temporaryDirectory();
  const generated = resolve(parent, "generated");
  const live = resolve(parent, "v0.1.3");
  const swap = new Error("injected initial swap failure");
  const cleanup = new Error("injected backup cleanup failure");
  mkdirSync(generated);
  mkdirSync(live);
  writeFileSync(resolve(live, "sentinel.txt"), "original fixture");
  await materializeValidFixture(generated);
  const failInitialSwap = async () => { throw swap; };
  const failBackupCleanup = async (path, options) => {
    if (basename(path).includes(".backup-")) throw cleanup;
    await rm(path, options);
  };

  await assert.rejects(
    () => installGeneratedFixture(generated, live, { rename: failInitialSwap, remove: failBackupCleanup }),
    (error) => {
      assert.equal(error instanceof AggregateError, true);
      assert.equal(error.errors[0], swap);
      assert.equal(error.errors[1], cleanup);
      assert.equal(error.cause, swap);
      return true;
    },
  );

  assert.equal(readFileSync(resolve(live, "sentinel.txt"), "utf8"), "original fixture");
});

test("restores the original fixture when the replacement rename fails", async () => {
  const parent = temporaryDirectory();
  const generated = resolve(parent, "generated");
  const live = resolve(parent, "v0.1.3");
  mkdirSync(generated);
  mkdirSync(live);
  writeFileSync(resolve(live, "sentinel.txt"), "original fixture");
  await materializeValidFixture(generated);
  let renameCount = 0;
  const failReplacementRename = async (source, destination) => {
    renameCount += 1;
    if (renameCount === 2) throw new Error("injected replacement rename failure");
    await rename(source, destination);
  };

  await assert.rejects(
    () => installGeneratedFixture(generated, live, { rename: failReplacementRename }),
    /injected replacement rename failure/i,
  );

  assert.equal(readFileSync(resolve(live, "sentinel.txt"), "utf8"), "original fixture");
  assert.deepEqual(readdirSync(parent).sort(), ["generated", "v0.1.3"]);
});

test("preserves replacement and restoration failures before replacement cleanup failure", async () => {
  const parent = temporaryDirectory();
  const generated = resolve(parent, "generated");
  const live = resolve(parent, "v0.1.3");
  const replacement = new Error("injected replacement rename failure");
  const restore = new Error("injected restoration rename failure");
  const cleanup = new Error("injected replacement cleanup failure");
  mkdirSync(generated);
  mkdirSync(live);
  writeFileSync(resolve(live, "sentinel.txt"), "original fixture");
  await materializeValidFixture(generated);
  let renameCount = 0;
  const failReplacementAndRestore = async (source, destination) => {
    renameCount += 1;
    if (renameCount === 2) throw replacement;
    if (renameCount === 3) throw restore;
    await rename(source, destination);
  };
  const failReplacementCleanup = async (path, options) => {
    if (basename(path).includes(".replacement-")) throw cleanup;
    await rm(path, options);
  };

  await assert.rejects(
    () => installGeneratedFixture(generated, live, { rename: failReplacementAndRestore, remove: failReplacementCleanup }),
    (error) => {
      assert.equal(error instanceof AggregateError, true);
      const primary = error.errors[0];
      assert.equal(primary instanceof AggregateError, true);
      assert.deepEqual(primary.errors, [replacement, restore]);
      assert.equal(primary.cause, replacement);
      assert.equal(error.errors[1], cleanup);
      assert.equal(error.cause, primary);
      return true;
    },
  );
});

test("preserves a primary generation failure before a cleanup failure", async () => {
  const primary = new Error("baseline assertion failed");
  const cleanup = new Error("Docker cleanup failed");

  await assert.rejects(
    () => runWithCleanup(
      async () => { throw primary; },
      async () => { throw cleanup; },
    ),
    (error) => {
      assert.equal(error instanceof AggregateError, true);
      assert.equal(error.errors[0], primary);
      assert.equal(error.errors[1], cleanup);
      assert.equal(error.cause, primary);
      assert.match(error.message, /baseline assertion failed/i);
      return true;
    },
  );
});
