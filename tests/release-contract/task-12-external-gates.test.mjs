import assert from "node:assert/strict";
import { createHash } from "node:crypto";
import {
  chmodSync,
  copyFileSync,
  existsSync,
  mkdirSync,
  mkdtempSync,
  readFileSync,
  renameSync,
  rmSync,
  symlinkSync,
  writeFileSync,
} from "node:fs";
import os from "node:os";
import path from "node:path";
import { spawnSync } from "node:child_process";
import test from "node:test";
import { fileURLToPath } from "node:url";
import {
  SOURCE_SHA as candidateSourceSha,
  VERSION as candidateVersion,
  createValidCandidate,
  mutateJsonFile,
  refreshChecksums,
  removeCandidate,
} from "./release-candidate-fixture.mjs";

const repositoryRoot = path.resolve(path.dirname(fileURLToPath(import.meta.url)), "../..");
const verifier = path.join(repositoryRoot, "scripts/release/verify-task-12-external-gate.ps1");
const stubSource = path.join(repositoryRoot, "tests/release-contract/fixtures/task-12-tool-stub.mjs");
const sourceSha = "1".repeat(40);
const apiDigest = `sha256:${"a".repeat(64)}`;
const adminDigest = `sha256:${"b".repeat(64)}`;
const runId = "12345";
const tag = "v1.2.3";
const version = "1.2.3";
const signerWorkflow = "Syntax-Circus/cmsify/.github/workflows/publish-cmsify.yml";
const cosignIdentity = `https://github.com/${signerWorkflow}@refs/tags/${tag}`;
const testNow = new Date(Math.floor(Date.now() / 1000) * 1000);
const requiredInputs = [
  "CMSIFY_RELEASE_RUN_ID",
  "CMSIFY_CHECKSUMS_PATH",
  "CMSIFY_API_DIGEST",
  "CMSIFY_ADMIN_DIGEST",
  "CMSIFY_RELEASE_VERSION",
  "CMSIFY_RELEASE_TAG",
  "CMSIFY_COSIGN_CERTIFICATE_IDENTITY",
  "CMSIFY_ATTESTATION_SIGNER_WORKFLOW",
  "CMSIFY_ACCESSIBILITY_JOB_ID",
  "CMSIFY_PROMOTE_JOB_ID",
  "CMSIFY_SMOKE_JOB_ID",
  "CMSIFY_UPGRADE_ROLLBACK_JOB_ID",
  "CMSIFY_RELEASE_SOURCE_SHA",
  "CMSIFY_SOAK_EVIDENCE_PATH",
  "CMSIFY_SOAK_EVIDENCE_SHA256",
];

function sha256(value) {
  return createHash("sha256").update(value).digest("hex");
}

function minutesFromNow(minutes) {
  return new Date(testNow.getTime() + minutes * 60_000).toISOString().replace(".000Z", "Z");
}

function makeRoot() {
  const root = mkdtempSync(path.join(os.tmpdir(), "cmsify-task12-gates-"));
  const bin = path.join(root, "bin");
  mkdirSync(bin);
  const stub = path.join(root, "tool-stub.mjs");
  copyFileSync(stubSource, stub);
  for (const tool of ["gh", "oras", "cosign", "dotnet", "curl"]) {
    if (process.platform === "win32") {
      writeFileSync(path.join(bin, `${tool}.ps1`), `& '${process.execPath.replaceAll("'", "''")}' '${stub.replaceAll("'", "''")}' '${tool}' @args\nexit $LASTEXITCODE\n`);
    } else {
      const executable = path.join(bin, tool);
      writeFileSync(executable, `#!/bin/sh\nexec "${process.execPath}" "${stub}" ${tool} "$@"\n`);
      chmodSync(executable, 0o755);
    }
  }
  return { root, bin };
}

