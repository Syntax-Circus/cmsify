import assert from "node:assert/strict";
import { createHash } from "node:crypto";
import { access, mkdir, mkdtemp, rm, writeFile } from "node:fs/promises";
import { tmpdir } from "node:os";
import { basename, join } from "node:path";
import test from "node:test";

import { createDockerAdapter } from "../../eng/release-smoke/harness.mjs";

const runId = "cmsify-smoke-1234abcd";

function primaryNames() {
  return {
    network: `${runId}-network`,
    postgres: `${runId}-postgres`,
    postgresVolume: `${runId}-postgres-data`,
    minio: `${runId}-minio`,
    mediaVolume: `${runId}-media-data`,
    oidc: `${runId}-oidc`,
    receiver: `${runId}-webhook`,
    api: `${runId}-api`,
    admin: `${runId}-admin`,
    adminKeysVolume: `${runId}-admin-keys`,
  };
}

function processResult(stdout = "") {
  return { exitCode: 0, stdout, stderr: "", durationMs: 1 };
}

test("foundation uses run-scoped TLS origins and an ordinary private Docker network without host route shadowing", async () => {
  const output = await mkdtemp(join(tmpdir(), "cmsify-release-foundation-"));
  const calls = [];
  let port = 41000;
  const run = async (command, args) => {
    calls.push([command, ...args]);
    if (command === "openssl") {
      const keyout = args.indexOf("-keyout");
      const out = args.indexOf("-out");
      if (keyout >= 0) await writeFile(args[keyout + 1], "TEST PRIVATE KEY");
      if (out >= 0) await writeFile(args[out + 1], "TEST CERTIFICATE");
      return processResult();
    }
    if (args[0] === "port") return processResult(`127.0.0.1:${port++}\n`);
    if (args[0] === "container" && args[1] === "inspect" && args.includes("{{json .}}")) {
      const name = args.at(-1);
      return processResult(JSON.stringify({
        Id: `sha256:${Buffer.from(name).toString("hex").padEnd(64, "0").slice(0, 64)}`,
        Name: `/${name}`,
        Config: { Labels: { "io.syntaxcircus.cmsify.release-smoke": "true", "io.syntaxcircus.cmsify.release-smoke-run": runId } },
      }));
    }
    if (args[0] === "volume" && args[1] === "inspect") {
      const name = args.at(-1);
      return processResult(JSON.stringify({
        Name: name,
        CreatedAt: "2026-08-29T12:00:00Z",
        Mountpoint: `/docker/${name}`,
        Labels: { "io.syntaxcircus.cmsify.release-smoke": "true", "io.syntaxcircus.cmsify.release-smoke-run": runId },
      }));
    }
    return processResult();
  };
  const adapter = createDockerAdapter({ run, repositoryRoot: process.cwd() });
  const context = {
    output,
    runId,
    maxAttempts: 2,
    onFirstResource() {},
    candidates: {
      api: { imageId: `sha256:${"a".repeat(64)}` },
      admin: { imageId: `sha256:${"b".repeat(64)}` },
    },
    runtime: {},
    secrets: {},
  };

  const result = await adapter.prepareFoundation(context);

  const networkCreate = calls.find(([, first, second]) => first === "network" && second === "create");
  assert.ok(networkCreate);
  assert.equal(networkCreate.includes("--subnet"), false);
  assert.equal(calls.some((call) => call.includes("--ip")), false);
  assert.equal(calls.filter(([command]) => command === "openssl").length, 3);
  const oidc = calls.find((call) => call.includes(`${runId}-oidc`) && call.includes("MODE=oidc"));
  const proxy = calls.find((call) => call.includes(`${runId}-admin-tls`) && call.includes("MODE=admin-proxy"));
  assert.ok(oidc.includes("type=bind") || oidc.some((value) => value.includes("target=/certs")));
  assert.ok(proxy);
  const receiver = calls.find((call) => call.includes("MODE=receiver"));
  assert.ok(receiver.includes("--network-alias"));
  assert.ok(receiver.includes(`webhook.${runId}.release-smoke.invalid`));
  const apiRun = calls.find((call) => call.includes(context.candidates.api.imageId) && call.includes("--pull"));
  assert.ok(apiRun.some((value) => value === "SSL_CERT_FILE=/certs/ca.crt"));
  assert.ok(apiRun.some((value) => value === "Auth__Oidc__Authority=https://oidc:8080"));
  const adminRun = calls.find((call) => call.includes(context.candidates.admin.imageId) && call.includes("--pull"));
  assert.ok(adminRun.some((value) => value === "ASPNETCORE_FORWARDEDHEADERS_ENABLED=true"));
  assert.ok(adminRun.some((value) => value === `Admin__ReleaseSmokeRunId=${runId}`));
  assert.match(result.runtime.adminBase, /^https:\/\/127\.0\.0\.1:/);
  assert.match(result.runtime.oidcBase, /^https:\/\/localhost:/);
  assert.equal(result.runtime.tlsCa, "TEST CERTIFICATE");
  await rm(output, { recursive: true, force: true });
});

