import { createHash } from "node:crypto";

const REQUEST_TIMEOUT_MS = 5_000;
const MAX_JSON_BYTES = 1024 * 1024;
const MAX_BYTE_RESPONSE_BYTES = 10 * 1024 * 1024;
const DIAGNOSTIC_VALUE = /^[a-zA-Z0-9._:-]{1,128}$/;

class HttpDiagnosticError extends Error {}

function assert(condition, message) {
  if (!condition) throw new Error(message);
}

function requestIdentity(request) {
  const method = typeof request?.method === "string" && request.method.length > 0
    ? request.method.toUpperCase()
    : "GET";
  let path = "/";
  try {
    path = new URL(request?.url).pathname;
  } catch {
    // Never reflect an invalid URL because it may contain credentials or query secrets.
  }
  return { method, path };
}

function expectedStatusSet(value) {
  const statuses = value instanceof Set ? [...value] : Array.isArray(value) ? [...value] : [];
  assert(statuses.length > 0, "HTTP expectedStatuses must be a non-empty exact set.");
  assert(new Set(statuses).size === statuses.length, "HTTP expectedStatuses must not contain duplicates.");
  assert(statuses.every((status) => Number.isInteger(status) && status >= 100 && status <= 599), "HTTP expectedStatuses contains an invalid status.");
  return new Set(statuses);
}

function safeDiagnosticValue(value) {
  return typeof value === "string" && DIAGNOSTIC_VALUE.test(value) ? value : "unavailable";
}

function correlationId(headers) {
  return safeDiagnosticValue(headers.get("x-correlation-id") ?? headers.get("x-correlationid"));
}

function decodeJson(bytes) {
  return JSON.parse(new TextDecoder("utf-8", { fatal: true }).decode(bytes));
}

function traceIdFrom(bytes) {
  if (bytes.length === 0) return "unavailable";
  try {
    const problem = decodeJson(bytes);
    return safeDiagnosticValue(problem?.traceId);
  } catch {
    return "unavailable";
  }
}

function diagnosticError(request, status, headers, bytes, reason = "unexpected status") {
  const { method, path } = requestIdentity(request);
  return new HttpDiagnosticError(
    `HTTP ${method} ${path} failed (${reason}); status ${status}; correlationId ${correlationId(headers)}; traceId ${traceIdFrom(bytes)}.`,
  );
}

function transportError(request) {
  const { method, path } = requestIdentity(request);
  return new HttpDiagnosticError(`HTTP ${method} ${path} failed (transport error); status unavailable; correlationId unavailable; traceId unavailable.`);
}

function requestPreparationError(request) {
  const { method, path } = requestIdentity(request);
  return new HttpDiagnosticError(`HTTP ${method} ${path} failed (request preparation error); status unavailable; correlationId unavailable; traceId unavailable.`);
}

function responseTooLargeError(request, status, headers, bytes, limitLabel) {
  return diagnosticError(request, status, headers, bytes, `response exceeds ${limitLabel}`);
}

function makeSignal(request) {
  const signalFactory = request.signalFactory ?? AbortSignal.timeout;
  assert(typeof signalFactory === "function", "HTTP signalFactory must be a function.");
  const timeoutSignal = signalFactory(REQUEST_TIMEOUT_MS);
  assert(timeoutSignal instanceof AbortSignal, "HTTP signalFactory must return an AbortSignal.");
  if (request.signal === undefined) return { signal: timeoutSignal, dispose() {} };
  assert(request.signal instanceof AbortSignal, "HTTP signal must be an AbortSignal.");

  const controller = new AbortController();
  const abort = (event) => controller.abort(event.target.reason);
  for (const signal of [timeoutSignal, request.signal]) {
    if (signal.aborted) controller.abort(signal.reason);
    else signal.addEventListener("abort", abort, { once: true });
  }
  return {
    signal: controller.signal,
    dispose() {
      timeoutSignal.removeEventListener("abort", abort);
      request.signal.removeEventListener("abort", abort);
    },
  };
}

