import { createHash } from "node:crypto";
import { execFileSync } from "node:child_process";
import { existsSync, mkdtempSync, readFileSync, readdirSync, rmSync, statSync } from "node:fs";
import { tmpdir } from "node:os";
import { resolve } from "node:path";
import { gunzipSync } from "node:zlib";

const argv = process.argv.slice(2);
const option = (name) => argv.includes(name) ? argv[argv.indexOf(name) + 1] : undefined;
const artifacts = resolve(option("--artifacts") ?? "");
const version = option("--version");
const sourceSha = option("--source-sha");
const errors = [];

const REPOSITORY_SOURCE = "https://github.com/Syntax-Circus/cmsify";
const NPM_REPOSITORY_SOURCE = "git+https://github.com/Syntax-Circus/cmsify.git";
const OCI_MANIFEST = "application/vnd.oci.image.manifest.v1+json";
const OCI_CONFIG = "application/vnd.oci.image.config.v1+json";
const OCI_LAYER = "application/vnd.oci.image.layer.v1.tar";
const OCI_LAYER_GZIP = "application/vnd.oci.image.layer.v1.tar+gzip";
const MIT_LICENSE_SHA256 = "119f46213616bf4e390565949d6a9e03e8b76c13d23c8dbfbaf2384a9ffa29a4";
const EXPECTED_NUGET = [
  "SyntaxCircus.Cmsify.Contracts",
  "SyntaxCircus.Cmsify.Client",
  "SyntaxCircus.Cmsify.Client.DistributedCaching",
];
const EXPECTED_NPM_MEMBERS = new Set([
  "package/LICENSE",
  "package/README.md",
  "package/package.json",
  "package/dist/index.cjs",
  "package/dist/index.cjs.map",
  "package/dist/index.d.cts",
  "package/dist/index.d.ts",
  "package/dist/index.js",
  "package/dist/index.js.map",
  "package/src/generated/client.ts",
  "package/src/generated/schema.ts",
]);

function expect(condition, message) {
  if (!condition) errors.push(message);
}

function allFiles(directory, prefix = "") {
  if (!existsSync(directory)) return [];
  return readdirSync(directory).flatMap((name) => {
    const path = resolve(directory, name);
    const key = `${prefix}${name}`;
    return statSync(path).isDirectory() ? allFiles(path, `${key}/`) : [key.replaceAll("\\", "/")];
  });
}

function command(name, args, context) {
  try {
    return execFileSync(name, args, { encoding: "utf8", maxBuffer: 16 * 1024 * 1024 });
  } catch (error) {
    errors.push(`${context}: ${name} failed to inspect the archive (${error.message}).`);
    return "";
  }
}

function parseJsonFile(path, context) {
  try {
    return JSON.parse(readFileSync(path, "utf8"));
  } catch {
    errors.push(`${context} must be valid JSON.`);
    return undefined;
  }
}

function parseJsonText(contents, context) {
  try {
    return JSON.parse(contents);
  } catch {
    errors.push(`${context} must be valid JSON.`);
    return undefined;
  }
}

function xmlElement(contents, name) {
  return contents.match(new RegExp(`<${name}(?:\\s[^>]*)?>([^<]*)<\\/${name}>`, "i"))?.[1]?.trim();
}

