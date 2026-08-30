import assert from "node:assert/strict";
import { cpSync, mkdirSync, mkdtempSync, readFileSync, rmSync, writeFileSync } from "node:fs";
import { tmpdir } from "node:os";
import { dirname, resolve } from "node:path";
import { fileURLToPath } from "node:url";
import { spawnSync } from "node:child_process";
import test from "node:test";

const repositoryRoot = resolve(dirname(fileURLToPath(import.meta.url)), "..", "..");
const verifier = resolve(repositoryRoot, "scripts", "release", "verify-release-contract.mjs");
const workflowPath = ".github/workflows/publish-cmsify.yml";

const contractFiles = [
  "LICENSE",
  "CHANGELOG.md",
  "Directory.Build.props",
  "Directory.Packages.props",
  "global.json",
  "Cmsify.slnx",
  "sdk/typescript/package.json",
  "sdk/typescript/package-lock.json",
  "sdk/typescript/LICENSE",
  "eng/accessibility/package.json",
  "eng/accessibility/package-lock.json",
  "eng/accessibility/run.mjs",
  "eng/upgrade-tests/process.mjs",
  "scripts/release/load-oci-candidate.mjs",
  "src/Cmsify.Contracts/Cmsify.Contracts.csproj",
  "src/Cmsify.Api/Dockerfile",
  "src/Cmsify.Admin/Dockerfile",
  "sdk/dotnet/src/SyntaxCircus.Cmsify.Client/SyntaxCircus.Cmsify.Client.csproj",
  "sdk/dotnet/src/SyntaxCircus.Cmsify.Client.DistributedCaching/SyntaxCircus.Cmsify.Client.DistributedCaching.csproj",
  ".github/workflows/dotnet-test.yml",
  ".github/workflows/admin-accessibility.yml",
  ".github/workflows/openapi-contract.yml",
  ".github/workflows/upgrade-rollback.yml",
  "SECURITY.md",
  "SUPPORT.md",
  ".github/CODEOWNERS",
  "docs/api-compatibility.md",
  "docs/operations.md",
  "docs/release-runbook.md",
  "docs/rollback-runbook.md",
  "docs/README.md",
  workflowPath,
];

function write(root, path, contents) {
  const destination = resolve(root, path);
  mkdirSync(dirname(destination), { recursive: true });
  writeFileSync(destination, contents);
}

function round5Workflow(contents) {
  return contents
    .replaceAll("docker run -d --rm --name", "docker run -d --name")
    .replace("curl --fail --silent --show-error --connect-timeout 2 --max-time 5 http://127.0.0.1:18081/ >/dev/null", "curl --fail --silent --show-error --connect-timeout 2 --max-time 5 http://127.0.0.1:18081/ | grep -Fq '<title>Cmsify Admin</title>'");
}

function createFixture(mutator) {
  const root = mkdtempSync(resolve(tmpdir(), "cmsify-release-contract-"));
  for (const path of contractFiles) {
    const destination = resolve(root, path);
    mkdirSync(dirname(destination), { recursive: true });
    cpSync(resolve(repositoryRoot, path), destination);
  }
  for (const path of ["sdk/typescript/package.json", "sdk/typescript/package-lock.json"]) {
    write(root, path, readFileSync(resolve(root, path), "utf8").replaceAll("github.com/SyntaxCircus/cmsify", "github.com/Syntax-Circus/cmsify"));
  }
  const branchWorkflow = readFileSync(resolve(root, ".github/workflows/dotnet-test.yml"), "utf8");
  write(root, ".github/workflows/dotnet-test.yml", branchWorkflow.includes("tests/release-contract/finalize-spdx.test.mjs")
    ? branchWorkflow
    : branchWorkflow.replace("tests/release-contract/validate-release-tag.test.mjs", "tests/release-contract/validate-release-tag.test.mjs tests/release-contract/finalize-spdx.test.mjs"));
  write(root, workflowPath, round5Workflow(readFileSync(resolve(root, workflowPath), "utf8")));
  write(root, "scripts/release/finalize-spdx.mjs", "// fixture: stable SPDX identities are finalized before checksums\n");
  mutator?.(root);
  return root;
}

function mutateWorkflow(root, mutate) {
  const path = resolve(root, workflowPath);
  writeFileSync(path, mutate(readFileSync(path, "utf8")));
}

function mutateReleaseJob(root, jobName, mutate) {
  mutateWorkflow(root, (workflow) => {
    const start = workflow.search(new RegExp(`^  ${jobName}:`, "m"));
    assert.notEqual(start, -1, `missing ${jobName} job in test fixture`);
    const following = workflow.slice(start + 1);
    const nextJob = following.search(/^  [A-Za-z0-9_-]+:/m);
    const end = nextJob === -1 ? workflow.length : start + 1 + nextJob;
    return `${workflow.slice(0, start)}${mutate(workflow.slice(start, end))}${workflow.slice(end)}`;
  });
}

function mutateUpgradeWorkflow(root, mutate) {
  const path = resolve(root, ".github/workflows/upgrade-rollback.yml");
  writeFileSync(path, mutate(readFileSync(path, "utf8")));
}

function mutateAccessibilityWorkflow(root, mutate) {
  const path = resolve(root, ".github/workflows/admin-accessibility.yml");
  writeFileSync(path, mutate(readFileSync(path, "utf8")));
}

function mutateOpenApiWorkflow(root, mutate) {
  const path = resolve(root, ".github/workflows/openapi-contract.yml");
  writeFileSync(path, mutate(readFileSync(path, "utf8")));
}

