import { createHash } from "node:crypto";
import { execFileSync } from "node:child_process";
import { existsSync, mkdtempSync, readFileSync, readdirSync, rmSync, statSync } from "node:fs";
import { tmpdir } from "node:os";
import { resolve } from "node:path";

const argv = process.argv.slice(2);
const option = (name) => argv.includes(name) ? argv[argv.indexOf(name) + 1] : undefined;
const artifacts = resolve(option("--artifacts") ?? "");
const version = option("--version");
const sourceSha = option("--source-sha");
const errors = [];

const REPOSITORY_SOURCE = "https://github.com/Syntax-Circus/cmsify";
const NPM_REPOSITORY_SOURCE = "git+https://github.com/SyntaxCircus/cmsify.git";
const OCI_MANIFEST = "application/vnd.oci.image.manifest.v1+json";
const OCI_CONFIG = "application/vnd.oci.image.config.v1+json";
const MIT_LICENSE_SHA256 = "119f46213616bf4e390565949d6a9e03e8b76c13d23c8dbfbaf2384a9ffa29a4";
const EXPECTED_NUGET = [
  "SyntaxCircus.Cmsify.Contracts",
  "SyntaxCircus.Cmsify.Client",
  "SyntaxCircus.Cmsify.Client.DistributedCaching",
];

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
  const invalidMembers = [...members].filter((name) => !(
    name === "package/package.json" || name === "package/LICENSE" || /^package\/README(?:\.[^/]+)?$/i.test(name) ||
    name.startsWith("package/dist/") || name.startsWith("package/src/generated/")
  ));
  for (const member of invalidMembers) errors.push(`Packed npm candidate contains unsupported archive member ${member}.`);

  const metadata = parseJsonText(command("tar", ["-xOf", path, "package/package.json"], "Packed npm candidate"), "Packed npm package/package.json");
  if (!metadata) return;
  expect(metadata.name === "@cmsify/client", "Packed npm name must be exactly @cmsify/client.");
  expect(metadata.version === version, `Packed npm version must be exactly ${version}.`);
  expect(metadata.license === "MIT", "Packed npm license must be exactly MIT.");
  expect(metadata.engines?.node === ">=20" || metadata.engines?.node === ">=20.0.0", "Packed npm Node floor must be exactly >=20.");
  expect(metadata.private === undefined || metadata.private === false, "Packed npm must be public: private must be absent or boolean false.");
  expect(metadata.repository?.type === "git", "Packed npm repository type must be exactly git.");
  expect(metadata.repository?.url === NPM_REPOSITORY_SOURCE, `Packed npm repository URL must be exactly ${NPM_REPOSITORY_SOURCE}.`);
  expect(metadata.repository?.directory === "sdk/typescript", "Packed npm repository directory must be exactly sdk/typescript.");
  expect(metadata.gitHead === sourceSha, "Packed npm gitHead must equal the immutable source SHA.");
  expect(metadata.type === "module", "Packed npm type must be exactly module.");
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
    const expectedRepository = `syntaxcircus/cmsify-${kind}`;
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
      const descriptors = (index.manifests ?? []).filter((candidate) => candidate.annotations?.["org.opencontainers.image.ref.name"] === expectedRef);
      expect(descriptors.length === 1, `OCI ${label} index must select exactly one descriptor by exact ref ${expectedRef}.`);
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
      const labels = config.config?.Labels ?? {};
      expect(labels["org.opencontainers.image.title"] === `Cmsify ${label}`, `OCI ${label} title label must be exactly Cmsify ${label}.`);
      expect(labels["org.opencontainers.image.ref.name"] === expectedRef, `OCI ${label} ref.name label must be exactly ${expectedRef}.`);
      expect(labels["org.opencontainers.image.source"] === REPOSITORY_SOURCE, `OCI ${label} source label must be exactly ${REPOSITORY_SOURCE}.`);
      expect(labels["org.opencontainers.image.revision"] === sourceSha, `OCI ${label} revision label must equal source SHA ${sourceSha}.`);
      expect(labels["org.opencontainers.image.version"] === version, `OCI ${label} version label must equal ${version}.`);
      expect(labels["org.opencontainers.image.licenses"] === "AGPL-3.0-or-later", `OCI ${label} license label must be AGPL-3.0-or-later.`);

      const metadata = parseJsonFile(metadataPath, `OCI ${label} metadata`)?.["containerimage.descriptor"];
      expect(metadata?.digest === descriptor.digest && metadata?.size === descriptor.size && metadata?.mediaType === descriptor.mediaType, `OCI ${label} metadata descriptor must exactly match the selected layout descriptor.`);
      const certified = releaseManifest?.oci?.[kind];
      expect(certified?.repository === expectedRepository, `Release manifest OCI ${label} repository must be ${expectedRepository}.`);
      expect(certified?.ref === expectedRef, `Release manifest OCI ${label} ref must be ${expectedRef}.`);
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
    nuget: { label: "NuGet", name: `Cmsify NuGet SDK ${version}`, packages: EXPECTED_NUGET, license: "MIT" },
    npm: { label: "npm", name: `Cmsify npm SDK ${version}`, packages: ["@cmsify/client"], license: "MIT" },
    api: { label: "API", name: `Cmsify API OCI ${version}`, packages: ["syntaxcircus/cmsify-api"], license: "AGPL-3.0-or-later" },
    admin: { label: "Admin", name: `Cmsify Admin OCI ${version}`, packages: ["syntaxcircus/cmsify-admin"], license: "AGPL-3.0-or-later" },
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
    expect(document.name === definition.name, `SPDX ${definition.label} document name must be exactly ${definition.name}.`);
    const namespace = `https://github.com/Syntax-Circus/cmsify/releases/download/v${version}/${fileName}`;
    expect(document.documentNamespace === namespace, `SPDX ${definition.label} document namespace must be exactly ${namespace}.`);
    expect(document.creationInfo?.comment?.includes(sourceSha), `SPDX ${definition.label} creation identity must contain source SHA ${sourceSha}.`);
    const subjects = definition.packages.map((name) => (document.packages ?? []).find((candidate) => candidate.name === name));
    for (let index = 0; index < definition.packages.length; index += 1) {
      const packageName = definition.packages[index];
      const subject = subjects[index];
      expect(Boolean(subject), `SPDX ${definition.label} must contain intended package ${packageName}.`);
      if (!subject) continue;
      expect(subject.versionInfo === version, `SPDX ${definition.label} package version for ${packageName} must be ${version}.`);
      expect(subject.licenseDeclared === definition.license, `SPDX ${definition.label} package license for ${packageName} must be ${definition.license}.`);
      expect(document.documentDescribes?.includes(subject.SPDXID), `SPDX ${definition.label} documentDescribes must contain subject ${subject.SPDXID}.`);
      if (kind === "api" || kind === "admin") {
        const expectedPurl = `pkg:oci/${packageName}@${oci[kind]?.digest}`;
        expect(subject.externalRefs?.some((entry) => entry.referenceCategory === "PACKAGE-MANAGER" && entry.referenceType === "purl" && entry.referenceLocator === expectedPurl), `SPDX ${definition.label} package purl must bind certified OCI digest ${oci[kind]?.digest}.`);
      }
    }
    const described = new Set(document.documentDescribes ?? []);
    const intendedIds = new Set(subjects.filter(Boolean).map((subject) => subject.SPDXID));
    expect(described.size === intendedIds.size && [...described].every((id) => intendedIds.has(id)), `SPDX ${definition.label} documentDescribes must identify only its intended release subjects.`);
  }
}

function verifyChecksums(expectedFiles) {
  const path = resolve(artifacts, "SHA256SUMS");
  if (!existsSync(path)) {
    errors.push("Candidate must contain SHA256SUMS.");
    return;
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
}
const oci = verifyOci(expectedFiles, releaseManifest);
verifySpdx(expectedFiles, oci);
verifyChecksums(expectedFiles);

if (errors.length > 0) {
  process.stderr.write(`${errors.join("\n")}\n`);
  process.exitCode = 1;
} else {
  process.stdout.write(`Release artifacts verified for ${version} from ${sourceSha}.\n`);
}
