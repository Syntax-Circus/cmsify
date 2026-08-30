import assert from "node:assert/strict";
import { spawnSync } from "node:child_process";
import { appendFileSync, readFileSync, unlinkSync, writeFileSync } from "node:fs";
import { dirname, resolve } from "node:path";
import { fileURLToPath } from "node:url";
import test from "node:test";
import {
  SOURCE_SHA,
  VERSION,
  candidatePath,
  createValidCandidate,
  mutateJsonFile,
  mutateOciLayout,
  removeCandidate,
  swapFiles,
} from "./release-candidate-fixture.mjs";

const repositoryRoot = resolve(dirname(fileURLToPath(import.meta.url)), "..", "..");
const verifier = resolve(repositoryRoot, "scripts", "release", "verify-release-artifacts.mjs");

function verify(root, { version = VERSION, sourceSha = SOURCE_SHA } = {}) {
  return spawnSync(process.execPath, [verifier, "--artifacts", root, "--version", version, "--source-sha", sourceSha], { encoding: "utf8" });
}

function expectInvalid(options, diagnostic) {
  const root = createValidCandidate(options);
  try {
    const result = verify(root);
    assert.notEqual(result.status, 0, "mutation unexpectedly passed artifact verification");
    assert.match(result.stderr, diagnostic);
  } finally {
    removeCandidate(root);
  }
}

test("accepts a complete Dockerless release candidate fixture", () => {
  const root = createValidCandidate();
  try {
    const result = verify(root);
    assert.equal(result.status, 0, result.stderr || result.stdout);
    assert.match(result.stdout, new RegExp(`Release artifacts verified for ${VERSION}`));
  } finally {
    removeCandidate(root);
  }
});

test("models BuildKit-normalized Docker Hub identity in each OCI layout, metadata descriptor, and release manifest", () => {
  const root = createValidCandidate();
  try {
    for (const kind of ["api", "admin"]) {
      const expected = `docker.io/syntaxcircus/cmsify-${kind}:${VERSION}`;
      const metadata = JSON.parse(readFileSync(candidatePath(root, `oci/cmsify-${kind}.metadata.json`), "utf8"))["containerimage.descriptor"];
      const manifest = JSON.parse(readFileSync(candidatePath(root, "release-manifest.json"), "utf8"));
      assert.equal(metadata.annotations["io.containerd.image.name"], expected);
      assert.equal(manifest.oci[kind].ref, expected);
      assert.equal(manifest.oci[kind].imageName, expected);
    }
  } finally { removeCandidate(root); }
});

test("rejects an invalid source SHA before artifact inspection", () => {
  const root = createValidCandidate();
  try {
    const result = verify(root, { sourceSha: "not-a-commit" });
    assert.notEqual(result.status, 0);
    assert.match(result.stderr, /immutable 40-character source SHA/i);
  } finally { removeCandidate(root); }
});

test("rejects an invalid SemVer candidate before artifact inspection", () => {
  const root = createValidCandidate();
  try {
    const result = verify(root, { version: "1.0" });
    assert.notEqual(result.status, 0);
    assert.match(result.stderr, /requires a SemVer version/i);
  } finally { removeCandidate(root); }
});

