import assert from "node:assert/strict";
import { mkdtempSync, mkdirSync, readFileSync, rmSync, symlinkSync, unlinkSync, writeFileSync } from "node:fs";
import { tmpdir } from "node:os";
import { resolve } from "node:path";
import { spawnSync } from "node:child_process";
import test from "node:test";

import { verifyFixtureChecksums, writeFixtureChecksums } from "../../../eng/upgrade-tests/checksums.mjs";
import { canonicalizeFixtureDump, compareFixtureTrees } from "../../../eng/upgrade-tests/fixture.mjs";
import { validateFixtureManifest } from "../../../eng/upgrade-tests/manifest.mjs";
import { validExpectedDocument, validManifestDocument } from "./fixture-documents.mjs";

const CLI = resolve(process.cwd(), "eng", "upgrade-tests", "cli.mjs");

function manifestDocument() {
  return validManifestDocument();
}

async function materializeFixture(root) {
  const document = manifestDocument();
  mkdirSync(resolve(root, "media"), { recursive: true });
  writeFileSync(resolve(root, "database.sql"), "SELECT 1;\n");
  writeFileSync(resolve(root, "expected.json"), JSON.stringify(validExpectedDocument()));
  writeFileSync(resolve(root, "manifest.json"), JSON.stringify(document));
  writeFileSync(resolve(root, "media", "a.txt"), "a");
  writeFileSync(resolve(root, "media", "z.txt"), "z");
  await writeFixtureChecksums(root, document.requiredFiles);
  return validateFixtureManifest(document, root);
}

function temporaryFixture() {
  const root = mkdtempSync(resolve(tmpdir(), "cmsify-upgrade-checksums-"));
  test.after(() => rmSync(root, { force: true, recursive: true }));
  return root;
}

test("the checked-in generator writes deterministic manifest and expected-data provenance", async () => {
  const repositoryRoot = mkdtempSync(resolve(tmpdir(), "cmsify-generation-provenance-"));
  const fixtureDirectory = resolve(repositoryRoot, "tests", "upgrade", "fixtures", "v0.1.3");
  const seedDirectory = resolve(repositoryRoot, "tests", "upgrade", "seed");
  mkdirSync(fixtureDirectory, { recursive: true });
  mkdirSync(seedDirectory, { recursive: true });
  writeFileSync(resolve(seedDirectory, "v0.1.3.sql"), "seed\n");
  writeFileSync(resolve(fixtureDirectory, "manifest.json"), `${JSON.stringify(validManifestDocument(), null, 2)}\n`);
  writeFileSync(resolve(fixtureDirectory, "expected.json"), `${JSON.stringify(validExpectedDocument(), null, 2)}\n`);

  try {
    const fixtureModule = await import("../../../eng/upgrade-tests/fixture.mjs");
    assert.equal(typeof fixtureModule.writeFixtureGenerationMetadata, "function");
    await fixtureModule.writeFixtureGenerationMetadata({ repositoryRoot, fixtureDirectory });
    const manifest = JSON.parse(readFileSync(resolve(fixtureDirectory, "manifest.json"), "utf8"));
    const expected = JSON.parse(readFileSync(resolve(fixtureDirectory, "expected.json"), "utf8"));
    const generation = {
      schemaVersion: 1,
      generatorVersion: "1.0.0",
      seed: {
        path: "tests/upgrade/seed/v0.1.3.sql",
        sha256: "4a6689419b00b11700c9b6246bcfa8936c8f5e1e824db3a7e57030e2d1c1a684",
      },
    };
    assert.deepEqual(manifest.generation, generation);
    assert.deepEqual(expected.provenance.generation, generation);
    assert.deepEqual(
      expected.scenarios.find(({ id }) => id === "permissions").assertions,
      ["editor-primary-write-grant", "global-admin-restricted-read", "reader-primary-resolve", "reader-restricted-hidden"],
    );
    assert.equal(JSON.stringify(generation).includes("generatedAt"), false);
  } finally {
    rmSync(repositoryRoot, { force: true, recursive: true });
  }
});

function linkedDirectory(target, link) {
  symlinkSync(target, link, process.platform === "win32" ? "junction" : "dir");
}

test("verifies every fixture payload against its SHA256SUMS entry", async () => {
  const root = temporaryFixture();
  const manifest = await materializeFixture(root);

  const hashes = await verifyFixtureChecksums(root, manifest);

  assert.equal(hashes.size, 5);
  assert.match(hashes.get("database.sql"), /^[0-9a-f]{64}$/);
});

test("rejects a tampered payload", async () => {
  const root = temporaryFixture();
  const manifest = await materializeFixture(root);
  writeFileSync(resolve(root, "database.sql"), "SELECT 2;\n");

  await assert.rejects(() => verifyFixtureChecksums(root, manifest), /checksum mismatch for database\.sql/i);
});

