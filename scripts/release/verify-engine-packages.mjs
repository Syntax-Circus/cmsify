import { spawnSync } from 'node:child_process';
import { createHash, randomBytes } from 'node:crypto';
import { copyFileSync, existsSync, mkdirSync, mkdtempSync, readFileSync, rmSync, writeFileSync } from 'node:fs';
import { tmpdir, userInfo } from 'node:os';
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
const evidence = option('--evidence') ? resolve(option('--evidence')) : undefined;
if (evidence && existsSync(evidence)) throw new Error('Evidence destination must be fresh; existing evidence will not be overwritten.');
if (process.env.CMSIFY_CONSUMER_POSTGRES && process.env.CMSIFY_CONSUMER_POSTGRES_DISPOSABLE !== '1') {
  throw new Error('External PostgreSQL must be explicitly disposable and caller-owned; verify its endpoint and ownership before setting CMSIFY_CONSUMER_POSTGRES_DISPOSABLE=1.');
}
if (process.env.CMSIFY_CONSUMER_POSTGRES && !process.env.CMSIFY_CONSUMER_POSTGRES_PROTECTED_PASSWORD) {
  throw new Error('External PostgreSQL requires CMSIFY_CONSUMER_POSTGRES_PROTECTED_PASSWORD containing its plaintext password; supply it privately so scalar child output can be withheld.');
}
if (!version || !/^\d+\.\d+\.\d+(?:-[0-9A-Za-z.-]+)?$/.test(version)
    || (sourceSha && !/^[a-f0-9]{40}$/i.test(sourceSha))) {
  throw new Error('Usage: node scripts/release/verify-engine-packages.mjs --version X.Y.Z[-suffix] [--packages directory] [--source-sha SHA] [--restore-only] [--evidence fresh-directory]');
}
if (evidence) mkdirSync(evidence, { recursive: true });
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
let logSequence = 0;
// The caller supplies the scalar separately: connection-string quoting and aliases belong to Npgsql.
const protectedValues = process.env.CMSIFY_CONSUMER_POSTGRES
  ? [process.env.CMSIFY_CONSUMER_POSTGRES, process.env.CMSIFY_CONSUMER_POSTGRES_PROTECTED_PASSWORD] : [];
