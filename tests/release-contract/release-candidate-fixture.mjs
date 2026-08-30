import { createHash } from "node:crypto";
import { execFileSync } from "node:child_process";
import {
  cpSync,
  mkdirSync,
  mkdtempSync,
  readFileSync,
  readdirSync,
  rmSync,
  statSync,
  writeFileSync,
} from "node:fs";
import { tmpdir } from "node:os";
import { basename, dirname, relative, resolve } from "node:path";
import { gzipSync } from "node:zlib";

export const VERSION = "1.2.3";
export const SOURCE_SHA = "0123456789abcdef0123456789abcdef01234567";

const OCI_MANIFEST = "application/vnd.oci.image.manifest.v1+json";
const OCI_CONFIG = "application/vnd.oci.image.config.v1+json";
const OCI_LAYER_GZIP = "application/vnd.oci.image.layer.v1.tar+gzip";
const MIT_LICENSE = `MIT License

Copyright (c) Syntax Circus LLC

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.
`;

function sha256(contents) {
  return `sha256:${createHash("sha256").update(contents).digest("hex")}`;
}

function json(value) {
  return Buffer.from(`${JSON.stringify(value)}\n`);
}

function uint16(value) {
  const result = Buffer.alloc(2);
  result.writeUInt16LE(value);
  return result;
}

function uint32(value) {
  const result = Buffer.alloc(4);
  result.writeUInt32LE(value >>> 0);
  return result;
}

const crcTable = Array.from({ length: 256 }, (_, initial) => {
  let value = initial;
  for (let index = 0; index < 8; index += 1) value = (value & 1) === 1 ? 0xedb88320 ^ (value >>> 1) : value >>> 1;
  return value >>> 0;
});

function crc32(contents) {
  let value = 0xffffffff;
  for (const byte of contents) value = crcTable[(value ^ byte) & 0xff] ^ (value >>> 8);
  return (value ^ 0xffffffff) >>> 0;
}

function zip(entries) {
  const local = [];
  const central = [];
  let offset = 0;
  for (const [name, rawContents] of Object.entries(entries)) {
    const nameBuffer = Buffer.from(name.replaceAll("\\", "/"));
    const contents = Buffer.isBuffer(rawContents) ? rawContents : Buffer.from(rawContents);
    const checksum = crc32(contents);
    const localHeader = Buffer.concat([
      uint32(0x04034b50), uint16(20), uint16(0x0800), uint16(0), uint16(0), uint16(0),
      uint32(checksum), uint32(contents.length), uint32(contents.length), uint16(nameBuffer.length), uint16(0), nameBuffer,
    ]);
    local.push(localHeader, contents);
    central.push(Buffer.concat([
      uint32(0x02014b50), uint16(20), uint16(20), uint16(0x0800), uint16(0), uint16(0), uint16(0),
      uint32(checksum), uint32(contents.length), uint32(contents.length), uint16(nameBuffer.length), uint16(0), uint16(0),
      uint16(0), uint16(0), uint32(0), uint32(offset), nameBuffer,
    ]));
    offset += localHeader.length + contents.length;
  }
  const centralDirectory = Buffer.concat(central);
  return Buffer.concat([
    ...local,
    centralDirectory,
    uint32(0x06054b50), uint16(0), uint16(0), uint16(central.length), uint16(central.length),
    uint32(centralDirectory.length), uint32(offset), uint16(0),
  ]);
}