async function readBounded(response, maximumBytes, request, limitLabel) {
  const length = response.headers.get("content-length");
  if (length !== null && /^\d+$/.test(length) && Number(length) > maximumBytes) {
    await response.body?.cancel().catch(() => {});
    throw responseTooLargeError(request, response.status, response.headers, Buffer.alloc(0), limitLabel);
  }
  if (response.body === null) return { bytes: Buffer.alloc(0), byteLength: 0, sha256: createHash("sha256").digest("hex") };

  const reader = response.body.getReader();
  const chunks = [];
  const hash = createHash("sha256");
  let byteLength = 0;
  try {
    while (true) {
      const { done, value } = await reader.read();
      if (done) break;
      const chunk = Buffer.from(value);
      byteLength += chunk.length;
      if (byteLength > maximumBytes) {
        await reader.cancel().catch(() => {});
        throw responseTooLargeError(request, response.status, response.headers, Buffer.alloc(0), limitLabel);
      }
      chunks.push(chunk);
      hash.update(chunk);
    }
  } catch (error) {
    await reader.cancel().catch(() => {});
    throw error;
  } finally {
    reader.releaseLock();
  }
  return { bytes: Buffer.concat(chunks, byteLength), byteLength, sha256: hash.digest("hex") };
}

async function send(request, maximumBytes, limitLabel) {
  assert(request && typeof request === "object", "An HTTP request is required.");
  let url;
  try {
    url = new URL(request.url);
  } catch {
    throw transportError(request);
  }
  assert(url.protocol === "http:" || url.protocol === "https:", "HTTP request URL must use http or https.");
  let expectedStatuses;
  let method;
  let headers;
  let body;
  let fetchImpl;
  let boundedSignal;
  try {
    expectedStatuses = expectedStatusSet(request.expectedStatuses);
    method = requestIdentity(request).method;
    headers = new Headers(request.headers);
    if (request.token !== undefined) {
      assert(typeof request.token === "string" && request.token.length > 0, "HTTP token must be a non-empty string.");
      headers.set("authorization", `Bearer ${request.token}`);
    } else {
      headers.delete("authorization");
    }
    if (request.body !== undefined) {
      headers.set("content-type", "application/json");
      body = JSON.stringify(request.body);
    }
    fetchImpl = request.fetchImpl ?? globalThis.fetch;
    assert(typeof fetchImpl === "function", "A fetch implementation is required.");
    boundedSignal = makeSignal(request);
  } catch (error) {
    if (error instanceof HttpDiagnosticError) throw error;
    throw requestPreparationError(request);
  }
  try {
    let response;
    try {
      response = await fetchImpl(url.toString(), {
        method,
        headers,
        body,
        signal: boundedSignal.signal,
        redirect: "manual",
        credentials: "omit",
      });
    } catch {
      throw transportError(request);
    }
    if (!(response instanceof Response)) throw transportError(request);
    let streamed;
    try {
      streamed = await readBounded(response, maximumBytes, request, limitLabel);
    } catch (error) {
      if (error instanceof HttpDiagnosticError) throw error;
      throw transportError(request);
    }
    if (!expectedStatuses.has(response.status)) {
      throw diagnosticError(request, response.status, response.headers, streamed.bytes);
    }
    return { response, streamed };
  } finally {
    boundedSignal.dispose();
  }
}

/**
 * Sends one bounded JSON request. Only statuses in expectedStatuses are accepted.
 * @param {object} request
 * @returns {Promise<{status:number,headers:Headers,body:unknown}>}
 */
export async function requestJson(request) {
  const { response, streamed } = await send(request, MAX_JSON_BYTES, "1 MiB");
  let body;
  if (streamed.byteLength > 0) {
    try {
      body = decodeJson(streamed.bytes);
    } catch {
      throw diagnosticError(request, response.status, response.headers, Buffer.alloc(0), "invalid JSON response");
    }
  }
  return Object.freeze({ status: response.status, headers: response.headers, body });
}