function mutateAccessibilityEventPaths(root, event, mutate) {
  mutateAccessibilityWorkflow(root, (workflow) => {
    const start = workflow.search(new RegExp(`^  ${event}:\\s*$`, "m"));
    assert.notEqual(start, -1, `missing ${event} event in test fixture`);
    const following = workflow.slice(start + 1);
    const nextEvent = following.search(/^  [A-Za-z0-9_-]+:/m);
    const end = nextEvent === -1 ? workflow.length : start + 1 + nextEvent;
    return `${workflow.slice(0, start)}${mutate(workflow.slice(start, end))}${workflow.slice(end)}`;
  });
}

function verify(root) {
  return spawnSync(process.execPath, [verifier, "--root", root], { encoding: "utf8" });
}

function expectInvalid(mutator, diagnostic) {
  const root = createFixture(mutator);
  try {
    const result = verify(root);
    assert.notEqual(result.status, 0, "contract mutation unexpectedly passed");
    assert.match(result.stderr, diagnostic);
  } finally { rmSync(root, { recursive: true, force: true }); }
}

test("accepts the checked-in repository release contract", () => {
  const result = verify(repositoryRoot);
  assert.equal(result.status, 0, result.stderr || result.stdout);
});

test("accepts the isolated complete release-contract source fixture", () => {
  const root = createFixture();
  try { const result = verify(root); assert.equal(result.status, 0, result.stderr || result.stdout); }
  finally { rmSync(root, { recursive: true, force: true }); }
});

test("copies buildable Dockerfiles unchanged into the isolated release-contract fixture", () => {
  const root = createFixture();
  try {
    for (const path of ["src/Cmsify.Api/Dockerfile", "src/Cmsify.Admin/Dockerfile"]) {
      assert.equal(readFileSync(resolve(root, path), "utf8"), readFileSync(resolve(repositoryRoot, path), "utf8"));
    }
  } finally { rmSync(root, { recursive: true, force: true }); }
});

test("requires canonical Docker Hub names in Buildx archive annotations", () => {
  const workflow = readFileSync(resolve(repositoryRoot, workflowPath), "utf8");
  for (const kind of ["api", "admin"]) {
    assert.match(workflow, new RegExp(`manifest-descriptor:io\\.containerd\\.image\\.name=docker\\.io/syntaxcircus/cmsify-${kind}:\\$VERSION`));
  }
});