function tar(entries) {
  const blocks = [];
  for (const [name, rawContents] of Object.entries(entries)) {
    const contents = Buffer.isBuffer(rawContents) ? rawContents : Buffer.from(rawContents);
    const header = Buffer.alloc(512);
    header.write(name, 0, 100, "utf8");
    header.write("0000644\0", 100, 8, "ascii");
    header.write("0000000\0", 108, 8, "ascii");
    header.write("0000000\0", 116, 8, "ascii");
    header.write(`${contents.length.toString(8).padStart(11, "0")}\0`, 124, 12, "ascii");
    header.write("00000000000\0", 136, 12, "ascii");
    header.fill(0x20, 148, 156);
    header.write("0", 156, 1, "ascii");
    header.write("ustar\0", 257, 6, "ascii");
    header.write("00", 263, 2, "ascii");
    const checksum = [...header].reduce((sum, value) => sum + value, 0);
    header.write(`${checksum.toString(8).padStart(6, "0")}\0 `, 148, 8, "ascii");
    blocks.push(header, contents, Buffer.alloc((512 - (contents.length % 512)) % 512));
  }
  blocks.push(Buffer.alloc(1024));
  return Buffer.concat(blocks);
}

function imageLayer(name, contents) {
  return {
    mediaType: OCI_LAYER_GZIP,
    uncompressed: tar({ [name]: contents }),
    digest: undefined,
    sizeDelta: 0,
  };
}

function allFiles(root, prefix = "") {
  return readdirSync(root).flatMap((name) => {
    const path = resolve(root, name);
    const key = `${prefix}${name}`;
    return statSync(path).isDirectory() ? allFiles(path, `${key}/`) : [key];
  });
}

function write(root, path, contents) {
  const destination = resolve(root, path);
  mkdirSync(dirname(destination), { recursive: true });
  writeFileSync(destination, contents);
}

function defaultState() {
  const packageIds = [
    "SyntaxCircus.Cmsify.Contracts",
    "SyntaxCircus.Cmsify.Client",
    "SyntaxCircus.Cmsify.Client.DistributedCaching",
  ];
  return {
    version: VERSION,
    sourceSha: SOURCE_SHA,
    nuget: packageIds.map((id) => ({
      fileId: id,
      id,
      version: VERSION,
      framework: "net10.0",
      licenseExpression: "MIT",
      licenseFile: "LICENSE-MIT.txt",
      licensePayload: MIT_LICENSE,
      repositoryType: "git",
      repositoryUrl: "https://github.com/Syntax-Circus/cmsify",
      repositoryCommit: SOURCE_SHA,
    })),
    npm: {
      metadata: {
        name: "@cmsify/client",
        version: VERSION,
        description: "First-party TypeScript client for the Cmsify API.",
        license: "MIT",
        repository: {
          type: "git",
          url: "git+https://github.com/Syntax-Circus/cmsify.git",
          directory: "sdk/typescript",
        },
        gitHead: SOURCE_SHA,
        engines: { node: ">=20" },
        type: "module",
        main: "./dist/index.cjs",
        module: "./dist/index.js",
        types: "./dist/index.d.ts",
        exports: {
          ".": {
            types: "./dist/index.d.ts",
            import: "./dist/index.js",
            require: "./dist/index.cjs",
          },
        },
        files: ["dist", "src/generated"],
      },
      members: {
        "package/LICENSE": MIT_LICENSE,
        "package/README.md": "# Cmsify TypeScript client\n",
        "package/dist/index.cjs": "module.exports = {};\n",
        "package/dist/index.cjs.map": "{}\n",
        "package/dist/index.js": "export {};\n",
        "package/dist/index.js.map": "{}\n",
        "package/dist/index.d.ts": "export {};\n",
        "package/dist/index.d.cts": "export {};\n",
        "package/src/generated/client.ts": "export const generatedClient = true;\n",
        "package/src/generated/schema.ts": "export interface GeneratedSchema {}\n",
      },
    },
    oci: {
      api: imageState("api"),
      admin: imageState("admin"),
    },
    spdx: {
      nuget: spdxState("nuget", packageIds, "MIT"),
      npm: spdxState("npm", ["@cmsify/client"], "MIT"),
      api: spdxState("api", ["syntaxcircus/cmsify-api"], "AGPL-3.0-or-later"),
      admin: spdxState("admin", ["syntaxcircus/cmsify-admin"], "AGPL-3.0-or-later"),
    },
  };
}