const nugetMutations = [
  ["nuspec package ID", (state) => { state.nuget[0].id = "SyntaxCircus.Cmsify.Wrong"; }, /NuGet.*exact package ID.*SyntaxCircus\.Cmsify\.Contracts/i],
  ["nuspec version", (state) => { state.nuget[0].version = "9.9.9"; }, /NuGet.*SyntaxCircus\.Cmsify\.Contracts.*release version 1\.2\.3/i],
  ["target framework", (state) => { state.nuget[0].framework = "net9.0"; }, /NuGet.*SyntaxCircus\.Cmsify\.Contracts.*net10\.0/i],
  ["MIT expression", (state) => { state.nuget[0].licenseExpression = "AGPL-3.0-or-later"; }, /NuGet.*SyntaxCircus\.Cmsify\.Contracts.*MIT license expression/i],
  ["license file declaration", (state) => { state.nuget[0].licenseFile = "COPYING.txt"; }, /NuGet.*SyntaxCircus\.Cmsify\.Contracts.*license file.*LICENSE-MIT\.txt/i],
  ["MIT license payload", (state) => { state.nuget[0].licensePayload = "not the MIT license\n"; }, /NuGet.*SyntaxCircus\.Cmsify\.Contracts.*MIT license payload/i],
  ["repository type", (state) => { state.nuget[0].repositoryType = "svn"; }, /NuGet.*SyntaxCircus\.Cmsify\.Contracts.*source repository/i],
  ["repository URL", (state) => { state.nuget[0].repositoryUrl = "https://example.invalid/repo"; }, /NuGet.*SyntaxCircus\.Cmsify\.Contracts.*source repository/i],
  ["repository commit", (state) => { state.nuget[0].repositoryCommit = "f".repeat(40); }, /NuGet.*SyntaxCircus\.Cmsify\.Contracts.*source commit/i],
];

for (const [name, mutate, diagnostic] of nugetMutations) {
  test(`rejects an otherwise-valid NuGet candidate with the wrong ${name}`, () => expectInvalid({ mutate }, diagnostic));
}

const npmMetadataMutations = [
  ["name", (metadata) => { metadata.name = "@cmsify/not-client"; }, /npm.*name.*@cmsify\/client/i],
  ["version", (metadata) => { metadata.version = "9.9.9"; }, /npm.*version.*1\.2\.3/i],
  ["license", (metadata) => { metadata.license = "AGPL-3.0-or-later"; }, /npm.*license.*MIT/i],
  ["Node floor", (metadata) => { metadata.engines.node = ">=18"; }, /npm.*Node.*>=20/i],
  ["private true", (metadata) => { metadata.private = true; }, /npm.*public.*private.*absent.*false/i],
  ["private string false", (metadata) => { metadata.private = "false"; }, /npm.*public.*private.*absent.*false/i],
  ["repository type", (metadata) => { metadata.repository.type = "svn"; }, /npm.*repository.*type/i],
  ["repository URL", (metadata) => { metadata.repository.url = "https://example.invalid/repo"; }, /npm.*repository.*url/i],
  ["legacy repository URL", (metadata) => { metadata.repository.url = "git+https://github.com/SyntaxCircus/cmsify.git"; }, /npm.*repository.*url.*Syntax-Circus/i],
  ["repository directory", (metadata) => { metadata.repository.directory = "sdk/js"; }, /npm.*repository.*directory.*sdk\/typescript/i],
  ["gitHead", (metadata) => { metadata.gitHead = "f".repeat(40); }, /npm.*gitHead.*source SHA/i],
  ["module type", (metadata) => { metadata.type = "commonjs"; }, /npm.*type.*module/i],
  ["main", (metadata) => { metadata.main = "./dist/not-cjs.cjs"; }, /npm.*main.*dist\/index\.cjs/i],
  ["module", (metadata) => { metadata.module = "./dist/not-esm.js"; }, /npm.*module.*dist\/index\.js/i],
  ["types", (metadata) => { metadata.types = "./dist/not-types.d.ts"; }, /npm.*types.*dist\/index\.d\.ts/i],
  ["exports import", (metadata) => { metadata.exports["."].import = "./dist/not-esm.js"; }, /npm.*exports.*import.*dist\/index\.js/i],
  ["exports require", (metadata) => { metadata.exports["."].require = "./dist/not-cjs.cjs"; }, /npm.*exports.*require.*dist\/index\.cjs/i],
  ["exports declarations", (metadata) => { metadata.exports["."].types = "./dist/not-types.d.ts"; }, /npm.*exports.*types.*dist\/index\.d\.ts/i],
  ["extra exports target", (metadata) => { metadata.exports["./private"] = "./dist/private.js"; }, /npm.*exports surface.*only.*public entrypoint/i],
  ["published files allowlist", (metadata) => { metadata.files = ["dist"]; }, /npm.*files.*dist.*src\/generated/i],
];