test("TLS generation failure still leaves cleanup ownership of generated private material", async () => {
  const output = await mkdtemp(join(tmpdir(), "cmsify-release-tls-failure-"));
  const run = async (command, args) => {
    if (command === "openssl") throw new Error("injected TLS generation failure");
    return processResult();
  };
  const adapter = createDockerAdapter({ run, repositoryRoot: process.cwd() });
  const context = {
    output,
    runId,
    maxAttempts: 1,
    onFirstResource() {},
    candidates: { api: { imageId: `sha256:${"a".repeat(64)}` }, admin: { imageId: `sha256:${"b".repeat(64)}` } },
    runtime: {},
    secrets: {},
  };

  await assert.rejects(adapter.prepareFoundation(context), /TLS generation failure/i);
  await access(join(output, ".tls"));
  await adapter.cleanup(context);
  await assert.rejects(access(join(output, ".tls")));
  await rm(output, { recursive: true, force: true });
});

test("matched backup keeps a multi-megabyte pg_dump out of diagnostic stdout and validates the copied file", async () => {
  const output = await mkdtemp(join(tmpdir(), "cmsify-release-backup-"));
  const calls = [];
  const run = async (command, args) => {
    assert.equal(command, "docker");
    calls.push([...args]);
    if (args[0] === "cp" && args[1].endsWith(":/tmp/release-smoke.dump")) {
      await writeFile(args[2], Buffer.alloc(2 * 1024 * 1024, 0x5a));
    }
    if (args[0] === "cp" && args[1].includes(":/data/.")) {
      await mkdir(args[2], { recursive: true });
      await writeFile(join(args[2], "object.bin"), Buffer.alloc(2048, 0x33));
    }
    if (args.includes("pg_restore") && args.includes("--list")) return processResult("1; 0 0 TABLE public release_smoke cmsify\n");
    return processResult();
  };
  const adapter = createDockerAdapter({ run, repositoryRoot: process.cwd() });
  const context = {
    output,
    runId,
    runtime: { names: primaryNames() },
    secrets: { postgresPassword: "pg-secret" },
  };

  const backup = await adapter.backup(context);

  const dump = calls.find((args) => args.includes("pg_dump"));
  assert.ok(dump.includes("--file") && dump.includes("/tmp/release-smoke.dump"));
  assert.equal(dump.some((argument) => argument === "--format"), true);
  assert.ok(calls.filter((args) => args.includes("pg_restore") && args.includes("--list")).length >= 2);
  assert.equal(backup.postgresBytes, 2 * 1024 * 1024);
  assert.match(backup.postgresSha256, /^[0-9a-f]{64}$/);
  await rm(output, { recursive: true, force: true });
});