/**
 * Streams, bounds, and incrementally hashes one byte response.
 * @param {object} request
 * @returns {Promise<{status:number,headers:Headers,bytes:Buffer,byteLength:number,sha256:string}>}
 */
export async function requestBytes(request) {
  const { response, streamed } = await send(request, MAX_BYTE_RESPONSE_BYTES, "10 MiB");
  return Object.freeze({ status: response.status, headers: response.headers, ...streamed });
}

function parseCurlHeaders(value) {
  const blocks = value.replaceAll("\r\n", "\n").split("\n\n").filter((block) => block.startsWith("HTTP/"));
  assert(blocks.length > 0, "Docker HTTP response headers are malformed.");
  const headers = new Headers();
  for (const line of blocks.at(-1).split("\n").slice(1)) {
    const colon = line.indexOf(":");
    if (colon > 0) headers.append(line.slice(0, colon).trim(), line.slice(colon + 1).trim());
  }
  return headers;
}

function dockerRequestPreparation(request, maximumBytes) {
  assert(request && typeof request === "object", "An HTTP request is required.");
  let url;
  try {
    url = new URL(request.url);
  } catch {
    throw transportError(request);
  }
  if (!["http:", "https:"].includes(url.protocol) || url.username || url.password) throw requestPreparationError(request);
  try {
    const expectedStatuses = expectedStatusSet(request.expectedStatuses);
    const method = requestIdentity(request).method;
    const headers = new Headers(request.headers);
    if (request.token !== undefined) {
      assert(typeof request.token === "string" && request.token.length > 0, "HTTP token must be a non-empty string.");
      headers.set("authorization", `Bearer ${request.token}`);
    } else {
      headers.delete("authorization");
    }
    let body;
    if (request.body !== undefined) {
      headers.set("content-type", "application/json");
      body = JSON.stringify(request.body);
    }
    const boundedSignal = makeSignal(request);
    const redactions = [...headers.values(), ...(body === undefined ? [] : [body])];
    const identity = createHash("sha256").update(`${method}\n${url.toString()}`).digest("hex").slice(0, 24);
    return Object.freeze({
      url,
      method,
      headers,
      body,
      expectedStatuses,
      boundedSignal,
      redactions,
      maximumBytes,
      bodyPath: `/tmp/cmsify-http-${identity}.body`,
      headersPath: `/tmp/cmsify-http-${identity}.headers`,
    });
  } catch (error) {
    if (error instanceof HttpDiagnosticError) throw error;
    throw requestPreparationError(request);
  }
}

/**
 * Creates the bounded HTTP adapter used from an isolated Docker service.
 * @param {{exec:(service:string,args:string[],options?:object)=>Promise<object>}} docker
 * @param {string} service
 */
