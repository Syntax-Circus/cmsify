#!/usr/bin/env node
import { pathToFileURL } from "node:url";

import { runProcess } from "../upgrade-tests/process.mjs";
import {
  ReleaseSmokeFailure,
  certifyRelease,
  createDockerAdapter,
  validateReleaseOptions,
} from "./harness.mjs";
import { createReleaseHttpAdapter } from "./http.mjs";

const FLAGS = Object.freeze({
  "--api-image": "apiImage",
  "--admin-image": "adminImage",
  "--version": "version",
  "--source-sha": "sourceSha",
  "--output": "output",
});

export function parseCliArguments(argv) {
  if (!Array.isArray(argv) || argv[0] !== "certify") throw new Error("Usage: cli.mjs certify --api-image <repo:tag> --admin-image <repo:tag> --version <semver> --source-sha <40hex> --output <directory>.");
  const result = {};
  for (let index = 1; index < argv.length; index += 2) {
    const flag = argv[index];
    const property = FLAGS[flag];
    if (!property) throw new Error(`Unknown release smoke argument ${String(flag)}.`);
    if (Object.hasOwn(result, property)) throw new Error(`Duplicate release smoke argument ${flag}.`);
    const value = argv[index + 1];
    if (typeof value !== "string" || value.length === 0 || value.startsWith("--")) throw new Error(`Release smoke argument ${flag} requires a value.`);
    result[property] = value;
  }
  const missing = Object.entries(FLAGS).filter(([, property]) => !Object.hasOwn(result, property)).map(([flag]) => flag);
  if (missing.length > 0) throw new Error(`Required release smoke arguments are missing: ${missing.join(", ")}.`);
  return result;
}

export function exitCodeForFailure(error) {
  if (error?.signal === "SIGINT") return 130;
  if (error?.signal === "SIGTERM") return 143;
  return 1;
}

export async function main(argv = process.argv.slice(2), dependencies = {}) {
  const parsed = parseCliArguments(argv);
  const options = validateReleaseOptions(parsed);
  const abortController = dependencies.abortController ?? new AbortController();
  const docker = dependencies.docker ?? createDockerAdapter({
    run: dependencies.run ?? runProcess,
    repositoryRoot: dependencies.repositoryRoot ?? process.cwd(),
    signal: abortController.signal,
  });
  const http = dependencies.http ?? createReleaseHttpAdapter({ request: dependencies.request, signal: abortController.signal });
  const evidence = await certifyRelease(options, {
    docker,
    http,
    abortController,
    ...(dependencies.registerSignals ? { registerSignals: dependencies.registerSignals } : {}),
    ...(dependencies.evidenceWriter ? { evidenceWriter: dependencies.evidenceWriter } : {}),
  });
  process.stdout.write(`${JSON.stringify({ status: evidence.status, schema: evidence.schema, runId: evidence.runId, output: options.output })}\n`);
  return evidence;
}

if (process.argv[1] && import.meta.url === pathToFileURL(process.argv[1]).href) {
  main().catch((error) => {
    if (error instanceof ReleaseSmokeFailure) {
      process.stderr.write(`Release smoke failed during ${error.scenario}; sanitized evidence was written to the requested output directory.\n`);
    } else {
      process.stderr.write(`${String(error?.message ?? "Release smoke failed.").replace(/[\r\n]+/g, " ").slice(0, 512)}\n`);
    }
    process.exitCode = exitCodeForFailure(error);
  });
}