test("the shared abort signal is forwarded to real Docker process boundaries", async () => {
  const controller = new AbortController();
  const observed = [];
  const inspected = {
    Id: `sha256:${"a".repeat(64)}`,
    RepoDigests: [],
    Os: "linux",
    Architecture: "amd64",
    Config: { Labels: { "org.opencontainers.image.version": "1.2.3", "org.opencontainers.image.revision": "1".repeat(40) } },
  };
  const adapter = createDockerAdapter({
    repositoryRoot: process.cwd(),
    signal: controller.signal,
    run: async (_command, _args, options) => {
      observed.push(options.signal);
      return processResult(JSON.stringify(inspected));
    },
  });

  await adapter.inspectCandidates({
    apiImage: "cmsify/api:test",
    adminImage: "cmsify/admin:test",
    version: "1.2.3",
    sourceSha: "1".repeat(40),
  });

  assert.deepEqual(observed, [controller.signal, controller.signal]);
});

test("PostgreSQL readiness abort stops retry attempts and sleeps immediately", async () => {
  const output = await mkdtemp(join(tmpdir(), "cmsify-release-postgres-abort-"));
  const controller = new AbortController();
  let readinessAttempts = 0;
  const adapter = createDockerAdapter({
    repositoryRoot: process.cwd(),
    signal: controller.signal,
    run: async (command, args) => {
      if (command === "openssl") {
        const keyout = args.indexOf("-keyout");
        const out = args.indexOf("-out");
        if (keyout >= 0) await writeFile(args[keyout + 1], "TEST PRIVATE KEY");
        if (out >= 0) await writeFile(args[out + 1], "TEST CERTIFICATE");
        return processResult();
      }
      if (args.includes("pg_isready")) {
        readinessAttempts += 1;
        controller.abort(new Error("SIGTERM"));
        throw new Error("postgres is not ready");
      }
      if (args[0] === "port") return processResult("127.0.0.1:41000\n");
      return processResult();
    },
  });
  const context = {
    output,
    runId,
    maxAttempts: 2,
    onFirstResource() {},
    candidates: { api: { imageId: `sha256:${"a".repeat(64)}` }, admin: { imageId: `sha256:${"b".repeat(64)}` } },
    runtime: {},
    secrets: {},
  };

  try {
    await assert.rejects(adapter.prepareFoundation(context), /(?:abort|SIGTERM)/i);
    assert.equal(readinessAttempts, 1);
  } finally {
    await adapter.cleanup(context);
    await rm(output, { recursive: true, force: true });
  }
});

test("MinIO readiness abort stops retry attempts and sleeps immediately", async () => {
  const output = await mkdtemp(join(tmpdir(), "cmsify-release-minio-abort-"));
  const controller = new AbortController();
  let aliasAttempts = 0;
  let readinessAttempts = 0;
  const adapter = createDockerAdapter({
    repositoryRoot: process.cwd(),
    signal: controller.signal,
    run: async (command, args) => {
      if (command === "openssl") {
        const keyout = args.indexOf("-keyout");
        const out = args.indexOf("-out");
        if (keyout >= 0) await writeFile(args[keyout + 1], "TEST PRIVATE KEY");
        if (out >= 0) await writeFile(args[out + 1], "TEST CERTIFICATE");
        return processResult();
      }
      if (args.includes("pg_isready")) return processResult();
      if (args.includes("mc") && args.includes("alias")) {
        aliasAttempts += 1;
        return processResult();
      }
      if (args.includes("mc") && args.includes("ready")) {
        readinessAttempts += 1;
        controller.abort(new Error("SIGINT"));
        throw new Error("minio is not ready");
      }
      if (args[0] === "port") return processResult("127.0.0.1:41000\n");
      return processResult();
    },
  });
  const context = {
    output,
    runId,
    maxAttempts: 2,
    onFirstResource() {},
    candidates: { api: { imageId: `sha256:${"a".repeat(64)}` }, admin: { imageId: `sha256:${"b".repeat(64)}` } },
    runtime: {},
    secrets: {},
  };

  try {
    await assert.rejects(adapter.prepareFoundation(context), /(?:abort|SIGINT)/i);
    assert.equal(aliasAttempts, 1);
    assert.equal(readinessAttempts, 1);
  } finally {
    await adapter.cleanup(context);
    await rm(output, { recursive: true, force: true });
  }
});