function baseEnvironment(bin) {
  const environment = { ...process.env };
  for (const input of requiredInputs) delete environment[input];
  return {
    ...environment,
    PATH: `${bin}${path.delimiter}${environment.PATH ?? ""}`,
    CMSIFY_RELEASE_RUN_ID: runId,
    CMSIFY_API_DIGEST: apiDigest,
    CMSIFY_ADMIN_DIGEST: adminDigest,
    CMSIFY_RELEASE_VERSION: version,
    CMSIFY_RELEASE_TAG: tag,
    CMSIFY_COSIGN_CERTIFICATE_IDENTITY: cosignIdentity,
    CMSIFY_ATTESTATION_SIGNER_WORKFLOW: signerWorkflow,
    CMSIFY_ACCESSIBILITY_JOB_ID: "201",
    CMSIFY_PROMOTE_JOB_ID: "202",
    CMSIFY_SMOKE_JOB_ID: "203",
    CMSIFY_UPGRADE_ROLLBACK_JOB_ID: "204",
    CMSIFY_RELEASE_SOURCE_SHA: sourceSha,
  };
}

function runGate(gate, { commands = {}, environment = {}, arrange } = {}) {
  const { root, bin } = makeRoot();
  try {
    const fixturePath = path.join(root, "commands.json");
    writeFileSync(fixturePath, JSON.stringify({ commands }));
    const logPath = path.join(root, "calls.log");
    const env = { ...baseEnvironment(bin), CMSIFY_TASK12_STUB_FIXTURE: fixturePath, CMSIFY_TASK12_STUB_LOG: logPath, ...environment };
    arrange?.({ root, env });
    const result = spawnSync("pwsh", ["-NoLogo", "-NoProfile", "-NonInteractive", "-File", verifier, "-Gate", gate], {
      cwd: repositoryRoot,
      env,
      encoding: "utf8",
    });
    result.calls = existsSync(logPath) ? readFileSync(logPath, "utf8").trim().split(/\r?\n/).filter(Boolean) : [];
    return result;
  } finally {
    rmSync(root, { recursive: true, force: true });
  }
}

const packageIdentity = {
  id: "SyntaxCircus.Http.Resilience",
  version: "0.2.0-cmsify.1",
  contentHash: "/wzJoTLh3ebeAzOdaT0yUXXznF4C/26eWS6js5dDzzgDKsxNpeOL+s0ZJTwaxZYj6wG5cr9I4rUYOzpXOWoW+w==",
};
const affectedAssets = [
  "sdk/dotnet/src/SyntaxCircus.Cmsify.Client/obj/project.assets.json",
  "sdk/dotnet/src/SyntaxCircus.Cmsify.Client.DistributedCaching/obj/project.assets.json",
  "sdk/dotnet/tests/SyntaxCircus.Cmsify.Client.Tests/obj/project.assets.json",
  "src/Cmsify.Admin/obj/project.assets.json",
  "tests/Cmsify.Admin.Integration.Tests/obj/project.assets.json",
];

function publicAssets(libraryPacksSource) {
  const packagePath = `${packageIdentity.id.toLowerCase()}/${packageIdentity.version}`;
  const document = {
    libraries: { [`${packageIdentity.id}/${packageIdentity.version}`]: { type: "package", path: packagePath, sha512: packageIdentity.contentHash } },
    packageFolders: { "{{ARG_AFTER:--packages}}": {} },
    project: { restore: { configFilePaths: ["{{ARG_AFTER:--configfile}}"], sources: { [libraryPacksSource]: {}, "https://api.nuget.org/v3/index.json": {} } } },
  };
  return affectedAssets.map((asset) => ({ path: asset, json: document }));
}