function xmlAttributes(contents, name) {
  const raw = contents.match(new RegExp(`<${name}\\s+([^>]*?)(?:\\s*\\/?)>`, "i"))?.[1] ?? "";
  return Object.fromEntries([...raw.matchAll(/([\w:-]+)="([^"]*)"/g)].map((match) => [match[1], match[2]]));
}

function readZipMember(path, member, context) {
  return command("unzip", ["-p", path, member], context);
}

function verifyNuget(expectedFiles) {
  const directory = resolve(artifacts, "nuget");
  const actual = existsSync(directory) ? readdirSync(directory).filter((name) => statSync(resolve(directory, name)).isFile()) : [];
  const expectedNames = EXPECTED_NUGET.map((id) => `${id}.${version}.nupkg`);
  expect(actual.length === expectedNames.length && actual.every((name) => expectedNames.includes(name)), `NuGet candidate must contain exactly ${expectedNames.join(", ")} and no symbols or extra files.`);

  for (const id of EXPECTED_NUGET) {
    const name = `${id}.${version}.nupkg`;
    const path = resolve(directory, name);
    expectedFiles.add(`nuget/${name}`);
    if (!existsSync(path)) {
      errors.push(`NuGet candidate is missing ${name}.`);
      continue;
    }
    const context = `NuGet ${id}`;
    const members = command("unzip", ["-Z1", path], context).split(/\r?\n/).filter(Boolean).map((member) => member.replaceAll("\\", "/"));
    const nuspecMembers = members.filter((member) => member.endsWith(".nuspec"));
    expect(nuspecMembers.length === 1, `${context} must contain exactly one nuspec.`);
    const nuspec = nuspecMembers.length === 1 ? readZipMember(path, nuspecMembers[0], context) : "";
    expect(xmlElement(nuspec, "id") === id, `${context} nuspec must contain exact package ID ${id}.`);
    expect(xmlElement(nuspec, "version") === version, `${context} nuspec must contain release version ${version}.`);
    const expression = [...nuspec.matchAll(/<license\s+type="expression">([^<]*)<\/license>/gi)].map((match) => match[1].trim());
    expect(expression.length === 1 && expression[0] === "MIT", `${context} nuspec must contain the exact MIT license expression.`);
    expect(members.includes("LICENSE-MIT.txt"), `${context} must include the exact license file LICENSE-MIT.txt.`);
    if (members.includes("LICENSE-MIT.txt")) {
      const payload = Buffer.from(readZipMember(path, "LICENSE-MIT.txt", context));
      expect(createHash("sha256").update(payload).digest("hex") === MIT_LICENSE_SHA256, `${context} must include the canonical MIT license payload.`);
    }
    const assemblies = members.filter((member) => /^lib\/net10\.0\/[^/]+\.dll$/i.test(member));
    expect(assemblies.length > 0, `${context} must contain a net10.0 DLL payload.`);
    expect(!members.some((member) => /^lib\/(?!net10\.0\/)/i.test(member)), `${context} must not contain a framework payload other than net10.0.`);
    const repository = xmlAttributes(nuspec, "repository");
    expect(repository.type === "git" && repository.url === REPOSITORY_SOURCE, `${context} nuspec must record the exact Cmsify git source repository.`);
    expect(repository.commit === sourceSha, `${context} nuspec must record source commit ${sourceSha}.`);
  }
}

function npmMember(metadata, field, expected, members, explicitValue) {
  const value = explicitValue ?? field.split(".").reduce((current, key) => current?.[key], metadata);
  expect(value === expected, `Packed npm ${field} must be exactly ${expected}.`);
  const member = typeof value === "string" && value.startsWith("./") ? `package/${value.slice(2)}` : undefined;
  if (member) expect(members.has(member), `Packed npm ${field} must resolve to exact archive member ${member}.`);
}

function verifyNpm(expectedFiles) {
  const directory = resolve(artifacts, "npm");
  const expectedName = `cmsify-client-${version}.tgz`;
  const actual = existsSync(directory) ? readdirSync(directory).filter((name) => statSync(resolve(directory, name)).isFile()) : [];
  expect(actual.length === 1 && actual[0] === expectedName, `npm candidate must contain exactly ${expectedName}.`);
  expectedFiles.add(`npm/${expectedName}`);
  const path = resolve(directory, expectedName);
  if (!existsSync(path)) return;

  const listing = command("tar", ["-tzf", path], "Packed npm candidate").split(/\r?\n/).filter(Boolean).map((name) => name.replace(/^\.\//, "").replaceAll("\\", "/"));
  const members = new Set(listing.filter((name) => !name.endsWith("/")));
  expect(members.has("package/package.json"), "Packed npm candidate must include exact member package/package.json.");
  expect(members.has("package/LICENSE"), "Packed npm candidate must include exact member package/LICENSE.");
  for (const member of EXPECTED_NPM_MEMBERS) {
    expect(members.has(member), `Packed npm candidate is missing archive member ${member}.`);
  }
  for (const member of members) {
    if (!EXPECTED_NPM_MEMBERS.has(member)) errors.push(`Packed npm candidate contains unsupported archive member ${member}; unexpected archive member ${member}.`);
  }

  const metadata = parseJsonText(command("tar", ["-xOf", path, "package/package.json"], "Packed npm candidate"), "Packed npm package/package.json");
  if (!metadata) return;
  expect(metadata.name === "@syntaxcircus/cmsify-client", "Packed npm name must be exactly @syntaxcircus/cmsify-client.");
  expect(metadata.version === version, `Packed npm version must be exactly ${version}.`);
  expect(metadata.license === "MIT", "Packed npm license must be exactly MIT.");
  expect(metadata.engines?.node === ">=20" || metadata.engines?.node === ">=20.0.0", "Packed npm Node floor must be exactly >=20.");
  expect(metadata.private === undefined || metadata.private === false, "Packed npm must be public: private must be absent or boolean false.");
  expect(metadata.repository?.type === "git", "Packed npm repository type must be exactly git.");
  expect(metadata.repository?.url === NPM_REPOSITORY_SOURCE, `Packed npm repository URL must be exactly ${NPM_REPOSITORY_SOURCE}.`);
  expect(metadata.repository?.directory === "sdk/typescript", "Packed npm repository directory must be exactly sdk/typescript.");
  expect(metadata.gitHead === sourceSha, "Packed npm gitHead must equal the immutable source SHA.");
  expect(metadata.type === "module", "Packed npm type must be exactly module.");
  expect(Array.isArray(metadata.files) && metadata.files.length === 2 && metadata.files[0] === "dist" && metadata.files[1] === "src/generated", "Packed npm files must contain exactly dist and src/generated.");
  const exportKeys = metadata.exports && typeof metadata.exports === "object" ? Object.keys(metadata.exports) : [];
  const rootExportKeys = metadata.exports?.["."] && typeof metadata.exports["."] === "object" ? Object.keys(metadata.exports["."]) : [];
  expect(exportKeys.length === 1 && exportKeys[0] === "." && rootExportKeys.length === 3 && ["types", "import", "require"].every((key) => rootExportKeys.includes(key)), "Packed npm exports surface must contain only the public entrypoint with types, import, and require targets.");
  npmMember(metadata, "main", "./dist/index.cjs", members);
  npmMember(metadata, "module", "./dist/index.js", members);
  npmMember(metadata, "types", "./dist/index.d.ts", members);
  npmMember(metadata, "exports.types", "./dist/index.d.ts", members, metadata.exports?.["."]?.types);
  npmMember(metadata, "exports.import", "./dist/index.js", members, metadata.exports?.["."]?.import);
  npmMember(metadata, "exports.require", "./dist/index.cjs", members, metadata.exports?.["."]?.require);
  if (members.has("package/LICENSE")) {
    const payload = Buffer.from(command("tar", ["-xOf", path, "package/LICENSE"], "Packed npm candidate"));
    expect(createHash("sha256").update(payload).digest("hex") === MIT_LICENSE_SHA256, "Packed npm LICENSE payload must be the canonical MIT License.");
  }
}

function readBlob(layout, descriptor, context) {
  if (!/^sha256:[0-9a-f]{64}$/i.test(descriptor?.digest ?? "")) {
    errors.push(`${context} digest must be a sha256 descriptor digest.`);
    return undefined;
  }
  const path = resolve(layout, "blobs", "sha256", descriptor.digest.slice(7));
  if (!existsSync(path)) {
    errors.push(`${context} digest must resolve to an existing blob.`);
    return undefined;
  }
  const contents = readFileSync(path);
  expect(`sha256:${createHash("sha256").update(contents).digest("hex")}` === descriptor.digest, `${context} digest must equal the blob SHA-256.`);
  expect(contents.length === descriptor.size, `${context} declared size must equal the blob size.`);
  return contents;
}

function verifyOci(expectedFiles, releaseManifest) {
  const result = {};
  for (const kind of ["api", "admin"]) {
    const label = kind === "api" ? "API" : "Admin";
    const expectedRepository = `docker.io/syntaxcircus/cmsify-${kind}`;
    const expectedRef = `${expectedRepository}:${version}`;
    const tarName = `cmsify-${kind}.oci.tar`;
    const metadataName = `cmsify-${kind}.metadata.json`;
    expectedFiles.add(`oci/${tarName}`);
    expectedFiles.add(`oci/${metadataName}`);
    const archive = resolve(artifacts, "oci", tarName);
    const metadataPath = resolve(artifacts, "oci", metadataName);
    if (!existsSync(archive) || !existsSync(metadataPath)) {
      errors.push(`OCI ${label} candidate must contain ${tarName} and ${metadataName}.`);
      continue;
    }
    const layout = mkdtempSync(resolve(tmpdir(), `cmsify-${kind}-verify-`));
    try {
      command("tar", ["-xf", archive, "-C", layout], `OCI ${label}`);
      expect(existsSync(resolve(layout, "oci-layout")) && existsSync(resolve(layout, "index.json")), `OCI ${label} archive must be a complete OCI layout.`);
      const index = parseJsonFile(resolve(layout, "index.json"), `OCI ${label} index`);
      if (!index) continue;
      const descriptors = (index.manifests ?? []).filter((candidate) => candidate.annotations?.["org.opencontainers.image.ref.name"] === version && candidate.annotations?.["io.containerd.image.name"] === expectedRef);
      expect(descriptors.length === 1, `OCI ${label} index must select exactly one descriptor by tag ref.name and exact containerd image name ${expectedRef}.`);
      if (descriptors.length !== 1) continue;
      const descriptor = descriptors[0];
      expect(descriptor.mediaType === OCI_MANIFEST, `OCI ${label} descriptor media type must be ${OCI_MANIFEST}.`);
      expect(descriptor.platform?.os === "linux" && descriptor.platform?.architecture === "amd64", `OCI ${label} descriptor platform must be linux/amd64.`);
      const manifestContents = readBlob(layout, descriptor, `OCI ${label} descriptor`);
      if (!manifestContents) continue;
      const manifest = parseJsonText(manifestContents.toString("utf8"), `OCI ${label} manifest`);
      if (!manifest) continue;
      expect(manifest.mediaType === OCI_MANIFEST, `OCI ${label} manifest media type must be ${OCI_MANIFEST}.`);
      expect(manifest.config?.mediaType === OCI_CONFIG, `OCI ${label} config media type must be ${OCI_CONFIG}.`);
      const configContents = readBlob(layout, manifest.config, `OCI ${label} config`);
      if (!configContents) continue;
      const config = parseJsonText(configContents.toString("utf8"), `OCI ${label} config blob`);
      if (!config) continue;
      expect(config.os === "linux" && config.architecture === "amd64", `OCI ${label} configuration platform must be linux/amd64.`);
      const layers = Array.isArray(manifest.layers) ? manifest.layers : [];
      expect(layers.length > 0, `OCI ${label} manifest must contain at least one filesystem layer.`);
      expect(config.rootfs?.type === "layers", `OCI ${label} rootfs type must be exactly layers.`);
      const diffIds = Array.isArray(config.rootfs?.diff_ids) ? config.rootfs.diff_ids : [];
      expect(diffIds.length === layers.length, `OCI ${label} rootfs diff_ids count must equal manifest layers count and preserve order.`);
      for (let index = 0; index < layers.length; index += 1) {
        const layer = layers[index];
        const context = `OCI ${label} layer ${index + 1}`;
        expect(layer?.mediaType === OCI_LAYER || layer?.mediaType === OCI_LAYER_GZIP, `${context} media type must be ${OCI_LAYER} or ${OCI_LAYER_GZIP}.`);
        const compressed = readBlob(layout, layer, context);
        if (!compressed || (layer.mediaType !== OCI_LAYER && layer.mediaType !== OCI_LAYER_GZIP)) continue;
        let uncompressed;
        try {
          uncompressed = layer.mediaType === OCI_LAYER_GZIP ? gunzipSync(compressed) : compressed;
        } catch {
          errors.push(`${context} gzip payload must decompress successfully.`);
          continue;
        }
        const actualDiffId = `sha256:${createHash("sha256").update(uncompressed).digest("hex")}`;
        expect(diffIds[index] === actualDiffId, `${context} rootfs diff_id must equal the uncompressed layer SHA-256 in manifest order.`);
      }
      if (Array.isArray(config.history)) {
        expect(config.history.filter((entry) => entry?.empty_layer !== true).length === layers.length, `OCI ${label} history non-empty layer count must equal manifest layers count.`);
      }
      const labels = config.config?.Labels ?? {};
      expect(labels["org.opencontainers.image.title"] === `Cmsify ${label}`, `OCI ${label} title label must be exactly Cmsify ${label}.`);
      expect(labels["org.opencontainers.image.source"] === REPOSITORY_SOURCE, `OCI ${label} source label must be exactly ${REPOSITORY_SOURCE}.`);
      expect(labels["org.opencontainers.image.revision"] === sourceSha, `OCI ${label} revision label must equal source SHA ${sourceSha}.`);
      expect(labels["org.opencontainers.image.version"] === version, `OCI ${label} version label must equal ${version}.`);
      expect(labels["org.opencontainers.image.licenses"] === "AGPL-3.0-or-later", `OCI ${label} license label must be AGPL-3.0-or-later.`);

      const metadata = parseJsonFile(metadataPath, `OCI ${label} metadata`)?.["containerimage.descriptor"];
      expect(metadata?.digest === descriptor.digest && metadata?.size === descriptor.size && metadata?.mediaType === descriptor.mediaType && metadata?.annotations?.["org.opencontainers.image.ref.name"] === version && metadata?.annotations?.["io.containerd.image.name"] === expectedRef, `OCI ${label} Buildx metadata descriptor must exactly match the selected layout descriptor and full identity.`);
      const certified = releaseManifest?.oci?.[kind];
      expect(certified?.repository === expectedRepository, `Release manifest OCI ${label} repository must be ${expectedRepository}.`);
      expect(certified?.ref === expectedRef, `Release manifest OCI ${label} ref must be ${expectedRef}.`);
      expect(certified?.tag === version, `Release manifest OCI ${label} tag must be ${version}.`);
      expect(certified?.imageName === expectedRef, `Release manifest OCI ${label} containerd image name must be ${expectedRef}.`);
      expect(certified?.digest === descriptor.digest, `Release manifest OCI ${label} digest must bind the selected descriptor digest.`);
      expect(certified?.size === descriptor.size, `Release manifest OCI ${label} size must bind the selected descriptor size.`);
      expect(certified?.mediaType === descriptor.mediaType, `Release manifest OCI ${label} media type must bind the selected descriptor media type.`);
      expect(certified?.platform?.os === "linux" && certified?.platform?.architecture === "amd64", `Release manifest OCI ${label} platform must bind linux/amd64.`);
      result[kind] = { digest: descriptor.digest };
    } finally {
      rmSync(layout, { recursive: true, force: true });
    }
  }
  return result;
}

function verifySpdx(expectedFiles, oci) {
  const definitions = {
    nuget: { label: "NuGet", name: `Cmsify NuGet SDK ${version}`, packages: EXPECTED_NUGET, license: "MIT", purl: (name) => `pkg:nuget/${name}@${version}` },
    npm: { label: "npm", name: `Cmsify npm SDK ${version}`, packages: ["@syntaxcircus/cmsify-client"], license: "MIT", purl: () => `pkg:npm/%40syntaxcircus/cmsify-client@${version}` },
    api: { label: "API", name: `Cmsify API OCI ${version}`, packages: ["syntaxcircus/cmsify-api"], license: "AGPL-3.0-or-later", purl: (name) => `pkg:oci/${name}@${oci.api?.digest}` },
    admin: { label: "Admin", name: `Cmsify Admin OCI ${version}`, packages: ["syntaxcircus/cmsify-admin"], license: "AGPL-3.0-or-later", purl: (name) => `pkg:oci/${name}@${oci.admin?.digest}` },
  };
  for (const [kind, definition] of Object.entries(definitions)) {
    const fileName = `cmsify-${kind}.spdx.json`;
    expectedFiles.add(`sbom/${fileName}`);
    const path = resolve(artifacts, "sbom", fileName);
    if (!existsSync(path)) {
      errors.push(`SPDX ${definition.label} candidate is missing ${fileName}.`);
      continue;
    }
    const document = parseJsonFile(path, `SPDX ${definition.label}`);
    if (!document) continue;
    expect(document.spdxVersion === "SPDX-2.3", `SPDX ${definition.label} version must be SPDX-2.3.`);
    expect(document.dataLicense === "CC0-1.0" && document.SPDXID === "SPDXRef-DOCUMENT", `SPDX ${definition.label} document identity must use CC0-1.0 and SPDXRef-DOCUMENT.`);
    expect(document.name === definition.name, `SPDX ${definition.label} document name must be exactly ${definition.name}.`);
    const namespace = `https://github.com/Syntax-Circus/cmsify/releases/download/v${version}/${fileName}`;
    expect(document.documentNamespace === namespace, `SPDX ${definition.label} document namespace must be exactly ${namespace}.`);
    expect(document.creationInfo?.comment === `Cmsify source ${sourceSha}`, `SPDX ${definition.label} creation identity must contain exact source SHA ${sourceSha}.`);
    const packages = Array.isArray(document.packages) ? document.packages : [];
    const subjects = definition.packages.map((name) => {
      const matches = packages.filter((candidate) => candidate.name === name);
      expect(matches.length === 1, `SPDX ${definition.label} must contain exactly one intended package ${name}.`);
      return matches[0];
    });
    for (let index = 0; index < definition.packages.length; index += 1) {
      const packageName = definition.packages[index];
      const subject = subjects[index];
      expect(Boolean(subject), `SPDX ${definition.label} must contain intended package ${packageName}.`);
      if (!subject) continue;
      expect(subject.versionInfo === version, `SPDX ${definition.label} package version for ${packageName} must be ${version}.`);
      expect(subject.licenseDeclared === definition.license, `SPDX ${definition.label} package license for ${packageName} must be ${definition.license}.`);
      expect(subject.licenseConcluded === definition.license, `SPDX ${definition.label} package concluded license for ${packageName} must be ${definition.license}.`);
      expect(document.documentDescribes?.includes(subject.SPDXID), `SPDX ${definition.label} documentDescribes must contain subject ${subject.SPDXID}.`);
      const expectedPurl = definition.purl(packageName);
      const purlMatches = (subject.externalRefs ?? []).filter((entry) => entry.referenceCategory === "PACKAGE-MANAGER" && entry.referenceType === "purl");
      const purlMessage = kind === "api" || kind === "admin"
        ? `SPDX ${definition.label} package purl must bind certified OCI digest ${oci[kind]?.digest}.`
        : `SPDX ${definition.label} package purl for ${packageName} must be exactly ${expectedPurl}.`;
      expect(purlMatches.length === 1 && purlMatches[0].referenceLocator === expectedPurl, purlMessage);
    }
    const described = new Set(document.documentDescribes ?? []);
    const intendedIds = new Set(subjects.filter(Boolean).map((subject) => subject.SPDXID));
    expect(described.size === intendedIds.size && [...described].every((id) => intendedIds.has(id)), `SPDX ${definition.label} documentDescribes must identify only its intended release subjects.`);
    const allElements = [document, ...packages, ...(document.files ?? []), ...(document.snippets ?? [])];
    const ids = allElements.map((element) => element?.SPDXID).filter(Boolean);
    expect(new Set(ids).size === ids.length, `SPDX ${definition.label} SPDXIDs must be unique.`);
    const ownedIds = new Set(ids);
    const externalDocumentIds = new Set((document.externalDocumentRefs ?? []).map((reference) => reference.externalDocumentId));
    const ownsReference = (reference) => ownedIds.has(reference) || [...externalDocumentIds].some((documentId) => reference?.startsWith(`${documentId}:`));
    const relationships = Array.isArray(document.relationships) ? document.relationships : [];
    expect(relationships.length > 0, `SPDX ${definition.label} must retain meaningful inventory relationships.`);
    for (const relationship of relationships) {
      for (const reference of [relationship?.spdxElementId, relationship?.relatedSpdxElement]) {
        if (!ownsReference(reference)) errors.push(`SPDX ${definition.label} relationship has dangling reference ${reference}.`);
      }
      if (relationship?.spdxElementId === document.SPDXID && relationship?.relationshipType === "DESCRIBES" && !described.has(relationship.relatedSpdxElement)) {
        errors.push(`SPDX ${definition.label} DESCRIBES relationship must agree with documentDescribes.`);
      }
      if (relationship?.relatedSpdxElement === document.SPDXID && relationship?.relationshipType === "DESCRIBED_BY" && !described.has(relationship.spdxElementId)) {
        errors.push(`SPDX ${definition.label} DESCRIBED_BY relationship must agree with documentDescribes.`);
      }
    }
    const dependencyIds = new Set(packages.filter((candidate) => !intendedIds.has(candidate.SPDXID)).map((candidate) => candidate.SPDXID));
    const inventoryRelated = relationships.some((relationship) => dependencyIds.has(relationship.spdxElementId) || dependencyIds.has(relationship.relatedSpdxElement));
    expect(dependencyIds.size > 0 && inventoryRelated, `SPDX ${definition.label} must retain nonempty meaningful dependency inventory.`);
  }
}

function verifyChecksums(expectedFiles) {
  const path = resolve(artifacts, "SHA256SUMS");
  if (!existsSync(path)) {
    errors.push("Candidate must contain SHA256SUMS.");
    return new Map();
  }
  const checksums = new Map();
  for (const line of readFileSync(path, "utf8").split(/\r?\n/).filter(Boolean)) {
    const match = line.match(/^([0-9a-f]{64})  ([^\\].*)$/i);
    if (!match || match[2].startsWith("/") || match[2].startsWith("artifacts/") || match[2].includes("..")) {
      errors.push(`SHA256SUMS entry must use a canonical candidate-root-relative path: ${line}.`);
      continue;
    }
    if (checksums.has(match[2])) errors.push(`SHA256SUMS contains duplicate entry ${match[2]}.`);
    checksums.set(match[2], match[1].toLowerCase());
  }
  for (const expected of expectedFiles) if (!checksums.has(expected)) errors.push(`SHA256SUMS omits certified file ${expected}.`);
  for (const actual of checksums.keys()) if (!expectedFiles.has(actual)) errors.push(`SHA256SUMS contains extra entry ${actual}.`);
  for (const expected of expectedFiles) {
    const candidate = resolve(artifacts, expected);
    if (!existsSync(candidate) || !checksums.has(expected)) continue;
    const actual = createHash("sha256").update(readFileSync(candidate)).digest("hex");
    expect(checksums.get(expected) === actual, `Checksum mismatch for ${expected}.`);
  }
  const actualFiles = new Set(allFiles(artifacts).filter((name) => name !== "SHA256SUMS"));
  for (const actual of actualFiles) if (!expectedFiles.has(actual)) errors.push(`Candidate contains unchecked extra file ${actual}.`);
  for (const expected of expectedFiles) if (!actualFiles.has(expected)) errors.push(`Candidate is missing certified file ${expected}.`);
  return checksums;
}

function verifyCertificationSubjectCoverage(expectedFiles, checksums) {
  const identitySubjects = [
    "release-manifest.json",
    "oci/cmsify-api.oci.tar",
    "oci/cmsify-admin.oci.tar",
    "sbom/cmsify-nuget.spdx.json",
    "sbom/cmsify-npm.spdx.json",
    "sbom/cmsify-api.spdx.json",
    "sbom/cmsify-admin.spdx.json",
  ];
  expect(identitySubjects.every((subject) => expectedFiles.has(subject) && /^[0-9a-f]{64}$/.test(checksums.get(subject) ?? "")), "Certification subject checksums must bind the release manifest, exact OCI archives, and all four SPDX identities.");
  expect(checksums.size === expectedFiles.size && [...checksums.keys()].every((subject) => expectedFiles.has(subject)), "Certification subject checksum set must equal the complete verified candidate file set.");
}

expect(/^(?:0|[1-9]\d*)\.(?:0|[1-9]\d*)\.(?:0|[1-9]\d*)(?:-[0-9A-Za-z.-]+)?$/.test(version ?? ""), "Release artifact verification requires a SemVer version.");
expect(/^[0-9a-f]{40}$/i.test(sourceSha ?? ""), "Release artifact verification requires the immutable 40-character source SHA.");

const expectedFiles = new Set();
verifyNuget(expectedFiles);
verifyNpm(expectedFiles);
expectedFiles.add("release-manifest.json");
const releaseManifestPath = resolve(artifacts, "release-manifest.json");
const releaseManifest = existsSync(releaseManifestPath) ? parseJsonFile(releaseManifestPath, "Release manifest") : undefined;
if (!existsSync(releaseManifestPath)) errors.push("Candidate must contain release-manifest.json.");
if (releaseManifest) {
  expect(releaseManifest.version === version, `Release manifest version must be ${version}.`);
  expect(releaseManifest.sourceSha === sourceSha, `Release manifest source SHA must be ${sourceSha}.`);
  const manifestKinds = Object.keys(releaseManifest.oci ?? {}).sort();
  expect(manifestKinds.length === 2 && manifestKinds[0] === "admin" && manifestKinds[1] === "api", "Release manifest must identify exactly the API and Admin OCI subjects.");
}
const oci = verifyOci(expectedFiles, releaseManifest);
expect(!oci.api?.digest || !oci.admin?.digest || oci.api.digest !== oci.admin.digest, "Release manifest API and Admin OCI subjects must have distinct certified descriptor digests.");
verifySpdx(expectedFiles, oci);
const checksums = verifyChecksums(expectedFiles);
verifyCertificationSubjectCoverage(expectedFiles, checksums);

if (errors.length > 0) {
  process.stderr.write(`${errors.join("\n")}\n`);
  process.exitCode = 1;
} else {
  process.stdout.write(`Release artifacts verified for ${version} from ${sourceSha}.\n`);
}