for (const [name, mutateMetadata, diagnostic] of npmMetadataMutations) {
  test(`rejects an otherwise-valid packed npm candidate with wrong ${name} metadata`, () => expectInvalid({ mutate(state) { mutateMetadata(state.npm.metadata); } }, diagnostic));
}

const npmMemberMutations = [
  ["LICENSE", "package/LICENSE", /npm.*exact member package\/LICENSE/i],
  ["CommonJS entrypoint", "package/dist/index.cjs", /npm.*main.*archive member.*package\/dist\/index\.cjs/i],
  ["ESM entrypoint", "package/dist/index.js", /npm.*module.*archive member.*package\/dist\/index\.js/i],
  ["declaration entrypoint", "package/dist/index.d.ts", /npm.*types.*archive member.*package\/dist\/index\.d\.ts/i],
];

for (const [name, member, diagnostic] of npmMemberMutations) {
  test(`rejects a packed npm candidate missing its exact ${name} member`, () => expectInvalid({ mutate(state) { delete state.npm.members[member]; state.npm.members[`${member}.bak`] = "wrong member\n"; } }, diagnostic));
}

test("rejects a packed npm candidate with a non-MIT LICENSE payload", () => expectInvalid({ mutate(state) { state.npm.members["package/LICENSE"] = "not the MIT license\n"; } }, /npm.*LICENSE payload.*MIT License/i));
test("rejects a packed npm candidate with a private implementation member", () => expectInvalid({ mutate(state) { state.npm.members["package/.env"] = "SECRET=fixture\n"; } }, /npm.*unsupported archive member.*package\/\.env/i));
test("rejects package/dist/private.js even when every declared entrypoint remains valid", () => expectInvalid({ mutate(state) { state.npm.members["package/dist/private.js"] = "export const secret = true;\n"; } }, /npm.*unexpected archive member.*package\/dist\/private\.js/i));
test("rejects package/src/generated/private.ts even when every declared entrypoint remains valid", () => expectInvalid({ mutate(state) { state.npm.members["package/src/generated/private.ts"] = "export interface Private {}\n"; } }, /npm.*unexpected archive member.*package\/src\/generated\/private\.ts/i));

for (const member of [
  "package/README.md",
  "package/dist/index.cjs.map",
  "package/dist/index.js.map",
  "package/dist/index.d.cts",
  "package/src/generated/client.ts",
  "package/src/generated/schema.ts",
]) {
  test(`rejects a packed npm candidate missing intentional member ${member}`, () => expectInvalid({ mutate(state) { delete state.npm.members[member]; } }, new RegExp(`npm.*missing archive member.*${member.replace(/[.*+?^${}()|[\]\\]/g, "\\$&")}`, "i")));
}

const ociStateMutations = [
  ["API repository", (state) => { state.oci.api.repository = "docker.io/syntaxcircus/not-api"; }, /OCI API.*repository.*docker\.io\/syntaxcircus\/cmsify-api/i],
  ["API qualified ref", (state) => { state.oci.api.ref = "docker.io/syntaxcircus/cmsify-api:wrong"; }, /OCI API.*ref.*docker\.io\/syntaxcircus\/cmsify-api:1\.2\.3/i],
  ["descriptor media type", (state) => { state.oci.api.descriptorMediaType = "application/json"; }, /OCI API.*descriptor media type/i],
  ["descriptor platform OS", (state) => { state.oci.api.descriptorPlatform.os = "windows"; }, /OCI API.*descriptor platform.*linux\/amd64/i],
  ["descriptor platform architecture", (state) => { state.oci.api.descriptorPlatform.architecture = "arm64"; }, /OCI API.*descriptor platform.*linux\/amd64/i],
  ["config media type", (state) => { state.oci.api.configMediaType = "application/json"; }, /OCI API.*config media type/i],
  ["config OS", (state) => { state.oci.api.config.os = "windows"; }, /OCI API.*configuration platform.*linux\/amd64/i],
  ["config architecture", (state) => { state.oci.api.config.architecture = "arm64"; }, /OCI API.*configuration platform.*linux\/amd64/i],
  ["source label", (state) => { state.oci.api.config.config.Labels["org.opencontainers.image.source"] = "https://example.invalid/repo"; }, /OCI API.*source label/i],
  ["source SHA label", (state) => { state.oci.api.config.config.Labels["org.opencontainers.image.revision"] = "f".repeat(40); }, /OCI API.*revision label.*source SHA/i],
  ["version label", (state) => { state.oci.api.config.config.Labels["org.opencontainers.image.version"] = "9.9.9"; }, /OCI API.*version label/i],
  ["license label", (state) => { state.oci.api.config.config.Labels["org.opencontainers.image.licenses"] = "MIT"; }, /OCI API.*license label.*AGPL-3\.0-or-later/i],
  ["image title", (state) => { state.oci.api.config.config.Labels["org.opencontainers.image.title"] = "Cmsify Admin"; }, /OCI API.*title label.*Cmsify API/i],
];