function runPublicRestore({ downloadBytes = "public-package-bytes", expectedBytes = downloadBytes, cacheBytes = downloadBytes, mutateScript, mutateIdentity, libraryPacksSource, captureTemporaryRoot = false } = {}) {
  const { root, bin } = makeRoot();
  const repo = path.join(root, "repo");
  const scriptPath = path.join(repo, "scripts/release/verify-task-12-external-gate.ps1");
  const evidencePath = path.join(repo, "docs/evidence/task-12-local-verification.json");
  mkdirSync(path.dirname(scriptPath), { recursive: true });
  mkdirSync(path.dirname(evidencePath), { recursive: true });
  let script = readFileSync(verifier, "utf8");
  if (mutateScript) script = mutateScript(script);
  writeFileSync(scriptPath, script);
  writeFileSync(path.join(repo, "Cmsify.slnx"), "<Solution />");
  const sha = sha256(expectedBytes).toUpperCase();
  const trackedIdentity = { ...packageIdentity, sha256: sha, publicRestoreValidated: false };
  mutateIdentity?.(trackedIdentity);
  writeFileSync(evidencePath, JSON.stringify({ localFeedPackage: trackedIdentity }));
  const fixturePath = path.join(root, "commands.json");
  const logPath = path.join(root, "calls.log");
  const temporaryRootCapturePath = path.join(root, "temporary-root.txt");
  const trustedLibraryPacks = libraryPacksSource ?? path.join(bin, "library-packs");
  const minimalConfig = `<?xml version="1.0" encoding="utf-8"?>\n<configuration>\n  <packageSources>\n    <clear />\n    <add key="nuget.org" value="https://api.nuget.org/v3/index.json" protocolVersion="3" />\n  </packageSources>\n</configuration>\n`;
  const fixture = {
    patterns: [
      { pattern: "^curl .*--output .+ https://api\\.nuget\\.org/v3-flatcontainer/syntaxcircus\\.http\\.resilience/0\\.2\\.0-cmsify\\.1/syntaxcircus\\.http\\.resilience\\.0\\.2\\.0-cmsify\\.1\\.nupkg$", response: { writeAfterArgument: { name: "--output", base64: Buffer.from(downloadBytes).toString("base64") } } },
      { pattern: "^dotnet restore Cmsify\\.slnx --configfile .+ --packages .+ --no-http-cache --locked-mode --force-evaluate$", response: { assertFileAfterArgument: { name: "--configfile", exact: minimalConfig }, writeFiles: [...publicAssets(trustedLibraryPacks), { path: `{{ARG_AFTER:--packages}}/${packageIdentity.id.toLowerCase()}/${packageIdentity.version}/${packageIdentity.id.toLowerCase()}.${packageIdentity.version}.nupkg`, base64: Buffer.from(cacheBytes).toString("base64") }] } },
    ],
  };
  writeFileSync(fixturePath, JSON.stringify(fixture));
  const result = spawnSync("pwsh", ["-NoLogo", "-NoProfile", "-NonInteractive", "-File", scriptPath, "-Gate", "public-package-restore"], {
    cwd: repo,
    env: { ...baseEnvironment(bin), CMSIFY_TASK12_STUB_FIXTURE: fixturePath, CMSIFY_TASK12_STUB_LOG: logPath, ...(captureTemporaryRoot ? { CMSIFY_TASK12_TEMP_CAPTURE: temporaryRootCapturePath } : {}) },
    encoding: "utf8",
  });
  result.calls = existsSync(logPath) ? readFileSync(logPath, "utf8").trim().split(/\r?\n/).filter(Boolean) : [];
  result.capturedTemporaryRoot = existsSync(temporaryRootCapturePath) ? readFileSync(temporaryRootCapturePath, "utf8") : undefined;
  result.temporaryRootExistsAfter = result.capturedTemporaryRoot ? existsSync(result.capturedTemporaryRoot) : undefined;
  rmSync(root, { recursive: true, force: true });
  return result;
}

function runResponse() {
  return {
    id: Number(runId),
    event: "push",
    head_sha: sourceSha,
    head_branch: tag,
    path: ".github/workflows/publish-cmsify.yml",
    workflow_ref: `Syntax-Circus/cmsify/.github/workflows/publish-cmsify.yml@refs/tags/${tag}`,
    status: "completed",
    conclusion: "success",
    created_at: minutesFromNow(-90),
  };
}

function job(id, name, startedAt, completedAt) {
  return { id: Number(id), name, status: "completed", conclusion: "success", started_at: startedAt, completed_at: completedAt };
}

function runAndJobsCommands() {
  return {
    [`gh api repos/Syntax-Circus/cmsify/actions/runs/${runId}`]: { json: runResponse() },
    [`gh api repos/Syntax-Circus/cmsify/actions/runs/${runId}/jobs?filter=latest&per_page=100`]: {
      json: {
        jobs: [
          job("201", "candidate-accessibility", minutesFromNow(-125), minutesFromNow(-115)),
          job("202", "promote", minutesFromNow(-70), minutesFromNow(-60)),
          job("203", "artifact-smoke", minutesFromNow(-125), minutesFromNow(-110)),
          job("204", "upgrade-rollback", minutesFromNow(-125), minutesFromNow(-108)),
        ],
      },
    },
  };
}

