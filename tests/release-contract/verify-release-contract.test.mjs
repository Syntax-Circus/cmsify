import assert from "node:assert/strict";
import { execFileSync, spawnSync } from "node:child_process";
import { mkdtempSync, mkdirSync, rmSync, writeFileSync } from "node:fs";
import { tmpdir } from "node:os";
import { dirname, resolve } from "node:path";
import { fileURLToPath } from "node:url";
import test from "node:test";

const repositoryRoot = resolve(dirname(fileURLToPath(import.meta.url)), "..", "..");
const verifier = resolve(repositoryRoot, "scripts", "release", "verify-release-contract.mjs");

const sha = "11bd71901bbe5b1630ceea73d27597364c9af683";

function write(root, relativePath, contents) {
  const destination = resolve(root, relativePath);
  mkdirSync(dirname(destination), { recursive: true });
  writeFileSync(destination, contents);
}

function createFixture(mutator) {
  const root = mkdtempSync(resolve(tmpdir(), "cmsify-release-contract-"));
  write(root, "LICENSE", "GNU AFFERO GENERAL PUBLIC LICENSE\n");
  write(root, "CHANGELOG.md", "# Changelog\n\n## [Unreleased]\n\n## [1.0.0] - 2026-08-25\n");
  write(root, "Directory.Build.props", "<Project><PropertyGroup><Version Condition=\"'$(Version)' == ''\">0.0.0-local</Version></PropertyGroup></Project>\n");
  write(root, "sdk/typescript/package.json", JSON.stringify({ name: "@cmsify/client", version: "0.0.0-local", license: "MIT", engines: { node: ">=20" } }));
  write(root, "sdk/typescript/LICENSE", "MIT License\n");
  for (const project of [
    "src/Cmsify.Contracts/Cmsify.Contracts.csproj",
    "sdk/dotnet/src/SyntaxCircus.Cmsify.Client/SyntaxCircus.Cmsify.Client.csproj",
    "sdk/dotnet/src/SyntaxCircus.Cmsify.Client.DistributedCaching/SyntaxCircus.Cmsify.Client.DistributedCaching.csproj",
  ]) write(root, project, "<Project><PropertyGroup><TargetFramework>net10.0</TargetFramework><PackageLicenseExpression>MIT</PackageLicenseExpression></PropertyGroup></Project>\n");
  write(root, ".github/workflows/publish-cmsify.yml", `name: Certify and promote Cmsify release
on:
  push:
    tags: ["v*"]
jobs:
  resolve:
    runs-on: ubuntu-latest
    outputs:
      source_sha: \${{ steps.release.outputs.source_sha }}
    steps:
      - uses: actions/checkout@${sha}
      - id: release
        run: |
          test "\${GITHUB_REF_TYPE}" = tag
          VERSION="\${GITHUB_REF_NAME#v}"
          node scripts/release/validate-release-tag.mjs "\${GITHUB_REF_NAME}" --source-sha "\${GITHUB_SHA}"
          echo "source_sha=\${GITHUB_SHA}" >> "\${GITHUB_OUTPUT}"
  build:
    needs: resolve
    runs-on: ubuntu-latest
    steps:
      - uses: actions/checkout@${sha}
        with:
          ref: \${{ needs.resolve.outputs.source_sha }}
      - uses: actions/setup-dotnet@c2fa09f4bde5ebb9d1777cf28262a3eb3db3ced7
      - uses: actions/setup-node@a0853c24544627f65ddf259abe73b1d18a591444
      - run: dotnet pack Cmsify.slnx -p:Version=\${VERSION}
      - run: npm pack --pack-destination artifacts/npm
      - run: docker buildx build --output type=oci,dest=artifacts/oci/cmsify-api.oci.tar .
      - run: node scripts/release/verify-release-artifacts.mjs --artifacts artifacts --version "\${VERSION}" --source-sha "\${{ needs.resolve.outputs.source_sha }}"
      - uses: actions/upload-artifact@ea165f8d65b6e75b540449e92b4886f43607fa02
        with:
          name: release-candidate
          path: artifacts
  dotnet-consumer:
    needs: [resolve, build]
    runs-on: ubuntu-latest
    steps:
      - uses: actions/setup-dotnet@c2fa09f4bde5ebb9d1777cf28262a3eb3db3ced7
      - uses: actions/download-artifact@d3f86a106a0bac45b974a628896c90dbdf5c8093
      - run: dotnet new console --framework net10.0 && dotnet add package SyntaxCircus.Cmsify.Contracts && dotnet add package SyntaxCircus.Cmsify.Client && dotnet add package SyntaxCircus.Cmsify.Client.DistributedCaching
  node-consumer:
    needs: [resolve, build]
    strategy:
      matrix:
        node-version: ["20", "22"]
    steps:
      - uses: actions/download-artifact@d3f86a106a0bac45b974a628896c90dbdf5c8093
      - run: CMSIFY_CLIENT_TARBALL=artifacts/npm/cmsify-client.tgz npm run test:consumer
  certify:
    needs: [resolve, build]
    runs-on: ubuntu-latest
    permissions:
      attestations: write
      id-token: write
    steps:
      - uses: actions/download-artifact@d3f86a106a0bac45b974a628896c90dbdf5c8093
        with:
          name: release-candidate
      - uses: actions/attest-build-provenance@e8998f949152b193b063cb0ec769d69d929409be
        with:
          subject-path: artifacts/SHA256SUMS
  promote:
    needs: [resolve, certify]
    runs-on: ubuntu-latest
    environment: release
    steps:
      - uses: actions/download-artifact@d3f86a106a0bac45b974a628896c90dbdf5c8093
        with:
          name: release-candidate
      - run: sha256sum --check artifacts/SHA256SUMS
      - run: dotnet nuget push artifacts/nuget/*.nupkg --source https://api.nuget.org/v3/index.json
      - run: npm publish artifacts/npm/*.tgz --provenance
      - run: docker load --input artifacts/oci/cmsify-api.oci.tar
      - run: docker push syntaxcircus/cmsify-api:\${VERSION}
      - run: gh release create "v\${VERSION}" artifacts/SHA256SUMS artifacts/sbom/*.spdx.json
`);
  mutator?.(root);
  return root;
}

