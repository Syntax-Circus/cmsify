import { test } from 'node:test';
import assert from 'node:assert/strict';
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