test("rejects branch publication", () => expectInvalid((root) => mutateWorkflow(root, (workflow) => workflow.replace('tags: ["v*"]', "branches: [main]")), /tag-only/i));
test("rejects tag-only branch accessibility", () => expectInvalid((root) => mutateAccessibilityWorkflow(root, (workflow) => workflow.replace("workflow_dispatch:", 'push:\n    tags: ["v*"]')), /accessibility.*manual.*main.*pull request/i));
test("rejects a mutable OpenAPI comparison image", () => expectInvalid((root) => mutateOpenApiWorkflow(root, (workflow) => workflow.replace(/tufin\/oasdiff:v1\.28\.0@sha256:[0-9a-f]{64}/, "tufin/oasdiff:v1.28.0")), /runtime image.*sha256/i));
test("rejects a PR OpenAPI checkout that uses the synthetic workflow SHA", () => expectInvalid((root) => mutateOpenApiWorkflow(root, (workflow) => workflow.replace("github.event.pull_request.head.sha", "github.sha")), /exact PR head/i));
test("rejects a PR OpenAPI record that substitutes the synthetic workflow SHA", () => expectInvalid((root) => mutateOpenApiWorkflow(root, (workflow) => workflow.replace('echo "head-sha=$HEAD_SHA"', 'echo "head-sha=${{ github.sha }}"')), /record exact base and head/i));
test("rejects an OpenAPI comparison without the /api/v1 scope", () => expectInvalid((root) => mutateOpenApiWorkflow(root, (workflow) => workflow.replace("--match-path '^/api/v1(?:/|$)'", "")), /scope comparison to \/api\/v1/i));
test("rejects an OpenAPI tool failure that can become an approval result", () => expectInvalid((root) => mutateOpenApiWorkflow(root, (workflow) => workflow.replace("elif [[ $result -eq 1 ]]; then", "else")), /exit 1 as breaking.*fatal/i));
for (const event of ["push", "pull_request"]) {
  test(`rejects paths-ignore substituted for ${event} accessibility paths`, () => expectInvalid((root) => mutateAccessibilityEventPaths(root, event, (body) => body.replace("    paths:", "    paths-ignore:")), /Accessibility path triggers.*main pushes.*pull requests/i));
  test(`rejects duplicated ${event} accessibility paths`, () => expectInvalid((root) => mutateAccessibilityEventPaths(root, event, (body) => body.replace('      - "src/Cmsify.Admin/**"\n      - "src/Cmsify.Contracts/**"\n', '      - "src/Cmsify.Admin/**"\n      - "src/Cmsify.Admin/**"\n')), /Accessibility path triggers.*main pushes.*pull requests/i));
  test(`rejects a negated required ${event} accessibility path`, () => expectInvalid((root) => mutateAccessibilityEventPaths(root, event, (body) => body.replace('      - "src/Cmsify.Admin/**"\n', '      - "src/Cmsify.Admin/**"\n      - "!src/Cmsify.Admin/**"\n')), new RegExp(`Accessibility ${event} path triggers must not contain negative entries`, "i")));
  test(`rejects a single-quoted negated ${event} accessibility path`, () => expectInvalid((root) => mutateAccessibilityEventPaths(root, event, (body) => body.replace('      - "src/Cmsify.Admin/**"\n', '      - "src/Cmsify.Admin/**"\n      - \'!src/Cmsify.Admin/**\'\n')), new RegExp(`Accessibility ${event} path triggers must not contain negative entries`, "i")));
  test(`rejects a blank/comment-separated negated ${event} accessibility path`, () => expectInvalid((root) => mutateAccessibilityEventPaths(root, event, (body) => body.replace('      - "src/Cmsify.Admin/**"\n', '      - "src/Cmsify.Admin/**"\n\n      # later path entry\n      - "!src/Cmsify.Admin/**"\n')), new RegExp(`Accessibility ${event} path triggers must not contain negative entries`, "i")));
  for (const [escapeName, escapeSequence] of [["hex", String.raw`\x21`], ["unicode", String.raw`\u0021`], ["long-unicode", String.raw`\U00000021`]]) {
    test(`rejects a ${escapeName}-escaped negated ${event} accessibility path`, () => expectInvalid((root) => mutateAccessibilityEventPaths(root, event, (body) => {
      const replacement = `      - "src/Cmsify.Admin/**"\n      - "${escapeSequence}src/Cmsify.Admin/**"\n`;
      assert.equal([...replacement].filter((character) => character === "\\").length, 1, `${event} ${escapeName} fixture must contain exactly one raw backslash`);
      return body.replace('      - "src/Cmsify.Admin/**"\n', replacement);
    }), new RegExp(`Accessibility ${event} path triggers must not contain YAML escape sequences`, "i")));
  }
}
test("rejects source-built release accessibility", () => expectInvalid((root) => mutateReleaseJob(root, "candidate-accessibility", (job) => job.replace(/node scripts\/release\/load-oci-candidate\.mjs load --archive artifacts\/oci\/cmsify-admin\.oci\.tar[^\n]*/, "dotnet run --project src/Cmsify.Admin/Cmsify.Admin.csproj")), /candidate accessibility.*Admin.*archive|candidate accessibility.*must not rebuild/i));
test("rejects an omitted clean candidate package", () => expectInvalid((root) => mutateReleaseJob(root, "dotnet-consumer", (job) => job.replace('            <package pattern="SyntaxCircus.Cmsify.Contracts" />\n', "")), /clean \.NET consumer.*all three.*local source/i));
test("rejects an unsigned promoted destination", () => expectInvalid((root) => mutateReleaseJob(root, "promote", (job) => job.replace(/\n\s*cosign sign --yes "\$API_SUBJECT"/, "")), /Cosign.*sign.*verify.*digest/i));
test("rejects certification that skips artifact smoke", () => expectInvalid((root) => mutateReleaseJob(root, "certify", (job) => job.replace("artifact-smoke, ", "")), /certify.*depend.*artifact-smoke/i));
test("rejects the legacy npm repository owner identity", () => expectInvalid((root) => { const path = resolve(root, "sdk/typescript/package.json"); writeFileSync(path, readFileSync(path, "utf8").replace("github.com/Syntax-Circus/cmsify", "github.com/SyntaxCircus/cmsify")); }, /@cmsify\/client.*repository.*Syntax-Circus/i));
test("rejects the legacy npm lockfile repository owner identity", () => expectInvalid((root) => { const path = resolve(root, "sdk/typescript/package-lock.json"); writeFileSync(path, readFileSync(path, "utf8").replace("github.com/Syntax-Circus/cmsify", "github.com/SyntaxCircus/cmsify")); }, /package-lock.*repository.*Syntax-Circus/i));
test("rejects an npm lockfile without the source private marker", () => expectInvalid((root) => {
  const path = resolve(root, "sdk/typescript/package-lock.json");
  const lock = JSON.parse(readFileSync(path, "utf8"));
  lock.packages[""].repository = { type: "git", url: "git+https://github.com/Syntax-Circus/cmsify.git", directory: "sdk/typescript" };
  delete lock.packages[""].private;
  writeFileSync(path, `${JSON.stringify(lock, null, 2)}\n`);
}, /package-lock.*private/i));
test("rejects an unpinned release action", () => expectInvalid((root) => mutateWorkflow(root, (workflow) => workflow.replace(/actions\/checkout@[0-9a-f]{40}/, "actions/checkout@v4")), /pinned/i));
test("rejects a mutable OCI loader helper image", () => expectInvalid((root) => {
  const path = resolve(root, "scripts/release/load-oci-candidate.mjs");
  writeFileSync(path, readFileSync(path, "utf8").replace(
    /quay\.io\/skopeo\/stable:v1\.22\.2@sha256:[0-9a-f]{64}/,
    "quay.io/skopeo/stable:v1.22.2",
  ));
}, /OCI loader.*Skopeo.*immutable|versioned.*digest/i));
test("rejects OCI loader topology contract drift", () => expectInvalid((root) => {
  const path = resolve(root, "scripts/release/load-oci-candidate.mjs");
  writeFileSync(path, readFileSync(path, "utf8").replace(
    'registry: "relay+importer"',
    'registry: "importer"',
  ));
}, /OCI loader.*Registry.*relay.*importer/i));
test("rejects a one-character repository action-pin mutation with file evidence", () => expectInvalid((root) => {
  const path = resolve(root, ".github/workflows/dotnet-test.yml");
  writeFileSync(path, readFileSync(path, "utf8").replace(
    "actions/checkout@11bd71901bbe5b1630ceea73d27597364c9af683",
    "actions/checkout@11bd71901bbe5b1630ceea73d27597364c9af68",
  ));
}, /dotnet-test\.yml:\d+: action reference/i));
test("rejects a promotion rebuild", () => expectInvalid((root) => mutateWorkflow(root, (workflow) => workflow.replace('REMOTE_SHA="${PEELED_SHA:-$LIGHTWEIGHT_SHA}";', 'docker buildx build --push .\n          REMOTE_SHA="${PEELED_SHA:-$LIGHTWEIGHT_SHA}";')), /Promotion must not rebuild/i));
test("rejects a second build candidate artifact", () => expectInvalid((root) => mutateReleaseJob(root, "build", (job) => `${job.trimEnd()}
      - uses: actions/upload-artifact@ea165f8d65b6e75b540449e92b4886f43607fa02 # v4.6.2
        with: { name: duplicate-release-candidate, path: artifacts, if-no-files-found: error, retention-days: 14 }
`), /build job.*exactly one.*candidate artifact/i));

