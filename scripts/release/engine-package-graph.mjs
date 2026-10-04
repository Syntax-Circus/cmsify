export const enginePackageIds = ['SyntaxCircus.Cmsify.Core', 'SyntaxCircus.Cmsify.Infrastructure', 'SyntaxCircus.Cmsify.Infrastructure.Sqlite'];

export function validateGraph(assets, version, sqlite) {
  for (const id of enginePackageIds.slice(0, sqlite ? 3 : 2)) {
    if (assets.libraries[`${id}/${version}`]?.type !== 'package') throw new Error(`Consumer did not resolve ${id}/${version} as a package.`);
  }
  if (Object.values(assets.libraries).some(library => library.type === 'project')) throw new Error('Consumer unexpectedly resolved a project reference.');
  if (!sqlite) {
    for (const name of Object.keys(assets.libraries)) {
      if (/^(?:Microsoft\.EntityFrameworkCore\.Sqlite(?:\.|\/)|Microsoft\.Data\.Sqlite|SQLitePCLRaw|SyntaxCircus\.Cmsify\.Infrastructure\.Sqlite\/)/i.test(name)) {
        throw new Error(`PostgreSQL consumer acquired a SQLite dependency: ${name}`);
      }
    }
  }
}
