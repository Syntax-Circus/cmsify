import { createHash } from "node:crypto";
import { execFileSync } from "node:child_process";
import { existsSync, readFileSync, readdirSync, statSync } from "node:fs";
import { resolve, relative } from "node:path";

const arguments_ = process.argv.slice(2);
const option = (name) => arguments_[arguments_.indexOf(name) + 1];
const artifacts = resolve(option("--artifacts") ?? "");
const version = option("--version");
const sourceSha = option("--source-sha");
const errors = [];

function expect(condition, message) {
  if (!condition) errors.push(message);
}

function files(directory, extension) {
  const path = resolve(artifacts, directory);
  return existsSync(path) ? readdirSync(path).filter((name) => name.endsWith(extension)).map((name) => resolve(path, name)) : [];
}

function allFiles(directory, prefix = "") {
  if (!existsSync(directory)) return [];
  return readdirSync(directory).flatMap((name) => {
    const path = resolve(directory, name);
    const relativeName = `${prefix}${name}`;
    return statSync(path).isDirectory() ? allFiles(path, `${relativeName}/`) : [relativeName];
  });
}

function command(commandName, commandArguments) {
  try {
    return execFileSync(commandName, commandArguments, { encoding: "utf8" });
  } catch (error) {
    errors.push(`${commandName} could not inspect a release artifact: ${error.message}`);
    return "";
  }
}

expect(/^(?:0|[1-9]\d*)\.(?:0|[1-9]\d*)\.(?:0|[1-9]\d*)(?:-[0-9A-Za-z.-]+)?$/.test(version ?? ""), "Release artifact verification requires a SemVer version.");
expect(/^[0-9a-f]{40}$/i.test(sourceSha ?? ""), "Release artifact verification requires the immutable 40-character source SHA.");

