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
  "sdk/typescript/package.json",
  "sdk/typescript/LICENSE",
  "src/Cmsify.Contracts/Cmsify.Contracts.csproj",
  "src/Cmsify.Api/Dockerfile",
  "src/Cmsify.Admin/Dockerfile",
  "sdk/dotnet/src/SyntaxCircus.Cmsify.Client/SyntaxCircus.Cmsify.Client.csproj",
  "sdk/dotnet/src/SyntaxCircus.Cmsify.Client.DistributedCaching/SyntaxCircus.Cmsify.Client.DistributedCaching.csproj",
  ".github/workflows/dotnet-test.yml",
  workflowPath,
];

function write(root, path, contents) {
  const destination = resolve(root, path);
  mkdirSync(dirname(destination), { recursive: true });
  writeFileSync(destination, contents);
}

function round4Workflow(contents) {
  return contents;
}

function createFixture(mutator) {
  const root = mkdtempSync(resolve(tmpdir(), "cmsify-release-contract-"));
  for (const path of contractFiles) {
    const destination = resolve(root, path);
    mkdirSync(dirname(destination), { recursive: true });
    cpSync(resolve(repositoryRoot, path), destination);
  }
  write(root, workflowPath, round4Workflow(readFileSync(resolve(root, workflowPath), "utf8")));
  write(root, "scripts/release/finalize-spdx.mjs", "// fixture: stable SPDX identities are finalized before checksums\n");
  for (const [path, image] of [["src/Cmsify.Api/Dockerfile", "api"], ["src/Cmsify.Admin/Dockerfile", "admin"]]) {
    const dockerfile = readFileSync(resolve(root, path), "utf8");
    if (!dockerfile.includes("org.opencontainers.image.ref.name")) write(root, path, dockerfile.replace("      org.opencontainers.image.source=", `      org.opencontainers.image.ref.name=\"syntaxcircus/cmsify-${image}:\${BUILD_VERSION}\" \\\n+      org.opencontainers.image.source=`));
  }
  mutator?.(root);
  return root;
}

