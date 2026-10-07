import { test } from 'node:test';
import assert from 'node:assert/strict';
import { spawnSync } from 'node:child_process';
import { fileURLToPath } from 'node:url';
import { validateGraph } from './engine-package-graph.mjs';

const version = '0.8.8-sqlite.1';
function graph(extra = {}) {
  return { libraries: {
    [`SyntaxCircus.Cmsify.Core/${version}`]: { type: 'package' },
    [`SyntaxCircus.Cmsify.Infrastructure/${version}`]: { type: 'package' },
    ...extra,
  } };
}
test('PostgreSQL accepts the exact package-only candidate graph', () => {
  assert.doesNotThrow(() => validateGraph(graph(), version, false));
});
for (const id of ['Microsoft.EntityFrameworkCore.Sqlite', 'Microsoft.Data.Sqlite.Core', 'SQLitePCLRaw.bundle_e_sqlite3']) {
  test(`PostgreSQL rejects leaked ${id}`, () => {
    assert.throws(() => validateGraph(graph({ [`${id}/10.0.11`]: { type: 'package' } }), version, false), /SQLite dependency/);
  });
}
test('SQLite requires its exact candidate package', () => {
  assert.throws(() => validateGraph(graph(), version, true), /Infrastructure.Sqlite/);
  assert.doesNotThrow(() => validateGraph(graph({ [`SyntaxCircus.Cmsify.Infrastructure.Sqlite/${version}`]: { type: 'package' } }), version, true));
});
test('Consumers reject wrong candidate versions and source project references', () => {
  assert.throws(() => validateGraph(graph(), '0.8.8-sqlite.2', false), /did not resolve/);
  assert.throws(() => validateGraph(graph({ 'Source/1.0.0': { type: 'project' } }), version, false), /project reference/);
});
test('External PostgreSQL requires explicit disposable ownership before restore or setup', () => {
  const result = spawnSync(process.execPath,
    [fileURLToPath(new URL('./verify-engine-packages.mjs', import.meta.url)), '--version', '0.0.0-package-test'],
    { encoding: 'utf8', timeout: 10_000, env: { ...process.env, PATH: '', CMSIFY_CONSUMER_POSTGRES: 'Host=unowned.invalid;Database=not_a_fixture', CMSIFY_CONSUMER_POSTGRES_DISPOSABLE: '' } });
  assert.notEqual(result.status, 0);
  assert.match(result.stderr, /explicitly disposable and caller-owned/);
});
for (const status of [0, 1]) {
  test(`Child output rejects protected fixture inputs before emission (exit ${status})`, () => {
    const runner = new URL('./verify-engine-packages.mjs', import.meta.url).href;
    const script = `import cp from 'node:child_process';
      import { syncBuiltinESMExports } from 'node:module';
      cp.spawnSync = () => ({ status: ${status}, stdout: process.env.CMSIFY_CONSUMER_POSTGRES, stderr: '' });
      syncBuiltinESMExports();
      process.argv = ['node', 'runner', '--version', '0.0.0-package-test'];
      try { await import(${JSON.stringify(runner)}); }
      catch (error) { console.error(error.message); process.exitCode = 1; }`;
    const protectedInput = 'Host=fixture.invalid;Database=owned_fixture';
    const result = spawnSync(process.execPath, ['--input-type=module', '--eval', script],
      { encoding: 'utf8', timeout: 10_000, env: { ...process.env, CMSIFY_CONSUMER_POSTGRES: protectedInput, CMSIFY_CONSUMER_POSTGRES_DISPOSABLE: '1' } });
    assert.notEqual(result.status, 0);
    assert.match(result.stderr, /protected fixture input; output withheld/);
    assert.ok(!result.stdout.includes(protectedInput) && !result.stderr.includes(protectedInput));
  });
}