function approvalCommands() {
  return {
    ...runAndJobsCommands(),
    "gh api repos/Syntax-Circus/cmsify/environments/release": {
      json: { protection_rules: [{ type: "required_reviewers", reviewers: [{ type: "User", reviewer: { login: "approver" } }] }] },
    },
    [`gh api repos/Syntax-Circus/cmsify/actions/runs/${runId}/approvals`]: {
      json: [{ state: "approved", comment: "Ship it", user: { login: "approver" }, environments: [{ name: "release" }] }],
    },
    [`gh api repos/Syntax-Circus/cmsify/deployments?environment=release&sha=${sourceSha}&per_page=100`]: {
      json: [{ id: 301, sha: sourceSha, ref: tag, environment: "release", created_at: minutesFromNow(-75) }],
    },
    "gh api repos/Syntax-Circus/cmsify/deployments/301/statuses?per_page=100": {
      json: [{ state: "success", created_at: minutesFromNow(-60), log_url: `https://github.com/Syntax-Circus/cmsify/actions/runs/${runId}/job/202`, environment_url: "https://cmsify.example.invalid" }],
    },
  };
}

test("declares exactly the immutable inputs consumed by gate subcommands", () => {
  const result = spawnSync("pwsh", ["-NoLogo", "-NoProfile", "-NonInteractive", "-File", verifier, "-ListInputs"], { cwd: repositoryRoot, encoding: "utf8" });
  assert.equal(result.status, 0, result.stderr);
  assert.deepEqual(JSON.parse(result.stdout).sort(), [...requiredInputs].sort());
});

test("public restore authenticates exact public bytes in isolated NuGet state and exactly five restored graphs", () => {
  const valid = runPublicRestore();
  assert.equal(valid.status, 0, valid.stderr);
  assert.equal(valid.calls.filter((call) => call.startsWith("curl ")).length, 1);
  assert.equal(valid.calls.filter((call) => call.startsWith("dotnet restore ")).length, 1);

  const wrongBytes = runPublicRestore({ downloadBytes: "wrong-public-package-bytes", expectedBytes: "public-package-bytes" });
  assert.notEqual(wrongBytes.status, 0, wrongBytes.stdout);
  const wrongCacheBytes = runPublicRestore({ cacheBytes: "wrong-restored-cache-bytes" });
  assert.notEqual(wrongCacheBytes.status, 0, wrongCacheBytes.stdout);
  const deceptiveLibraryPacks = runPublicRestore({ libraryPacksSource: path.join(os.tmpdir(), "attacker", "library-packs") });
  assert.notEqual(deceptiveLibraryPacks.status, 0, deceptiveLibraryPacks.stdout);
  for (const mutateIdentity of [
    (identity) => { delete identity.contentHash; },
    (identity) => { identity.contentHash = "wrong"; },
  ]) {
    const result = runPublicRestore({ mutateIdentity });
    assert.notEqual(result.status, 0, result.stdout);
    assert.equal(result.calls.length, 0, result.calls.join("\n"));
  }

  for (const mutateScript of [
    (script) => script.replace('@("restore", "Cmsify.slnx", "--configfile", $configPath, "--packages", $packagesRoot, "--no-http-cache", "--locked-mode", "--force-evaluate")', '@("restore", "Cmsify.slnx", "--locked-mode")'),
    (script) => script.replace('"--packages", $packagesRoot, ', ""),
    (script) => script.replace('"--no-http-cache", ', ""),
    (script) => script.replace('https://api.nuget.org/v3/index.json', 'artifacts/local-nuget/http-resilience'),
  ]) {
    const result = runPublicRestore({ mutateScript });
    assert.notEqual(result.status, 0, `security mutation unexpectedly passed: ${result.stdout}`);
  }

  const inspectionFailure = runPublicRestore({
    captureTemporaryRoot: true,
    mutateScript(script) {
      return script
        .replace('[void] (New-Item -ItemType Directory -Path $temporaryRoot)', '[void] (New-Item -ItemType Directory -Path $temporaryRoot); [IO.File]::WriteAllText($env:CMSIFY_TASK12_TEMP_CAPTURE, $temporaryRoot)')
        .replace('$temporaryItem = Get-Item -LiteralPath $temporaryRoot -Force', 'throw "Injected temporary-root inspection failure."');
    },
  });
  assert.notEqual(inspectionFailure.status, 0, inspectionFailure.stdout);
  assert.ok(inspectionFailure.capturedTemporaryRoot, inspectionFailure.stderr);
  assert.equal(inspectionFailure.temporaryRootExistsAfter, false, `temporary root leaked: ${inspectionFailure.capturedTemporaryRoot}`);
});