test("rejects a missing payload", async () => {
  const root = temporaryFixture();
  const manifest = await materializeFixture(root);
  unlinkSync(resolve(root, "media", "a.txt"));

  await assert.rejects(() => verifyFixtureChecksums(root, manifest), /missing fixture payload: media\/a\.txt/i);
});

test("rejects an unlisted payload", async () => {
  const root = temporaryFixture();
  const manifest = await materializeFixture(root);
  writeFileSync(resolve(root, "media", "unexpected.bin"), "x");

  await assert.rejects(() => verifyFixtureChecksums(root, manifest), /unlisted fixture payload: media\/unexpected\.bin/i);
});

test("writes ordinal forward-slash SHA256SUMS", async () => {
  const root = temporaryFixture();
  mkdirSync(resolve(root, "media"), { recursive: true });
  writeFileSync(resolve(root, "database.sql"), "database");
  writeFileSync(resolve(root, "media", "a.txt"), "a");
  writeFileSync(resolve(root, "media", "z.txt"), "z");

  const text = await writeFixtureChecksums(root, ["media/z.txt", "database.sql", "media/a.txt"]);

  assert.deepEqual(text.split("\n").filter(Boolean).map((line) => line.slice(66)), ["database.sql", "media/a.txt", "media/z.txt"]);
});

test("reports the first byte-level fixture drift", async () => {
  const first = temporaryFixture();
  const second = temporaryFixture();
  writeFileSync(resolve(first, "database.sql"), "SELECT 1;\n");
  writeFileSync(resolve(second, "database.sql"), "SELECT 2;\n");

  await assert.rejects(
    () => compareFixtureTrees(first, second),
    /fixture drift: database\.sql/i,
  );
});

test("tree comparison rejects linked or unsupported fixture entries", async () => {
  const first = temporaryFixture();
  const second = temporaryFixture();
  const outside = temporaryFixture();
  mkdirSync(resolve(second, "linked"));
  linkedDirectory(outside, resolve(first, "linked"));

  await assert.rejects(
    () => compareFixtureTrees(first, second),
    /fixture.*symbolic link|unsupported fixture entry/i,
  );
});

test("tree comparison rejects a linked fixture root", async () => {
  const target = temporaryFixture();
  const linkParent = temporaryFixture();
  const link = resolve(linkParent, "fixture-link");
  const second = temporaryFixture();
  linkedDirectory(target, link);

  await assert.rejects(
    () => compareFixtureTrees(link, second),
    /fixture.*symbolic link/i,
  );
});

test("canonicalizes anonymous COPY row UUIDs independently of insertion order", () => {
  const expected = {
    ids: { primaryWorkspace: "11111111-1111-4111-8111-111111111111" },
    relatedIds: {},
  };
  const firstObserved = { ids: { primaryWorkspace: "01a04440-0000-7000-8000-000000000001" }, relatedIds: {} };
  const secondObserved = { ids: { primaryWorkspace: "01a04441-0000-7000-8000-000000000001" }, relatedIds: {} };
  const first = [
    'COPY "public"."empty_options" ("id", "workspace_id", "label") FROM stdin;',
    "\\.",
    "",
    'COPY "public"."options" ("id", "workspace_id", "label") FROM stdin;',
    "01a04440-0000-7000-8000-000000000010\t01a04440-0000-7000-8000-000000000001\tBeta",
    "01a04440-0000-7000-8000-000000000011\t01a04440-0000-7000-8000-000000000001\tAlpha",
    "\\.",
    "",
  ].join("\n");
  const second = [
    'COPY "public"."empty_options" ("id", "workspace_id", "label") FROM stdin;',
    "\\.",
    "",
    'COPY "public"."options" ("id", "workspace_id", "label") FROM stdin;',
    "01a04441-0000-7000-8000-000000000020\t01a04441-0000-7000-8000-000000000001\tAlpha",
    "01a04441-0000-7000-8000-000000000021\t01a04441-0000-7000-8000-000000000001\tBeta",
    "\\.",
    "",
  ].join("\n");

  const firstCanonical = canonicalizeFixtureDump(first, firstObserved, expected);
  const secondCanonical = canonicalizeFixtureDump(second, secondObserved, expected);
  assert.equal(firstCanonical, secondCanonical);
  assert.match(firstCanonical, /\n\\\.\n$/);
});

test("refuses to hash a payload through a linked parent directory", async () => {
  const root = temporaryFixture();
  const outside = temporaryFixture();
  writeFileSync(resolve(outside, "payload.txt"), "outside fixture");
  linkedDirectory(outside, resolve(root, "media"));

  await assert.rejects(() => writeFixtureChecksums(root, ["media/payload.txt"]), /symbolic link/i);
});