function imageState(kind) {
  const repository = `syntaxcircus/cmsify-${kind}`;
  return {
    kind,
    repository,
    ref: `${repository}:${VERSION}`,
    descriptorMediaType: OCI_MANIFEST,
    descriptorPlatform: { os: "linux", architecture: "amd64" },
    configMediaType: OCI_CONFIG,
    rootfsType: "layers",
    rootfsDiffIds: (diffIds) => diffIds,
    layers: [
      imageLayer("cmsify-layer-one.txt", "first Cmsify fixture layer\n"),
      imageLayer("cmsify-layer-two.txt", "second Cmsify fixture layer\n"),
    ],
    config: {
      architecture: "amd64",
      os: "linux",
      config: {
        Labels: {
          "org.opencontainers.image.title": `Cmsify ${kind === "api" ? "API" : "Admin"}`,
          "org.opencontainers.image.source": "https://github.com/Syntax-Circus/cmsify",
          "org.opencontainers.image.revision": SOURCE_SHA,
          "org.opencontainers.image.version": VERSION,
          "org.opencontainers.image.licenses": "AGPL-3.0-or-later",
        },
      },
    },
  };
}

function spdxState(kind, names, license) {
  return {
    name: `Cmsify ${kind === "nuget" ? "NuGet SDK" : kind === "npm" ? "npm SDK" : kind === "api" ? "API OCI" : "Admin OCI"} ${VERSION}`,
    namespaceFile: `cmsify-${kind}.spdx.json`,
    packageNames: names,
    license,
  };
}

function renderNuget(root, packageState) {
  const nuspec = `<?xml version="1.0"?><package><metadata><id>${packageState.id}</id><version>${packageState.version}</version><license type="expression">${packageState.licenseExpression}</license><license type="file">${packageState.licenseFile}</license><repository type="${packageState.repositoryType}" url="${packageState.repositoryUrl}" commit="${packageState.repositoryCommit}" /></metadata></package>`;
  const entries = {
    [`${packageState.id}.nuspec`]: nuspec,
    [packageState.licenseFile]: packageState.licensePayload,
    [`lib/${packageState.framework}/${packageState.id}.dll`]: Buffer.from("minimal-test-assembly"),
  };
  write(root, `nuget/${packageState.fileId}.${VERSION}.nupkg`, zip(entries));
}

function renderNpm(root, state) {
  const staging = mkdtempSync(resolve(tmpdir(), "cmsify-npm-fixture-"));
  try {
    for (const [name, contents] of Object.entries({ ...state.members, "package/package.json": `${JSON.stringify(state.metadata, null, 2)}\n` })) write(staging, name, contents);
    const target = resolve(root, `npm/cmsify-client-${VERSION}.tgz`);
    mkdirSync(dirname(target), { recursive: true });
    execFileSync("tar", ["-czf", target, "-C", staging, "package"]);
  } finally {
    rmSync(staging, { recursive: true, force: true });
  }
}