test("immutable OCI verification fails closed on a native fetch failure", () => {
  const commands = {
    [`oras manifest fetch --descriptor docker.io/syntaxcircus/cmsify-api:${version}`]: { exitCode: 7, stderr: "registry unavailable" },
  };
  const result = runGate("immutable-oci-promotion", { commands });
  assert.notEqual(result.status, 0, result.stdout);
  assert.match(result.stderr, /API descriptor fetch failed/i);
});

test("protected approval binds exact workflow, tag, SHA, promote job, reviewers, approval, and deployment", () => {
  const valid = runGate("protected-approvals", { commands: approvalCommands() });
  assert.equal(valid.status, 0, valid.stderr);

  for (const mutate of [
    (commands) => { commands[`gh api repos/Syntax-Circus/cmsify/actions/runs/${runId}`].json.head_sha = "2".repeat(40); },
    (commands) => { commands[`gh api repos/Syntax-Circus/cmsify/actions/runs/${runId}`].json.path = ".github/workflows/other.yml"; },
    (commands) => { commands[`gh api repos/Syntax-Circus/cmsify/actions/runs/${runId}`].json.workflow_ref = `Syntax-Circus/cmsify/.github/workflows/other.yml@refs/tags/${tag}`; },
    (commands) => { commands[`gh api repos/Syntax-Circus/cmsify/actions/runs/${runId}`].json.workflow_ref = "Syntax-Circus/cmsify/.github/workflows/publish-cmsify.yml@refs/tags/v9.9.9"; },
    (commands) => { commands[`gh api repos/Syntax-Circus/cmsify/actions/runs/${runId}/jobs?filter=latest&per_page=100`].json.jobs[1].name = "other"; },
    (commands) => { commands["gh api repos/Syntax-Circus/cmsify/environments/release"].json.protection_rules[0].reviewers = []; },
    (commands) => { commands[`gh api repos/Syntax-Circus/cmsify/actions/runs/${runId}/approvals`].json[0].state = "rejected"; },
    (commands) => { commands[`gh api repos/Syntax-Circus/cmsify/deployments?environment=release&sha=${sourceSha}&per_page=100`].json[0].ref = "v9.9.9"; },
    (commands) => { commands["gh api repos/Syntax-Circus/cmsify/deployments/301/statuses?per_page=100"].json[0].log_url = "https://github.com/Syntax-Circus/cmsify/actions/runs/99999/job/202"; },
  ]) {
    const commands = structuredClone(approvalCommands());
    mutate(commands);
    const result = runGate("protected-approvals", { commands });
    assert.notEqual(result.status, 0, `mutation unexpectedly passed: ${JSON.stringify(commands)}`);
  }
});

function runCanonicalAttestation(mutate) {
  const candidate = createValidCandidate();
  try {
    mutate?.(candidate);
    const sumsPath = path.join(candidate, "SHA256SUMS");
    const subjects = readFileSync(sumsPath, "utf8").trim().split(/\r?\n/).map((line) => line.replace(/^[0-9a-f]{64}  /, ""));
    const commands = Object.fromEntries(subjects.map((subject) => [
      `gh attestation verify ${path.join(candidate, ...subject.split("/"))} --repo Syntax-Circus/cmsify --signer-workflow ${signerWorkflow} --source-digest ${candidateSourceSha}`,
      {},
    ]));
    return runGate("artifact-attestation", {
      commands,
      environment: {
        CMSIFY_CHECKSUMS_PATH: sumsPath,
        CMSIFY_RELEASE_SOURCE_SHA: candidateSourceSha,
        CMSIFY_RELEASE_VERSION: candidateVersion,
      },
    });
  } finally {
    removeCandidate(candidate);
  }
}