export function createDockerHttpAdapter(docker, service) {
  assert(docker && typeof docker.exec === "function", "A Docker HTTP adapter is required.");
  assert(typeof service === "string" && /^[a-zA-Z0-9][a-zA-Z0-9_.-]*$/.test(service), "A canonical Docker service is required.");

  async function execute(prepared, args, { stdoutEncoding } = {}) {
    try {
      return await docker.exec(service, args, {
        timeoutMs: REQUEST_TIMEOUT_MS,
        signal: prepared.boundedSignal.signal,
        redact: prepared.redactions,
        ...(stdoutEncoding ? { stdoutEncoding } : {}),
      });
    } catch {
      throw transportError({ method: prepared.method, url: prepared.url.toString() });
    }
  }

  async function responseSize(prepared, path) {
    const result = await execute(prepared, ["wc", "-c", path]);
    const value = typeof result.stdout === "string" ? result.stdout : "";
    const match = /^\s*(\d+)\s+(\S+)\s*$/.exec(value);
    if (!match || match[2] !== path) throw transportError({ method: prepared.method, url: prepared.url.toString() });
    return Number(match[1]);
  }

  async function exchange(request, maximumBytes, limitLabel, kind) {
    const prepared = dockerRequestPreparation(request, maximumBytes);
    const curlArgs = [
      "curl", "--disable", "--silent", "--show-error",
      "--max-time", String(REQUEST_TIMEOUT_MS / 1_000),
      "--max-redirs", "0", "--proto", "=http,https", "--noproxy", "*",
      "--max-filesize", String(maximumBytes),
      "--output", prepared.bodyPath, "--dump-header", prepared.headersPath,
      "--write-out", "%{http_code}", "--request", prepared.method,
    ];
    for (const [name, value] of prepared.headers) curlArgs.push("--header", `${name}: ${value}`);
    if (prepared.body !== undefined) curlArgs.push("--data-binary", prepared.body);
    curlArgs.push(prepared.url.toString());

    try {
      const curl = await execute(prepared, curlArgs);
      const statusText = typeof curl.stdout === "string" ? curl.stdout.trim() : "";
      if (!/^\d{3}$/.test(statusText)) throw transportError(request);
      const status = Number(statusText);
      const [bodyLength, headerLength] = await Promise.all([
        responseSize(prepared, prepared.bodyPath),
        responseSize(prepared, prepared.headersPath),
      ]);
      if (headerLength > 64 * 1024) throw transportError(request);
      const headerResult = await execute(prepared, ["cat", prepared.headersPath]);
      if (typeof headerResult.stdout !== "string") throw transportError(request);
      let headers;
      try {
        headers = parseCurlHeaders(headerResult.stdout);
      } catch {
        throw transportError(request);
      }
      if (bodyLength > maximumBytes) throw responseTooLargeError(request, status, headers, Buffer.alloc(0), limitLabel);

      let bytes = Buffer.alloc(0);
      if (kind === "json" || !prepared.expectedStatuses.has(status)) {
        if (bodyLength <= MAX_JSON_BYTES) {
          const bodyResult = await execute(prepared, ["cat", prepared.bodyPath], { stdoutEncoding: "buffer" });
          if (!Buffer.isBuffer(bodyResult.stdout) || bodyResult.stdout.length !== bodyLength) throw transportError(request);
          bytes = bodyResult.stdout;
        }
      }
      if (!prepared.expectedStatuses.has(status)) throw diagnosticError(request, status, headers, bytes);

      if (kind === "json") {
        let body;
        if (bytes.length > 0) {
          try {
            body = decodeJson(bytes);
          } catch {
            throw diagnosticError(request, status, headers, Buffer.alloc(0), "invalid JSON response");
          }
        }
        return Object.freeze({ status, headers, body });
      }

      const digest = await execute(prepared, ["sha256sum", prepared.bodyPath]);
      const sha256 = typeof digest.stdout === "string" ? digest.stdout.trim().split(/\s+/, 1)[0] : "";
      if (!/^[0-9a-f]{64}$/.test(sha256)) throw transportError(request);
      return Object.freeze({ status, headers, bytes: Buffer.alloc(0), byteLength: bodyLength, sha256 });
    } finally {
      prepared.boundedSignal.dispose();
      try {
        await docker.exec(service, ["rm", "--force", "--", prepared.bodyPath, prepared.headersPath], {
          timeoutMs: REQUEST_TIMEOUT_MS,
          redact: prepared.redactions,
        });
      } catch {
        // Containers are run-scoped and will be removed; primary HTTP evidence remains authoritative.
      }
    }
  }

  return Object.freeze({
    requestJson(request) {
      return exchange(request, MAX_JSON_BYTES, "1 MiB", "json");
    },
    requestBytes(request) {
      return exchange(request, MAX_BYTE_RESPONSE_BYTES, "10 MiB", "bytes");
    },
  });
}

export const HTTP_LIMITS = Object.freeze({
  requestTimeoutMs: REQUEST_TIMEOUT_MS,
  maximumJsonBytes: MAX_JSON_BYTES,
  maximumByteResponseBytes: MAX_BYTE_RESPONSE_BYTES,
});
