import { existsSync, readFileSync } from "node:fs";
import { resolve } from "node:path";
import { fileURLToPath } from "node:url";

const defaultRoot = resolve(fileURLToPath(new URL("../..", import.meta.url)));
const rootArgument = process.argv.indexOf("--root");
const repositoryRoot = rootArgument === -1 ? defaultRoot : resolve(process.argv[rootArgument + 1] ?? defaultRoot);
const errors = [];

function file(relativePath) {
  const path = resolve(repositoryRoot, relativePath);
  if (!existsSync(path)) {
    errors.push(`Missing required release file: ${relativePath}`);
    return "";
  }
  return readFileSync(path, "utf8");
}

function expect(condition, message) {
  if (!condition) errors.push(message);
}

function projectMetadata(relativePath) {
  const contents = file(relativePath);
  expect(/<TargetFramework>net10\.0<\/TargetFramework>/i.test(contents), `${relativePath} must support .NET 10 only.`);
  expect(/<PackageLicenseExpression>MIT<\/PackageLicenseExpression>/i.test(contents), `${relativePath} must declare the MIT package license.`);
}

const sourceLicense = file("LICENSE");
expect(/GNU AFFERO GENERAL PUBLIC LICENSE/i.test(sourceLicense), "Repository/server source must remain AGPL-3.0-or-later.");

const sourceVersion = file("Directory.Build.props");
expect(/<Version[^>]*>0\.0\.0-local<\/Version>/i.test(sourceVersion), "Source builds must use the non-publishable 0.0.0-local version.");
expect(/<IsPackable[^>]*CmsifyReleaseBuild[^>]*>false<\/IsPackable>/i.test(sourceVersion) && /RequireCmsifyReleaseInputs/i.test(sourceVersion), "Source .NET packages must be non-packable unless validated release inputs explicitly enable packing.");

const packageJson = file("sdk/typescript/package.json");
try {
  const typeScriptPackage = JSON.parse(packageJson);
  expect(typeScriptPackage.license === "MIT", "@cmsify/client must declare the MIT license.");
  expect(typeScriptPackage.version === "0.0.0-local", "@cmsify/client source version must be 0.0.0-local.");
  expect(typeScriptPackage.private === true, "@cmsify/client source package must be private until the validated release build overrides it.");
  expect(/^>=20(?:\.0\.0)?$/.test(typeScriptPackage.engines?.node ?? ""), "@cmsify/client must require Node 20 or later.");
} catch {
  errors.push("sdk/typescript/package.json must be valid JSON.");
}
expect(/MIT License/i.test(file("sdk/typescript/LICENSE")), "@cmsify/client archive must include the MIT license text.");

for (const project of [
  "src/Cmsify.Contracts/Cmsify.Contracts.csproj",
  "sdk/dotnet/src/SyntaxCircus.Cmsify.Client/SyntaxCircus.Cmsify.Client.csproj",
  "sdk/dotnet/src/SyntaxCircus.Cmsify.Client.DistributedCaching/SyntaxCircus.Cmsify.Client.DistributedCaching.csproj",
]) projectMetadata(project);

const workflowPath = ".github/workflows/publish-cmsify.yml";
const workflow = file(workflowPath);
expect(!existsSync(resolve(repositoryRoot, ".github/workflows/npm-publish-cmsify-client.yml")), "A separate npm publication workflow is forbidden; promotion must be unified.");
expect(/\bpush:\s*\n\s+tags:/m.test(workflow) && !/\bbranches:/m.test(workflow), "Release workflow must be tag-only; branch builds never publish or tag.");
expect(/node scripts\/release\/validate-release-tag\.mjs\s+"?\$\{\{\s*github\.ref_name\s*\}\}"?/i.test(workflow) || /validate-release-tag\.mjs/i.test(workflow), "Release workflow must validate the vX.Y.Z or vX.Y.Z-prerelease tag.");
expect(/validate-release-tag\.mjs[^\n]*--require-changelog/i.test(workflow), "Reviewed tag promotion must require an exact dated changelog entry.");
expect(/source_sha/i.test(workflow) && /needs\.resolve\.outputs\.source_sha/i.test(workflow), "Release workflow must carry one resolved immutable source SHA into build and promotion.");
expect(/is_prerelease/i.test(workflow) && /npm_channel/i.test(workflow), "Release workflow must derive prerelease state and npm channel from validated SemVer.");

