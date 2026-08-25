import { execFileSync } from "node:child_process";
import { mkdirSync, writeFileSync } from "node:fs";
import { dirname, resolve } from "node:path";
import { fileURLToPath } from "node:url";

const sdkRoot = resolve(dirname(fileURLToPath(import.meta.url)), "..");
const argumentsByName = new Map();
for (let index = 0; index < process.argv.length; index += 1) {
  if (process.argv[index].startsWith("--")) {
    argumentsByName.set(process.argv[index], process.argv[index + 1]);
  }
}

const input = argumentsByName.get("--input");
const outputDirectory = argumentsByName.get("--output-dir");
if (!input || !outputDirectory) {
  throw new Error("Usage: node scripts/generate.mjs --input <openapi.json> --output-dir <directory>");
}

const schema = resolve(outputDirectory, "schema.ts");
const client = resolve(outputDirectory, "client.ts");
mkdirSync(outputDirectory, { recursive: true });
execFileSync(
  process.execPath,
  [resolve(sdkRoot, "node_modules/openapi-typescript/bin/cli.js"), resolve(input), "--output", schema],
  { cwd: sdkRoot, stdio: "inherit" },
);

writeFileSync(client, `import createClient from "openapi-fetch";\nimport type { paths } from "./schema";\n\nexport const createCmsifyFetchClient = (baseUrl: string, fetchImpl?: typeof fetch) =>\n  fetchImpl ? createClient<paths>({ baseUrl, fetch: fetchImpl as never }) : createClient<paths>({ baseUrl });\n`);