test("cleanup remains runnable after the shared scenario signal is aborted", async () => {
  const controller = new AbortController();
  controller.abort(new Error("SIGTERM"));
  const observed = [];
  const adapter = createDockerAdapter({
    repositoryRoot: process.cwd(),
    signal: controller.signal,
    run: async (_command, _args, options) => {
      observed.push(options.signal);
      return processResult();
    },
  });

  await adapter.cleanup({ runId });

  assert.deepEqual(observed, [undefined, undefined, undefined]);
});

test("destructive canary revalidates backup bytes and hashes before issuing any removal", async () => {
  const output = await mkdtemp(join(tmpdir(), "cmsify-release-corrupt-"));
  const backupDirectory = join(output, "backup");
  const mediaDirectory = join(backupDirectory, "media");
  const postgresPath = join(backupDirectory, "postgres.dump");
  await mkdir(mediaDirectory, { recursive: true });
  await writeFile(postgresPath, Buffer.alloc(2048, 0x11));
  await writeFile(join(mediaDirectory, "object.bin"), Buffer.alloc(2048, 0x22));
  const calls = [];
  const adapter = createDockerAdapter({
    repositoryRoot: process.cwd(),
    run: async (_command, args) => {
      calls.push([...args]);
      return processResult();
    },
  });
  const context = {
    output,
    runId,
    runtime: { names: primaryNames(), primaryResources: {} },
    secrets: { postgresPassword: "pg-secret" },
    artifacts: {
      backup: {
        directory: backupDirectory,
        postgresPath,
        mediaDirectory,
        postgresBytes: 2048,
        postgresSha256: "1".repeat(64),
        mediaSha256: "2".repeat(64),
      },
    },
  };

  await assert.rejects(adapter.destructiveCanary(context), /backup.*(?:changed|hash|valid)/i);
  assert.equal(calls.some((args) => args[0] === "rm" || (args[0] === "volume" && args[1] === "rm")), false);
  await rm(output, { recursive: true, force: true });
});

test("destructive canary rejects a truncated dump before copying or removing resources", async () => {
  const output = await mkdtemp(join(tmpdir(), "cmsify-release-truncated-"));
  const backupDirectory = join(output, "backup");
  const mediaDirectory = join(backupDirectory, "media");
  const postgresPath = join(backupDirectory, "postgres.dump");
  await mkdir(mediaDirectory, { recursive: true });
  await writeFile(postgresPath, Buffer.alloc(1023, 0x11));
  const calls = [];
  const adapter = createDockerAdapter({
    repositoryRoot: process.cwd(),
    run: async (_command, args) => { calls.push([...args]); return processResult(); },
  });

  await assert.rejects(adapter.destructiveCanary({
    output,
    runId,
    runtime: { names: primaryNames(), primaryResources: { containers: {}, volumes: {} } },
    secrets: { postgresPassword: "pg-secret" },
    artifacts: {
      backup: {
        directory: backupDirectory,
        postgresPath,
        mediaDirectory,
        postgresBytes: 1023,
        postgresSha256: createHash("sha256").update(Buffer.alloc(1023, 0x11)).digest("hex"),
        mediaSha256: createHash("sha256").digest("hex"),
      },
    },
  }), /meaningful validated file/i);
  assert.deepEqual(calls, []);
  await rm(output, { recursive: true, force: true });
});