function renderOci(root, state) {
  const manifests = {};
  for (const [kind, image] of Object.entries(state)) {
    const staging = mkdtempSync(resolve(tmpdir(), `cmsify-${kind}-oci-`));
    try {
      const layers = image.layers.map((layer) => {
        const contents = gzipSync(layer.uncompressed, { level: 9, mtime: 0 });
        const digest = sha256(contents);
        write(staging, `blobs/sha256/${digest.slice(7)}`, contents);
        return {
          mediaType: layer.mediaType,
          digest: layer.digest ?? digest,
          size: contents.length + (layer.sizeDelta ?? 0),
        };
      });
      const diffIds = image.layers.map((layer) => sha256(layer.uncompressed));
      const config = json({
        ...image.config,
        rootfs: {
          type: image.rootfsType,
          diff_ids: image.rootfsDiffIds([...diffIds]),
        },
        history: image.layers.map((_, index) => ({ created_by: `fixture layer ${index + 1}` })),
      });
      const configDigest = sha256(config);
      write(staging, `blobs/sha256/${configDigest.slice(7)}`, config);
      const manifest = json({
        schemaVersion: 2,
        mediaType: OCI_MANIFEST,
        config: { mediaType: image.configMediaType, digest: configDigest, size: config.length },
        layers,
      });
      const manifestDigest = sha256(manifest);
      write(staging, `blobs/sha256/${manifestDigest.slice(7)}`, manifest);
      const descriptor = {
        mediaType: image.descriptorMediaType,
        digest: manifestDigest,
        size: manifest.length,
        platform: image.descriptorPlatform,
        annotations: { "org.opencontainers.image.ref.name": VERSION, "io.containerd.image.name": image.ref },
      };
      write(staging, "oci-layout", json({ imageLayoutVersion: "1.0.0" }));
      write(staging, "index.json", json({ schemaVersion: 2, manifests: [
        { ...descriptor, annotations: { "org.opencontainers.image.ref.name": "old", "io.containerd.image.name": `unrelated/${kind}:old` } },
        descriptor,
      ] }));
      const target = resolve(root, `oci/cmsify-${kind}.oci.tar`);
      mkdirSync(dirname(target), { recursive: true });
      execFileSync("tar", ["-cf", target, "-C", staging, "oci-layout", "index.json", "blobs"]);
      write(root, `oci/cmsify-${kind}.metadata.json`, json({ "containerimage.descriptor": descriptor }));
      manifests[kind] = { repository: image.repository, ref: image.ref, tag: VERSION, imageName: image.ref, ...descriptor };
    } finally {
      rmSync(staging, { recursive: true, force: true });
    }
  }
  return manifests;
}

function renderSpdx(root, state, manifests) {
  for (const [kind, sbom] of Object.entries(state)) {
    const packages = sbom.packageNames.map((name, index) => {
      const SPDXID = `SPDXRef-${kind}-${index + 1}`;
      const result = { SPDXID, name, versionInfo: VERSION, licenseConcluded: sbom.license, licenseDeclared: sbom.license, downloadLocation: "NOASSERTION" };
      if (kind === "api" || kind === "admin") result.externalRefs = [{ referenceCategory: "PACKAGE-MANAGER", referenceType: "purl", referenceLocator: `pkg:oci/${name}@${manifests[kind].digest}` }];
      else if (kind === "nuget") result.externalRefs = [{ referenceCategory: "PACKAGE-MANAGER", referenceType: "purl", referenceLocator: `pkg:nuget/${name}@${VERSION}` }];
      else result.externalRefs = [{ referenceCategory: "PACKAGE-MANAGER", referenceType: "purl", referenceLocator: `pkg:npm/%40cmsify/client@${VERSION}` }];
      return result;
    });
    const dependency = {
      SPDXID: `SPDXRef-${kind}-dependency`,
      name: `fixture-${kind}-dependency`,
      versionInfo: "4.5.6",
      licenseDeclared: "Apache-2.0",
      downloadLocation: "NOASSERTION",
    };
    const documentDescribes = packages.map(({ SPDXID }) => SPDXID);
    const relationships = [
      ...packages.map(({ SPDXID }) => ({ spdxElementId: SPDXID, relationshipType: "DEPENDS_ON", relatedSpdxElement: dependency.SPDXID })),
      ...packages.map(({ SPDXID }) => ({ spdxElementId: "SPDXRef-DOCUMENT", relationshipType: "DESCRIBES", relatedSpdxElement: SPDXID })),
    ];
    write(root, `sbom/cmsify-${kind}.spdx.json`, json({
      spdxVersion: "SPDX-2.3",
      dataLicense: "CC0-1.0",
      SPDXID: "SPDXRef-DOCUMENT",
      name: sbom.name,
      documentNamespace: `https://github.com/Syntax-Circus/cmsify/releases/download/v${VERSION}/${sbom.namespaceFile}`,
      creationInfo: { created: "2026-08-25T00:00:00Z", creators: ["Tool: syft-1.42.3"], comment: `Cmsify source ${SOURCE_SHA}` },
      documentDescribes,
      packages: [...packages, dependency],
      relationships,
    }));
  }
}

