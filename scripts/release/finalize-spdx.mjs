import { readFileSync, writeFileSync } from "node:fs";
import { resolve } from "node:path";

const argv = process.argv.slice(2);
const option = (name) => argv.includes(name) ? argv[argv.indexOf(name) + 1] : undefined;
const artifacts = resolve(option("--artifacts") ?? "");
const version = option("--version");
const sourceSha = option("--source-sha");

if (!/^(?:0|[1-9]\d*)\.(?:0|[1-9]\d*)\.(?:0|[1-9]\d*)(?:-[0-9A-Za-z.-]+)?$/.test(version ?? "")) throw new Error("SPDX finalization requires a SemVer version.");
if (!/^[0-9a-f]{40}$/i.test(sourceSha ?? "")) throw new Error("SPDX finalization requires an immutable 40-character source SHA.");

function metadataDigest(kind) {
  const metadata = JSON.parse(readFileSync(resolve(artifacts, "oci", `cmsify-${kind}.metadata.json`), "utf8"));
  const digest = metadata["containerimage.descriptor"]?.digest;
  if (!/^sha256:[0-9a-f]{64}$/i.test(digest ?? "")) throw new Error(`Cmsify ${kind} metadata is missing its OCI descriptor digest.`);
  return digest;
}

const definitions = {
  nuget: {
    documentName: `Cmsify NuGet SDK ${version}`,
    license: "MIT",
    names: ["SyntaxCircus.Cmsify.Contracts", "SyntaxCircus.Cmsify.Client", "SyntaxCircus.Cmsify.Client.DistributedCaching"],
    purl: (name) => `pkg:nuget/${name}@${version}`,
  },
  npm: {
    documentName: `Cmsify npm SDK ${version}`,
    license: "MIT",
    names: ["@cmsify/client"],
    purl: () => `pkg:npm/%40cmsify/client@${version}`,
  },
  api: {
    documentName: `Cmsify API OCI ${version}`,
    license: "AGPL-3.0-or-later",
    names: ["syntaxcircus/cmsify-api"],
    purl: (name) => `pkg:oci/${name}@${metadataDigest("api")}`,
  },
  admin: {
    documentName: `Cmsify Admin OCI ${version}`,
    license: "AGPL-3.0-or-later",
    names: ["syntaxcircus/cmsify-admin"],
    purl: (name) => `pkg:oci/${name}@${metadataDigest("admin")}`,
  },
};

function relationshipReferences(document) {
  const owned = new Set([
    document.SPDXID,
    ...(document.packages ?? []).map((candidate) => candidate.SPDXID),
    ...(document.files ?? []).map((candidate) => candidate.SPDXID),
    ...(document.snippets ?? []).map((candidate) => candidate.SPDXID),
  ].filter(Boolean));
  const external = new Set((document.externalDocumentRefs ?? []).map((reference) => reference.externalDocumentId));
  return {
    owned,
    exists(reference) {
      return owned.has(reference) || [...external].some((documentId) => reference?.startsWith(`${documentId}:`));
    },
  };
}

function validateInventory(kind, document) {
  const packages = Array.isArray(document.packages) ? document.packages : [];
  if (packages.length === 0) throw new Error(`SPDX ${kind} requires meaningful existing inventory before subject finalization.`);
  const packageIds = packages.map((candidate) => candidate.SPDXID);
  if (packageIds.some((id) => !/^SPDXRef-[A-Za-z0-9.-]+$/.test(id ?? "")) || new Set(packageIds).size !== packageIds.length) {
    throw new Error(`SPDX ${kind} existing package inventory must have unique valid SPDXIDs.`);
  }
  const described = Array.isArray(document.documentDescribes) ? document.documentDescribes : [];
  if (described.length === 0 || described.some((id) => !packageIds.includes(id))) {
    throw new Error(`SPDX ${kind} requires documentDescribes target evidence from its existing package inventory.`);
  }
  const relationships = Array.isArray(document.relationships) ? document.relationships : [];
  if (relationships.length === 0) throw new Error(`SPDX ${kind} requires meaningful existing inventory relationships.`);
  const references = relationshipReferences(document);
  const describedIds = new Set(described);
  for (const relationship of relationships) {
    for (const reference of [relationship?.spdxElementId, relationship?.relatedSpdxElement]) {
      if (!references.exists(reference)) throw new Error(`SPDX ${kind} has dangling relationship reference ${reference}.`);
    }
    if (relationship?.spdxElementId === document.SPDXID && relationship?.relationshipType === "DESCRIBES" && !describedIds.has(relationship.relatedSpdxElement)) {
      throw new Error(`SPDX ${kind} DESCRIBES relationship contradicts documentDescribes.`);
    }
    if (relationship?.relatedSpdxElement === document.SPDXID && relationship?.relationshipType === "DESCRIBED_BY" && !describedIds.has(relationship.spdxElementId)) {
      throw new Error(`SPDX ${kind} DESCRIBED_BY relationship contradicts documentDescribes.`);
    }
  }
  return { packages, described, relationships };
}

