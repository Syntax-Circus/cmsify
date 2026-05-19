import { execFileSync } from "node:child_process";
import { existsSync, readFileSync, writeFileSync } from "node:fs";
import { dirname, resolve } from "node:path";
import { fileURLToPath } from "node:url";

const root = resolve(dirname(fileURLToPath(import.meta.url)), "..");
const snapshot = resolve(root, "openapi.snapshot.json");
const schema = resolve(root, "src/generated/schema.ts");
const check = process.argv.includes("--check");

execFileSync(
  process.platform === "win32" ? "npx.cmd" : "npx",
  ["openapi-typescript", snapshot, "--output", schema],
  { cwd: root, stdio: "inherit" },
);

const client = resolve(root, "src/generated/client.ts");
const clientSource = `import createClient from "openapi-fetch";\nimport type { paths } from "./schema";\n\nexport const createCmsifyFetchClient = (baseUrl: string, fetchImpl?: typeof fetch) =>\n  fetchImpl ? createClient<paths>({ baseUrl, fetch: fetchImpl as never }) : createClient<paths>({ baseUrl });\n`;

const normalize = (value: string) => value.replace(/\r\n/g, "\n");
if (check && existsSync(client) && normalize(readFileSync(client, "utf8")) !== normalize(clientSource)) {
  throw new Error("Generated client output is out of date.");
}

writeFileSync(client, clientSource);
