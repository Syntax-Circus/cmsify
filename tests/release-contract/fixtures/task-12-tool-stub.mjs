import { readFileSync } from "node:fs";

const [tool, ...arguments_] = process.argv.slice(2);
const fixturePath = process.env.CMSIFY_TASK12_STUB_FIXTURE;

if (!tool || !fixturePath) {
  process.stderr.write("Task 12 tool stub requires a tool name and CMSIFY_TASK12_STUB_FIXTURE.\n");
  process.exit(97);
}

const fixture = JSON.parse(readFileSync(fixturePath, "utf8"));
const key = [tool, ...arguments_].join(" ");
const response = fixture.commands?.[key];

if (!response) {
  process.stderr.write(`Unexpected Task 12 tool invocation: ${key}\n`);
  process.exit(98);
}

if (response.stdout !== undefined) process.stdout.write(String(response.stdout));
if (response.json !== undefined) process.stdout.write(JSON.stringify(response.json));
if (response.stderr !== undefined) process.stderr.write(String(response.stderr));
process.exit(response.exitCode ?? 0);