const nuget = files("nuget", ".nupkg").filter((path) => !path.endsWith(".snupkg"));
expect(nuget.length === 3, "Candidate must contain exactly three NuGet .nupkg archives.");
const expectedNuget = ["Cmsify.Contracts", "SyntaxCircus.Cmsify.Client", "SyntaxCircus.Cmsify.Client.DistributedCaching"];
for (const id of expectedNuget) expect(nuget.some((path) => path.endsWith(`${id}.${version}.nupkg`)), `Candidate must contain ${id}.${version}.nupkg.`);
for (const packagePath of nuget) {
  const nuspec = command("unzip", ["-p", packagePath, "*.nuspec"]);
  const listing = command("unzip", ["-Z1", packagePath]);
  expect(new RegExp(`<version>${version.replace(/[.*+?^${}()|[\]\\]/g, "\\$&")}</version>`, "i").test(nuspec), `NuGet archive ${relative(artifacts, packagePath)} must have release version ${version}.`);
  expect(/<license type="expression">MIT<\/license>/i.test(nuspec), `NuGet archive ${relative(artifacts, packagePath)} must retain the MIT license expression.`);
  expect(new RegExp(`<repository[^>]*commit="${sourceSha}"`, "i").test(nuspec), `NuGet archive ${relative(artifacts, packagePath)} must record source commit ${sourceSha}.`);
  expect(/lib\/net10\.0\//i.test(listing), `NuGet archive ${relative(artifacts, packagePath)} must contain net10.0 assets.`);
  expect(/LICENSE-MIT\.txt/i.test(listing), `NuGet archive ${relative(artifacts, packagePath)} must include its MIT license payload.`);
}

const npm = files("npm", ".tgz");
expect(npm.length === 1, "Candidate must contain exactly one packed npm archive.");
for (const packagePath of npm) {
  const packageJson = command("tar", ["-xOf", packagePath, "package/package.json"]);
  try {
    const metadata = JSON.parse(packageJson);
    expect(metadata.name === "@cmsify/client", "Packed npm archive must be @cmsify/client.");
    expect(metadata.version === version, `Packed npm archive must have release version ${version}.`);
    expect(metadata.license === "MIT", "Packed npm archive must retain the MIT license.");
    expect(/^>=20(?:\.0\.0)?$/.test(metadata.engines?.node ?? ""), "Packed npm archive must support Node 20 or later.");
    expect(metadata.private === undefined || metadata.private === false, "Packed npm archive must be public (private absent or boolean false).");
    expect(metadata.repository?.type === "git" && metadata.repository?.url === "git+https://github.com/SyntaxCircus/cmsify.git", "Packed npm archive must retain the public GitHub repository identity.");
    expect(metadata.gitHead === sourceSha, "Packed npm archive must record the immutable source commit.");
    const listing = command("tar", ["-tzf", packagePath]);
    expect(/package\/LICENSE\r?$/m.test(listing) && /package\/dist\/index\.js/m.test(listing) && /package\/dist\/index\.d\.ts/m.test(listing), "Packed npm archive must include MIT LICENSE and supported public distribution files.");
  } catch {
    errors.push("Packed npm archive must contain valid package/package.json metadata.");
  }
}

let manifestDigest = {};
try {
  const manifest = JSON.parse(readFileSync(resolve(artifacts, "release-manifest.json"), "utf8"));
  manifestDigest = { api: manifest.oci?.api?.digest, admin: manifest.oci?.admin?.digest };
} catch { /* the manifest validator below reports this independently */ }

const oci = files("oci", ".oci.tar");
expect(oci.length === 2, "Candidate must contain API and Admin OCI image layouts.");
for (const name of ["cmsify-api", "cmsify-admin"]) expect(oci.some((path) => path.endsWith(`${name}.oci.tar`)), `Candidate must contain ${name}.oci.tar.`);
for (const imagePath of oci) {
  const layout = command("tar", ["-tf", imagePath]);
  expect(/^index\.json$/m.test(layout) && /^oci-layout$/m.test(layout), `OCI archive ${relative(artifacts, imagePath)} must be a complete OCI image layout.`);
  try {
    const index = JSON.parse(command("tar", ["-xOf", imagePath, "index.json"]));
    const descriptor = index.manifests?.[0];
    const manifest = JSON.parse(command("tar", ["-xOf", imagePath, `blobs/sha256/${descriptor.digest.slice("sha256:".length)}`]));
    const config = JSON.parse(command("tar", ["-xOf", imagePath, `blobs/sha256/${manifest.config.digest.slice("sha256:".length)}`]));
    const kind = imagePath.endsWith("cmsify-api.oci.tar") ? "api" : "admin";
    expect(descriptor?.digest === manifestDigest?.[kind], `OCI ${kind} layout descriptor must equal its certified manifest digest.`);
    expect(config.config?.Labels?.["org.opencontainers.image.version"] === version && config.config?.Labels?.["org.opencontainers.image.revision"] === sourceSha && config.config?.Labels?.["org.opencontainers.image.licenses"] === "AGPL-3.0-or-later", `OCI ${kind} configuration labels must bind version, source SHA, and AGPL-3.0-or-later.`);
  } catch {
    errors.push(`OCI archive ${relative(artifacts, imagePath)} must contain parseable index, manifest, and config.`);
  }
}

const sboms = files("sbom", ".spdx.json");
const expectedSboms = ["cmsify-nuget.spdx.json", "cmsify-npm.spdx.json", "cmsify-api.spdx.json", "cmsify-admin.spdx.json"];
expect(sboms.length === expectedSboms.length, "Candidate must contain exactly four named SPDX SBOMs.");
for (const name of expectedSboms) expect(sboms.some((path) => path.endsWith(name)), `Candidate must contain ${name}.`);
for (const sbom of sboms) {
  try {
    const document = JSON.parse(readFileSync(sbom, "utf8"));
    expect(document.spdxVersion?.startsWith("SPDX-") && Array.isArray(document.packages) && document.packages.length > 0, `SBOM ${relative(artifacts, sbom)} must be SPDX JSON with a subject/package inventory.`);
  } catch {
    errors.push(`SBOM ${relative(artifacts, sbom)} must be valid SPDX JSON.`);
  }
}

const manifestPath = resolve(artifacts, "release-manifest.json");
try {
  const manifest = JSON.parse(readFileSync(manifestPath, "utf8"));
  expect(manifest.version === version, "Release manifest version must equal the tag-derived version.");
  expect(manifest.sourceSha === sourceSha, "Release manifest source SHA must equal the resolved immutable commit.");
  for (const image of ["api", "admin"]) {
    expect(manifest.oci?.[image]?.repository === `syntaxcircus/cmsify-${image}`, `Release manifest must bind the Cmsify ${image} OCI repository.`);
    expect(/^sha256:[0-9a-f]{64}$/i.test(manifest.oci?.[image]?.digest ?? ""), `Release manifest must bind the Cmsify ${image} OCI descriptor digest.`);
  }
} catch {
  errors.push("Candidate must contain a valid release-manifest.json with version and sourceSha.");
}

const checksumsPath = resolve(artifacts, "SHA256SUMS");
if (!existsSync(checksumsPath)) {
  errors.push("Candidate must contain SHA256SUMS checksums.");
} else {
  const checksums = new Map(readFileSync(checksumsPath, "utf8").trim().split(/\r?\n/).filter(Boolean).map((line) => [line.slice(66).replace(/^\*/, ""), line.slice(0, 64)]));
  const expectedFiles = new Set([...nuget, ...npm, ...oci, ...files("oci", ".metadata.json"), ...sboms, manifestPath].map((candidate) => relative(artifacts, candidate).replaceAll("\\", "/")));
  const actualFiles = new Set(allFiles(artifacts).filter((name) => name !== "SHA256SUMS"));
  expect(actualFiles.size === expectedFiles.size && [...actualFiles].every((name) => expectedFiles.has(name)), "Candidate must not contain unchecked extra files.");
  expect(checksums.size === expectedFiles.size && [...checksums.keys()].every((name) => expectedFiles.has(name)), "SHA256SUMS must cover exactly every certified candidate file.");
  for (const candidate of [...nuget, ...npm, ...oci, ...files("oci", ".metadata.json"), ...sboms, manifestPath]) {
    if (!existsSync(candidate)) continue;
    const name = relative(artifacts, candidate).replaceAll("\\", "/");
    const actual = createHash("sha256").update(readFileSync(candidate)).digest("hex");
    expect((checksums.get(name) ?? checksums.get(`artifacts/${name}`)) === actual, `Checksum must certify ${name}.`);
  }
}

if (errors.length > 0) {
  process.stderr.write(`${errors.join("\n")}\n`);
  process.exitCode = 1;
} else {
  process.stdout.write(`Release artifacts verified for ${version} from ${sourceSha}.\n`);
}
