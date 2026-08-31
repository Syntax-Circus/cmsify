import assert from "node:assert/strict";
import { spawnSync } from "node:child_process";
import { readFileSync } from "node:fs";
import { dirname, resolve } from "node:path";
import { fileURLToPath } from "node:url";
import test from "node:test";
import {
  SOURCE_SHA,
  VERSION,
  candidatePath,
  createValidCandidate,
  mutateJsonFile,
  refreshChecksums,
  removeCandidate,
} from "./release-candidate-fixture.mjs";

const repositoryRoot = resolve(dirname(fileURLToPath(import.meta.url)), "..", "..");
const finalizer = resolve(repositoryRoot, "scripts", "release", "finalize-spdx.mjs");
const verifier = resolve(repositoryRoot, "scripts", "release", "verify-release-artifacts.mjs");

function runFinalizer(root) {
  return spawnSync(process.execPath, [finalizer, "--artifacts", root, "--version", VERSION, "--source-sha", SOURCE_SHA], { encoding: "utf8" });
}

function makeSyftLike(root) {
  for (const kind of ["nuget", "npm", "api", "admin"]) {
    mutateJsonFile(root, `sbom/cmsify-${kind}.spdx.json`, (document) => {
      const replacements = new Map(document.packages.map((candidate, index) => [candidate.SPDXID, `SPDXRef-syft-${kind}-${index + 1}`]));
      for (const candidate of document.packages) {
        candidate.SPDXID = replacements.get(candidate.SPDXID);
        if (!candidate.name.startsWith("fixture-")) {
          candidate.versionInfo = "0.0.0-syft";
          candidate.licenseDeclared = "NOASSERTION";
          candidate.externalRefs = [{ referenceCategory: "OTHER", referenceType: "cpe23Type", referenceLocator: "cpe:2.3:a:fixture:subject:*:*:*:*:*:*:*:*" }];
        }
      }
      document.documentDescribes = document.documentDescribes.map((id) => replacements.get(id));
      for (const relationship of document.relationships) {
        relationship.spdxElementId = replacements.get(relationship.spdxElementId) ?? relationship.spdxElementId;
        relationship.relatedSpdxElement = replacements.get(relationship.relatedSpdxElement) ?? relationship.relatedSpdxElement;
      }
    });
  }
}

function removeDirectoryScanDescribes(root, kind) {
  mutateJsonFile(root, `sbom/cmsify-${kind}.spdx.json`, (document) => {
    document.documentDescribes = [];
    document.relationships = document.relationships.filter((relationship) =>
      !(
        (relationship.spdxElementId === document.SPDXID && relationship.relationshipType === "DESCRIBES") ||
        (relationship.relatedSpdxElement === document.SPDXID && relationship.relationshipType === "DESCRIBED_BY")
      ));
    const directoryRoot = document.packages.find((candidate) => candidate.name === `fixture-${kind}-dependency`);
    document.relationships.push({
      spdxElementId: document.SPDXID,
      relationshipType: "DESCRIBES",
      relatedSpdxElement: directoryRoot.SPDXID,
    });
  });
}

test("finalizes real subject and dependency inventory without replacing SPDXIDs or dependency relationships", () => {
  const root = createValidCandidate();
  try {
    makeSyftLike(root);
    const before = JSON.parse(readFileSync(candidatePath(root, "sbom/cmsify-npm.spdx.json"), "utf8"));
    const subjectBefore = before.packages.find((candidate) => candidate.name === "@cmsify/client");
    const dependencyBefore = before.packages.find((candidate) => candidate.name === "fixture-npm-dependency");
    const dependencyRelationship = before.relationships.find((relationship) => relationship.relationshipType === "DEPENDS_ON");

    const result = runFinalizer(root);
    assert.equal(result.status, 0, result.stderr || result.stdout);

    const after = JSON.parse(readFileSync(candidatePath(root, "sbom/cmsify-npm.spdx.json"), "utf8"));
    assert.equal(after.packages.find((candidate) => candidate.name === "@cmsify/client").SPDXID, subjectBefore.SPDXID);
    assert.deepEqual(after.packages.find((candidate) => candidate.name === "fixture-npm-dependency"), dependencyBefore);
    assert.ok(after.relationships.some((relationship) =>
      relationship.spdxElementId === dependencyRelationship.spdxElementId &&
      relationship.relationshipType === dependencyRelationship.relationshipType &&
      relationship.relatedSpdxElement === dependencyRelationship.relatedSpdxElement));

    refreshChecksums(root);
    const verified = spawnSync(process.execPath, [verifier, "--artifacts", root, "--version", VERSION, "--source-sha", SOURCE_SHA], { encoding: "utf8" });
    assert.equal(verified.status, 0, verified.stderr || verified.stdout);
  } finally {
    removeCandidate(root);
  }
});

