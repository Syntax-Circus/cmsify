import { spawn } from "node:child_process";
import { basename } from "node:path";

const MAX_CAPTURED_BYTES = 1024 * 1024;
const SECRET_ENVIRONMENT_NAMES = new Set([
  "CMSIFY_FIXTURE_TOKEN",
  "POSTGRES_PASSWORD",
  "MINIO_ROOT_PASSWORD",
  "Secrets__EncryptionKey",
]);

function assert(condition, message) {
  if (!condition) throw new Error(message);
}

function appendBounded(chunks, capturedBytes, chunk) {
  if (capturedBytes >= MAX_CAPTURED_BYTES) return capturedBytes;
  const bytes = Buffer.isBuffer(chunk) ? chunk : Buffer.from(chunk);
  const remaining = MAX_CAPTURED_BYTES - capturedBytes;
  chunks.push(bytes.subarray(0, remaining));
  return capturedBytes + Math.min(bytes.length, remaining);
}

function collectRedactions(environment, additionalRedactions) {
  const values = new Set();
  for (const [name, value] of Object.entries(environment)) {
    if (SECRET_ENVIRONMENT_NAMES.has(name) && typeof value === "string" && value.length > 0) values.add(value);
  }
  for (const value of additionalRedactions) {
    if (typeof value === "string" && value.length > 0) values.add(value);
  }
  return [...values].sort((left, right) => right.length - left.length);
}

function redact(value, redactions) {
  let sanitized = value;
  for (const secret of redactions) sanitized = sanitized.split(secret).join("<redacted>");
  return sanitized;
}

function capCapturedText(value) {
  if (Buffer.byteLength(value, "utf8") <= MAX_CAPTURED_BYTES) return value;
  const bytes = Buffer.from(value, "utf8");
  let end = MAX_CAPTURED_BYTES;
  while (end > 0 && (bytes[end] & 0xc0) === 0x80) end -= 1;
  return bytes.subarray(0, end).toString("utf8");
}

function redactAndCap(value, redactions) {
  return capCapturedText(redact(value, redactions));
}

function sanitizedTail(stdout, stderr, redactions) {
  const combined = redact(`${stderr}\n${stdout}`, redactions).replace(/[\r\n]+/g, " ").trim();
  return combined.slice(-2048) || "no diagnostic output";
}

async function terminateProcessTree(child) {
  if (!child.pid || child.exitCode !== null) return;

  if (process.platform === "win32") {
    await new Promise((resolve) => {
      let settled = false;
      const finish = () => {
        if (settled) return;
        settled = true;
        resolve();
      };
      let taskkill;
      try {
        taskkill = spawn("taskkill", ["/pid", String(child.pid), "/T", "/F"], {
          shell: false,
          stdio: "ignore",
          windowsHide: true,
        });
      } catch {
        child.kill("SIGKILL");
        finish();
        return;
      }
      taskkill.once("error", () => {
        child.kill("SIGKILL");
        finish();
      });
      taskkill.once("close", (exitCode) => {
        if (exitCode !== 0) child.kill("SIGKILL");
        finish();
      });
    });
    return;
  }

  try {
    process.kill(-child.pid, "SIGKILL");
  } catch {
    child.kill("SIGKILL");
  }
}

/** A bounded child-process failure with redacted captured output. */
export class ProcessFailure extends Error {
  /**
   * @param {{command:string,phase:string,exitCode:number|null,stdout:string,stderr:string,durationMs:number,redactions:string[],cause?:unknown}} details
   */
  constructor(details) {
    const tail = sanitizedTail(details.stdout, details.stderr, details.redactions);
    const displayedExitCode = details.exitCode ?? -1;
    super(`Process ${basename(details.command)} failed during ${details.phase} with exit code ${displayedExitCode}: ${tail}`, { cause: details.cause });
    this.name = "ProcessFailure";
    this.command = details.command;
    this.phase = details.phase;
    this.exitCode = details.exitCode;
    this.stdout = redactAndCap(details.stdout, details.redactions);
    this.stderr = redactAndCap(details.stderr, details.redactions);
    this.durationMs = details.durationMs;
  }
}

/**
 * @typedef {{cwd?:string,env?:Record<string,string>,timeoutMs:number,signal?:AbortSignal,phase?:string,redact?:string[],stdoutEncoding?:"utf8"|"buffer",stdin?:string|Buffer}} ProcessOptions
 * @typedef {{exitCode:number,stdout:string|Buffer,stderr:string,durationMs:number}} ProcessResult
 */