for (const [name, mutate, diagnostic] of ociStateMutations) {
  test(`rejects an otherwise-valid OCI candidate with the wrong ${name}`, () => expectInvalid({ mutate }, diagnostic));
}

const ociDescriptorMutations = [
  ["digest", ({ descriptor }) => { descriptor.digest = `sha256:${"f".repeat(64)}`; }, /OCI API.*descriptor digest.*blob/i],
  ["size", ({ descriptor }) => { descriptor.size += 1; }, /OCI API.*descriptor size/i],
  ["media type", ({ descriptor }) => { descriptor.mediaType = "application/json"; }, /OCI API.*descriptor media type/i],
  ["platform", ({ descriptor }) => { descriptor.platform.architecture = "arm64"; }, /OCI API.*descriptor platform.*linux\/amd64/i],
];

for (const [name, mutate, diagnostic] of ociDescriptorMutations) {
  test(`rejects an OCI index whose selected descriptor has the wrong ${name}`, () => expectInvalid({ afterRender({ root }) { mutateOciLayout(root, "api", mutate); } }, diagnostic));
}

test("resolves the OCI descriptor by exact ref rather than manifests[0]", () => {
  const root = createValidCandidate();
  try { const result = verify(root); assert.equal(result.status, 0, result.stderr || result.stdout); }
  finally { removeCandidate(root); }
});
test("rejects an OCI archive descriptor whose ref.name contains the full repository identity", () => expectInvalid({ afterRender({ root }) { mutateOciLayout(root, "api", ({ descriptor }) => { descriptor.annotations["org.opencontainers.image.ref.name"] = `syntaxcircus/cmsify-api:${VERSION}`; }); } }, /OCI API.*tag ref\.name/i));
test("rejects a BuildKit-normalized archive descriptor whose containerd name loses the canonical Docker Hub registry", () => expectInvalid({ afterRender({ root }) { mutateOciLayout(root, "api", ({ descriptor }) => { descriptor.annotations["io.containerd.image.name"] = `syntaxcircus/cmsify-api:${VERSION}`; }); } }, /OCI API.*containerd image name/i));

