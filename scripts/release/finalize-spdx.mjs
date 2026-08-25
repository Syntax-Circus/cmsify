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

for (const [kind, definition] of Object.entries(definitions)) {
  const fileName = `cmsify-${kind}.spdx.json`;
  const path = resolve(artifacts, "sbom", fileName);
  const document = JSON.parse(readFileSync(path, "utf8"));
  const retainedPackages = (document.packages ?? []).filter((candidate) => !definition.names.includes(candidate.name));
  const subjects = definition.names.map((name, index) => ({
    SPDXID: `SPDXRef-${kind}-${index + 1}`,
    name,
    versionInfo: version,
    licenseConcluded: definition.license,
    licenseDeclared: definition.license,
    downloadLocation: "NOASSERTION",
    filesAnalyzed: false,
    externalRefs: [{
      referenceCategory: "PACKAGE-MANAGER",
      referenceType: "purl",
      referenceLocator: definition.purl(name),
    }],
  }));
  document.spdxVersion = "SPDX-2.3";
  document.dataLicense = "CC0-1.0";
  document.SPDXID = "SPDXRef-DOCUMENT";
  document.name = definition.documentName;
  document.documentNamespace = `https://github.com/Syntax-Circus/cmsify/releases/download/v${version}/${fileName}`;
  document.creationInfo ??= {};
  document.creationInfo.comment = `Cmsify source ${sourceSha}`;
  document.packages = [...subjects, ...retainedPackages];
  document.documentDescribes = subjects.map((subject) => subject.SPDXID);
  writeFileSync(path, `${JSON.stringify(document, null, 2)}\n`);
}