test("attestation verifies the complete canonical candidate before any exact subject attestation", () => {
  const valid = runCanonicalAttestation();
  assert.equal(valid.status, 0, valid.stderr);
  assert.ok(valid.calls.filter((call) => call.startsWith("gh attestation verify ")).length > 7);

  for (const mutate of [
    (candidate) => {
      const sumsPath = path.join(candidate, "SHA256SUMS");
      const lines = readFileSync(sumsPath, "utf8").trimEnd().split(/\r?\n/);
      writeFileSync(sumsPath, `${lines.slice(1).join("\n")}\n`);
    },
    (candidate) => {
      writeFileSync(path.join(candidate, "unexpected.bin"), "unexpected");
      const sumsPath = path.join(candidate, "SHA256SUMS");
      writeFileSync(sumsPath, `${readFileSync(sumsPath, "utf8")}${sha256("unexpected")}  unexpected.bin\n`);
    },
    (candidate) => {
      mutateJsonFile(candidate, "release-manifest.json", (manifest) => { manifest.sourceSha = "f".repeat(40); });
      refreshChecksums(candidate);
    },
    (candidate) => {
      mutateJsonFile(candidate, "release-manifest.json", (manifest) => { manifest.version = "9.9.9"; });
      refreshChecksums(candidate);
    },
  ]) {
    const result = runCanonicalAttestation(mutate);
    assert.notEqual(result.status, 0, `candidate mutation unexpectedly passed: ${result.stdout}`);
    assert.equal(result.calls.filter((call) => call.startsWith("gh attestation verify ")).length, 0, result.calls.join("\n"));
  }
});

test("attestation rejects link escape and linked trust root", () => {
  const escapedCandidate = createValidCandidate();
  try {
    const escaped = runGate("artifact-attestation", {
      environment: { CMSIFY_RELEASE_SOURCE_SHA: candidateSourceSha, CMSIFY_RELEASE_VERSION: candidateVersion },
      arrange({ root, env }) {
        const outside = path.join(root, "outside-nuget");
        renameSync(path.join(escapedCandidate, "nuget"), outside);
        symlinkSync(outside, path.join(escapedCandidate, "nuget"), process.platform === "win32" ? "junction" : "dir");
        env.CMSIFY_CHECKSUMS_PATH = path.join(escapedCandidate, "SHA256SUMS");
      },
    });
    assert.notEqual(escaped.status, 0, escaped.stdout);
    assert.match(escaped.stderr, /link|reparse|complete release candidate verification/i);
    assert.equal(escaped.calls.filter((call) => call.startsWith("gh attestation verify ")).length, 0);
  } finally {
    removeCandidate(escapedCandidate);
  }

  const actualCandidate = createValidCandidate();
  try {
    const linkedRoot = runGate("artifact-attestation", {
      environment: { CMSIFY_RELEASE_SOURCE_SHA: candidateSourceSha, CMSIFY_RELEASE_VERSION: candidateVersion },
      arrange({ root, env }) {
        const candidateLink = path.join(root, "linked-candidate");
        symlinkSync(actualCandidate, candidateLink, process.platform === "win32" ? "junction" : "dir");
        env.CMSIFY_CHECKSUMS_PATH = path.join(candidateLink, "SHA256SUMS");
      },
    });
    assert.notEqual(linkedRoot.status, 0, linkedRoot.stdout);
    assert.match(linkedRoot.stderr, /trust root.*link|link.*trust root|reparse/i);
  } finally {
    removeCandidate(actualCandidate);
  }

  const parentCandidate = createValidCandidate();
  try {
    const linkedParent = runGate("artifact-attestation", {
      environment: { CMSIFY_RELEASE_SOURCE_SHA: candidateSourceSha, CMSIFY_RELEASE_VERSION: candidateVersion },
      arrange({ root, env }) {
        const candidateParentLink = path.join(root, "linked-parent");
        symlinkSync(path.dirname(parentCandidate), candidateParentLink, process.platform === "win32" ? "junction" : "dir");
        env.CMSIFY_CHECKSUMS_PATH = path.join(candidateParentLink, path.basename(parentCandidate), "SHA256SUMS");
      },
    });
    assert.notEqual(linkedParent.status, 0, linkedParent.stdout);
    assert.match(linkedParent.stderr, /link|reparse/i);
    assert.equal(linkedParent.calls.filter((call) => call.startsWith("gh attestation verify ")).length, 0);
  } finally {
    removeCandidate(parentCandidate);
  }
});

