import { createHash, randomBytes } from "node:crypto";
import { mkdir, readdir, readFile, rm, writeFile } from "node:fs/promises";
import { basename, join, parse, resolve } from "node:path";
import { performance } from "node:perf_hooks";

import {
  RELEASE_SMOKE_SCENARIOS,
  createEvidence,
  writeEvidence,
} from "./evidence.mjs";
import { retryBounded } from "./http.mjs";

export { RELEASE_SMOKE_SCENARIOS };

const POSTGRES_IMAGE = "docker.io/library/postgres:17-alpine@sha256:7456ef82e5f5bc43d997f4781bbd7c0d6389bff397564649a356e206ba473aee";
const MINIO_IMAGE = "docker.io/minio/minio:RELEASE.2025-09-07T16-13-09Z@sha256:a1a8bd4ac40ad7881a245bab97323e18f971e4d4cba2c2007ec1bedd21cbaba2";
const NODE_IMAGE = "docker.io/library/node:22-alpine@sha256:c610fcdfb1d5b4740dd70c284ed3cb16bb857e0f7166196e36a5501df7a3aa32";
const LABEL_SMOKE = "io.syntaxcircus.cmsify.release-smoke";
const LABEL_RUN = "io.syntaxcircus.cmsify.release-smoke-run";
const SOURCE_SHA = /^[0-9a-f]{40}$/;
const SEMVER = /^(?:0|[1-9]\d*)\.(?:0|[1-9]\d*)\.(?:0|[1-9]\d*)(?:-[0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*)?$/;
const RUN_ID = /^cmsify-smoke-[a-z0-9-]{8,32}$/;
const IMAGE_REFERENCE = /^(?=.{3,255}$)(?:[a-z0-9]+(?:(?:[._-]|__)[a-z0-9]+)*(?::[0-9]+)?\/)*(?:[a-z0-9]+(?:(?:[._-]|__)[a-z0-9]+)*):[A-Za-z0-9_][A-Za-z0-9._-]{0,127}$/;

const HELPER_SCRIPT = String.raw`
const http=require('http'),crypto=require('crypto');
const mode=process.env.MODE,issuer=process.env.ISSUER||'http://oidc:8080',clientId=process.env.CLIENT_ID||'cmsify-admin';
let workspace=process.env.WORKSPACE_ID||'',nonce='',events=[];
const {publicKey,privateKey}=crypto.generateKeyPairSync('rsa',{modulusLength:2048});
const jwk=publicKey.export({format:'jwk'});Object.assign(jwk,{kid:'release-smoke',use:'sig',alg:'RS256'});
const b64=v=>Buffer.from(JSON.stringify(v)).toString('base64url');
const token=aud=>{const now=Math.floor(Date.now()/1000),payload={iss:issuer,aud,sub:'99999999-9999-4999-8999-999999999999',iat:now,nbf:now-5,exp:now+600,name:'Release Smoke OIDC',email:'oidc@release-smoke.invalid',cmsify_role:'Admin',cmsify_workspace:workspace};if(nonce&&aud===clientId)payload.nonce=nonce;const input=b64({alg:'RS256',typ:'JWT',kid:jwk.kid})+'.'+b64(payload);return input+'.'+crypto.sign('RSA-SHA256',Buffer.from(input),privateKey).toString('base64url')};
const send=(res,status,body,type='application/json')=>{const data=typeof body==='string'?body:JSON.stringify(body);res.writeHead(status,{'content-type':type,'content-length':Buffer.byteLength(data)});res.end(data)};
const body=req=>new Promise((ok,no)=>{let chunks=[],size=0;req.on('data',c=>{size+=c.length;if(size>1048576){no(new Error('too large'));req.destroy();}else chunks.push(c)});req.on('end',()=>ok(Buffer.concat(chunks).toString()));req.on('error',no)});
http.createServer(async(req,res)=>{try{const u=new URL(req.url,'http://helper');
if(mode==='receiver'){if(req.method==='POST'&&u.pathname==='/hook'){const raw=await body(req);events.push({event:req.headers['x-cmsify-event']||'unknown',bytes:Buffer.byteLength(raw)});return send(res,204,'','text/plain')}if(u.pathname==='/status')return send(res,200,{count:events.length,eventTypes:events.map(x=>x.event)});return send(res,404,{error:'not-found'})}
if(u.pathname==='/.well-known/openid-configuration')return send(res,200,{issuer,authorization_endpoint:issuer+'/authorize',token_endpoint:issuer+'/token',userinfo_endpoint:issuer+'/userinfo',jwks_uri:issuer+'/jwks',end_session_endpoint:issuer+'/logout',response_types_supported:['code'],subject_types_supported:['public'],id_token_signing_alg_values_supported:['RS256'],scopes_supported:['openid','profile','email','offline_access'],token_endpoint_auth_methods_supported:['client_secret_post','client_secret_basic'],claims_supported:['sub','name','email','cmsify_role','cmsify_workspace']});
if(u.pathname==='/jwks')return send(res,200,{keys:[jwk]});
if(u.pathname==='/configure'){workspace=u.searchParams.get('workspaceId')||'';return send(res,200,{configured:Boolean(workspace)})}
if(u.pathname==='/test-token')return send(res,200,{access_token:token('cmsify'),token_type:'Bearer',expires_in:600});
if(u.pathname==='/authorize'){nonce=u.searchParams.get('nonce')||'';const redirect=new URL(u.searchParams.get('redirect_uri'));redirect.searchParams.set('code','release-smoke-code');redirect.searchParams.set('state',u.searchParams.get('state')||'');res.writeHead(302,{location:redirect.toString()});return res.end()}
if(u.pathname==='/token'){await body(req);return send(res,200,{access_token:token('cmsify'),id_token:token(clientId),refresh_token:'release-smoke-refresh',token_type:'Bearer',expires_in:600,scope:'openid profile email offline_access'})}
if(u.pathname==='/userinfo')return send(res,200,{sub:'99999999-9999-4999-8999-999999999999',name:'Release Smoke OIDC',email:'oidc@release-smoke.invalid',cmsify_role:'Admin',cmsify_workspace:workspace});
if(u.pathname==='/logout'){const target=u.searchParams.get('post_logout_redirect_uri')||'/';res.writeHead(302,{location:target});return res.end()}
return send(res,404,{error:'not-found'})}catch{return send(res,500,{error:'helper-failed'})}}).listen(8080,'0.0.0.0');
`;

function assert(condition, message) {
  if (!condition) throw new Error(message);
}

function nowIso(clock) {
  const value = clock();
  assert(typeof value === "string" && Number.isFinite(Date.parse(value)), "Release smoke clock must return a timestamp.");
  return new Date(value).toISOString();
}

function generatedRunId() {
  return `cmsify-smoke-${randomBytes(6).toString("hex")}`;
}

export function validateReleaseOptions(input) {
  assert(input && typeof input === "object" && !Array.isArray(input), "Release smoke options are required.");
  assert(typeof input.apiImage === "string" && IMAGE_REFERENCE.test(input.apiImage), "API image must be a repository:tag reference.");
  assert(typeof input.adminImage === "string" && IMAGE_REFERENCE.test(input.adminImage), "Admin image must be a repository:tag reference.");
  assert(typeof input.version === "string" && SEMVER.test(input.version), "Release smoke version must be valid SemVer without build metadata.");
  assert(typeof input.sourceSha === "string" && SOURCE_SHA.test(input.sourceSha), "Release smoke source SHA must be a full lowercase commit.");
  assert(typeof input.output === "string" && input.output.trim().length > 0 && !/[\0\r\n]/.test(input.output), "Release smoke output directory is required.");
  const output = resolve(input.output);
  assert(output !== parse(output).root, "Release smoke output must not be a filesystem root.");
  const runId = input.runId ?? generatedRunId();
  assert(typeof runId === "string" && RUN_ID.test(runId), "Release smoke run ID is invalid.");
  return Object.freeze({
    apiImage: input.apiImage,
    adminImage: input.adminImage,
    version: input.version,
    sourceSha: input.sourceSha,
    output,
    runId,
  });
}

function unknownCandidates(options) {
  return {
    api: { reference: options.apiImage, imageId: null, digest: null },
    admin: { reference: options.adminImage, imageId: null, digest: null },
  };
}

function registerSignalCleanup(cleanup) {
  let running = false;
  const handler = () => {
    if (running) return;
    running = true;
    void cleanup().finally(() => { running = false; });
  };
  process.once("SIGINT", handler);
  process.once("SIGTERM", handler);
  return () => {
    process.removeListener("SIGINT", handler);
    process.removeListener("SIGTERM", handler);
  };
}

export class ReleaseSmokeFailure extends Error {
  constructor(scenario, evidence) {
    super(`Release smoke failed during ${scenario}.`);
    this.name = "ReleaseSmokeFailure";
    this.scenario = scenario;
    this.evidence = evidence;
  }
}

function scenarioOperation(name, context, docker, http, onFirstResource) {
  switch (name) {
    case "descriptor-label-identity": return docker.inspectCandidates(context);
    case "postgresql-readiness": return docker.prepareFoundation({ ...context, onFirstResource, maxAttempts: 30 });
    case "api-live-ready": return http.waitForApi({ ...context, maxAttempts: 30 });
    case "admin-static-assets": return http.waitForAdmin({ ...context, maxAttempts: 30 });
    case "local-login": return http.localLogin(context);
    case "workspace-api-client-auth": return http.apiClientAuth(context);
    case "template-content-crud-etag": return http.templateContentCrud(context);
    case "media-upload-download": return http.mediaRoundTrip(context);
    case "oidc-api-admin-token-forwarding": return http.oidcFlow(context);
    case "webhook-delivery": return http.webhookDelivery(context);
    case "scheduled-publication": return http.scheduledPublication(context);
    case "graceful-restart-persistence": return docker.restartCandidates({ ...context, verify: http.verifyPersistence?.bind(http) });
    case "matched-backup": return docker.backup(context);
    case "destructive-canary": return docker.destructiveCanary(context);
    case "fresh-restore": return docker.restoreFresh(context);
    case "restored-state-verification": return docker.verifyRestoredState({ ...context, verify: http.verifyRestoredState?.bind(http) });
    default: throw new Error(`Unknown release smoke scenario ${name}.`);
  }
}

export async function certifyRelease(input, dependencies = {}) {
  const options = validateReleaseOptions(input);
  const docker = dependencies.docker;
  const http = dependencies.http;
  assert(docker && typeof docker === "object", "A Docker adapter is required.");
  assert(http && typeof http === "object", "An HTTP adapter is required.");
  const clock = dependencies.now ?? (() => new Date().toISOString());
  const evidenceWriter = dependencies.evidenceWriter ?? ((evidence) => writeEvidence(options.output, evidence));
  const registerCleanup = dependencies.registerCleanup ?? registerSignalCleanup;
  const redactions = [...(dependencies.redactions ?? [])];
  const startedAt = nowIso(clock);
  const states = RELEASE_SMOKE_SCENARIOS.map((name) => ({ name, status: "pending", durationMs: 0 }));
  const context = { ...options, runtime: {}, secrets: {}, artifacts: {}, candidates: unknownCandidates(options) };
  let firstResource = false;
  let unregisterCleanup = () => {};
  let failure;
  let failureScenario;
  let cleanupStatus = "passed";

  const cleanup = () => docker.cleanup({ ...context, runId: options.runId });
  const onFirstResource = () => {
    if (firstResource) return;
    firstResource = true;
    unregisterCleanup = registerCleanup(cleanup);
    assert(typeof unregisterCleanup === "function", "Cleanup registration must return an unregister function.");
  };

  try {
    for (let index = 0; index < RELEASE_SMOKE_SCENARIOS.length; index += 1) {
      const name = RELEASE_SMOKE_SCENARIOS[index];
      const started = performance.now();
      try {
        const value = await scenarioOperation(name, context, docker, http, onFirstResource);
        if (name === "descriptor-label-identity") context.candidates = value;
        else if (name === "postgresql-readiness" && value && typeof value === "object") Object.assign(context.runtime, value.runtime ?? value);
        else if (name === "local-login" && value && typeof value === "object") Object.assign(context.runtime, value);
        else if (name === "workspace-api-client-auth" && value && typeof value === "object") Object.assign(context.runtime, value);
        else if (name === "template-content-crud-etag" && value && typeof value === "object") Object.assign(context.artifacts, value);
        else if (name === "media-upload-download" && value && typeof value === "object") Object.assign(context.artifacts, value);
        else if (name === "scheduled-publication" && value && typeof value === "object") Object.assign(context.artifacts, value);
        else if (name === "matched-backup" && value && typeof value === "object") Object.assign(context.artifacts, { backup: value });
        else if (name === "destructive-canary" && value && typeof value === "object") Object.assign(context.artifacts, { destructiveCanary: value });
        else if (name === "fresh-restore" && value && typeof value === "object") {
          const restoredVolumes = value.volumes;
          assert(Array.isArray(restoredVolumes) && restoredVolumes.length > 0 && restoredVolumes.every((volume) => typeof volume === "string" && volume.length > 0), "Fresh restore must report its target volumes.");
          assert(restoredVolumes.every((volume) => volume.includes(`${options.runId}-restore-`)), "Fresh restore must use run-owned restore volumes.");
          const destroyedVolumes = context.artifacts.destructiveCanary?.volumes;
          if (Array.isArray(destroyedVolumes)) {
            assert(!restoredVolumes.some((volume) => destroyedVolumes.includes(volume)), "Fresh restore reused a destroyed primary data volume.");
          }
          Object.assign(context.artifacts, { freshRestore: value });
        }
        states[index] = { name, status: "passed", durationMs: Math.max(0, Math.round(performance.now() - started)) };
      } catch (error) {
        states[index] = { name, status: "failed", durationMs: Math.max(0, Math.round(performance.now() - started)) };
        failure = error;
        failureScenario = name;
        break;
      }
    }
  } finally {
    if (failure) {
      try {
        await docker.captureLogs({ ...context, maxLines: 200, maxBytes: 256 * 1024 });
      } catch {
        // The primary scenario failure remains authoritative and diagnostics never alter evidence.
      }
    }
    try {
      await cleanup();
    } catch (error) {
      cleanupStatus = "failed";
      if (!failure) {
        failure = error;
        failureScenario = "cleanup";
      }
    } finally {
      unregisterCleanup();
    }
  }

  redactions.push(...Object.values(context.secrets).filter((value) => typeof value === "string"));
  const evidence = createEvidence({
    version: options.version,
    sourceSha: options.sourceSha,
    runId: options.runId,
    candidates: context.candidates,
    startedAt,
    completedAt: nowIso(clock),
    status: failure ? "failed" : "passed",
    scenarios: states,
    backupHashes: context.artifacts.backup
      ? { postgresSha256: context.artifacts.backup.postgresSha256, mediaSha256: context.artifacts.backup.mediaSha256 }
      : null,
    failure: failure ? { error: failure, scenario: failureScenario, redactions } : null,
    cleanup: { status: cleanupStatus },
  });
  await evidenceWriter(evidence);
  if (failure) throw new ReleaseSmokeFailure(failureScenario, evidence);
  return evidence;
}

function resourceNames(runId, restored = false) {
  const prefix = restored ? `${runId}-restore` : runId;
  return Object.freeze({
    network: `${runId}-network`,
    postgres: `${prefix}-postgres`,
    postgresVolume: `${prefix}-postgres-data`,
    minio: `${prefix}-minio`,
    mediaVolume: `${prefix}-media-data`,
    oidc: `${runId}-oidc`,
    receiver: `${runId}-webhook`,
    api: `${prefix}-api`,
    admin: `${prefix}-admin`,
    adminKeysVolume: `${prefix}-admin-keys`,
  });
}

function publicTestSubnet(runId) {
  const digest = createHash("sha256").update(runId).digest();
  const third = 101 + (digest[0] % 100);
  return Object.freeze({ subnet: `198.51.${third}.0/28`, receiverIp: `198.51.${third}.6` });
}

function labels(runId) {
  return ["--label", `${LABEL_SMOKE}=true`, "--label", `${LABEL_RUN}=${runId}`];
}

function environmentArgs(environment) {
  return Object.entries(environment).flatMap(([name, value]) => ["--env", `${name}=${value}`]);
}

function parseMappedPort(stdout) {
  const match = String(stdout).trim().match(/(?:127\.0\.0\.1|0\.0\.0\.0|\[::\]):(\d+)$/m);
  assert(match && Number(match[1]) >= 1 && Number(match[1]) <= 65535, "Docker did not return a mapped host port.");
  return Number(match[1]);
}

function imageDigest(inspected, reference) {
  const repository = reference.slice(0, reference.lastIndexOf(":"));
  const match = inspected.RepoDigests?.find((value) => value.startsWith(`${repository}@sha256:`))
    ?? inspected.RepoDigests?.find((value) => /@sha256:[0-9a-f]{64}$/.test(value));
  return match?.split("@").at(-1) ?? inspected.Id;
}

async function inspectCandidate(execute, reference, expected) {
  const result = await execute(["image", "inspect", "--format", "{{json .}}", reference], "candidate-image-inspect");
  let inspected;
  try { inspected = JSON.parse(String(result.stdout).trim()); } catch { throw new Error("Candidate image inspection returned invalid JSON."); }
  assert(inspected?.Os === "linux" && inspected?.Architecture === "amd64", "Candidate image must be linux/amd64.");
  assert(typeof inspected.Id === "string" && /^sha256:[0-9a-f]{64}$/.test(inspected.Id), "Candidate image ID is invalid.");
  assert(inspected.Config?.Labels?.["org.opencontainers.image.version"] === expected.version, "Candidate OCI version label mismatch.");
  assert(inspected.Config?.Labels?.["org.opencontainers.image.revision"] === expected.sourceSha, "Candidate OCI revision label mismatch.");
  return Object.freeze({
    reference,
    imageId: inspected.Id,
    digest: imageDigest(inspected, reference),
    version: expected.version,
    sourceSha: expected.sourceSha,
  });
}

function generatedSecrets() {
  return Object.freeze({
    postgresPassword: `pg-${randomBytes(18).toString("base64url")}`,
    minioAccessKey: `smoke${randomBytes(8).toString("hex")}`,
    minioSecretKey: randomBytes(24).toString("base64url"),
    seedPassword: `seed-${randomBytes(18).toString("base64url")}`,
    oidcClientSecret: randomBytes(24).toString("base64url"),
    encryptionKey: randomBytes(32).toString("base64"),
  });
}

function asText(result) {
  return Buffer.isBuffer(result.stdout) ? result.stdout.toString("utf8") : String(result.stdout ?? "");
}

export function createDockerAdapter({ run, repositoryRoot }) {
  assert(typeof run === "function", "Docker adapter requires a process runner.");
  assert(typeof repositoryRoot === "string" && repositoryRoot.length > 0, "Docker adapter requires a repository root.");
  const root = resolve(repositoryRoot);
  const execute = (args, phase, options = {}) => run("docker", args, {
    cwd: root,
    timeoutMs: options.timeoutMs ?? 120_000,
    phase: `release-smoke:${phase}`,
    ...(options.redact ? { redact: options.redact } : {}),
    ...(options.stdoutEncoding ? { stdoutEncoding: options.stdoutEncoding } : {}),
    ...(options.stdin !== undefined ? { stdin: options.stdin } : {}),
  });

  async function mappedBase(name) {
    const result = await execute(["port", name, "8080/tcp"], "mapped-port");
    return `http://127.0.0.1:${parseMappedPort(asText(result))}`;
  }

  async function waitForPostgres(name, password, maxAttempts) {
    await retryBounded(() => execute([
      "exec", "--env", `PGPASSWORD=${password}`, name,
      "pg_isready", "--username", "cmsify", "--dbname", "cmsify",
    ], "postgres-readiness", { timeoutMs: 10_000, redact: [password] }), { maxAttempts, delayMs: 2_000 });
  }

  async function waitForMinio(name, accessKey, secretKey, maxAttempts) {
    await retryBounded(async () => {
      await execute(["exec", name, "mc", "alias", "set", "smoke", "http://127.0.0.1:9000", accessKey, secretKey], "minio-alias", { redact: [accessKey, secretKey] });
      return execute(["exec", name, "mc", "ready", "smoke"], "minio-readiness", { timeoutMs: 10_000, redact: [accessKey, secretKey] });
    }, { maxAttempts, delayMs: 2_000 });
  }

  async function createVolume(name, runId) {
    await execute(["volume", "create", ...labels(runId), name], "volume-create");
  }

  async function runPostgres(context, names, volume) {
    await execute(["run", "--detach", "--name", names.postgres, "--network", names.network, "--network-alias", "postgres",
      ...labels(context.runId), "--env", "POSTGRES_DB=cmsify", "--env", "POSTGRES_USER=cmsify",
      "--env", `POSTGRES_PASSWORD=${context.secrets.postgresPassword}`, "--mount", `type=volume,source=${volume},target=/var/lib/postgresql/data`,
      POSTGRES_IMAGE], "postgres-run", { redact: [context.secrets.postgresPassword] });
  }

  async function runMinio(context, names, volume) {
    await execute(["run", "--detach", "--name", names.minio, "--network", names.network, "--network-alias", "minio",
      ...labels(context.runId), "--env", `MINIO_ROOT_USER=${context.secrets.minioAccessKey}`, "--env", `MINIO_ROOT_PASSWORD=${context.secrets.minioSecretKey}`,
      "--mount", `type=volume,source=${volume},target=/data`, MINIO_IMAGE, "server", "/data", "--address", ":9000"], "minio-run",
    { redact: [context.secrets.minioAccessKey, context.secrets.minioSecretKey] });
  }

  async function startCandidates(context, { restored = false } = {}) {
    const names = resourceNames(context.runId, restored);
    context.secrets ??= {};
    context.runtime ??= {};
    const secrets = Object.keys(context.secrets).length > 0 ? context.secrets : generatedSecrets();
    Object.assign(context.secrets, secrets);
    const common = ["run", "--detach", "--pull", "never", "--platform", "linux/amd64", "--network", names.network, ...labels(context.runId), "--publish", "127.0.0.1::8080"];
    const apiEnvironment = {
      ASPNETCORE_ENVIRONMENT: "Development",
      ASPNETCORE_URLS: "http://+:8080",
      ConnectionStrings__Cmsify: `Host=postgres;Port=5432;Database=cmsify;Username=cmsify;Password=${secrets.postgresPassword}`,
      Seed__DefaultWorkspace__Name: "Release Smoke Workspace",
      Seed__DefaultWorkspace__Slug: "release-smoke",
      Seed__Admin__Email: "admin@release-smoke.invalid",
      Seed__Admin__DisplayName: "Release Smoke Admin",
      Seed__Admin__Password: secrets.seedPassword,
      Auth__BcryptCost: "4",
      Auth__Oidc__Enabled: "true",
      Auth__Oidc__Authority: `http://${resourceNames(context.runId).oidc}:8080`,
      Auth__Oidc__RequireHttpsMetadata: "false",
      Auth__Oidc__Audiences__0: "cmsify",
      Auth__Oidc__ClaimsMapping__Role: "cmsify_role",
      Auth__Oidc__ClaimsMapping__WorkspaceId: "cmsify_workspace",
      Storage__Provider: "s3",
      Storage__S3__BucketName: "cmsify",
      Storage__S3__Region: "us-east-1",
      Storage__S3__AccessKey: secrets.minioAccessKey,
      Storage__S3__SecretKey: secrets.minioSecretKey,
      Storage__S3__ServiceUrl: "http://minio:9000",
      Storage__S3__ForcePathStyle: "true",
      Secrets__ActiveKeyId: "release_smoke",
      Secrets__EncryptionKeys__release_smoke: secrets.encryptionKey,
      Webhook__AllowHttp: "true",
      Webhook__OutboxPollIntervalSeconds: "1",
      Webhook__RetryIntervalSeconds: "1",
      Webhook__RequestTimeoutSeconds: "5",
      Scheduler__PublishingIntervalSeconds: "1",
      Scheduler__PublishingLeaseDurationSeconds: "15",
      Media__Operations__ReconciliationIntervalSeconds: "60",
      TrustedProxy__RequireTrustedProxiesInProduction: "false",
    };
    const adminEnvironment = {
      ASPNETCORE_ENVIRONMENT: "Development",
      ASPNETCORE_URLS: "http://+:8080",
      Admin__ApiBaseUrl: "http://api:8080",
      Admin__DataProtection__KeysPath: "/var/cmsify/admin-keys",
      Auth__Oidc__Enabled: "true",
      Auth__Oidc__Authority: `http://${resourceNames(context.runId).oidc}:8080`,
      Auth__Oidc__ClientId: "cmsify-admin",
      Auth__Oidc__ClientSecret: secrets.oidcClientSecret,
      Auth__Oidc__RequireHttpsMetadata: "false",
      Auth__Oidc__ClaimsMapping__Role: "cmsify_role",
    };
    await createVolume(names.adminKeysVolume, context.runId);
    await execute([...common, "--name", names.api, "--network-alias", "api", ...environmentArgs(apiEnvironment), context.candidates.api.imageId], "api-run", { redact: Object.values(secrets) });
    await execute([...common, "--name", names.admin, "--network-alias", "admin", "--mount", `type=volume,source=${names.adminKeysVolume},target=/var/cmsify/admin-keys`, ...environmentArgs(adminEnvironment), context.candidates.admin.imageId], "admin-run", { redact: Object.values(secrets) });
    context.runtime.names = names;
    context.runtime.apiBase = await mappedBase(names.api);
    context.runtime.adminBase = await mappedBase(names.admin);
    return context.runtime;
  }

  async function startHelpers(context, names, receiverIp) {
    const issuer = `http://${names.oidc}:8080`;
    await execute(["run", "--detach", "--name", names.receiver, "--network", names.network, "--network-alias", "webhook", "--ip", receiverIp,
      ...labels(context.runId), "--publish", "127.0.0.1::8080", "--env", "MODE=receiver", NODE_IMAGE, "node", "--eval", HELPER_SCRIPT], "webhook-helper-run");
    await execute(["run", "--detach", "--name", names.oidc, "--network", names.network, "--network-alias", "oidc",
      ...labels(context.runId), "--publish", "127.0.0.1::8080", "--env", "MODE=oidc", "--env", `ISSUER=${issuer}`, "--env", "CLIENT_ID=cmsify-admin",
      NODE_IMAGE, "node", "--eval", HELPER_SCRIPT], "oidc-helper-run");
    context.runtime.oidcBase = await mappedBase(names.oidc);
    context.runtime.webhookBase = await mappedBase(names.receiver);
    context.runtime.webhookIp = receiverIp;
  }

  async function inspectContainerImage(name) {
    const result = await execute(["container", "inspect", "--format", "{{.Image}}", name], "container-image-inspect");
    return asText(result).trim();
  }

  return Object.freeze({
    async inspectCandidates(options) {
      const api = await inspectCandidate(execute, options.apiImage, options);
      const admin = await inspectCandidate(execute, options.adminImage, options);
      return Object.freeze({ api, admin });
    },

    startCandidates,

    async prepareFoundation(context) {
      Object.assign(context.secrets, generatedSecrets());
      const names = resourceNames(context.runId);
      const subnet = publicTestSubnet(context.runId);
      await execute(["network", "create", ...labels(context.runId), "--subnet", subnet.subnet, names.network], "network-create");
      context.onFirstResource();
      await createVolume(names.postgresVolume, context.runId);
      await createVolume(names.mediaVolume, context.runId);
      await runPostgres(context, names, names.postgresVolume);
      await runMinio(context, names, names.mediaVolume);
      await startHelpers(context, names, subnet.receiverIp);
      await waitForPostgres(names.postgres, context.secrets.postgresPassword, context.maxAttempts);
      await waitForMinio(names.minio, context.secrets.minioAccessKey, context.secrets.minioSecretKey, context.maxAttempts);
      await execute(["exec", names.minio, "mc", "mb", "--ignore-existing", "smoke/cmsify"], "minio-bucket-create", { redact: Object.values(context.secrets) });
      context.runtime.names = names;
      await startCandidates(context, { restored: false });
      return { runtime: context.runtime, attempts: context.maxAttempts };
    },

    async restartCandidates(context) {
      const names = context.runtime.names;
      const before = {
        api: await inspectContainerImage(names.api),
        admin: await inspectContainerImage(names.admin),
      };
      assert(before.api === context.candidates.api.imageId && before.admin === context.candidates.admin.imageId, "Running candidate image identity changed before restart.");
      await execute(["stop", "--time", "20", names.api, names.admin], "candidate-stop");
      await execute(["start", names.api, names.admin], "candidate-start");
      const after = {
        api: await inspectContainerImage(names.api),
        admin: await inspectContainerImage(names.admin),
      };
      assert(after.api === before.api && after.admin === before.admin, "Candidate restart did not preserve exact image identity.");
      if (context.verify) await context.verify(context);
      return after;
    },

    async backup(context) {
      const names = context.runtime.names;
      const backupDirectory = resolve(context.output, "backup");
      const mediaDirectory = resolve(backupDirectory, "media");
      const postgresPath = resolve(backupDirectory, "postgres.dump");
      await rm(backupDirectory, { recursive: true, force: true });
      await mkdir(mediaDirectory, { recursive: true });
      await execute(["stop", "--time", "20", names.api, names.admin, names.minio], "backup-quiesce");
      try {
        const dump = await execute(["exec", "--env", `PGPASSWORD=${context.secrets.postgresPassword}`, names.postgres,
          "pg_dump", "--username", "cmsify", "--dbname", "cmsify", "--format", "custom", "--no-owner"], "postgres-backup",
        { stdoutEncoding: "buffer", redact: [context.secrets.postgresPassword] });
        const bytes = Buffer.isBuffer(dump.stdout) ? dump.stdout : Buffer.from(dump.stdout);
        await writeFile(postgresPath, bytes, { mode: 0o600 });
        await execute(["cp", `${names.minio}:/data/.`, mediaDirectory], "media-backup");
      } finally {
        await execute(["start", names.minio, names.api, names.admin], "backup-resume");
      }
      return Object.freeze({
        directory: backupDirectory,
        postgresPath,
        mediaDirectory,
        postgresSha256: createHash("sha256").update(await readFile(postgresPath)).digest("hex"),
        mediaSha256: await hashDirectory(mediaDirectory),
      });
    },

    async destructiveCanary(context) {
      assert(context.artifacts.backup?.postgresSha256 && context.artifacts.backup?.mediaSha256, "A matched backup is required before destructive canary execution.");
      const names = context.runtime.names;
      await execute(["rm", "--force", names.api, names.admin, names.postgres, names.minio], "destructive-canary-containers");
      await execute(["volume", "rm", names.postgresVolume, names.mediaVolume, names.adminKeysVolume], "destructive-canary-volumes");
      context.runtime.destroyedNames = names;
      return { destroyed: true, volumes: [names.postgresVolume, names.mediaVolume, names.adminKeysVolume] };
    },

    async restoreFresh(context) {
      assert(context.runtime.destroyedNames, "Destructive canary must complete before restore.");
      const old = context.runtime.destroyedNames;
      const names = resourceNames(context.runId, true);
      assert(names.postgresVolume !== old.postgresVolume && names.mediaVolume !== old.mediaVolume, "Restore target volumes must be fresh.");
      await createVolume(names.postgresVolume, context.runId);
      await createVolume(names.mediaVolume, context.runId);
      await runPostgres(context, names, names.postgresVolume);
      await runMinio(context, names, names.mediaVolume);
      await waitForPostgres(names.postgres, context.secrets.postgresPassword, 30);
      await waitForMinio(names.minio, context.secrets.minioAccessKey, context.secrets.minioSecretKey, 30);
      await execute(["cp", context.artifacts.backup.postgresPath, `${names.postgres}:/tmp/release-smoke.dump`], "postgres-restore-copy");
      await execute(["exec", "--env", `PGPASSWORD=${context.secrets.postgresPassword}`, names.postgres,
        "pg_restore", "--username", "cmsify", "--dbname", "cmsify", "--no-owner", "/tmp/release-smoke.dump"], "postgres-restore", { redact: [context.secrets.postgresPassword] });
      await execute(["stop", "--time", "20", names.minio], "media-restore-stop");
      await execute(["cp", `${context.artifacts.backup.mediaDirectory}${process.platform === "win32" ? "\\." : "/."}`, `${names.minio}:/data`], "media-restore-copy");
      await execute(["start", names.minio], "media-restore-start");
      context.runtime.names = names;
      await startCandidates(context, { restored: true });
      return { volumes: [names.postgresVolume, names.mediaVolume] };
    },

    async verifyRestoredState(context) {
      const names = context.runtime.names;
      assert(names.postgres.includes("-restore-") && names.postgresVolume.includes("-restore-"), "Restored-state verification must target fresh resources.");
      assert(await inspectContainerImage(names.api) === context.candidates.api.imageId, "Restored API did not use the exact candidate image.");
      assert(await inspectContainerImage(names.admin) === context.candidates.admin.imageId, "Restored Admin did not use the exact candidate image.");
      if (context.verify) await context.verify(context);
      return { restored: true };
    },

    async captureLogs(context) {
      const filter = ["--filter", `label=${LABEL_SMOKE}=true`, "--filter", `label=${LABEL_RUN}=${context.runId}`];
      let discovered;
      try { discovered = await execute(["ps", "--all", "--quiet", ...filter], "logs-discover", { timeoutMs: 15_000 }); } catch { return; }
      const ids = asText(discovered).split(/\r?\n/).filter(Boolean).slice(0, 16);
      let remaining = context.maxBytes;
      for (const id of ids) {
        if (remaining <= 0) break;
        try {
          const result = await execute(["logs", "--tail", String(context.maxLines), id], "logs-read", { timeoutMs: 15_000, redact: Object.values(context.secrets) });
          let output = `${asText(result)}\n${String(result.stderr ?? "")}`;
          for (const secret of Object.values(context.secrets)) if (secret) output = output.split(secret).join("<redacted>");
          const bytes = Buffer.from(output).subarray(0, remaining);
          remaining -= bytes.length;
          process.stderr.write(bytes);
        } catch {
          // Continue collecting other bounded container logs.
        }
      }
    },

    async cleanup(context) {
      assert(RUN_ID.test(context.runId), "Cleanup requires a validated release smoke run ID.");
      const filter = ["--filter", `label=${LABEL_SMOKE}=true`, "--filter", `label=${LABEL_RUN}=${context.runId}`];
      const kinds = [
        { list: ["ps", "--all", "--quiet"], inspect: ["container", "inspect", "--format", "{{json .}}"], remove: ["rm", "--force"], name: (item) => String(item.Name ?? "").replace(/^\//, "") },
        { list: ["volume", "ls", "--quiet"], inspect: ["volume", "inspect", "--format", "{{json .}}"], remove: ["volume", "rm"], name: (item) => item.Name },
        { list: ["network", "ls", "--quiet"], inspect: ["network", "inspect", "--format", "{{json .}}"], remove: ["network", "rm"], name: (item) => item.Name },
      ];
      const failures = [];
      for (const kind of kinds) {
        let ids = [];
        try {
          const listed = await execute([...kind.list, ...filter], "cleanup-discover", { timeoutMs: 30_000 });
          ids = asText(listed).split(/\r?\n/).filter(Boolean).slice(0, 64);
        } catch (error) { failures.push(error); continue; }
        for (const id of ids) {
          try {
            const result = await execute([...kind.inspect, id], "cleanup-inspect", { timeoutMs: 15_000 });
            const inspected = JSON.parse(asText(result).trim());
            const resourceLabels = inspected.Config?.Labels ?? inspected.Labels;
            const name = kind.name(inspected);
            assert(resourceLabels?.[LABEL_SMOKE] === "true" && resourceLabels?.[LABEL_RUN] === context.runId, "Cleanup discovered a resource without exact ownership labels.");
            assert(typeof name === "string" && name.startsWith(`${context.runId}-`), "Cleanup discovered a resource outside the validated run scope.");
            await execute([...kind.remove, id], "cleanup-remove", { timeoutMs: 30_000 });
          } catch (error) { failures.push(error); }
        }
      }
      if (failures.length === 1) throw failures[0];
      if (failures.length > 1) throw new AggregateError(failures, "Release smoke cleanup failed.");
    },
  });
}

async function hashDirectory(directory) {
  const hash = createHash("sha256");
  async function visit(path, relativePath) {
    const entries = await readdir(path, { withFileTypes: true });
    entries.sort((left, right) => left.name.localeCompare(right.name));
    for (const entry of entries) {
      const entryPath = join(path, entry.name);
      const relative = relativePath ? `${relativePath}/${entry.name}` : entry.name;
      if (entry.isDirectory()) await visit(entryPath, relative);
      else if (entry.isFile()) {
        hash.update(relative, "utf8");
        hash.update(Buffer.from([0]));
        hash.update(await readFile(entryPath));
      } else throw new Error(`Media backup contains unsupported entry ${basename(entryPath)}.`);
    }
  }
  await visit(directory, "");
  return hash.digest("hex");
}