test("refuses a linked SHA256SUMS output target", async () => {
  const root = temporaryFixture();
  const outside = temporaryFixture();
  writeFileSync(resolve(root, "database.sql"), "database");
  linkedDirectory(outside, resolve(root, "SHA256SUMS"));

  await assert.rejects(() => writeFixtureChecksums(root, ["database.sql"]), /symbolic link/i);
});

test("verify-fixture reports success for a complete temporary fixture", async () => {
  const root = temporaryFixture();
  await materializeFixture(root);

  const result = spawnSync(process.execPath, [CLI, "verify-fixture", "--fixture", root], { encoding: "utf8" });

  assert.equal(result.status, 0, result.stderr);
  assert.match(result.stdout, /fixture verified/i);
  assert.equal(result.stderr, "");
});

test("verify-fixture rejects incomplete scenario coverage without leaking the fixture path", async () => {
  const root = temporaryFixture();
  await materializeFixture(root);
  const expected = validExpectedDocument();
  expected.scenarios = expected.scenarios.slice(0, 1);
  writeFileSync(resolve(root, "expected.json"), JSON.stringify(expected));
  await writeFixtureChecksums(root, manifestDocument().requiredFiles);

  const result = spawnSync(process.execPath, [CLI, "verify-fixture", "--fixture", root], { encoding: "utf8" });

  assert.equal(result.status, 1);
  assert.match(result.stderr, /expected fixture data.*scenario/i);
  assert.doesNotMatch(result.stderr, new RegExp(root.replace(/[.*+?^${}()|[\]\\]/g, "\\$&"), "i"));
});

test("verify-fixture rejects expected provenance that contradicts the manifest", async () => {
  const root = temporaryFixture();
  await materializeFixture(root);
  const expected = validExpectedDocument();
  expected.provenance.apiImageDigest = `sha256:${"0".repeat(64)}`;
  writeFileSync(resolve(root, "expected.json"), JSON.stringify(expected));
  await writeFixtureChecksums(root, manifestDocument().requiredFiles);

  const result = spawnSync(process.execPath, [CLI, "verify-fixture", "--fixture", root], { encoding: "utf8" });

  assert.equal(result.status, 1);
  assert.match(result.stderr, /provenance\.apiImageDigest.*manifest/i);
});

test("verify-fixture rejects checksum-consistent metadata with the wrong checked-in seed hash", async () => {
  const root = temporaryFixture();
  await materializeFixture(root);
  const manifest = manifestDocument();
  const expected = validExpectedDocument();
  const wrongHash = "0".repeat(64);
  manifest.generation.seed.sha256 = wrongHash;
  expected.provenance.generation.seed.sha256 = wrongHash;
  writeFileSync(resolve(root, "manifest.json"), JSON.stringify(manifest));
  writeFileSync(resolve(root, "expected.json"), JSON.stringify(expected));
  await writeFixtureChecksums(root, manifest.requiredFiles);

  const result = spawnSync(process.execPath, [CLI, "verify-fixture", "--fixture", root], { encoding: "utf8" });

  assert.equal(result.status, 1);
  assert.match(result.stderr, /seed\.sha256.*checked-in seed/i);
  assert.doesNotMatch(result.stderr, new RegExp(root.replace(/[.*+?^${}()|[\]\\]/g, "\\$&"), "i"));
});

test("verify-fixture rejects scenarios stripped of assertion categories", async () => {
  const root = temporaryFixture();
  await materializeFixture(root);
  const expected = validExpectedDocument();
  expected.scenarios = expected.scenarios.map(({ id }) => ({ id, assertions: [] }));
  writeFileSync(resolve(root, "expected.json"), JSON.stringify(expected));
  await writeFixtureChecksums(root, manifestDocument().requiredFiles);

  const result = spawnSync(process.execPath, [CLI, "verify-fixture", "--fixture", root], { encoding: "utf8" });

  assert.equal(result.status, 1);
  assert.match(result.stderr, /assertion categor/i);
});

test("verify-fixture redacts malformed checksum paths without inventory noise", async () => {
  const root = temporaryFixture();
  await materializeFixture(root);
  writeFileSync(resolve(root, "SHA256SUMS"), `${"0".repeat(64)}  C:\\leaked\\secret\n`);

  const result = spawnSync(process.execPath, [CLI, "verify-fixture", "--fixture", root], { encoding: "utf8" });

  assert.equal(result.status, 1);
  assert.match(result.stderr, /SHA256SUMS.*invalid.*path/i);
  assert.doesNotMatch(result.stderr, /C:\\leaked\\secret/i);
  assert.doesNotMatch(result.stderr, /SHA256SUMS (omits|contains extra)/i);
});
