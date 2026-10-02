import { spawnSync } from 'node:child_process';
import { createHash } from 'node:crypto';
import { copyFileSync, existsSync, mkdirSync, mkdtempSync, readFileSync, rmSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { dirname, join, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
import { setTimeout as delay } from 'node:timers/promises';

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
function run(command, commandArgs, { capture = false, extraEnv = {} } = {}) {
  const result = spawnSync(command, commandArgs, {
    cwd: staging, env: { ...env, ...extraEnv }, encoding: 'utf8',
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
  for (const id of ['SyntaxCircus.Cmsify.Core', 'SyntaxCircus.Cmsify.Infrastructure']) {
    const file = `${id}.${version}.nupkg`;
    // Empty local feed deliberately yields NU1101 when an engine package is absent.
    if (existsSync(join(packages, file))) copyFileSync(join(packages, file), join(feed, file));
  }
  copyFileSync(join(fixture, 'Program.cs'), join(staging, 'Program.cs'));
  writeFileSync(join(staging, 'EngineConsumer.csproj'),
    readFileSync(join(fixture, 'EngineConsumer.csproj'), 'utf8').replaceAll('__PACKAGE_VERSION__', version));
  writeFileSync(join(staging, 'NuGet.Config'), `<?xml version="1.0" encoding="utf-8"?>
<configuration>
  <packageSources><clear/><add key="candidate" value="${feedXml}"/><add key="public" value="https://api.nuget.org/v3/index.json"/></packageSources>
  <packageSourceMapping>
    <packageSource key="candidate"><package pattern="SyntaxCircus.Cmsify.Core"/><package pattern="SyntaxCircus.Cmsify.Infrastructure"/></packageSource>
    <packageSource key="public"><package pattern="*"/></packageSource>
  </packageSourceMapping>
  <fallbackPackageFolders><clear/></fallbackPackageFolders>
</configuration>`);
  console.log(`Restoring engine ${version} packages in an isolated consumer with an empty cache.`);
  run('dotnet', ['restore', 'EngineConsumer.csproj', '--configfile', 'NuGet.Config', '--no-cache']);
  const assets = JSON.parse(readFileSync(join(staging, 'obj/project.assets.json'), 'utf8'));
  for (const id of ['SyntaxCircus.Cmsify.Core', 'SyntaxCircus.Cmsify.Infrastructure']) {
    if (assets.libraries[`${id}/${version}`]?.type !== 'package') throw new Error(`Consumer did not resolve ${id}/${version} as a package.`);
  }
  if (Object.values(assets.libraries).some(library => library.type === 'project')) throw new Error('Consumer unexpectedly resolved a project reference.');
  if (!args.includes('--restore-only')) {
    run('dotnet', ['build', 'EngineConsumer.csproj', '--configuration', 'Release', '--no-restore']);
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
    run('dotnet', ['run', '--project', 'EngineConsumer.csproj', '--configuration', 'Release', '--no-build', '--no-restore',
      '--', feed, version, sourceSha ?? '', licenseHash], { extraEnv: {
      CMSIFY_CONSUMER_POSTGRES: `Host=127.0.0.1;Port=${port};Database=cmsify_consumer;Username=cmsify;Password=package-test-only`,
    } });
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
