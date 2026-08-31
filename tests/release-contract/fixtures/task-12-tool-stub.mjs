import { appendFileSync, mkdirSync, readFileSync, writeFileSync } from "node:fs";
import { dirname, resolve } from "node:path";

const [tool, ...arguments_] = process.argv.slice(2);
const fixturePath = process.env.CMSIFY_TASK12_STUB_FIXTURE;

if (!tool || !fixturePath) {
  process.stderr.write("Task 12 tool stub requires a tool name and CMSIFY_TASK12_STUB_FIXTURE.\n");
  process.exit(97);
}

const fixture = JSON.parse(readFileSync(fixturePath, "utf8"));
const key = [tool, ...arguments_].join(" ");
const response = fixture.commands?.[key] ?? fixture.patterns?.find((entry) => new RegExp(entry.pattern).test(key))?.response;

if (process.env.CMSIFY_TASK12_STUB_LOG) appendFileSync(process.env.CMSIFY_TASK12_STUB_LOG, `${key}\n`);

if (!response) {
  process.stderr.write(`Unexpected Task 12 tool invocation: ${key}\n`);
  process.exit(98);
}

const argumentAfter = (name) => {
  const index = arguments_.indexOf(name);
  return index >= 0 ? arguments_[index + 1] : undefined;
};
const expand = (value) => String(value).replace(/\{\{ARG_AFTER:([^}]+)\}\}/g, (_, name) => argumentAfter(name) ?? "");
const expandValue = (value) => {
  if (Array.isArray(value)) return value.map(expandValue);
  if (value && typeof value === "object") return Object.fromEntries(Object.entries(value).map(([key, nested]) => [expand(key), expandValue(nested)]));
  return typeof value === "string" ? expand(value) : value;
};

if (response.assertFileAfterArgument) {
  const target = argumentAfter(response.assertFileAfterArgument.name);
  const contents = target ? readFileSync(target, "utf8") : "";
  if (response.assertFileAfterArgument.exact !== undefined && contents !== response.assertFileAfterArgument.exact) {
    process.stderr.write(`Unexpected contents for ${response.assertFileAfterArgument.name}.\n`);
    process.exit(96);
  }
}
if (response.writeAfterArgument) {
  const target = argumentAfter(response.writeAfterArgument.name);
  if (!target) process.exit(95);
  mkdirSync(dirname(target), { recursive: true });
  writeFileSync(target, Buffer.from(response.writeAfterArgument.base64, "base64"));
}
for (const file of response.writeFiles ?? []) {
  const target = resolve(process.cwd(), expand(file.path));
  mkdirSync(dirname(target), { recursive: true });
  const contents = file.base64 === undefined
    ? (file.json === undefined ? expand(file.contents) : JSON.stringify(expandValue(file.json)))
    : Buffer.from(file.base64, "base64");
  writeFileSync(target, contents);
}

if (response.stdout !== undefined) process.stdout.write(String(response.stdout));
if (response.json !== undefined) process.stdout.write(JSON.stringify(response.json));
if (response.stderr !== undefined) process.stderr.write(String(response.stderr));
process.exit(response.exitCode ?? 0);