test("rejects an OCI manifest whose config digest does not match a blob", () => expectInvalid({ afterRender({ root }) { mutateOciLayout(root, "api", ({ manifest }) => { manifest.config.digest = `sha256:${"e".repeat(64)}`; }); } }, /OCI API.*config digest.*blob/i));
test("rejects an OCI manifest whose config declared size is wrong", () => expectInvalid({ afterRender({ root }) { mutateOciLayout(root, "api", ({ manifest }) => { manifest.config.size += 1; }); } }, /OCI API.*config.*declared size/i));
test("rejects swapped API and Admin OCI subjects", () => expectInvalid({ afterRender({ root }) { swapFiles(root, "oci/cmsify-api.oci.tar", "oci/cmsify-admin.oci.tar"); swapFiles(root, "oci/cmsify-api.metadata.json", "oci/cmsify-admin.metadata.json"); } }, /OCI API.*containerd image name.*cmsify-api|API.*Admin.*swap/i));
test("rejects an OCI image with no filesystem layers", () => expectInvalid({ mutate(state) { state.oci.api.layers = []; } }, /OCI API.*at least one filesystem layer/i));
test("rejects an OCI layer whose digest is not sha256", () => expectInvalid({ mutate(state) { state.oci.api.layers[0].digest = `sha512:${"f".repeat(128)}`; } }, /OCI API.*layer 1 digest.*sha256/i));
test("rejects an OCI layer whose declared size is wrong", () => expectInvalid({ mutate(state) { state.oci.api.layers[0].sizeDelta = 1; } }, /OCI API.*layer 1.*declared size/i));
test("rejects an OCI layer whose media type is unsupported", () => expectInvalid({ mutate(state) { state.oci.api.layers[0].mediaType = "application/json"; } }, /OCI API.*layer 1 media type/i));
test("rejects an OCI manifest whose referenced layer blob is missing", () => expectInvalid({ afterRender({ root }) { mutateOciLayout(root, "api", ({ staging, manifest }) => { unlinkSync(resolve(staging, "blobs", "sha256", manifest.layers[0].digest.slice(7))); }); } }, /OCI API.*layer 1 digest.*existing blob/i));
test("rejects an OCI layer whose compressed bytes do not match its digest", () => expectInvalid({ afterRender({ root }) { mutateOciLayout(root, "api", ({ staging, manifest }) => { writeFileSync(resolve(staging, "blobs", "sha256", manifest.layers[0].digest.slice(7)), "corrupt compressed layer"); }); } }, /OCI API.*layer 1 digest.*blob SHA-256/i));
test("rejects an OCI config whose rootfs type is not layers", () => expectInvalid({ mutate(state) { state.oci.api.rootfsType = "rootfs"; } }, /OCI API.*rootfs type.*layers/i));
test("rejects an OCI config with fewer diff_ids than manifest layers", () => expectInvalid({ mutate(state) { state.oci.api.rootfsDiffIds = (diffIds) => diffIds.slice(1); } }, /OCI API.*rootfs diff_ids count.*manifest layers/i));
test("rejects an OCI config whose rootfs diff_ids are in the wrong layer order", () => expectInvalid({ mutate(state) { state.oci.api.rootfsDiffIds = (diffIds) => diffIds.reverse(); } }, /OCI API.*layer 1.*diff_id.*uncompressed/i));
test("rejects an OCI config whose rootfs diff_id does not match the uncompressed layer", () => expectInvalid({ mutate(state) { state.oci.api.rootfsDiffIds = (diffIds) => [`sha256:${"f".repeat(64)}`, ...diffIds.slice(1)]; } }, /OCI API.*layer 1.*diff_id.*uncompressed/i));

const spdxDefinitions = [
  ["nuget", "NuGet", "SyntaxCircus.Cmsify.Contracts", "AGPL-3.0-or-later", "MIT"],
  ["npm", "npm", "@cmsify/client", "AGPL-3.0-or-later", "MIT"],
  ["api", "API", "syntaxcircus/cmsify-api", "MIT", "AGPL-3.0-or-later"],
  ["admin", "Admin", "syntaxcircus/cmsify-admin", "MIT", "AGPL-3.0-or-later"],
];

for (const [kind, label, expectedPackage, wrongLicense, expectedLicense] of spdxDefinitions) {
  test(`rejects the ${label} SPDX document with the wrong document name`, () => expectInvalid({ mutate(state) { state.spdx[kind].name = "Unrelated SBOM"; } }, new RegExp(`SPDX ${label}.*document name`, "i")));
  test(`rejects the ${label} SPDX document with the wrong document namespace`, () => expectInvalid({ mutate(state) { state.spdx[kind].namespaceFile = "unrelated.spdx.json"; } }, new RegExp(`SPDX ${label}.*document namespace`, "i")));
  test(`rejects the ${label} SPDX document with an unrelated subject package`, () => expectInvalid({ mutate(state) { state.spdx[kind].packageNames = state.spdx[kind].packageNames.map((name, index) => index === 0 ? "unrelated-package" : name); } }, new RegExp(`SPDX ${label}.*package.*${expectedPackage.replace(/[.*+?^${}()|[\]\\]/g, "\\$&")}`, "i")));
  test(`rejects the ${label} SPDX subject package with the wrong license`, () => expectInvalid({ mutate(state) { state.spdx[kind].license = wrongLicense; } }, new RegExp(`SPDX ${label}.*license.*${expectedLicense.replaceAll(".", "\\.")}`, "i")));
}