/**
 * Runs a command without invoking a shell, with bounded output and cancellation.
 * @param {string} command
 * @param {string[]} args
 * @param {ProcessOptions} options
 * @returns {Promise<ProcessResult>}
 */
export function runProcess(command, args, options) {
  assert(typeof command === "string" && command.length > 0, "A process command is required.");
  assert(Array.isArray(args) && args.every((arg) => typeof arg === "string"), "Process arguments must be an array of strings.");
  assert(options && Number.isFinite(options.timeoutMs) && options.timeoutMs > 0, "A positive process timeout is required.");
  assert(options.env === undefined || (typeof options.env === "object" && Object.values(options.env).every((value) => typeof value === "string")), "Process environment values must be strings.");
  assert(options.redact === undefined || (Array.isArray(options.redact) && options.redact.every((value) => typeof value === "string")), "Additional process redactions must be strings.");
  assert(options.stdoutEncoding === undefined || ["utf8", "buffer"].includes(options.stdoutEncoding), "Process stdoutEncoding must be utf8 or buffer.");
  assert(options.stdin === undefined || ((typeof options.stdin === "string" || Buffer.isBuffer(options.stdin)) && Buffer.byteLength(options.stdin) <= MAX_CAPTURED_BYTES), "Process stdin must be a string or buffer no larger than one MiB.");

  const environment = { ...process.env, ...options.env };
  const redactions = collectRedactions(environment, options.redact ?? []);
  const startedAt = Date.now();
  const phase = options.phase ?? "process";

  return new Promise((resolve, reject) => {
    const stdoutChunks = [];
    const stderrChunks = [];
    let stdoutBytes = 0;
    let stderrBytes = 0;
    let settled = false;
    let terminationPhase;
    let child;

    const durationMs = () => Date.now() - startedAt;
    const capturedOutput = () => ({
      stdout: Buffer.concat(stdoutChunks).toString("utf8"),
      stderr: Buffer.concat(stderrChunks).toString("utf8"),
    });
    const cleanup = () => {
      clearTimeout(timeout);
      options.signal?.removeEventListener("abort", abort);
    };
    const fail = (failurePhase, exitCode, cause) => {
      if (settled) return;
      settled = true;
      cleanup();
      const output = capturedOutput();
      reject(new ProcessFailure({
        command,
        phase: failurePhase,
        exitCode,
        stdout: output.stdout,
        stderr: output.stderr,
        durationMs: durationMs(),
        redactions,
        cause,
      }));
    };
    const terminate = (reason) => {
      if (terminationPhase || settled) return;
      terminationPhase = `${phase}: ${reason}`;
      void terminateProcessTree(child).catch(() => child.kill("SIGKILL"));
    };
    const abort = () => terminate("aborted");
    const timeout = setTimeout(() => terminate("timeout"), options.timeoutMs);

    try {
      child = spawn(command, args, {
        cwd: options.cwd,
        env: environment,
        shell: false,
        windowsHide: true,
        detached: process.platform !== "win32",
        stdio: [options.stdin === undefined ? "ignore" : "pipe", "pipe", "pipe"],
      });
    } catch (error) {
      fail(phase, null, error);
      return;
    }

    child.stdout.on("data", (chunk) => {
      stdoutBytes = appendBounded(stdoutChunks, stdoutBytes, chunk);
    });
    child.stderr.on("data", (chunk) => {
      stderrBytes = appendBounded(stderrChunks, stderrBytes, chunk);
    });
    child.once("error", (error) => fail(phase, null, error));
    if (options.stdin !== undefined) {
      child.stdin.once("error", () => terminate("stdin error"));
      child.stdin.end(options.stdin, typeof options.stdin === "string" ? "utf8" : undefined);
    }
    child.once("close", (exitCode) => {
      const output = capturedOutput();
      if (terminationPhase) {
        fail(terminationPhase, exitCode, undefined);
      } else if (exitCode !== 0) {
        fail(phase, exitCode, undefined);
      } else if (!settled) {
        settled = true;
        cleanup();
        resolve({
          exitCode,
          stdout: options.stdoutEncoding === "buffer" ? Buffer.concat(stdoutChunks) : redactAndCap(output.stdout, redactions),
          stderr: redactAndCap(output.stderr, redactions),
          durationMs: durationMs(),
        });
      }
    });

    if (options.signal?.aborted) abort();
    else options.signal?.addEventListener("abort", abort, { once: true });
  });
}
