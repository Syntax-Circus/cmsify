import assert from "node:assert/strict";
import { mkdirSync, mkdtempSync, readdirSync, readFileSync, rmSync, writeFileSync } from "node:fs";
import { rename } from "node:fs/promises";
import { tmpdir } from "node:os";
import { resolve } from "node:path";
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