function arrangeSoak({ root, env }, overrides = {}) {
  const soak = {
    schema: "cmsify.hosted-soak-evidence.v1",
    releaseRunId: runId,
    sourceSha,
    smokeJobId: "203",
    upgradeRollbackJobId: "204",
    smokePassed: true,
    upgradeRollbackPassed: true,
    passed: true,
    startedAtUtc: minutesFromNow(-105),
    completedAtUtc: minutesFromNow(-45),
    ...overrides,
  };
  const soakPath = path.join(root, "soak.json");
  const contents = JSON.stringify(soak);
  writeFileSync(soakPath, contents);
  env.CMSIFY_SOAK_EVIDENCE_PATH = soakPath;
  env.CMSIFY_SOAK_EVIDENCE_SHA256 = sha256(contents);
  return soakPath;
}

function soakCommands(soakPath) {
  return {
    ...runAndJobsCommands(),
    [`gh attestation verify ${soakPath} --repo Syntax-Circus/cmsify --signer-workflow ${signerWorkflow} --source-digest ${sourceSha}`]: {},
  };
}

test("soak evidence is authenticated, boolean-exact, run-bound, and at least 60 minutes", () => {
  let commands;
  const valid = runGate("hosted-smoke-soak", {
    commands: {},
    arrange(context) {
      const soakPath = arrangeSoak(context);
      commands = soakCommands(soakPath);
      writeFileSync(context.env.CMSIFY_TASK12_STUB_FIXTURE, JSON.stringify({ commands }));
    },
  });
  assert.equal(valid.status, 0, valid.stderr);

  for (const overrides of [
    { passed: "false" },
    { completedAtUtc: minutesFromNow(-46) },
    { startedAtUtc: minutesFromNow(-109), completedAtUtc: minutesFromNow(-49) },
    { startedAtUtc: minutesFromNow(60), completedAtUtc: minutesFromNow(120) },
    { sourceSha: "2".repeat(40) },
  ]) {
    const result = runGate("hosted-smoke-soak", {
      commands: {},
      arrange(context) {
        const soakPath = arrangeSoak(context, overrides);
        const fixture = soakCommands(soakPath);
        writeFileSync(context.env.CMSIFY_TASK12_STUB_FIXTURE, JSON.stringify({ commands: fixture }));
      },
    });
    assert.notEqual(result.status, 0, `soak mutation unexpectedly passed: ${JSON.stringify(overrides)}`);
  }
});

function finalReleaseCommands(releaseOverrides = {}) {
  return {
    [`gh release view ${tag} --repo Syntax-Circus/cmsify --json tagName,isDraft,isPrerelease,publishedAt,url`]: {
      json: { tagName: tag, isDraft: false, isPrerelease: false, publishedAt: minutesFromNow(-30), url: "https://example.invalid/release", ...releaseOverrides },
    },
    [`gh api repos/Syntax-Circus/cmsify/git/ref/tags/${tag}`]: { json: { object: { type: "commit", sha: sourceSha } } },
  };
}

test("final release requires exact stable tag, source, and published non-prerelease", () => {
  const valid = runGate("final-release", { commands: finalReleaseCommands() });
  assert.equal(valid.status, 0, valid.stderr);

  for (const options of [
    { commands: finalReleaseCommands({ isPrerelease: true }) },
    { commands: { ...finalReleaseCommands(), [`gh api repos/Syntax-Circus/cmsify/git/ref/tags/${tag}`]: { json: { object: { type: "commit", sha: "2".repeat(40) } } } } },
    { commands: finalReleaseCommands(), environment: { CMSIFY_RELEASE_VERSION: "1.2.3-rc.1", CMSIFY_RELEASE_TAG: "v1.2.3-rc.1" } },
  ]) {
    const result = runGate("final-release", options);
    assert.notEqual(result.status, 0);
  }
});