test("destructive canary re-inspects every captured member immediately before exact-ID removal", async () => {
  const output = await mkdtemp(join(tmpdir(), "cmsify-release-canary-success-"));
  const names = primaryNames();
  const containerNames = [names.api, names.admin, names.postgres, names.minio];
  const volumeNames = [names.postgresVolume, names.mediaVolume, names.adminKeysVolume];
  const containerIds = Object.fromEntries(containerNames.map((name, index) => [name, `sha256:${String(index + 1).repeat(64)}`]));
  const volumeIds = Object.fromEntries(volumeNames.map((name) => [name, [name, "2026-08-29T12:00:00Z", `/docker/${name}`].join(String.fromCharCode(0))]));
  const calls = [];
  const adapter = createDockerAdapter({
    repositoryRoot: process.cwd(),
    run: async (_command, args) => {
      calls.push([...args]);
      if (args[0] === "container" && args[1] === "inspect") {
        const name = args.at(-1);
        return processResult(JSON.stringify({
          Id: containerIds[name],
          Name: `/${name}`,
          Config: { Labels: { "io.syntaxcircus.cmsify.release-smoke": "true", "io.syntaxcircus.cmsify.release-smoke-run": runId } },
        }));
      }
      if (args[0] === "volume" && args[1] === "inspect") {
        const name = args.at(-1);
        return processResult(JSON.stringify({
          Name: name,
          CreatedAt: "2026-08-29T12:00:00Z",
          Mountpoint: `/docker/${name}`,
          Labels: { "io.syntaxcircus.cmsify.release-smoke": "true", "io.syntaxcircus.cmsify.release-smoke-run": runId },
        }));
      }
      if (args.includes("pg_restore") && args.includes("--list")) return processResult("1; valid backup\n");
      return processResult();
    },
  });
  const backupDirectory = join(output, "backup");
  const mediaDirectory = join(backupDirectory, "media");
  const postgresPath = join(backupDirectory, "postgres.dump");
  const postgresBytes = Buffer.alloc(2048, 0x71);
  const mediaBytes = Buffer.alloc(2048, 0x72);
  await mkdir(mediaDirectory, { recursive: true });
  await writeFile(postgresPath, postgresBytes);
  await writeFile(join(mediaDirectory, "object.bin"), mediaBytes);
  const context = {
    output,
    runId,
    runtime: {
      names,
      primaryResources: {
        containers: Object.fromEntries(containerNames.map((name) => [name, { id: containerIds[name], name }])),
        volumes: Object.fromEntries(volumeNames.map((name) => [name, { id: volumeIds[name], name }])),
      },
    },
    secrets: { postgresPassword: "pg-secret" },
    artifacts: {
      backup: {
        directory: backupDirectory,
        postgresPath,
        mediaDirectory,
        postgresBytes: postgresBytes.length,
        postgresSha256: createHash("sha256").update(postgresBytes).digest("hex"),
        mediaSha256: createHash("sha256").update("object.bin", "utf8").update(Buffer.from([0])).update(mediaBytes).digest("hex"),
      },
    },
  };

  const result = await adapter.destructiveCanary(context);

  assert.equal(result.destroyed, true);
  for (const name of containerNames) {
    const inspectIndex = calls.findIndex((args) => args[0] === "container" && args[1] === "inspect" && args.at(-1) === name);
    const removeIndex = calls.findIndex((args) => args[0] === "rm" && args.at(-1) === containerIds[name]);
    assert.ok(inspectIndex >= 0 && removeIndex === inspectIndex + 1);
  }
  for (const name of volumeNames) {
    const inspectIndex = calls.findIndex((args) => args[0] === "volume" && args[1] === "inspect" && args.at(-1) === name);
    const removeIndex = calls.findIndex((args) => args[0] === "volume" && args[1] === "rm" && args.at(-1) === name);
    assert.ok(inspectIndex >= 0 && removeIndex === inspectIndex + 1);
  }
  await rm(output, { recursive: true, force: true });
});