test("rejects a missing dedicated upgrade and rollback workflow", () => expectInvalid((root) => {
  rmSync(resolve(root, ".github/workflows/upgrade-rollback.yml"), { force: true });
}, /dedicated upgrade.*workflow|required release file.*upgrade-rollback/i));

test("rejects a mutable action reference in the dedicated workflow", () => expectInvalid((root) => mutateUpgradeWorkflow(root, (workflow) => workflow.replace(/actions\/checkout@[0-9a-f]{40}/, "actions/checkout@v4")), /upgrade.*action.*pinned/i));

test("rejects missing upgrade-relevant path triggers", () => expectInvalid((root) => mutateUpgradeWorkflow(root, (workflow) => workflow.replaceAll('      - "eng/upgrade-tests/**"\n', "")), /path triggers.*eng\/upgrade-tests/i));

for (const composePath of [
  "**/compose*.yml",
  "**/compose*.yaml",
  "**/docker-compose*.yml",
  "**/docker-compose*.yaml",
]) {
  test(`rejects missing recursive ${composePath} path triggers`, () => expectInvalid((root) => mutateUpgradeWorkflow(root, (workflow) => workflow.replaceAll(`      - "${composePath}"\n`, "")), new RegExp(`path triggers.*${composePath.replace(/[.*+?^${}()|[\]\\]/g, "\\$&")}`, "i")));
}

test("rejects a dedicated workflow without fast fixture verification", () => expectInvalid((root) => mutateUpgradeWorkflow(root, (workflow) => workflow.replace(/\s*- name: Verify the checked-in fixture\n\s*run: node eng\/upgrade-tests\/cli\.mjs verify-fixture[^\n]*\n/, "\n")), /dedicated.*verify.*fixture/i));

test("rejects rehearsal without a preceding deterministic fixture check", () => expectInvalid((root) => mutateUpgradeWorkflow(root, (workflow) => workflow.replace(/\s*- name: Check deterministic fixture regeneration\n\s*run: node eng\/upgrade-tests\/cli\.mjs generate-fixture[^\n]*--check\n/, "\n")), /deterministic fixture.*before.*rehearsal/i));

test("rejects a release rehearsal that rebuilds the candidate", () => expectInvalid((root) => mutateReleaseJob(root, "upgrade-rollback", (job) => job.replace("          (cd artifacts && sha256sum --check SHA256SUMS)", "          docker build --tag replacement .\n          (cd artifacts && sha256sum --check SHA256SUMS)")), /release upgrade.*must not rebuild/i));

test("rejects native docker load consumption of an OCI-layout candidate", () => expectInvalid((root) => mutateReleaseJob(root, "artifact-smoke", (job) => job.replace(
  '          node scripts/release/load-oci-candidate.mjs load --archive artifacts/oci/cmsify-api.oci.tar --manifest artifacts/release-manifest.json --kind api --version "$VERSION"',
  "          docker load --input artifacts/oci/cmsify-api.oci.tar",
)), /artifact smoke.*OCI loader|native docker load/i));

test("rejects artifact smoke without both exact loader invocations", () => expectInvalid((root) => mutateReleaseJob(root, "artifact-smoke", (job) => job.replace(
  /\n\s*node scripts\/release\/load-oci-candidate\.mjs load --archive artifacts\/oci\/cmsify-admin\.oci\.tar[^\n]*/,
  "",
)), /artifact smoke.*both exact.*OCI/i));

test("rejects candidate accessibility with the wrong loader kind", () => expectInvalid((root) => mutateReleaseJob(root, "candidate-accessibility", (job) => job.replace(
  "--kind admin --version",
  "--kind api --version",
)), /candidate accessibility.*(?:Admin.*OCI loader|OCI loader.*Admin)/i));

test("rejects release upgrade without the descriptor-bound manifest argument", () => expectInvalid((root) => mutateReleaseJob(root, "upgrade-rollback", (job) => job.replace(
  " --manifest artifacts/release-manifest.json",
  "",
)), /release upgrade.*OCI loader.*manifest/i));

test("rejects certification that does not depend on upgrade-rollback", () => expectInvalid((root) => mutateReleaseJob(root, "certify", (job) => job.replace(", upgrade-rollback]", "]")), /certify.*depend.*upgrade-rollback/i));

test("rejects a release rehearsal without failure diagnostics upload", () => expectInvalid((root) => mutateWorkflow(root, (workflow) => workflow.replace("path: artifacts/upgrade-tests/**", "path: artifacts/not-upgrade-tests/**")), /release upgrade.*diagnostics.*failure/i));