function verify(root) {
  return spawnSync(process.execPath, [verifier, "--root", root], { encoding: "utf8" });
}

test("accepts the repository release contract", () => {
  const result = verify(repositoryRoot);
  assert.equal(result.status, 0, result.stderr || result.stdout);
});

test("rejects automatic branch publication, mutable promotion, and unpinned actions", () => {
  const root = createFixture((fixtureRoot) => {
    const workflow = resolve(fixtureRoot, ".github/workflows/publish-cmsify.yml");
    const contents = execFileSync(process.execPath, ["-e", `process.stdout.write(require('fs').readFileSync(${JSON.stringify(workflow)}, 'utf8'))`], { encoding: "utf8" })
      .replace('tags: ["v*"]', 'branches: [main]')
      .replace(`actions/checkout@${sha}`, "actions/checkout@v4")
      .replace('docker load --input artifacts/oci/cmsify-api.oci.tar', 'docker buildx build --push .');
    writeFileSync(workflow, contents);
  });
  try {
    const result = verify(root);
    assert.notEqual(result.status, 0);
    assert.match(result.stderr, /tag-only|pinned|rebuild/i);
  } finally {
    rmSync(root, { recursive: true, force: true });
  }
});

test("rejects a promotion job that rebuilds its OCI candidate", () => {
  const root = createFixture((fixtureRoot) => {
    const workflow = resolve(fixtureRoot, ".github/workflows/publish-cmsify.yml");
    writeFileSync(workflow, execFileSync(process.execPath, ["-e", `process.stdout.write(require('fs').readFileSync(${JSON.stringify(workflow)}, 'utf8'))`], { encoding: "utf8" })
      .replace('docker load --input artifacts/oci/cmsify-api.oci.tar', 'docker buildx build --push .'));
  });
  try {
    const result = verify(root);
    assert.notEqual(result.status, 0);
    assert.match(result.stderr, /Promotion must not rebuild/i);
  } finally {
    rmSync(root, { recursive: true, force: true });
  }
});

test("rejects a release candidate that skips clean .NET 10 or Node 20/22 consumers", () => {
  const root = createFixture();
  try {
    const result = verify(root);
    assert.notEqual(result.status, 0);
    assert.match(result.stderr, /clean \.NET 10|all three candidate packages|Node 20\/22|Source \.NET packages/i);
  } finally {
    rmSync(root, { recursive: true, force: true });
  }
});

test("rejects a candidate/promotion path without deterministic OCI tooling, trusted publishing, or digest-preserving copy", () => {
  const root = createFixture();
  try {
    const result = verify(root);
    assert.notEqual(result.status, 0);
    assert.match(result.stderr, /buildx|SBOM|trusted npm|NuGet OIDC|digest-preserving|remote tag/i);
  } finally {
    rmSync(root, { recursive: true, force: true });
  }
});

test("rejects a mismatched SDK license and a publishable source version", () => {
  const root = createFixture((fixtureRoot) => {
    write(fixtureRoot, "sdk/typescript/package.json", JSON.stringify({ name: "@cmsify/client", version: "1.0.0", license: "AGPL-3.0-only", engines: { node: ">=18" } }));
  });
  try {
    const result = verify(root);
    assert.notEqual(result.status, 0);
    assert.match(result.stderr, /MIT|0\.0\.0-local|Node 20/i);
  } finally {
    rmSync(root, { recursive: true, force: true });
  }
});
