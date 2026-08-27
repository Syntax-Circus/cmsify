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

function traceIdFrom(bytes) {
  if (bytes.length === 0) return "unavailable";
  try {
    const problem = JSON.parse(bytes.toString("utf8"));
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
  const fetchImpl = request.fetchImpl ?? globalThis.fetch;
  assert(typeof fetchImpl === "function", "A fetch implementation is required.");
  const boundedSignal = makeSignal(request);
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
      body = JSON.parse(streamed.bytes.toString("utf8"));
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

export const HTTP_LIMITS = Object.freeze({
  requestTimeoutMs: REQUEST_TIMEOUT_MS,
  maximumJsonBytes: MAX_JSON_BYTES,
  maximumByteResponseBytes: MAX_BYTE_RESPONSE_BYTES,
});
