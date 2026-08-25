import { execFileSync } from "node:child_process";
import { copyFileSync, existsSync, mkdirSync, mkdtempSync, rmSync, writeFileSync } from "node:fs";
import { tmpdir } from "node:os";
import { dirname, resolve } from "node:path";
import { fileURLToPath } from "node:url";

const sdkRoot = resolve(dirname(fileURLToPath(import.meta.url)), "..");
const examplesRoot = resolve(sdkRoot, "..", "..", "examples");
const temporaryRoot = mkdtempSync(resolve(tmpdir(), "cmsify-client-consumer-"));
const typescriptPackage = resolve(sdkRoot, "node_modules", "typescript");
const openapiFetchPackage = resolve(sdkRoot, "node_modules", "openapi-fetch");
const openapiHelpersPackage = resolve(sdkRoot, "node_modules", "openapi-typescript-helpers");
const npmCli = process.env.npm_execpath ?? resolve(dirname(process.execPath), "node_modules", "npm", "bin", "npm-cli.js");
const npmCache = resolve(temporaryRoot, "npm-cache");

if (!existsSync(npmCli)) throw new Error(`npm CLI was not found at ${npmCli}.`);

const npm = (arguments_, cwd) => execFileSync(process.execPath, [npmCli, ...arguments_, "--cache", npmCache], {
  cwd,
  encoding: "utf8",
  stdio: ["ignore", "pipe", "inherit"],
  env: { ...process.env, npm_config_cache: npmCache },
});

try {
  npm(["run", "build"], sdkRoot);
  const packed = JSON.parse(npm(["pack", "--json", "--pack-destination", temporaryRoot], sdkRoot));
  const tarball = resolve(temporaryRoot, packed[0].filename);
  const consumerRoot = resolve(temporaryRoot, "consumer");
  mkdirSync(consumerRoot);
  writeFileSync(resolve(consumerRoot, "package.json"), JSON.stringify({ private: true, type: "module" }, null, 2));
  writeFileSync(resolve(consumerRoot, "tsconfig.json"), JSON.stringify({
    compilerOptions: { target: "ES2022", module: "NodeNext", moduleResolution: "NodeNext", strict: true, noEmit: true },
  }, null, 2));
  writeFileSync(resolve(consumerRoot, "index.ts"), [
    'import { CmsifyClient, createCmsifyFetchClient, type ContentListItem, type PagedResult } from "@cmsify/client";',
    'const cms = new CmsifyClient({ baseUrl: "https://cms.example", workspaceId: "5c2d8d41-16aa-4d08-910d-110644525b67" });',
    'const page: Promise<PagedResult<ContentListItem>> = cms.content.list({ status: "Review", page: 1, pageSize: 20 });',
    'const raw = createCmsifyFetchClient("https://cms.example");',
    'void page; void raw;',
    "",
  ].join("\n"));
  copyFileSync(resolve(examplesRoot, "nextjs-app-router", "cmsify.ts"), resolve(consumerRoot, "nextjs-cmsify.ts"));
  copyFileSync(resolve(examplesRoot, "astro", "cmsify.ts"), resolve(consumerRoot, "astro-cmsify.ts"));
  copyFileSync(resolve(examplesRoot, "sveltekit", "cmsify.ts"), resolve(consumerRoot, "sveltekit-cmsify.ts"));
  writeFileSync(resolve(consumerRoot, "framework-globals.d.ts"), [
    "declare const process: { env: Record<string, string | undefined> };",
    "interface ImportMeta { env: Record<string, string> }",
    'declare module "$env/static/private" {',
    "  export const CMSIFY_API_TOKEN: string; export const CMSIFY_API_URL: string; export const CMSIFY_WORKSPACE_ID: string;",
    "}",
    "",
  ].join("\n"));
  npm(["install", "--offline", "--ignore-scripts", "--no-audit", "--no-fund", tarball, `file:${typescriptPackage}`, `file:${openapiFetchPackage}`, `file:${openapiHelpersPackage}`], consumerRoot);
  execFileSync(process.execPath, [resolve(consumerRoot, "node_modules", "typescript", "bin", "tsc"), "--project", "tsconfig.json"], { cwd: consumerRoot, stdio: "inherit" });
  console.log("Clean consumer typecheck passed.");
} finally {
  rmSync(temporaryRoot, { recursive: true, force: true });
}