test("finalizes directory-scanned package inventories without input documentDescribes", () => {
  const root = createValidCandidate();
  try {
    makeSyftLike(root);
    removeDirectoryScanDescribes(root, "nuget");
    removeDirectoryScanDescribes(root, "npm");
    mutateJsonFile(root, "sbom/cmsify-nuget.spdx.json", (document) => {
      document.packages.find((candidate) => candidate.name === "SyntaxCircus.Cmsify.Contracts").name = "Cmsify.Contracts";
    });

    const result = runFinalizer(root);
    assert.equal(result.status, 0, result.stderr || result.stdout);

    for (const [kind, names] of [
      ["nuget", ["SyntaxCircus.Cmsify.Contracts", "SyntaxCircus.Cmsify.Client", "SyntaxCircus.Cmsify.Client.DistributedCaching"]],
      ["npm", ["@cmsify/client"]],
    ]) {
      const document = JSON.parse(readFileSync(candidatePath(root, `sbom/cmsify-${kind}.spdx.json`), "utf8"));
      const subjectIds = names.map((name) => document.packages.find((candidate) => candidate.name === name)?.SPDXID);
      assert.deepEqual(document.documentDescribes, subjectIds);
      assert.ok(subjectIds.every(Boolean));
    }
  } finally {
    removeCandidate(root);
  }
});

test("rejects directory-scanned inventory without the exact named release subject", () => {
  const root = createValidCandidate();
  try {
    makeSyftLike(root);
    removeDirectoryScanDescribes(root, "npm");
    mutateJsonFile(root, "sbom/cmsify-npm.spdx.json", (document) => {
      document.packages.find((candidate) => candidate.name === "@cmsify/client").name = "unrelated-package";
    });

    const result = runFinalizer(root);
    assert.notEqual(result.status, 0, "unnamed directory-scanned subject unexpectedly finalized");
    assert.match(result.stderr, /npm.*missing existing target evidence.*@cmsify\/client/i);
  } finally {
    removeCandidate(root);
  }
});

test("rejects empty Syft inventory instead of synthesizing a passing subject", () => {
  const root = createValidCandidate();
  try {
    mutateJsonFile(root, "sbom/cmsify-npm.spdx.json", (document) => {
      document.packages = [];
      document.documentDescribes = [];
      document.relationships = [];
    });
    const result = runFinalizer(root);
    assert.notEqual(result.status, 0, "empty Syft inventory unexpectedly finalized");
    assert.match(result.stderr, /npm.*meaningful existing inventory/i);
  } finally {
    removeCandidate(root);
  }
});

test("rejects dangling Syft relationships before subject normalization", () => {
  const root = createValidCandidate();
  try {
    makeSyftLike(root);
    mutateJsonFile(root, "sbom/cmsify-npm.spdx.json", (document) => {
      document.relationships[0].relatedSpdxElement = "SPDXRef-missing";
    });
    const result = runFinalizer(root);
    assert.notEqual(result.status, 0, "dangling Syft relationship unexpectedly finalized");
    assert.match(result.stderr, /npm.*dangling relationship.*SPDXRef-missing/i);
  } finally {
    removeCandidate(root);
  }
});

test("rejects contradictory Syft DESCRIBES relationships before subject normalization", () => {
  const root = createValidCandidate();
  try {
    makeSyftLike(root);
    mutateJsonFile(root, "sbom/cmsify-npm.spdx.json", (document) => {
      const dependency = document.packages.find((candidate) => candidate.name === "fixture-npm-dependency");
      document.relationships.push({
        spdxElementId: document.SPDXID,
        relationshipType: "DESCRIBES",
        relatedSpdxElement: dependency.SPDXID,
      });
    });
    const result = runFinalizer(root);
    assert.notEqual(result.status, 0, "contradictory Syft DESCRIBES relationship unexpectedly finalized");
    assert.match(result.stderr, /npm.*DESCRIBES relationship.*documentDescribes/i);
  } finally {
    removeCandidate(root);
  }
});