function run(command, commandArgs, { capture = false, extraEnv = {} } = {}) {
  const result = spawnSync(command, commandArgs, {
    cwd: consumerDirectory, env: { ...consumerEnv, ...extraEnv }, encoding: 'utf8',
    stdio: 'pipe', timeout: 600_000,
  });
  const output = (result.stdout ?? '') + (result.stderr ?? '');
  if (protectedValues.some(value => output.includes(value))) {
    throw new Error('Qualification output contained a protected fixture input; output withheld.');
  }
  if (evidence) writeFileSync(join(evidence, `${String(++logSequence).padStart(3, '0')}-${command}-${commandArgs[0].replaceAll(/[^a-zA-Z0-9.-]/g, '_')}.log`), output);
  if (!capture) {
    process.stdout.write(result.stdout ?? '');
    process.stderr.write(result.stderr ?? '');
  }
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
  copyFileSync(join(fixture, 'WorkspaceVisibilityQualification.cs'), join(consumerDirectory, 'WorkspaceVisibilityQualification.cs'));
  if (!sqlite) copyFileSync(join(fixture, 'EmbeddedContentQualification.cs'), join(consumerDirectory, 'EmbeddedContentQualification.cs'));
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
  const sdk = run('dotnet', ['--version'], { capture: true });
  run('dotnet', ['restore', project, '--configfile', 'NuGet.Config', '--no-cache', '--use-lock-file']);
  run('dotnet', ['restore', project, '--configfile', 'NuGet.Config', '--no-cache', '--locked-mode']);
  const assets = JSON.parse(readFileSync(join(consumerDirectory, 'obj/project.assets.json'), 'utf8'));
  validateGraph(assets, version, sqlite);
  if (evidence) {
    const destination = join(evidence, sqlite ? 'sqlite' : 'postgres');
    mkdirSync(destination);
    copyFileSync(join(consumerDirectory, 'packages.lock.json'), join(destination, 'packages.lock.json'));
    copyFileSync(join(consumerDirectory, 'obj/project.assets.json'), join(destination, 'project.assets.json'));
    const graph = [];
    for (const [identity, library] of Object.entries(assets.libraries).filter(([, library]) => library.type === 'package')) {
      const [id, resolvedVersion] = identity.split('/');
      const cache = join(consumerDirectory, 'packages', id.toLowerCase(), resolvedVersion.toLowerCase());
      const archive = join(cache, `${id.toLowerCase()}.${resolvedVersion.toLowerCase()}.nupkg`);
      const nuspec = join(cache, `${id.toLowerCase()}.nuspec`);
      const owned = join(destination, 'archives', id.toLowerCase(), resolvedVersion.toLowerCase());
      mkdirSync(owned, { recursive: true });
      copyFileSync(archive, join(owned, `${id}.${resolvedVersion}.nupkg`));
      copyFileSync(nuspec, join(owned, `${id}.nuspec`));
      const metadata = readFileSync(nuspec, 'utf8');
      graph.push({ id, version: resolvedVersion, archiveSha256: createHash('sha256').update(readFileSync(archive)).digest('hex'),
        nuspecSha256: createHash('sha256').update(metadata).digest('hex'),
        license: metadata.match(/<license\b[^>]*>([^<]*)<\/license>/)?.[1] ?? null,
        licenseUrl: metadata.match(/<licenseUrl>([^<]*)<\/licenseUrl>/)?.[1] ?? null });
    }
    writeFileSync(join(destination, 'provenance.json'), JSON.stringify({ sdk, platform: process.platform, architecture: process.arch, sourceSha, version,
      licenseHash, packages: graph.sort((a, b) => a.id.localeCompare(b.id)) }, null, 2));
    const vulnerability = run('dotnet', ['list', project, 'package', '--vulnerable', '--include-transitive', '--format', 'json', '--no-restore'], { capture: true });
    writeFileSync(join(destination, 'vulnerability.json'), vulnerability);
  }
  if (!args.includes('--restore-only')) {
    run('dotnet', ['build', project, '--configuration', 'Release', '--no-restore']);
    let extraEnv = {};
    if (!sqlite && process.env.CMSIFY_CONSUMER_POSTGRES) {
      // Caller owns this fixture; never create or remove Docker resources on this path.
      extraEnv = { CMSIFY_CONSUMER_POSTGRES: process.env.CMSIFY_CONSUMER_POSTGRES };
    } else if (!sqlite) {
    const password = randomBytes(32).toString('hex');
    protectedValues.push(password);
    const environmentFile = join(staging, 'postgres.env');
    writeFileSync(environmentFile, '', { mode: 0o600 });
    if (process.platform === 'win32') {
      run('icacls', [environmentFile, '/inheritance:r', '/grant:r', `${userInfo().username}:(F)`], { capture: true });
    }
    writeFileSync(environmentFile, `POSTGRES_DB=cmsify_consumer\nPOSTGRES_USER=cmsify\nPOSTGRES_PASSWORD=${password}\n`, { mode: 0o600 });
    run('docker', ['run', '--detach', '--rm', '--name', container, '--publish', '127.0.0.1::5432',
      '--env-file', environmentFile, 'postgres:17-alpine']);
    containerStarted = true;
    let ready = false;
    for (let attempt = 0; attempt < 60; attempt++) {
      const probe = spawnSync('docker', ['exec', container, 'pg_isready', '-U', 'cmsify', '-d', 'cmsify_consumer'], { stdio: 'ignore', timeout: 10_000 });
      if (probe.status === 0) { ready = true; break; }
      await delay(1000);
    }
    if (!ready) throw new Error('Disposable PostgreSQL did not become ready in 60 seconds.');
    const port = run('docker', ['port', container, '5432/tcp'], { capture: true }).split(':').at(-1);
    extraEnv = { CMSIFY_CONSUMER_POSTGRES: `Host=127.0.0.1;Port=${port};Database=cmsify_consumer;Username=cmsify;Password=${password}` };
    }
    if (extraEnv.CMSIFY_CONSUMER_POSTGRES) protectedValues.push(extraEnv.CMSIFY_CONSUMER_POSTGRES);
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