test("rejects an SPDX package with the wrong release version", () => expectInvalid({ afterRender({ root }) { mutateJsonFile(root, "sbom/cmsify-npm.spdx.json", (document) => { document.packages[0].versionInfo = "9.9.9"; }); } }, /SPDX npm.*package version.*1\.2\.3/i));
test("rejects an SPDX documentDescribes identity unrelated to its subject", () => expectInvalid({ afterRender({ root }) { mutateJsonFile(root, "sbom/cmsify-npm.spdx.json", (document) => { document.documentDescribes = ["SPDXRef-unrelated"]; }); } }, /SPDX npm.*documentDescribes.*SPDXRef-npm-1/i));
test("rejects an OCI SPDX package whose purl is not digest-bound", () => expectInvalid({ afterRender({ root }) { mutateJsonFile(root, "sbom/cmsify-api.spdx.json", (document) => { document.packages[0].externalRefs[0].referenceLocator = "pkg:oci/syntaxcircus/cmsify-api@sha256:bad"; }); } }, /SPDX API.*purl.*certified OCI digest/i));
test("rejects a one-byte release-manifest OCI digest disagreement even when checksums match", () => expectInvalid({ afterRender({ root }) { mutateJsonFile(root, "release-manifest.json", (manifest) => { const digest = manifest.oci.api.digest; manifest.oci.api.digest = `${digest.slice(0, -1)}${digest.endsWith("0") ? "1" : "0"}`; }); } }, /Release manifest OCI API digest.*selected descriptor digest/i));
test("rejects a release manifest whose OCI tag or full containerd name disagrees with the certified layout", () => expectInvalid({ afterRender({ root }) { mutateJsonFile(root, "release-manifest.json", (manifest) => { manifest.oci.api.tag = "wrong"; manifest.oci.api.imageName = `syntaxcircus/cmsify-admin:${VERSION}`; }); } }, /Release manifest OCI API tag.*1\.2\.3|containerd image name.*cmsify-api/i));
test("rejects a one-byte OCI SPDX subject disagreement even when checksums match", () => expectInvalid({ afterRender({ root }) { mutateJsonFile(root, "sbom/cmsify-api.spdx.json", (document) => { const reference = document.packages[0].externalRefs[0].referenceLocator; document.packages[0].externalRefs[0].referenceLocator = `${reference.slice(0, -1)}${reference.endsWith("0") ? "1" : "0"}`; }); } }, /SPDX API.*purl.*certified OCI digest/i));
test("rejects a NuGet SPDX package with the wrong exact purl", () => expectInvalid({ afterRender({ root }) { mutateJsonFile(root, "sbom/cmsify-nuget.spdx.json", (document) => { document.packages[0].externalRefs[0].referenceLocator = `pkg:nuget/SyntaxCircus.Cmsify.Wrong@${VERSION}`; }); } }, /SPDX NuGet.*purl.*SyntaxCircus\.Cmsify\.Contracts/i));
test("rejects an npm SPDX package with the wrong scoped-package purl", () => expectInvalid({ afterRender({ root }) { mutateJsonFile(root, "sbom/cmsify-npm.spdx.json", (document) => { document.packages[0].externalRefs[0].referenceLocator = `pkg:npm/@cmsify%2Fclient@${VERSION}`; }); } }, /SPDX npm.*purl.*%40cmsify\/client/i));
test("rejects swapped NuGet SPDX purls", () => expectInvalid({ afterRender({ root }) { mutateJsonFile(root, "sbom/cmsify-nuget.spdx.json", (document) => { const first = document.packages[0].externalRefs[0].referenceLocator; document.packages[0].externalRefs[0].referenceLocator = document.packages[1].externalRefs[0].referenceLocator; document.packages[1].externalRefs[0].referenceLocator = first; }); } }, /SPDX NuGet.*purl.*SyntaxCircus\.Cmsify\.(?:Contracts|Client)/i));
test("rejects an SPDX document with no retained dependency inventory", () => expectInvalid({ afterRender({ root }) { mutateJsonFile(root, "sbom/cmsify-npm.spdx.json", (document) => { document.packages = document.packages.filter((candidate) => candidate.name === "@cmsify/client"); document.relationships = document.relationships.filter((relationship) => relationship.relationshipType === "DESCRIBES"); }); } }, /SPDX npm.*meaningful dependency inventory/i));
test("rejects a dangling SPDX relationship reference", () => expectInvalid({ afterRender({ root }) { mutateJsonFile(root, "sbom/cmsify-npm.spdx.json", (document) => { document.relationships[0].relatedSpdxElement = "SPDXRef-missing-dependency"; }); } }, /SPDX npm.*relationship.*dangling.*SPDXRef-missing-dependency/i));
test("rejects a contradictory SPDX DESCRIBES relationship", () => expectInvalid({ afterRender({ root }) { mutateJsonFile(root, "sbom/cmsify-npm.spdx.json", (document) => { document.relationships.push({ spdxElementId: "SPDXRef-DOCUMENT", relationshipType: "DESCRIBES", relatedSpdxElement: "SPDXRef-npm-dependency" }); }); } }, /SPDX npm.*DESCRIBES relationship.*documentDescribes/i));
test("rejects a retained dependency whose changed SPDXID leaves a stale relationship", () => expectInvalid({ afterRender({ root }) { mutateJsonFile(root, "sbom/cmsify-npm.spdx.json", (document) => { document.packages.find((candidate) => candidate.name === "fixture-npm-dependency").SPDXID = "SPDXRef-renamed-dependency"; }); } }, /SPDX npm.*relationship.*dangling.*SPDXRef-npm-dependency/i));
test("rejects an SPDX document without the immutable source SHA", () => expectInvalid({ afterRender({ root }) { mutateJsonFile(root, "sbom/cmsify-npm.spdx.json", (document) => { document.creationInfo.comment = "unrelated source"; }); } }, /SPDX npm.*source SHA/i));
test("rejects swapped npm and NuGet SPDX documents", () => expectInvalid({ afterRender({ root }) { swapFiles(root, "sbom/cmsify-npm.spdx.json", "sbom/cmsify-nuget.spdx.json"); } }, /SPDX npm.*document name|SPDX npm.*package/i));
test("rejects swapped API and Admin SPDX subjects", () => expectInvalid({ afterRender({ root }) { swapFiles(root, "sbom/cmsify-api.spdx.json", "sbom/cmsify-admin.spdx.json"); } }, /SPDX API.*document name|SPDX API.*package/i));

test("rejects a checksum omission", () => expectInvalid({ afterChecksums({ root }) { const path = candidatePath(root, "SHA256SUMS"); const lines = readFileSync(path, "utf8").trimEnd().split(/\r?\n/); writeFileSync(path, `${lines.slice(1).join("\n")}\n`); } }, /SHA256SUMS.*omits/i));
test("rejects a changed checksum", () => expectInvalid({ afterChecksums({ root }) { const path = candidatePath(root, "SHA256SUMS"); const contents = readFileSync(path, "utf8"); writeFileSync(path, contents.replace(/^[0-9a-f]{64}/, "f".repeat(64))); } }, /Checksum mismatch/i));
test("rejects an extra checksum entry", () => expectInvalid({ afterChecksums({ root }) { appendFileSync(candidatePath(root, "SHA256SUMS"), `${"0".repeat(64)}  unrelated.txt\n`); } }, /SHA256SUMS.*extra entry.*unrelated\.txt/i));
test("rejects an unchecked extra candidate file", () => expectInvalid({ afterChecksums({ root }) { writeFileSync(candidatePath(root, "unrelated.txt"), "extra\n"); } }, /unchecked extra file.*unrelated\.txt/i));