test("destructive canary refuses a replaced or relabelled primary resource before removing it", async () => {
  const output = await mkdtemp(join(tmpdir(), "cmsify-release-fence-"));
  const names = primaryNames();
  const calls = [];
  const inspected = {
    Id: `sha256:${"9".repeat(64)}`,
    Name: `/${names.api}`,
    Config: { Labels: { "io.syntaxcircus.cmsify.release-smoke": "true", "io.syntaxcircus.cmsify.release-smoke-run": runId } },
  };
  const adapter = createDockerAdapter({
    repositoryRoot: process.cwd(),
    run: async (_command, args) => {
      calls.push([...args]);
      if (args[0] === "container" && args[1] === "inspect") return processResult(JSON.stringify(inspected));
      return processResult("1; valid backup\n");
    },
  });
  const backupDirectory = join(output, "backup");
  const mediaDirectory = join(backupDirectory, "media");
  const postgresPath = join(backupDirectory, "postgres.dump");
  await mkdir(mediaDirectory, { recursive: true });
  await writeFile(postgresPath, Buffer.alloc(2048, 0x44));
  await writeFile(join(mediaDirectory, "object.bin"), Buffer.alloc(2048, 0x55));
  const postgresSha256 = createHash("sha256").update(Buffer.alloc(2048, 0x44)).digest("hex");
  const mediaSha256 = createHash("sha256")
    .update("object.bin", "utf8")
    .update(Buffer.from([0]))
    .update(Buffer.alloc(2048, 0x55))
    .digest("hex");
  const context = {
    output,
    runId,
    runtime: {
      names,
      primaryResources: { containers: { [names.api]: { id: `sha256:${"8".repeat(64)}`, name: names.api } }, volumes: {} },
    },
    secrets: { postgresPassword: "pg-secret" },
    artifacts: {
        backup: { directory: backupDirectory, postgresPath, mediaDirectory, postgresBytes: 2048, postgresSha256, mediaSha256 },
    },
  };

  await assert.rejects(adapter.destructiveCanary(context), /replaced before destructive removal/i);
  assert.equal(calls.some((args) => args[0] === "rm" && args.includes(names.api)), false);
  await rm(output, { recursive: true, force: true });
});

test("destructive canary refuses changed ownership labels before removing a primary resource", async () => {
  const output = await mkdtemp(join(tmpdir(), "cmsify-release-label-fence-"));
  const names = primaryNames();
  const expectedId = `sha256:${"8".repeat(64)}`;
  const calls = [];
  const adapter = createDockerAdapter({
    repositoryRoot: process.cwd(),
    run: async (_command, args) => {
      calls.push([...args]);
      if (args[0] === "container" && args[1] === "inspect") {
        return processResult(JSON.stringify({
          Id: expectedId,
          Name: `/${names.api}`,
          Config: { Labels: { "io.syntaxcircus.cmsify.release-smoke": "true", "io.syntaxcircus.cmsify.release-smoke-run": "cmsify-smoke-relabelled" } },
        }));
      }
      return processResult("1; valid backup\n");
    },
  });
  const backupDirectory = join(output, "backup");
  const mediaDirectory = join(backupDirectory, "media");
  const postgresPath = join(backupDirectory, "postgres.dump");
  const postgresBytes = Buffer.alloc(2048, 0x67);
  const mediaBytes = Buffer.alloc(2048, 0x68);
  await mkdir(mediaDirectory, { recursive: true });
  await writeFile(postgresPath, postgresBytes);
  await writeFile(join(mediaDirectory, "object.bin"), mediaBytes);
  const context = {
    output,
    runId,
    runtime: {
      names,
      primaryResources: { containers: { [names.api]: { id: expectedId, name: names.api } }, volumes: {} },
    },
    secrets: { postgresPassword: "pg-secret" },
    artifacts: {
      backup: {
        directory: backupDirectory,
        postgresPath,
        mediaDirectory,
        postgresBytes: postgresBytes.length,
        postgresSha256: createHash("sha256").update(postgresBytes).digest("hex"),
        mediaSha256: createHash("sha256").update("object.bin", "utf8").update(Buffer.from([0])).update(mediaBytes).digest("hex"),
      },
    },
  };

  await assert.rejects(adapter.destructiveCanary(context), /ownership labels changed/i);
  assert.equal(calls.some((args) => args[0] === "rm"), false);
  await rm(output, { recursive: true, force: true });
});