function mutateWorkflow(root, mutate) {
  const path = resolve(root, workflowPath);
  writeFileSync(path, mutate(readFileSync(path, "utf8")));
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

test("rejects branch publication", () => expectInvalid((root) => mutateWorkflow(root, (workflow) => workflow.replace('tags: ["v*"]', "branches: [main]")), /tag-only/i));
test("rejects an unpinned release action", () => expectInvalid((root) => mutateWorkflow(root, (workflow) => workflow.replace(/actions\/checkout@[0-9a-f]{40}/, "actions/checkout@v4")), /pinned/i));
test("rejects a promotion rebuild", () => expectInvalid((root) => mutateWorkflow(root, (workflow) => workflow.replace('REMOTE_SHA="${PEELED_SHA:-$LIGHTWEIGHT_SHA}";', 'docker buildx build --push .\n          REMOTE_SHA="${PEELED_SHA:-$LIGHTWEIGHT_SHA}";')), /Promotion must not rebuild/i));

test("rejects combined ORAS boolean and path flags", () => expectInvalid((root) => mutateWorkflow(root, (workflow) => workflow.replace("oras manifest fetch --descriptor --oci-layout-path artifacts/oci/api", "oras manifest fetch --descriptor --oci-layout --oci-layout-path artifacts/oci/api")), /ORAS.*combined.*--oci-layout/i));
test("rejects combined ORAS copy boolean and path flags", () => expectInvalid((root) => mutateWorkflow(root, (workflow) => workflow.replace("oras cp --from-oci-layout-path artifacts/oci/api", "oras cp --from-oci-layout --from-oci-layout-path artifacts/oci/api")), /ORAS.*combined.*--from-oci-layout/i));
test("rejects Docker Hub preflight without a scoped Bearer token", () => expectInvalid((root) => mutateWorkflow(root, (workflow) => workflow.replace("https://auth.docker.io/token?service=registry.docker.io&scope=repository:$image:pull,push", "https://registry-1.docker.io/v2/token")), /Docker Hub.*scoped Bearer token/i));
test("rejects Docker Hub preflight without all OCI and Docker manifest media types", () => expectInvalid((root) => mutateWorkflow(root, (workflow) => workflow.replace(", application/vnd.docker.distribution.manifest.list.v2+json", "")), /Docker Hub.*four manifest media types/i));
test("rejects Docker Hub preflight that treats HTTP 200 as absent", () => expectInvalid((root) => mutateWorkflow(root, (workflow) => workflow.replace("case \"$status\" in 404) ;; *)", "case \"$status\" in 404|200) ;; *)")), /Docker Hub.*only HTTP 404/i));
test("rejects a missing exact npm-version absence preflight", () => expectInvalid((root) => mutateWorkflow(root, (workflow) => workflow.replace("https://registry.npmjs.org/@cmsify%2Fclient/$VERSION", "https://registry.npmjs.org/@cmsify%2Fclient/latest")), /npm.*exact-version.*404/i));
test("rejects a NuGet preflight that treats HTTP 200 as absent", () => expectInvalid((root) => mutateWorkflow(root, (workflow) => workflow.replace('case "$http_code" in 404) ;; 200) exit 1 ;;', 'case "$http_code" in 404|200) ;;')), /NuGet.*only.*HTTP 404/i));
test("rejects package publication before OCI digest-preserving copy and equality", () => expectInvalid((root) => mutateWorkflow(root, (workflow) => workflow.replace("oras cp --from-oci-layout-path artifacts/oci/api", "dotnet nuget push premature.nupkg\n          oras cp --from-oci-layout-path artifacts/oci/api")), /OCI.*remote digest equality.*before.*NuGet.*npm/i));
test("rejects release npm packing without the resolved source SHA", () => expectInvalid((root) => mutateWorkflow(root, (workflow) => workflow.replace('npm pkg set version="$VERSION" gitHead="$SOURCE_SHA"', 'npm pkg set version="$VERSION"')), /npm candidate.*gitHead.*SOURCE_SHA/i));
test("rejects NuGet packing without explicit source SHA binding", () => expectInvalid((root) => mutateWorkflow(root, (workflow) => workflow.replaceAll(' -p:RepositoryCommit="$SOURCE_SHA"', "")), /three NuGet candidates.*RepositoryCommit.*SOURCE_SHA/i));
test("rejects OCI layouts whose platform descriptor can be obscured by inline Buildx provenance", () => expectInvalid((root) => mutateWorkflow(root, (workflow) => workflow.replace(" --provenance=false", "")), /OCI candidate.*provenance=false.*single linux\/amd64 manifest/i));
test("rejects an OCI Dockerfile with a wrong qualified image identity label", () => expectInvalid((root) => { const path = resolve(root, "src/Cmsify.Api/Dockerfile"); writeFileSync(path, readFileSync(path, "utf8").replace("syntaxcircus/cmsify-api:${BUILD_VERSION}", "syntaxcircus/cmsify-admin:${BUILD_VERSION}")); }, /API Dockerfile.*ref.name.*qualified image identity/i));
test("rejects SBOM generation without stable identity finalization", () => expectInvalid((root) => mutateWorkflow(root, (workflow) => workflow.replace(/\n\s*node scripts\/release\/finalize-spdx\.mjs[^\n]*/, "")), /four SPDX[\s\S]*stable[\s\S]*identit/i));
test("rejects smoke resources created before cleanup registration", () => expectInvalid((root) => mutateWorkflow(root, (workflow) => workflow.replace("trap cleanup EXIT", "docker run -d --name leaked-resource busybox true\n          trap cleanup EXIT")), /smoke cleanup.*immediately after first resource/i));
test("rejects unbounded PostgreSQL readiness", () => expectInvalid((root) => mutateWorkflow(root, (workflow) => workflow.replace("for attempt in {1..30}; do\n            if docker exec cmsify-postgres-smoke", "while true; do\n            if docker exec cmsify-postgres-smoke")), /PostgreSQL readiness.*bounded/i));