test("rejects promotion that is reachable without certification and the upgrade gate", () => expectInvalid((root) => mutateWorkflow(root, (workflow) => workflow.replace("promote:\n    needs: [resolve, certify]", "promote:\n    needs: [resolve, build]")), /promotion.*certify.*upgrade.*gate/i));

test("rejects upgrade-rollback job error continuation", () => expectInvalid((root) => mutateReleaseJob(root, "upgrade-rollback", (job) => job.replace("    runs-on: ubuntu-latest", "    runs-on: ubuntu-latest\n    continue-on-error: true")), /upgrade-rollback.*continue-on-error.*fail closed/i));
test("rejects upgrade-rollback verification step error continuation", () => expectInvalid((root) => mutateReleaseJob(root, "upgrade-rollback", (job) => job.replace("      - name: Load and verify the exact release candidate\n        run:", "      - name: Load and verify the exact release candidate\n        continue-on-error: true\n        run:")), /upgrade-rollback.*continue-on-error.*fail closed/i));
test("rejects upgrade-rollback verification step bypass conditions", () => expectInvalid((root) => mutateReleaseJob(root, "upgrade-rollback", (job) => job.replace("      - name: Load and verify the exact release candidate\n        run:", "      - name: Load and verify the exact release candidate\n        if: always()\n        run:")), /upgrade-rollback.*step conditions.*failure diagnostics/i));
test("rejects certify job bypass conditions", () => expectInvalid((root) => mutateReleaseJob(root, "certify", (job) => job.replace("    runs-on: ubuntu-latest", "    runs-on: ubuntu-latest\n    if: always()")), /certify.*condition.*success.*upgrade.*gate/i));
test("rejects certify step error continuation", () => expectInvalid((root) => mutateReleaseJob(root, "certify", (job) => job.replace("      - run: (cd artifacts && sha256sum --check SHA256SUMS)", "      - continue-on-error: true\n        run: (cd artifacts && sha256sum --check SHA256SUMS)")), /certify.*continue-on-error.*fail closed/i));
test("rejects certify first-key step bypass conditions", () => expectInvalid((root) => mutateReleaseJob(root, "certify", (job) => job.replace("      - uses: actions/attest-build-provenance", "      - if: always()\n        uses: actions/attest-build-provenance")), /certify.*step condition.*success.*candidate.*gate/i));
test("rejects promote job bypass conditions", () => expectInvalid((root) => mutateReleaseJob(root, "promote", (job) => job.replace("    runs-on: ubuntu-latest", "    runs-on: ubuntu-latest\n    if: ${{ always() }}")), /promotion.*condition.*success.*certify/i));
test("rejects promotion step error continuation", () => expectInvalid((root) => mutateReleaseJob(root, "promote", (job) => job.replace("      - name: Copy certified OCI descriptors and compare remote digests\n        shell: bash", "      - name: Copy certified OCI descriptors and compare remote digests\n        continue-on-error: true\n        shell: bash")), /promotion.*continue-on-error.*fail closed/i));
test("rejects promotion step bypass conditions", () => expectInvalid((root) => mutateReleaseJob(root, "promote", (job) => job.replace("      - name: Copy certified OCI descriptors and compare remote digests\n        shell: bash", "      - name: Copy certified OCI descriptors and compare remote digests\n        if: always()\n        shell: bash")), /promotion.*step condition.*success.*certify/i));

test("rejects a release gate that does not import the exact built OCI archive", () => expectInvalid((root) => mutateReleaseJob(root, "upgrade-rollback", (job) => job.replace(
  /node scripts\/release\/load-oci-candidate\.mjs load --archive artifacts\/oci\/cmsify-api\.oci\.tar[^\n]*/,
  "docker pull docker.io/syntaxcircus/cmsify-api:$VERSION",
)), /release upgrade.*exact.*API archive|release upgrade.*OCI loader/i));

test("rejects a release gate without moving-baseline verification", () => expectInvalid((root) => mutateWorkflow(root, (workflow) => workflow.replace(/\n\s*node eng\/upgrade-tests\/cli\.mjs verify-release-baseline[^\n]*/, "")), /release upgrade.*moving baseline/i));

test("rejects a release gate that does not derive every prerequisite image from the fixture manifest", () => expectInvalid((root) => mutateReleaseJob(root, "upgrade-rollback", (job) => job.replace(/\n\s*BASELINE_API_IMAGE=.*\.baseline\.apiImage[^\n]*/, "")), /release upgrade.*manifest-derived.*baseline API/i));
test("rejects a release gate that omits the exact historical API pull", () => expectInvalid((root) => mutateReleaseJob(root, "upgrade-rollback", (job) => job.replace(/\n\s*docker pull --platform linux\/amd64 "\$BASELINE_API_IMAGE"/, "")), /release upgrade.*pull.*baseline API/i));
test("rejects a release gate that omits the exact PostgreSQL pull", () => expectInvalid((root) => mutateReleaseJob(root, "upgrade-rollback", (job) => job.replace(/\n\s*docker pull --platform linux\/amd64 "\$POSTGRES_IMAGE"/, "")), /release upgrade.*pull.*PostgreSQL/i));
test("rejects a release gate that omits the exact MinIO pull", () => expectInvalid((root) => mutateReleaseJob(root, "upgrade-rollback", (job) => job.replace(/\n\s*docker pull --platform linux\/amd64 "\$MINIO_IMAGE"/, "")), /release upgrade.*pull.*MinIO/i));
test("rejects prerequisite pulls without an explicit linux/amd64 platform", () => expectInvalid((root) => mutateReleaseJob(root, "upgrade-rollback", (job) => job.replace('docker pull --platform linux/amd64 "$POSTGRES_IMAGE"', 'docker pull "$POSTGRES_IMAGE"')), /release upgrade.*pull.*linux\/amd64/i));
test("rejects importing the candidate archive before all prerequisite pulls", () => expectInvalid((root) => mutateReleaseJob(root, "upgrade-rollback", (job) => job.replace(
  '          docker pull --platform linux/amd64 "$MINIO_IMAGE"\n          node scripts/release/load-oci-candidate.mjs load --archive artifacts/oci/cmsify-api.oci.tar --manifest artifacts/release-manifest.json --kind api --version "$VERSION"',
  '          node scripts/release/load-oci-candidate.mjs load --archive artifacts/oci/cmsify-api.oci.tar --manifest artifacts/release-manifest.json --kind api --version "$VERSION"\n          docker pull --platform linux/amd64 "$MINIO_IMAGE"',
)), /release upgrade.*pull.*archive/i));

