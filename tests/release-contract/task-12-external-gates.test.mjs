import assert from "node:assert/strict";
import { createHash } from "node:crypto";
import {
  chmodSync,
  copyFileSync,
  mkdirSync,
  mkdtempSync,
  readFileSync,
  rmSync,
  symlinkSync,
  writeFileSync,
} from "node:fs";
import os from "node:os";
import path from "node:path";
import { spawnSync } from "node:child_process";
import test from "node:test";
import { fileURLToPath } from "node:url";

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
  for (const tool of ["gh", "oras", "cosign", "dotnet"]) {
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
    const env = { ...baseEnvironment(bin), CMSIFY_TASK12_STUB_FIXTURE: fixturePath, ...environment };
    arrange?.({ root, env });
    return spawnSync("pwsh", ["-NoLogo", "-NoProfile", "-NonInteractive", "-File", verifier, "-Gate", gate], {
      cwd: repositoryRoot,
      env,
      encoding: "utf8",
    });
  } finally {
    rmSync(root, { recursive: true, force: true });
  }
}

function runResponse() {
  return {
    id: Number(runId),
    event: "push",
    head_sha: sourceSha,
    head_branch: tag,
    path: `.github/workflows/publish-cmsify.yml@${tag}`,
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

test("attestation verifies every confined checksummed file and rejects link escape", () => {
  const commands = {};
  const valid = runGate("artifact-attestation", {
    commands,
    arrange({ root, env }) {
      const candidate = path.join(root, "candidate");
      mkdirSync(candidate);
      const first = path.join(candidate, "first.bin");
      const second = path.join(candidate, "second.bin");
      writeFileSync(first, "first");
      writeFileSync(second, "second");
      const sums = `${sha256("first")}  first.bin\n${sha256("second")}  second.bin\n`;
      const sumsPath = path.join(candidate, "SHA256SUMS");
      writeFileSync(sumsPath, sums);
      env.CMSIFY_CHECKSUMS_PATH = sumsPath;
      commands[`gh attestation verify ${first} --repo Syntax-Circus/cmsify --signer-workflow ${signerWorkflow} --source-digest ${sourceSha}`] = {};
      commands[`gh attestation verify ${second} --repo Syntax-Circus/cmsify --signer-workflow ${signerWorkflow} --source-digest ${sourceSha}`] = {};
      writeFileSync(env.CMSIFY_TASK12_STUB_FIXTURE, JSON.stringify({ commands }));
    },
  });
  assert.equal(valid.status, 0, valid.stderr);

  const escaped = runGate("artifact-attestation", {
    arrange({ root, env }) {
      const candidate = path.join(root, "candidate");
      const outside = path.join(root, "outside");
      mkdirSync(candidate);
      mkdirSync(outside);
      writeFileSync(path.join(outside, "subject.bin"), "outside");
      symlinkSync(outside, path.join(candidate, "link"), process.platform === "win32" ? "junction" : "dir");
      const sumsPath = path.join(candidate, "SHA256SUMS");
      writeFileSync(sumsPath, `${sha256("outside")}  link/subject.bin\n`);
      env.CMSIFY_CHECKSUMS_PATH = sumsPath;
    },
  });
  assert.notEqual(escaped.status, 0, escaped.stdout);
  assert.match(escaped.stderr, /link|reparse/i);
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