export function refreshChecksums(root) {
  const names = allFiles(root).filter((name) => name !== "SHA256SUMS").sort();
  writeFileSync(resolve(root, "SHA256SUMS"), `${names.map((name) => `${createHash("sha256").update(readFileSync(resolve(root, name))).digest("hex")}  ${name.replaceAll("\\", "/")}`).join("\n")}\n`);
}

export function createValidCandidate({ mutate, afterRender, afterChecksums } = {}) {
  const root = mkdtempSync(resolve(tmpdir(), "cmsify-release-candidate-"));
  const state = defaultState();
  mutate?.(state);
  for (const packageState of state.nuget) renderNuget(root, packageState);
  renderNpm(root, state.npm);
  const manifests = renderOci(root, state.oci);
  renderSpdx(root, state.spdx, manifests);
  write(root, "release-manifest.json", json({ version: VERSION, sourceSha: SOURCE_SHA, oci: manifests }));
  afterRender?.({ root, state, manifests });
  refreshChecksums(root);
  afterChecksums?.({ root, state, manifests });
  return root;
}

export function copyCandidate(source) {
  const destination = mkdtempSync(resolve(tmpdir(), "cmsify-release-candidate-copy-"));
  cpSync(source, destination, { recursive: true });
  return destination;
}

export function swapFiles(root, first, second) {
  const firstPath = resolve(root, first);
  const secondPath = resolve(root, second);
  const contents = readFileSync(firstPath);
  writeFileSync(firstPath, readFileSync(secondPath));
  writeFileSync(secondPath, contents);
}

export function mutateJsonFile(root, relativePath, mutate) {
  const path = resolve(root, relativePath);
  const document = JSON.parse(readFileSync(path, "utf8"));
  mutate(document);
  writeFileSync(path, json(document));
}

export function mutateOciLayout(root, kind, mutate) {
  const archive = resolve(root, `oci/cmsify-${kind}.oci.tar`);
  const staging = mkdtempSync(resolve(tmpdir(), `cmsify-${kind}-oci-mutation-`));
  try {
    execFileSync("tar", ["-xf", archive, "-C", staging]);
    const indexPath = resolve(staging, "index.json");
    const index = JSON.parse(readFileSync(indexPath, "utf8"));
    const expectedRef = `syntaxcircus/cmsify-${kind}:${VERSION}`;
    const descriptor = index.manifests.find((candidate) => candidate.annotations?.["io.containerd.image.name"] === expectedRef);
    const manifestPath = descriptor?.digest?.startsWith("sha256:") ? resolve(staging, `blobs/sha256/${descriptor.digest.slice(7)}`) : undefined;
    const manifest = manifestPath && statSync(manifestPath).isFile() ? JSON.parse(readFileSync(manifestPath, "utf8")) : undefined;
    const configPath = manifest?.config?.digest?.startsWith("sha256:") ? resolve(staging, `blobs/sha256/${manifest.config.digest.slice(7)}`) : undefined;
    const config = configPath && statSync(configPath).isFile() ? JSON.parse(readFileSync(configPath, "utf8")) : undefined;
    mutate({ staging, index, descriptor, manifest, config, manifestPath, configPath });
    writeFileSync(indexPath, json(index));
    if (manifest && manifestPath) writeFileSync(manifestPath, json(manifest));
    if (config && configPath) writeFileSync(configPath, json(config));
    execFileSync("tar", ["-cf", archive, "-C", staging, "oci-layout", "index.json", "blobs"]);
  } finally {
    rmSync(staging, { recursive: true, force: true });
  }
}

export function removeCandidate(root) {
  rmSync(root, { recursive: true, force: true });
}

export function candidatePath(root, relativePath) {
  return resolve(root, relativePath);
}

export function fileName(path) {
  return basename(path);
}