for (const match of workflow.matchAll(/^\s*-?\s*uses:\s*([^\s#]+)/gm)) {
  expect(/@[0-9a-f]{40}$/i.test(match[1]), `Release action must be pinned by immutable SHA: ${match[1]}`);
}

for (const job of ["resolve", "build", "certify", "promote"]) {
  expect(new RegExp(`^\\s{2}${job}:`, "m").test(workflow), `Release workflow must include the ${job} job.`);
}

expect(/build:[\s\S]*dotnet pack[\s\S]*npm pack[\s\S]*docker buildx build[\s\S]*verify-release-artifacts\.mjs[\s\S]*upload-artifact/s.test(workflow), "The build job must build candidate NuGet, npm, and OCI artifacts once, verify them, and upload one candidate artifact.");
expect(/docker\/setup-buildx-action@[0-9a-f]{40}[\s\S]*driver:\s*docker-container/s.test(workflow), "OCI candidates require a SHA-pinned docker-container Buildx builder.");
expect(/anchore\/sbom-action\/download-syft@[0-9a-f]{40}[\s\S]*syft-version:/s.test(workflow), "Candidate SBOM generation must explicitly provision a pinned SBOM tool.");
expect(/docker run -d[\s\S]*health\/live[\s\S]*curl[\s\S]*18081/s.test(workflow), "OCI candidate verification must start the exact API/Admin images and probe health/static behavior.");
expect(/cmsify-api\.metadata\.json[\s\S]*containerimage\.descriptor[\s\S]*release-manifest\.json/s.test(workflow), "Candidate manifest must bind OCI descriptor digests before checksum/provenance certification.");
expect(/dotnet-consumer:[\s\S]*setup-dotnet[\s\S]*download-artifact[\s\S]*dotnet new console[\s\S]*SyntaxCircus\.Cmsify\.Contracts[\s\S]*SyntaxCircus\.Cmsify\.Client[\s\S]*SyntaxCircus\.Cmsify\.Client\.DistributedCaching/s.test(workflow), "Release workflow must install all three candidate packages into a clean .NET 10 consumer.");
expect(/node-consumer:[\s\S]*matrix:[\s\S]*node-version:\s*\["20", "22"\][\s\S]*download-artifact[\s\S]*CMSIFY_CLIENT_TARBALL=[\s\S]*npm run test:consumer/s.test(workflow), "Release workflow must install the candidate through the reused clean Node 20/22 consumer check.");
expect(/certify:[\s\S]*download-artifact[\s\S]*attest-build-provenance/s.test(workflow), "The certify job must attest the downloaded immutable candidate.");
expect(/promote:[\s\S]*environment:\s*release[\s\S]*download-artifact[\s\S]*git ls-remote[\s\S]*sha256sum --check[\s\S]*NuGet\/login@[0-9a-f]{40}[\s\S]*dotnet nuget push[\s\S]*npm publish[\s\S]*oras cp[\s\S]*oras manifest fetch[\s\S]*gh release create/s.test(workflow), "Protected promotion must revalidate the remote tag and publish only the certified candidate by descriptor-preserving copy.");

function jobBody(name) {
  const start = workflow.search(new RegExp(`^  ${name}:`, "m"));
  if (start === -1) return "";
  const afterStart = workflow.slice(start + 1);
  const nextJob = afterStart.search(/^  [A-Za-z0-9_-]+:/m);
  return nextJob === -1 ? workflow.slice(start) : workflow.slice(start, start + 1 + nextJob);
}

const promotion = jobBody("promote");
expect(!/\b(dotnet pack|npm pack|npm run build|docker buildx build|docker build)\b/i.test(promotion), "Promotion must not rebuild mutable artifacts.");
expect(!/--skip-duplicate|NUGET_API_KEY\s*:\s*\$\{\{\s*secrets\./i.test(promotion), "NuGet promotion must use the short-lived OIDC key and reject pre-existing package versions.");
expect(/id-token:\s*write[\s\S]*registry-url:\s*https:\/\/registry\.npmjs\.org[\s\S]*npm@11\.11\.0[\s\S]*--provenance[\s\S]*--tag "\$NPM_CHANNEL"/s.test(promotion), "npm trusted publishing must have OIDC, registry configuration, supported npm, provenance, and a prerelease-safe tag.");
expect(/--prerelease/.test(promotion), "GitHub Release promotion must mark SemVer prereleases as prereleases.");
expect(!/docker push/i.test(promotion) && /oras cp --from-oci-layout[\s\S]*oras manifest fetch[\s\S]*API_EXPECTED/s.test(promotion), "OCI promotion must copy certified descriptors and compare remote digests without mutable docker push.");

const branchWorkflow = file(".github/workflows/dotnet-test.yml");
expect(/pull_request:/i.test(branchWorkflow) && /verify-release-contract\.mjs/i.test(branchWorkflow) && /tests\/release-contract/i.test(branchWorkflow), "Branch/PR validation must execute the release-contract verifier and tests.");

if (errors.length > 0) {
  process.stderr.write(`${errors.join("\n")}\n`);
  process.exitCode = 1;
} else {
  process.stdout.write(`Release contract verified for ${repositoryRoot}.\n`);
}