test("bounded log capture ignores an aborted scenario signal, redacts secrets, and visits at most sixteen owned containers", async () => {
  const controller = new AbortController();
  controller.abort(new Error("SIGTERM"));
  const calls = [];
  const adapter = createDockerAdapter({
    repositoryRoot: process.cwd(),
    signal: controller.signal,
    run: async (_command, args, options) => {
      calls.push({ args: [...args], options });
      if (args[0] === "ps") return processResult(Array.from({ length: 20 }, (_, index) => `container-${index}`).join("\n"));
      if (args[0] === "logs") return processResult("token=log-secret\n");
      return processResult();
    },
  });
  let written = Buffer.alloc(0);
  const originalWrite = process.stderr.write;
  process.stderr.write = (chunk) => { written = Buffer.concat([written, Buffer.from(chunk)]); return true; };
  try {
    await adapter.captureLogs({ runId, maxLines: 7, maxBytes: 96, secrets: { token: "log-secret" } });
  } finally {
    process.stderr.write = originalWrite;
  }

  const logCalls = calls.filter(({ args }) => args[0] === "logs");
  assert.equal(logCalls.length <= 16, true);
  assert.equal(logCalls.every(({ args, options }) => args[1] === "--tail" && args[2] === "7" && options.signal === undefined), true);
  assert.equal(calls[0].options.signal, undefined);
  assert.equal(written.length <= 96, true);
  assert.equal(written.toString("utf8").includes("log-secret"), false);
  assert.equal(written.toString("utf8").includes("<redacted>"), true);
});

test("cleanup re-inspects and removes only exact labelled container, volume, and network IDs", async () => {
  const calls = [];
  const ids = { container: "container-id", volume: "volume-id", network: "network-id" };
  const adapter = createDockerAdapter({
    repositoryRoot: process.cwd(),
    run: async (_command, args, options) => {
      calls.push({ args: [...args], options });
      if (args[0] === "ps") return processResult(`${ids.container}\n`);
      if (args[0] === "volume" && args[1] === "ls") return processResult(`${ids.volume}\n`);
      if (args[0] === "network" && args[1] === "ls") return processResult(`${ids.network}\n`);
      if (args[0] === "container" && args[1] === "inspect") return processResult(JSON.stringify({
        Name: `/${runId}-api`,
        Config: { Labels: { "io.syntaxcircus.cmsify.release-smoke": "true", "io.syntaxcircus.cmsify.release-smoke-run": runId } },
      }));
      if (args[0] === "volume" && args[1] === "inspect") return processResult(JSON.stringify({
        Name: `${runId}-postgres-data`,
        Labels: { "io.syntaxcircus.cmsify.release-smoke": "true", "io.syntaxcircus.cmsify.release-smoke-run": runId },
      }));
      if (args[0] === "network" && args[1] === "inspect") return processResult(JSON.stringify({
        Name: `${runId}-network`,
        Labels: { "io.syntaxcircus.cmsify.release-smoke": "true", "io.syntaxcircus.cmsify.release-smoke-run": runId },
      }));
      return processResult();
    },
  });

  await adapter.cleanup({ runId });

  assert.ok(calls.some(({ args, options }) => args[0] === "rm" && args.at(-1) === ids.container && options.signal === undefined));
  assert.ok(calls.some(({ args }) => args[0] === "volume" && args[1] === "rm" && args.at(-1) === ids.volume));
  assert.ok(calls.some(({ args }) => args[0] === "network" && args[1] === "rm" && args.at(-1) === ids.network));
});

