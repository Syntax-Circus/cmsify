import assert from "node:assert/strict";
import { execFileSync } from "node:child_process";
import { mkdirSync, mkdtempSync, rmSync, writeFileSync } from "node:fs";
import { tmpdir } from "node:os";
import path from "node:path";
import test from "node:test";
import { fileURLToPath } from "node:url";
import { validateRepositorySupplyChain } from "../../scripts/release/verify-release-contract.mjs";

const repositoryRoot = path.resolve(path.dirname(fileURLToPath(import.meta.url)), "../..");
const checkout = "actions/checkout@11bd71901bbe5b1630ceea73d27597364c9af683";
const sdkDigest = "e1ffd2a92ae84c1291bc1b6887501f8af98e6331e7af6d4c8d37168c5e87a64c";
const postgresDigest = "67f41722b7a8cbdb868a44a4995c846eddfdc2973bccb291ce937dce88ad5675";

function write(root, relativePath, contents) {
  const destination = path.join(root, relativePath);
  mkdirSync(path.dirname(destination), { recursive: true });
  writeFileSync(destination, contents);
}

function validRepository(root) {
  write(root, ".github/workflows/supply-chain.yml", `jobs:
  build:
    steps:
      - uses: ./.github/actions/local
      - uses: ${checkout} # v4.2.2
      - run: docker build --tag cmsify-candidate:local .
      - run: docker run cmsify-candidate:local
`);
  write(root, "src/Cmsify.Api/Dockerfile", `FROM mcr.microsoft.com/dotnet/sdk:10.0.400@sha256:${sdkDigest} AS build
FROM build AS final
`);
  write(root, "docker-compose.yml", `services:
  postgres:
    image: postgres:17@sha256:${postgresDigest}
`);
}

function withTemporaryRepository(action) {
  const root = mkdtempSync(path.join(tmpdir(), "cmsify-supply-chain-"));
  try {
    validRepository(root);
    action(root);
  } finally {
    rmSync(root, { recursive: true, force: true });
  }
}

test("repository supply-chain inputs are immutable with file and line evidence", () => {
  assert.deepEqual(validateRepositorySupplyChain(repositoryRoot), []);
});

test("supply-chain controls allow local actions, stage aliases, and workflow-built candidates", () => {
  withTemporaryRepository((root) => assert.deepEqual(validateRepositorySupplyChain(root), []));
});

test("supply-chain validator rejects one-character action and digest mutations", () => {
  withTemporaryRepository((root) => {
    const actionRoot = path.join(root, "action");
    mkdirSync(actionRoot);
    validRepository(actionRoot);
    const actionWorkflow = path.join(actionRoot, ".github/workflows/supply-chain.yml");
    writeFileSync(actionWorkflow, `jobs:\n  build:\n    steps:\n      - uses: ${checkout.slice(0, -1)}\n`);
    assert.match(validateRepositorySupplyChain(actionRoot).join("\n"), /supply-chain\.yml:4: action reference/);

    const digestRoot = path.join(root, "digest");
    mkdirSync(digestRoot);
    validRepository(digestRoot);
    const dockerfile = path.join(digestRoot, "src/Cmsify.Api/Dockerfile");
    writeFileSync(dockerfile, `FROM mcr.microsoft.com/dotnet/sdk:10.0.400@sha256:${sdkDigest.slice(0, -1)} AS build\n`);
    assert.match(validateRepositorySupplyChain(digestRoot).join("\n"), /Cmsify\.Api\/Dockerfile:1: runtime image/);
  });
});

test("supply-chain validator rejects multiline and untagged docker run images", () => {
  withTemporaryRepository((root) => {
    const multilineRoot = path.join(root, "multiline");
    mkdirSync(multilineRoot);
    validRepository(multilineRoot);
    write(multilineRoot, ".github/workflows/multiline.yml", `jobs:
  test:
    steps:
      - run: |
          docker run \\
            ubuntu:latest
`);
    assert.match(validateRepositorySupplyChain(multilineRoot).join("\n"), /multiline\.yml:6: runtime image/);

    const untaggedRoot = path.join(root, "untagged");
    mkdirSync(untaggedRoot);
    validRepository(untaggedRoot);
    write(untaggedRoot, ".github/workflows/untagged.yml", "jobs:\n  test:\n    steps:\n      - run: docker run ubuntu\n");
    assert.match(validateRepositorySupplyChain(untaggedRoot).join("\n"), /untagged\.yml:4: runtime image/);
  });
});

test("supply-chain validator only exempts images built by Docker", () => {
  withTemporaryRepository((root) => {
    write(root, ".github/workflows/echo.yml", "jobs:\n  test:\n    steps:\n      - run: echo --tag postgres:17-alpine\n      - run: docker run postgres:17-alpine\n");
    assert.match(validateRepositorySupplyChain(root).join("\n"), /echo\.yml:5: runtime image/);
  });
});

test("supply-chain validator requires interpolated Compose references to retain a digest", () => {
  withTemporaryRepository((root) => {
    write(root, "compose-mutation.yml", "services:\n  app:\n    image: ${UNPINNED_IMAGE}\n");
    assert.match(validateRepositorySupplyChain(root).join("\n"), /compose-mutation\.yml:3: runtime image/);
  });
});

test("supply-chain validator requires reviewed action version comments", () => {
  withTemporaryRepository((root) => {
    write(root, ".github/workflows/comment.yml", `jobs:\n  test:\n    steps:\n      - uses: ${checkout}\n`);
    assert.match(validateRepositorySupplyChain(root).join("\n"), /comment\.yml:4: action reference must include a version comment/);
  });
});

test("supply-chain validator ignores untracked Compose drafts", () => {
  withTemporaryRepository((root) => {
    execFileSync("git", ["init", "--quiet"], { cwd: root });
    execFileSync("git", ["add", ".github/workflows/supply-chain.yml", "src/Cmsify.Api/Dockerfile", "docker-compose.yml"], { cwd: root });
    write(root, "compose-draft.yml", "services:\n  app:\n    image: ubuntu:latest\n");
    assert.deepEqual(validateRepositorySupplyChain(root), []);
  });
});