function selectSubjects(kind, definition, packages, described, relationships) {
  const selected = [];
  const used = new Set();
  const describedPackages = described.map((id) => packages.find((candidate) => candidate.SPDXID === id)).filter(Boolean);
  for (const [index, name] of definition.names.entries()) {
    const exact = packages.filter((candidate) => candidate.name === name && !used.has(candidate));
    if (exact.length > 1) throw new Error(`SPDX ${kind} has ambiguous existing target evidence for ${name}.`);
    const subject = exact[0] ?? describedPackages[index] ?? describedPackages.find((candidate) => !used.has(candidate));
    if (!subject || used.has(subject)) throw new Error(`SPDX ${kind} is missing existing target evidence for ${name}.`);
    selected.push(subject);
    used.add(subject);
  }
  const retained = packages.filter((candidate) => !used.has(candidate));
  if (retained.length === 0) throw new Error(`SPDX ${kind} requires meaningful dependency inventory in addition to its release subject.`);
  const retainedIds = new Set(retained.map((candidate) => candidate.SPDXID));
  if (!relationships.some((relationship) => retainedIds.has(relationship.spdxElementId) || retainedIds.has(relationship.relatedSpdxElement))) {
    throw new Error(`SPDX ${kind} requires retained dependency relationship evidence.`);
  }
  return { selected, retained };
}

for (const [kind, definition] of Object.entries(definitions)) {
  const fileName = `cmsify-${kind}.spdx.json`;
  const path = resolve(artifacts, "sbom", fileName);
  const document = JSON.parse(readFileSync(path, "utf8"));
  const inventory = validateInventory(kind, document);
  const { selected: subjects } = selectSubjects(kind, definition, inventory.packages, inventory.described, inventory.relationships);
  for (const [index, subject] of subjects.entries()) {
    const nonPurlRefs = (subject.externalRefs ?? []).filter((reference) => !(reference.referenceCategory === "PACKAGE-MANAGER" && reference.referenceType === "purl"));
    Object.assign(subject, {
      name: definition.names[index],
      versionInfo: version,
      licenseConcluded: definition.license,
      licenseDeclared: definition.license,
      downloadLocation: "NOASSERTION",
      filesAnalyzed: false,
      externalRefs: [...nonPurlRefs, {
      referenceCategory: "PACKAGE-MANAGER",
      referenceType: "purl",
        referenceLocator: definition.purl(definition.names[index]),
      }],
    });
  }
  const oldDocumentId = document.SPDXID;
  const normalizedRelationships = inventory.relationships
    .map((relationship) => ({
      ...relationship,
      spdxElementId: relationship.spdxElementId === oldDocumentId ? "SPDXRef-DOCUMENT" : relationship.spdxElementId,
      relatedSpdxElement: relationship.relatedSpdxElement === oldDocumentId ? "SPDXRef-DOCUMENT" : relationship.relatedSpdxElement,
    }))
    .filter((relationship) => !(
      (relationship.spdxElementId === "SPDXRef-DOCUMENT" && relationship.relationshipType === "DESCRIBES") ||
      (relationship.relatedSpdxElement === "SPDXRef-DOCUMENT" && relationship.relationshipType === "DESCRIBED_BY")
    ));
  document.spdxVersion = "SPDX-2.3";
  document.dataLicense = "CC0-1.0";
  document.SPDXID = "SPDXRef-DOCUMENT";
  document.name = definition.documentName;
  document.documentNamespace = `https://github.com/Syntax-Circus/cmsify/releases/download/v${version}/${fileName}`;
  document.creationInfo ??= {};
  document.creationInfo.comment = `Cmsify source ${sourceSha}`;
  document.packages = inventory.packages;
  document.documentDescribes = subjects.map((subject) => subject.SPDXID);
  document.relationships = [
    ...normalizedRelationships,
    ...subjects.map((subject) => ({
      spdxElementId: "SPDXRef-DOCUMENT",
      relationshipType: "DESCRIBES",
      relatedSpdxElement: subject.SPDXID,
    })),
  ];
  writeFileSync(path, `${JSON.stringify(document, null, 2)}\n`);
}