test("restart and fresh restore preserve candidate IDs, validate archives, and mount only restore volumes", async () => {
  const output = await mkdtemp(join(tmpdir(), "cmsify-release-restore-"));
  const calls = [];
  const apiId = `sha256:${"a".repeat(64)}`;
  const adminId = `sha256:${"b".repeat(64)}`;
  let persistenceChecks = 0;
  const run = async (_command, args) => {
    calls.push([...args]);
    if (args[0] === "container" && args[1] === "inspect" && args.includes("{{.Image}}")) {
      return processResult(args.at(-1).endsWith("-api") ? `${apiId}\n` : `${adminId}\n`);
    }
    if (args[0] === "port") return processResult("127.0.0.1:42000\n");
    if (args.includes("pg_restore") && args.includes("--list")) return processResult("1; valid archive\n");
    return processResult();
  };
  const adapter = createDockerAdapter({ run, repositoryRoot: process.cwd() });
  const names = primaryNames();
  const backupDirectory = join(output, "backup");
  const mediaDirectory = join(backupDirectory, "media");
  const postgresPath = join(backupDirectory, "postgres.dump");
  const tlsDirectory = join(output, ".tls");
  await mkdir(mediaDirectory, { recursive: true });
  await mkdir(tlsDirectory, { recursive: true });
  await writeFile(postgresPath, Buffer.alloc(2048, 0x66));
  const context = {
    output,
    runId,
    runtime: { names, destroyedNames: names, tlsDirectory, adminBase: "https://127.0.0.1:43000" },
    secrets: {
      postgresPassword: "pg-secret", minioAccessKey: "minio-key", minioSecretKey: "minio-secret",
      seedPassword: "seed-password", changedAdminPassword: "changed-password", oidcClientSecret: "oidc-secret", encryptionKey: Buffer.alloc(32).toString("base64"),
    },
    candidates: { api: { imageId: apiId }, admin: { imageId: adminId } },
    artifacts: { backup: { postgresPath, mediaDirectory } },
    verify: async () => { persistenceChecks += 1; },
  };

  await adapter.restartCandidates(context);
  const restored = await adapter.restoreFresh(context);
  await adapter.verifyRestoredState(context);

  assert.equal(persistenceChecks, 2);
  const stop = calls.find((args) => args[0] === "stop" && args.includes(names.api));
  const start = calls.find((args) => args[0] === "start" && args.includes(names.api));
  assert.ok(stop && start);
  const listIndex = calls.findIndex((args) => args.includes("pg_restore") && args.includes("--list") && args.includes("/tmp/release-smoke.dump"));
  const restoreIndex = calls.findIndex((args) => args.includes("pg_restore") && !args.includes("--list"));
  assert.ok(listIndex >= 0 && listIndex < restoreIndex);
  assert.ok(restored.volumes.every((volume) => volume.includes(`${runId}-restore-`)));
  const postgresRun = calls.find((args) => args.includes(`${runId}-restore-postgres`) && args.includes("POSTGRES_DB=cmsify"));
  const minioRun = calls.find((args) => args.includes(`${runId}-restore-minio`) && args.includes("MINIO_ROOT_USER=minio-key"));
  assert.ok(postgresRun.some((value) => value.includes(`source=${runId}-restore-postgres-data`)));
  assert.ok(minioRun.some((value) => value.includes(`source=${runId}-restore-media-data`)));
  const candidateRuns = calls.filter((args) => args[0] === "run" && (args.includes(apiId) || args.includes(adminId)));
  assert.equal(candidateRuns.length, 2);
  assert.ok(candidateRuns.every((args) => args.includes("--pull") && args.includes("never")));
  await rm(output, { recursive: true, force: true });
});