test("rejects release rehearsal candidate-variable drift", () => expectInvalid((root) => mutateReleaseJob(root, "upgrade-rollback", (job) => job.replace('--candidate-image "$CANDIDATE_IMAGE"', '--candidate-image "syntaxcircus/cmsify-api:latest"')), /release upgrade.*exact loaded candidate.*CANDIDATE_IMAGE/i));
test("rejects a pull between exact archive import and rehearsal", () => expectInvalid((root) => mutateReleaseJob(root, "upgrade-rollback", (job) => job.replace(/(\s*node scripts\/release\/load-oci-candidate\.mjs load --archive artifacts\/oci\/cmsify-api\.oci\.tar[^\n]*)/, '$1\n          docker image pull "$CANDIDATE_IMAGE"')), /release upgrade.*pull.*between.*load.*rehearsal/i));
test("rejects a re-tag between exact archive load and rehearsal", () => expectInvalid((root) => mutateReleaseJob(root, "upgrade-rollback", (job) => job.replace("          node eng/upgrade-tests/cli.mjs rehearse", "          docker image tag syntaxcircus/cmsify-api:other \"$CANDIDATE_IMAGE\"\n          node eng/upgrade-tests/cli.mjs rehearse")), /release upgrade.*re-tag.*between.*load.*rehearsal/i));

for (const [separator, compound] of [
  ["and", 'true && docker pull "$CANDIDATE_IMAGE"'],
  ["semicolon", 'true; docker image pull "$CANDIDATE_IMAGE"'],
  ["or", 'false || docker pull "$CANDIDATE_IMAGE"'],
  ["pipe", 'true | docker image pull "$CANDIDATE_IMAGE"'],
  ["background", 'true & docker pull "$CANDIDATE_IMAGE"'],
]) {
  test(`rejects a ${separator}-separated pull between exact archive load and rehearsal`, () => expectInvalid((root) => mutateReleaseJob(root, "upgrade-rollback", (job) => job.replace(
    "          node eng/upgrade-tests/cli.mjs rehearse",
    `          ${compound}\n          node eng/upgrade-tests/cli.mjs rehearse`,
  )), /release upgrade.*pull.*between.*load.*rehearsal/i));
}

test("rejects a compound re-tag between exact archive load and rehearsal", () => expectInvalid((root) => mutateReleaseJob(root, "upgrade-rollback", (job) => job.replace(
  "          node eng/upgrade-tests/cli.mjs rehearse",
  '          true && docker tag syntaxcircus/cmsify-api:other "$CANDIDATE_IMAGE"\n          node eng/upgrade-tests/cli.mjs rehearse',
)), /release upgrade.*re-tag.*between.*load.*rehearsal/i));

