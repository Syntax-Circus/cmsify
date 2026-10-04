import { spawnSync } from 'node:child_process';
import { createHash } from 'node:crypto';
import { copyFileSync, existsSync, mkdirSync, mkdtempSync, readFileSync, rmSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { dirname, join, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
import { setTimeout as delay } from 'node:timers/promises';
import { enginePackageIds, validateGraph } from './engine-package-graph.mjs';

const args = process.argv.slice(2);
function option(name) {
  const index = args.indexOf(name);
  return index < 0 ? undefined : args[index + 1];
}
const version = option('--version');
const sourceSha = option('--source-sha');
const packages = resolve(option('--packages') ?? 'artifacts/nuget');
if (!version || !/^\d+\.\d+\.\d+(?:-[0-9A-Za-z.-]+)?$/.test(version)
    || (sourceSha && !/^[a-f0-9]{40}$/i.test(sourceSha))) {
  throw new Error('Usage: node scripts/release/verify-engine-packages.mjs --version X.Y.Z[-suffix] [--packages directory] [--source-sha SHA] [--restore-only]');
}
const fixture = resolve(dirname(fileURLToPath(import.meta.url)), '../../tests/package-consumer');
const licenseHash = createHash('sha256').update(readFileSync(resolve(fixture, '../../LICENSE'))).digest('hex');
// Outside the checkout: no Directory.Build.*, Directory.Packages.props, source references or repo NuGet settings.
const staging = mkdtempSync(join(tmpdir(), 'cmsify-engine-consumer-'));
const feed = join(staging, 'feed');
const feedXml = feed.replaceAll('&', '&amp;').replaceAll('"', '&quot;').replaceAll('<', '&lt;');
const container = `cmsify-engine-consumer-${process.pid}-${Date.now()}`;
let containerStarted = false;
const env = {
  ...process.env,
  NUGET_PACKAGES: join(staging, 'packages'),
  NUGET_HTTP_CACHE_PATH: join(staging, 'http-cache'),
  DOTNET_CLI_HOME: join(staging, 'dotnet-home'),
  DOTNET_NOLOGO: '1',
};
let consumerDirectory = staging;
let consumerEnv = env;
function run(command, commandArgs, { capture = false, extraEnv = {} } = {}) {
  const result = spawnSync(command, commandArgs, {
    cwd: consumerDirectory, env: { ...consumerEnv, ...extraEnv }, encoding: 'utf8',
    stdio: capture ? 'pipe' : 'inherit', timeout: 600_000,
  });
  if (result.error || result.status !== 0) {
    if (capture) process.stderr.write((result.stdout ?? '') + (result.stderr ?? ''));
    throw new Error(`${command} ${commandArgs[0]} failed (${result.status ?? result.error?.message})`);
  }
  return result.stdout?.trim();
}
try {
  mkdirSync(feed);
  for (const id of enginePackageIds) {
    const file = `${id}.${version}.nupkg`;
    // Empty local feed deliberately yields NU1101 when an engine package is absent.
    if (existsSync(join(packages, file))) copyFileSync(join(packages, file), join(feed, file));
  }
  for (const sqlite of [false, true]) {
  const consumerFixture = sqlite ? `${fixture}-sqlite` : fixture;
  const project = sqlite ? 'SqliteEngineConsumer.csproj' : 'EngineConsumer.csproj';
  consumerDirectory = join(staging, sqlite ? 'sqlite' : 'postgres');
  mkdirSync(consumerDirectory);
  consumerEnv = { ...env, NUGET_PACKAGES: join(consumerDirectory, 'packages'), NUGET_HTTP_CACHE_PATH: join(consumerDirectory, 'http-cache'), NUGET_SCRATCH: join(consumerDirectory, 'scratch'), DOTNET_CLI_HOME: join(consumerDirectory, 'dotnet-home') };
  copyFileSync(join(consumerFixture, 'Program.cs'), join(consumerDirectory, 'Program.cs'));
  copyFileSync(join(fixture, 'ContentQueryQualification.cs'), join(consumerDirectory, 'ContentQueryQualification.cs'));
  writeFileSync(join(consumerDirectory, project),
    readFileSync(join(consumerFixture, project), 'utf8').replaceAll('__PACKAGE_VERSION__', version));
  writeFileSync(join(consumerDirectory, 'NuGet.Config'), `<?xml version="1.0" encoding="utf-8"?>
<configuration>
  <packageSources><clear/><add key="candidate" value="${feedXml}"/><add key="public" value="https://api.nuget.org/v3/index.json"/></packageSources>
  <packageSourceMapping>
    <packageSource key="candidate">${enginePackageIds.map(id => `<package pattern="${id}"/>`).join('')}</packageSource>
    <packageSource key="public"><package pattern="*"/></packageSource>
  </packageSourceMapping>
  <fallbackPackageFolders><clear/></fallbackPackageFolders>
</configuration>`);
  console.log(`Restoring ${sqlite ? 'SQLite' : 'PostgreSQL'} engine ${version} packages in an isolated consumer with an empty cache.`);
  run('dotnet', ['restore', project, '--configfile', 'NuGet.Config', '--no-cache']);
  const assets = JSON.parse(readFileSync(join(consumerDirectory, 'obj/project.assets.json'), 'utf8'));
  validateGraph(assets, version, sqlite);
  if (!args.includes('--restore-only')) {
    run('dotnet', ['build', project, '--configuration', 'Release', '--no-restore']);
    let extraEnv = {};
    if (!sqlite && process.env.CMSIFY_CONSUMER_POSTGRES) {
      // Caller owns this fixture; never create or remove Docker resources on this path.
      extraEnv = { CMSIFY_CONSUMER_POSTGRES: process.env.CMSIFY_CONSUMER_POSTGRES };
    } else if (!sqlite) {
    run('docker', ['run', '--detach', '--rm', '--name', container, '--publish', '127.0.0.1::5432',
      '--env', 'POSTGRES_DB=cmsify_consumer', '--env', 'POSTGRES_USER=cmsify',
      '--env', 'POSTGRES_PASSWORD=package-test-only', 'postgres:17-alpine']);
    containerStarted = true;
    let ready = false;
    for (let attempt = 0; attempt < 60; attempt++) {
      const probe = spawnSync('docker', ['exec', container, 'pg_isready', '-U', 'cmsify', '-d', 'cmsify_consumer'], { stdio: 'ignore', timeout: 10_000 });
      if (probe.status === 0) { ready = true; break; }
      await delay(1000);
    }
    if (!ready) throw new Error('Disposable PostgreSQL did not become ready in 60 seconds.');
    const port = run('docker', ['port', container, '5432/tcp'], { capture: true }).split(':').at(-1);
    extraEnv = { CMSIFY_CONSUMER_POSTGRES: `Host=127.0.0.1;Port=${port};Database=cmsify_consumer;Username=cmsify;Password=package-test-only` };
    }
    run('dotnet', ['run', '--project', project, '--configuration', 'Release', '--no-build', '--no-restore',
      '--', feed, version, sourceSha ?? '', licenseHash], { extraEnv });
  }
  }
  console.log(`PASS isolated engine package ${args.includes('--restore-only') ? 'restore' : 'qualification'}.`);
} finally {
  if (containerStarted) {
    const result = spawnSync('docker', ['rm', '--force', container], { stdio: 'inherit', timeout: 30_000 });
    if (result.status !== 0) process.exitCode = 1;
  }
  // Only this invocation's mkdtemp directory is removed.
  rmSync(staging, { recursive: true, force: true });
}
