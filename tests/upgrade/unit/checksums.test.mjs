import assert from "node:assert/strict";
import { mkdtempSync, mkdirSync, rmSync, symlinkSync, unlinkSync, writeFileSync } from "node:fs";
import { tmpdir } from "node:os";
import { resolve } from "node:path";
import { spawnSync } from "node:child_process";
import test from "node:test";

import { verifyFixtureChecksums, writeFixtureChecksums } from "../../../eng/upgrade-tests/checksums.mjs";
import { canonicalizeFixtureDump, compareFixtureTrees } from "../../../eng/upgrade-tests/fixture.mjs";
import { REQUIRED_SCENARIOS, validateFixtureManifest } from "../../../eng/upgrade-tests/manifest.mjs";

const CLI = resolve(process.cwd(), "eng", "upgrade-tests", "cli.mjs");

function manifestDocument() {
  return {
    schemaVersion: 1,
    baseline: {
      version: "0.1.3",
      sourceSha: "bc652aec1acad7ef440576b5019a0fe7c72004b3",
      apiImage: {
        repository: "docker.io/syntaxcircus/cmsify-api",
        tag: "0.1.3",
        digest: "sha256:e28a7c884ed4cc4933fbb58608ba8d1dd97bf6a1e443ef234e0a0aa8b5c51931",
        platform: "linux/amd64",
      },
      postgresImage: {
        repository: "docker.io/library/postgres",
        tag: "17-alpine",
        digest: "sha256:7456ef82e5f5bc43d997f4781bbd7c0d6389bff397564649a356e206ba473aee",
        platform: "linux/amd64",
      },
      minioImage: {
        repository: "docker.io/minio/minio",
        tag: "RELEASE.2025-09-07T16-13-09Z",
        digest: "sha256:a1a8bd4ac40ad7881a245bab97323e18f971e4d4cba2c2007ec1bedd21cbaba2",
        platform: "linux/amd64",
      },
    },
    requiredFiles: ["database.sql", "expected.json", "manifest.json", "media/a.txt", "media/z.txt"],
    requiredScenarios: [...REQUIRED_SCENARIOS],
    expectedDataFile: "expected.json",
  };
}

async function materializeFixture(root) {
  const document = manifestDocument();
  mkdirSync(resolve(root, "media"), { recursive: true });
  writeFileSync(resolve(root, "database.sql"), "SELECT 1;\n");
  writeFileSync(resolve(root, "expected.json"), JSON.stringify({ scenarios: [...REQUIRED_SCENARIOS].map((id) => ({ id })) }));
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
  writeFileSync(resolve(root, "expected.json"), JSON.stringify({ scenarios: [{ id: "workspaces" }] }));
  await writeFixtureChecksums(root, manifestDocument().requiredFiles);

  const result = spawnSync(process.execPath, [CLI, "verify-fixture", "--fixture", root], { encoding: "utf8" });

  assert.equal(result.status, 1);
  assert.match(result.stderr, /expected\.json.*scenario/i);
  assert.doesNotMatch(result.stderr, new RegExp(root.replace(/[.*+?^${}()|[\]\\]/g, "\\$&"), "i"));
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