test("rejects combined ORAS boolean and path flags", () => expectInvalid((root) => mutateWorkflow(root, (workflow) => workflow.replace("oras manifest fetch --descriptor --oci-layout-path artifacts/oci/api", "oras manifest fetch --descriptor --oci-layout --oci-layout-path artifacts/oci/api")), /ORAS.*combined.*--oci-layout/i));
test("rejects combined ORAS copy boolean and path flags", () => expectInvalid((root) => mutateWorkflow(root, (workflow) => workflow.replace("oras cp --from-oci-layout-path artifacts/oci/api", "oras cp --from-oci-layout --from-oci-layout-path artifacts/oci/api")), /ORAS.*combined.*--from-oci-layout/i));
test("rejects Docker Hub preflight without a scoped Bearer token", () => expectInvalid((root) => mutateWorkflow(root, (workflow) => workflow.replace("https://auth.docker.io/token?service=registry.docker.io&scope=repository:$image:pull,push", "https://registry-1.docker.io/v2/token")), /Docker Hub.*scoped Bearer token/i));
test("rejects Docker Hub preflight without all OCI and Docker manifest media types", () => expectInvalid((root) => mutateWorkflow(root, (workflow) => workflow.replace(", application/vnd.docker.distribution.manifest.list.v2+json", "")), /Docker Hub.*four manifest media types/i));
test("rejects Docker Hub preflight that treats HTTP 200 as absent", () => expectInvalid((root) => mutateWorkflow(root, (workflow) => workflow.replace("case \"$status\" in 404) ;; *)", "case \"$status\" in 404|200) ;; *)")), /Docker Hub.*only HTTP 404/i));
test("rejects a missing exact npm-version absence preflight", () => expectInvalid((root) => mutateWorkflow(root, (workflow) => workflow.replace("https://registry.npmjs.org/@cmsify%2Fclient/$VERSION", "https://registry.npmjs.org/@cmsify%2Fclient/latest")), /npm.*exact-version.*404/i));
test("rejects a NuGet preflight that treats HTTP 200 as absent", () => expectInvalid((root) => mutateWorkflow(root, (workflow) => workflow.replace('case "$http_code" in 404) ;; 200) exit 1 ;;', 'case "$http_code" in 404|200) ;;')), /NuGet.*only.*HTTP 404/i));
test("rejects a NuGet preflight that preserves uppercase prerelease versions", () => expectInvalid((root) => mutateWorkflow(root, (workflow) => workflow.replace('NUGET_VERSION="${VERSION,,}"', 'NUGET_VERSION="$VERSION"')), /NuGet.*normalize.*flat-container.*version/i));
test("rejects package publication before OCI digest-preserving copy and equality", () => expectInvalid((root) => mutateWorkflow(root, (workflow) => workflow.replace("oras cp --from-oci-layout-path artifacts/oci/api", "dotnet nuget push premature.nupkg\n          oras cp --from-oci-layout-path artifacts/oci/api")), /OCI.*remote digest equality.*before.*NuGet.*npm/i));
test("rejects release npm packing without the resolved source SHA", () => expectInvalid((root) => mutateWorkflow(root, (workflow) => workflow.replace('npm pkg set version="$VERSION" gitHead="$SOURCE_SHA"', 'npm pkg set version="$VERSION"')), /npm candidate.*gitHead.*SOURCE_SHA/i));
test("rejects NuGet packing without explicit source SHA binding", () => expectInvalid((root) => mutateWorkflow(root, (workflow) => workflow.replaceAll(' -p:RepositoryCommit="$SOURCE_SHA"', "")), /three NuGet candidates.*RepositoryCommit.*SOURCE_SHA/i));
test("rejects OCI layouts whose platform descriptor can be obscured by inline Buildx provenance", () => expectInvalid((root) => mutateWorkflow(root, (workflow) => workflow.replace(" --provenance=false", "")), /OCI candidate.*canonical Docker Hub BuildKit/i));
test("rejects OCI output without the canonical Docker Hub tag and containerd annotations", () => expectInvalid((root) => mutateWorkflow(root, (workflow) => workflow.replace('--annotation "manifest-descriptor:io.containerd.image.name=docker.io/syntaxcircus/cmsify-api:$VERSION" ', "")), /OCI candidate.*canonical Docker Hub/i));
test("rejects NuGet SBOM staging that omits an exact candidate archive", () => expectInvalid((root) => mutateReleaseJob(root, "build", (job) => job.replace(
  "for package in SyntaxCircus.Cmsify.Contracts SyntaxCircus.Cmsify.Client SyntaxCircus.Cmsify.Client.DistributedCaching; do",
  "for package in SyntaxCircus.Cmsify.Contracts SyntaxCircus.Cmsify.Client; do",
)), /NuGet SBOM.*all three exact candidate archives/i));
test("rejects NuGet SBOM restore that can ignore a copied candidate archive", () => expectInvalid((root) => mutateReleaseJob(root, "build", (job) => job.replace(
  '                <package pattern="SyntaxCircus.Cmsify.Contracts" />\n',
  "",
)), /NuGet SBOM.*map all three candidate IDs.*isolated source/i));
test("rejects npm SBOM staging that installs from the source tree", () => expectInvalid((root) => mutateReleaseJob(root, "build", (job) => job.replace(
  'TARBALL="$GITHUB_WORKSPACE/artifacts/npm/cmsify-client-$VERSION.tgz"',
  'TARBALL="$GITHUB_WORKSPACE/sdk/typescript"',
)), /npm SBOM.*exact candidate tarball/i));
test("rejects NuGet SBOM restore outside its isolated package cache", () => expectInvalid((root) => mutateReleaseJob(root, "build", (job) => job.replace(
  '--packages "$NUGET_CACHE"',
  '--packages artifacts/nuget',
)), /NuGet SBOM.*isolated package cache/i));
test("rejects npm SBOM install without its isolated cache", () => expectInvalid((root) => mutateReleaseJob(root, "build", (job) => job.replace(
  'NPM_CONFIG_CACHE="$SBOM_STAGING_ROOT/npm-cache" npm install',
  "npm install",
)), /npm SBOM.*isolated consumer and cache/i));
test("rejects package SBOM generation that scans candidate archive directories", () => expectInvalid(
  (root) => mutateReleaseJob(root, "build", (job) => job
    .replace('dir:"$NUGET_CACHE"', "dir:artifacts/nuget")
    .replace('dir:"$NPM_CONSUMER"', "dir:artifacts/npm")),
  /package SBOM.*populated.*trees/i,
));
test("rejects package SBOM staging inside the uploaded artifact root", () => expectInvalid((root) => mutateReleaseJob(root, "build", (job) => job.replace(
  'SBOM_STAGING_ROOT: ${{ runner.temp }}/cmsify-sbom-inputs',
  "SBOM_STAGING_ROOT: artifacts/sbom-inputs",
)), /SBOM staging.*run-owned temporary root.*outside artifacts/i));
test("rejects package SBOM staging cleanup after checksum construction", () => expectInvalid((root) => mutateReleaseJob(root, "build", (job) => job.replace(
  '          rm -rf "$SBOM_STAGING_ROOT"\n          test ! -e "$SBOM_STAGING_ROOT"\n',
  "",
).replace(
  "          (cd artifacts && find . -type f ! -name SHA256SUMS -printf '%P\\0' | sort -z | xargs -0 sha256sum) > artifacts/SHA256SUMS",
  "          (cd artifacts && find . -type f ! -name SHA256SUMS -printf '%P\\0' | sort -z | xargs -0 sha256sum) > artifacts/SHA256SUMS\n          rm -rf \"$SBOM_STAGING_ROOT\"\n          test ! -e \"$SBOM_STAGING_ROOT\"",
)), /SBOM staging.*removed before checksum construction and upload/i));
test("rejects SBOM generation without stable identity finalization", () => expectInvalid((root) => mutateWorkflow(root, (workflow) => workflow.replace(/\n\s*node scripts\/release\/finalize-spdx\.mjs[^\n]*/, "")), /four SPDX[\s\S]*stable[\s\S]*identit/i));
test("rejects artifact smoke without candidate-root checksum verification", () => expectInvalid((root) => mutateReleaseJob(root, "artifact-smoke", (job) => job.replace("          (cd artifacts && sha256sum --check SHA256SUMS)\n", "")), /artifact smoke.*checksums/i));
test("rejects artifact smoke that omits the exact Admin archive", () => expectInvalid((root) => mutateReleaseJob(root, "artifact-smoke", (job) => job.replace(/\s*node scripts\/release\/load-oci-candidate\.mjs load --archive artifacts\/oci\/cmsify-admin\.oci\.tar[^\n]*\n/, "\n")), /artifact smoke.*both exact.*OCI/i));
test("rejects artifact smoke that duplicates the old shell instead of Task 4", () => expectInvalid((root) => mutateReleaseJob(root, "artifact-smoke", (job) => job.replace("          node eng/release-smoke/cli.mjs certify", "          docker run syntaxcircus/cmsify-api:$VERSION\n          node eng/release-smoke/cli.mjs certify")), /artifact smoke.*Task 4|must not rebuild or pull/i));
test("rejects artifact smoke replacement-image pulls", () => expectInvalid((root) => mutateReleaseJob(root, "artifact-smoke", (job) => job.replace(/(\s*node scripts\/release\/load-oci-candidate\.mjs load --archive artifacts\/oci\/cmsify-api\.oci\.tar[^\n]*)/, '          docker pull docker.io/syntaxcircus/cmsify-api:$VERSION\n$1')), /artifact smoke.*must not rebuild or pull/i));
test("rejects artifact smoke error continuation", () => expectInvalid((root) => mutateReleaseJob(root, "artifact-smoke", (job) => job.replace("    runs-on: ubuntu-latest", "    runs-on: ubuntu-latest\n    continue-on-error: true")), /artifact smoke.*fail closed/i));
test("rejects candidate accessibility replacement-image pulls", () => expectInvalid((root) => mutateReleaseJob(root, "candidate-accessibility", (job) => job.replace(/(\s*node scripts\/release\/load-oci-candidate\.mjs load --archive artifacts\/oci\/cmsify-admin\.oci\.tar[^\n]*)/, '          docker pull docker.io/syntaxcircus/cmsify-admin:$VERSION\n$1')), /candidate accessibility.*must not rebuild or pull/i));
test("rejects candidate accessibility without pull-never", () => expectInvalid((root) => mutateReleaseJob(root, "candidate-accessibility", (job) => job.replace("docker run -d --pull=never", "docker run -d")), /candidate accessibility.*without pulling/i));
test("rejects candidate accessibility error continuation", () => expectInvalid((root) => mutateReleaseJob(root, "candidate-accessibility", (job) => job.replace("    runs-on: ubuntu-latest", "    runs-on: ubuntu-latest\n    continue-on-error: true")), /candidate accessibility.*fail closed/i));
test("rejects provenance that does not attest SHA256SUMS subjects", () => expectInvalid((root) => mutateReleaseJob(root, "certify", (job) => job.replace("subject-checksums: artifacts/SHA256SUMS", "subject-path: artifacts/release-manifest.json")), /attest.*exact checked candidate subjects/i));
test("rejects Cosign signing a mutable tag", () => expectInvalid((root) => mutateReleaseJob(root, "promote", (job) => job.replace('API_SUBJECT="docker.io/syntaxcircus/cmsify-api@$API_REMOTE"', 'API_SUBJECT="docker.io/syntaxcircus/cmsify-api:$VERSION"')), /Cosign.*repository@sha256:digest|Cosign.*digest subjects/i));
test("rejects branch validation that omits the complete release-contract suite", () => expectInvalid((root) => { const path = resolve(root, ".github/workflows/dotnet-test.yml"); writeFileSync(path, readFileSync(path, "utf8").replace("tests/release-contract/*.test.mjs", "tests/release-contract/validate-release-tag.test.mjs")); }, /branch.*all release-contract tests/i));
test("rejects branch validation that omits fast upgrade unit tests", () => expectInvalid((root) => { const path = resolve(root, ".github/workflows/dotnet-test.yml"); writeFileSync(path, readFileSync(path, "utf8").replace("tests/upgrade/unit/*.test.mjs ", "")); }, /branch.*upgrade unit tests/i));
test("rejects branch validation that omits fixture verification", () => expectInvalid((root) => { const path = resolve(root, ".github/workflows/dotnet-test.yml"); writeFileSync(path, readFileSync(path, "utf8").replace(/\n\s*node eng\/upgrade-tests\/cli\.mjs verify-fixture[^\n]*/, "")); }, /branch.*verify.*fixture/i));
