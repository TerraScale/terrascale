// Internal typed transport for the TerraBase v1 client. It is not exported
// from any package entrypoint: callers reach it only through named operations.
import { parseV1Error } from "./errors.js";
import { requireDatabaseSelector, requireProject } from "./ids.js";
import { parseExactJson } from "./json.js";
import { v1Route, v1RoutePath } from "./routes.js";
/**
 * @import { FetchLike } from "../config.js"
 * @import { TerraBaseV1Error } from "./errors.js"
 * @import { JsonValue } from "./json.js"
 * @import { V1Operation } from "./routes.js"
 */

/** Server request body limit (api-v1.md). */
export const maximumRequestBytes = 4 * 1024 * 1024;
// Query budgets count stored JSON before wrappers and serializer escaping.
const defaultMaximumResponseBytes = 64 * 1024 * 1024;
// Validate header-safe credential syntax only; the producer validates JWT claims/signatures.
// Retain C# keys; current project routes also authenticate project access JWTs.
const tokenPattern = /^(?!tbk1\.)(?:[A-Za-z0-9_-]+\.[A-Za-z0-9_-]+\.[A-Za-z0-9_-]+|tb_[A-Za-z0-9_-]{43})$/u;
/**
 * Resolver input contains no credential. A successful URL is cached for this client/database.
 * @typedef {{ readonly project: string | undefined; readonly database?: string | undefined; readonly signal?: AbortSignal | undefined }} V1EndpointContext
 */
/** @typedef {(context: V1EndpointContext) => string | Promise<string>} V1EndpointResolver */
/** A fresh access token for each authenticated request, including stream reconnects. @typedef {() => string | Promise<string>} V1TokenProvider */
/**
 * Project-scoped C# Documents API client. Supply exactly one of baseUrl or endpointResolver.
 * URLs must use HTTPS or loopback HTTP. Mutations are never automatically replayed.
 * @typedef {object} TerraBaseV1ClientConfig
 * @property {string} project
 * @property {string | V1TokenProvider} token An access JWT (or a legacy C# tb_ key), or a provider resolved per authenticated request; held privately. Browser callers must use end-user tokens, never project credentials.
 * @property {string | undefined} [baseUrl]
 * @property {V1EndpointResolver | undefined} [endpointResolver]
 * @property {FetchLike | undefined} [fetch]
 * @property {number | undefined} [maxResponseBytes] Default 64 MiB, including query framing and escaping.
 */

/**
 * @typedef {object} V1CallOptions
 * @property {string | Uint8Array} [body]
 * @property {string} [contentType]
 * @property {Readonly<Record<string, string>>} [headers] Contract headers only (If-Match, If-None-Match).
 * @property {URLSearchParams | undefined} [query]
 * @property {AbortSignal | undefined} [signal]
 * @property {"no-store" | undefined} [cache] Authenticated shape epoch keys must bypass caches.
 * @property {"follow" | undefined} [redirect] Only the shape bootstrap, after a browser hides its 307 behind an opaque redirect.
 */

/**
 * @typedef {object} V1CallResult
 * @property {number} status
 * @property {Headers} headers
 * @property {JsonValue | undefined} value
 * @property {string | undefined} [redirectedTo] The final URL after a followed redirect.
 */

/**
 * @param {string} host
 * @returns {boolean}
 */
function isLoopbackHost(host) {
  const name = host.toLowerCase();
  return (
    name === "localhost" ||
    name.endsWith(".localhost") ||
    name === "[::1]" ||
    isLoopbackIpv4(name)
  );
}

/**
 * A dotted-decimal address in 127.0.0.0/8 with every octet at most 255. An
 * out-of-range octet would make the URL parser treat the host as a DNS name.
 * @param {string} name
 * @returns {boolean}
 */
function isLoopbackIpv4(name) {
  const octets = /^127\.(\d{1,3})\.(\d{1,3})\.(\d{1,3})$/u.exec(name);
  return octets !== null && octets.slice(1).every((octet) => Number(octet) <= 255);
}

/**
 * @param {string} raw
 * @param {string} what
 * @returns {URL}
 */
export function requireServiceUrl(raw, what) {
  /** @type {URL} */
  let url;
  if (typeof raw !== "string") throw new TypeError(`${what} must be an absolute URL.`);
  try {
    url = new URL(raw);
  } catch {
    throw new TypeError(`${what} must be an absolute URL.`);
  }
  if (url.username !== "" || url.password !== "" || url.hash !== "")
    throw new TypeError(`${what} must not carry credentials or a fragment.`);
  if (
    url.protocol !== "https:" &&
    !(url.protocol === "http:" && isLoopbackHost(url.hostname))
  )
    throw new TypeError(`${what} must use HTTPS; plain HTTP is allowed only for loopback hosts.`);
  return url;
}

/**
 * An explicitly configured endpoint, with no discovery or hardcoded hosts.
 * @param {string} baseUrl
 * @returns {V1EndpointResolver}
 */
export function staticBaseUrlResolver(baseUrl) {
  const base = canonicalBaseUrl(baseUrl);
  return () => base;
}
/** @param {string} raw @returns {string} */
function canonicalBaseUrl(raw) {
  const url = requireServiceUrl(raw, "baseUrl");
  if (url.search !== "") throw new TypeError("baseUrl must not carry a query.");
  return `${url.origin}${url.pathname.replace(/\/+$/u, "")}`;
}

/** @param {unknown} token @returns {string} */
function requireToken(token) {
  if (typeof token !== "string" || token.trim() !== token || !tokenPattern.test(token))
    throw new TypeError("token must be a compact access JWT or a legacy C# tb_ API key.");
  return token;
}

/** A response body that failed in transit after its headers arrived. */
export class BodyTransportError extends Error {
  /** @param {unknown} cause */
  constructor(cause) {
    super("TerraBase response body failed in transit.", { cause });
    this.name = "BodyTransportError";
  }
}

/** A request that failed in transit before any response; the native failure is the cause. */
export class RequestTransportError extends TypeError {
  /** @param {unknown} cause */
  constructor(cause) {
    super(cause instanceof Error ? cause.message : "TerraBase request failed in transit.", { cause });
    this.name = "RequestTransportError";
  }
}

/** A browser hid a 307 behind an opaque redirect; only the shape bootstrap may follow it. */
export class OpaqueRedirectError extends TypeError {
  constructor() {
    super("Fresh shape bootstrap requires server fetch; obtain a handle and snapshot offset with collection.shape() on your server.");
    this.name = "OpaqueRedirectError";
  }
}

/** Internal transport bound to one project credential. */
export class V1Transport {
  /** @type {string | undefined} */
  #project;
  /** @type {V1EndpointResolver} */
  #resolver;
  /** @type {Map<string, { promise: Promise<string>; timer: ReturnType<typeof setTimeout>; waiters: number }>} */
  #bases = new Map();
  /** @type {string | V1TokenProvider | undefined} */
  #token;
  /** @type {FetchLike} */
  #fetch;
  /** @type {number} */
  #maxResponseBytes;

  /** @param {TerraBaseV1ClientConfig | (import("./system.js").TerraBaseSystemClientConfig & { project?: undefined; endpointResolver?: undefined })} config */
  constructor(config) {
    if (typeof config !== "object" || config === null) throw new TypeError("TerraBase v1 client config is required.");
    this.#project = config.project === undefined ? undefined : requireProject(config.project);
    if ((config.baseUrl === undefined) === (config.endpointResolver === undefined))
      throw new TypeError("Supply exactly one of baseUrl or endpointResolver.");
    if (config.endpointResolver !== undefined && typeof config.endpointResolver !== "function")
      throw new TypeError("endpointResolver must be a function.");
    this.#resolver = config.endpointResolver ?? staticBaseUrlResolver(/** @type {string} */ (config.baseUrl));
    if ((config.project !== undefined || config.token !== undefined) && typeof config.token !== "function")
      requireToken(config.token);
    if ("autoIdempotencyKey" in config || "maxRetries" in config || "resolver" in config)
      throw new TypeError("Unsupported client configuration member.");
    const fetchHandler = config.fetch ?? globalThis.fetch?.bind(globalThis);
    if (typeof fetchHandler !== "function") throw new TypeError("TerraBase v1 client requires a fetch implementation.");
    const maxResponseBytes = config.maxResponseBytes ?? defaultMaximumResponseBytes;
    if (!Number.isSafeInteger(maxResponseBytes) || maxResponseBytes < 1)
      throw new TypeError("maxResponseBytes must be a positive integer.");
    this.#token = config.token;
    this.#fetch = fetchHandler;
    this.#maxResponseBytes = maxResponseBytes;
  }

  /** Internal credential lifetime metadata; never exposes the bearer value. */
  get hasTokenProvider() { return typeof this.#token === "function"; }

  /** @param {AbortSignal | undefined} signal @returns {Promise<string>} */
  async #accessToken(signal) {
    signal?.throwIfAborted();
    const provider = this.#token;
    if (typeof provider !== "function") return requireToken(provider);
    /** @type {() => void} */
    let abort = () => {};
    try {
      const token = await new Promise((resolve, reject) => {
        abort = () => reject(signal?.reason);
        signal?.addEventListener("abort", abort, { once: true });
        void Promise.resolve().then(() => {
          signal?.throwIfAborted();
          return provider();
        }).then(resolve, () => {
          // Provider errors may contain credentials; do not retain their message or cause.
          reject(new Error("TerraBase token provider failed."));
        });
      });
      signal?.throwIfAborted();
      return requireToken(token);
    } finally {
      signal?.removeEventListener("abort", abort);
    }
  }

  /**
   * @param {V1Operation} operation
   * @param {Readonly<Record<string, string>>} params
   * @param {AbortSignal | undefined} [signal]
   * @returns {Promise<string>}
   */
  async url(operation, params, signal) {
    signal?.throwIfAborted();
    const database = v1Route(operation).pathTemplate.includes("{database}")
      ? requireDatabaseSelector(params.database)
      : undefined;
    const session = database ?? "";
    let base = this.#bases.get(session);
    if (base === undefined) {
      const controller = new AbortController();
      const timer = setTimeout(() => controller.abort(new DOMException(
        "TerraBase endpoint resolution timed out after 30 seconds.", "TimeoutError",
      )), 30_000);
      /** @type {Promise<string>} */
      const promise = new Promise((resolve, reject) => {
        controller.signal.addEventListener("abort", () => reject(controller.signal.reason), { once: true });
        void Promise.resolve().then(() => this.#resolver(Object.freeze({
          project: this.#project, ...(database === undefined ? {} : { database }), signal: controller.signal,
        }))).then(canonicalBaseUrl).then(resolve, reject).finally(() => clearTimeout(timer));
      });
      base = { promise, timer, waiters: 0 };
      this.#bases.set(session, base);
      // A failed resolution is not a session; a later operation may resolve again.
      void promise.catch(() => { if (this.#bases.get(session) === base) this.#bases.delete(session); });
    }
    base.waiters++;
    if (typeof base.timer === "object") base.timer.ref();
    try {
      // Each caller cancels its own wait; one caller cannot abort shared session resolution.
      const resolved = signal === undefined ? await base.promise : await new Promise((resolve, reject) => {
        const abort = () => reject(signal.reason);
        signal.addEventListener("abort", abort, { once: true });
        void base.promise.then(value => {
          signal.removeEventListener("abort", abort);
          resolve(value);
        }, error => {
          signal.removeEventListener("abort", abort);
          reject(error);
        });
      });
      signal?.throwIfAborted();
      if (v1Route(operation).pathTemplate.includes("{project}") && this.#project === undefined) throw new TypeError("This operation requires a project client.");
      return `${resolved}${v1RoutePath(v1Route(operation), { ...params, ...(this.#project === undefined ? {} : { project: this.#project }) })}`;
    } finally {
      // Keep Node alive only while callers are waiting for this resolution.
      if (--base.waiters === 0 && typeof base.timer === "object") base.timer.unref();
    }
  }

  /**
   * Reads a response body as UTF-8 text within the response byte bound. A
   * connection failure while reading is a BodyTransportError.
   * @param {Response} response
   * @param {AbortSignal | undefined} [signal]
   * @returns {Promise<string>}
   */
  async readText(response, signal) {
    signal?.throwIfAborted();
    const reader = response.body?.getReader();
    if (reader === undefined) return "";
    /** @type {Uint8Array[]} */
    const parts = [];
    let size = 0;
    try {
      while (true) {
        signal?.throwIfAborted();
        /** @type {ReadableStreamReadResult<Uint8Array>} */
        let part;
        try {
          part = await reader.read();
        } catch (error) {
          signal?.throwIfAborted();
          throw new BodyTransportError(error);
        }
        signal?.throwIfAborted();
        if (part.done) break;
        size += part.value.byteLength;
        if (size > this.#maxResponseBytes)
          throw new TypeError(`TerraBase response exceeds ${this.#maxResponseBytes} bytes.`);
        parts.push(part.value);
      }
    } catch (error) {
      void reader.cancel().catch(() => undefined);
      throw error;
    } finally {
      reader.releaseLock();
    }
    const bytes = new Uint8Array(size);
    let offset = 0;
    for (const part of parts) {
      bytes.set(part, offset);
      offset += part.byteLength;
    }
    return new TextDecoder("utf-8", { fatal: true, ignoreBOM: true }).decode(bytes);
  }

  /**
   * Parses a JSON response body exactly.
   * @param {Response} response
   * @param {AbortSignal | undefined} [signal]
   * @returns {Promise<JsonValue | undefined>}
   */
  async readJson(response, signal) {
    return V1Transport.#decodeJson(response, await this.readText(response, signal));
  }

  /**
   * @param {Response} response
   * @param {string} text
   * @returns {JsonValue | undefined}
   */
  static #decodeJson(response, text) {
    if (text === "") return undefined;
    const mediaType = response.headers.get("content-type")?.split(";", 1)[0]?.trim().toLowerCase();
    if (mediaType !== "application/json")
      throw new TypeError(`TerraBase returned ${mediaType ?? "no media type"} instead of application/json.`);
    return parseExactJson(text);
  }

  /**
   * Converts a non-success response to a TerraBaseV1Error.
   * @param {Response} response
   * @param {AbortSignal | undefined} [signal]
   * @returns {Promise<TerraBaseV1Error>}
   */
  async error(response, signal) {
    let text = "";
    try {
      text = await this.readText(response, signal);
    } catch {
      signal?.throwIfAborted();
      text = "";
    }
    return parseV1Error(response.status, response.headers, text);
  }

  /**
   * Calls only a named producer route. Redirects and mutations are never replayed.
   * A replay-safe read has one 60-second deadline over resolution, credentials, retries and its body.
   * @param {V1Operation} operation
   * @param {Readonly<Record<string, string>>} params
   * @param {V1CallOptions} [options]
   * @returns {Promise<V1CallResult>}
   */
  async call(operation, params, options = {}) {
    const deadline = operationDeadline(operation, options.signal);
    try {
      const scoped = { ...options, signal: deadline.signal };
      const response = await this.#send(operation, params, scoped, deadline.expiresAt);
      if (response.status === 307) {
        await response.body?.cancel();
        return { status: response.status, headers: response.headers, value: undefined };
      }
      return { status: response.status, headers: response.headers, value: await this.readJson(response, scoped.signal),
        ...(response.redirected ? { redirectedTo: response.url } : {}) };
    } finally {
      deadline.clear();
    }
  }

  /**
   * The read deadline lasts until response headers; the open stream then ends only by the caller's signal.
   * @param {"streamDatabaseChanges" | "exportDatabase"} operation
   * @param {Readonly<Record<string, string>>} params
   * @param {V1CallOptions} [options]
   * @returns {Promise<Response>}
   */
  async openStream(operation, params, options = {}) {
    const deadline = operationDeadline(operation, options.signal);
    /** @type {Response} */
    let response;
    try {
      response = await this.#send(operation, params, { ...options, signal: deadline.signal }, deadline.expiresAt);
    } finally {
      deadline.clear();
    }
    const mediaType = response.headers.get("content-type")?.split(";", 1)[0]?.trim().toLowerCase();
    const expected = v1Route(operation).responseMediaType;
    if (mediaType !== expected || response.body === null) {
      void response.body?.cancel().catch(() => undefined);
      throw new TypeError(`TerraBase ${operation} requires a ${expected} response body.`);
    }
    return response;
  }

  /**
   * @param {V1Operation} operation @param {Readonly<Record<string, string>>} params @param {V1CallOptions} options
   * @param {number} expiresAt Epoch milliseconds when the read deadline expires.
   * @returns {Promise<Response>}
   */
  async #send(operation, params, options, expiresAt) {
    const route = v1Route(operation);
    const signal = options.signal;
    const bytes = typeof options.body === "string" ? new TextEncoder().encode(options.body) : options.body;
    if (bytes !== undefined && bytes.byteLength > maximumRequestBytes)
      throw new TypeError(`TerraBase request bodies are limited to ${maximumRequestBytes} bytes.`);
    let url = await this.url(operation, params, signal);
    if (options.query !== undefined && options.query.size > 0) url += `?${options.query}`;
    // Shape reads publish stale state and resume through their own recovery; they are not resent here.
    const replaySafe = isReplaySafe(operation) && !selfRecovering.has(operation);
    for (let attempt = 0; ; attempt++) {
      const headers = new Headers(options.headers);
      // Resolve the credential per attempt so a retry never sends a token the provider has replaced.
      if (route.permission !== "anonymous" && this.#token !== undefined)
        headers.set("authorization", `Bearer ${await this.#accessToken(signal)}`);
      if (route.responseMediaType !== "") headers.set("accept", route.responseMediaType);
      if (options.body !== undefined) headers.set("content-type", options.contentType ?? "application/json");
      /** @type {RequestInit & { duplex?: "half" }} */
      const init = { method: route.method, headers, redirect: options.redirect ?? "manual" };
      if (options.cache !== undefined) init.cache = options.cache;
      if (route.permission.endsWith(":write") && typeof process !== "undefined" && process.release?.name === "node") {
        // Node fetch replays buffered or absent bodies on 421; use a one-shot stream, including DELETE.
        if (operation === "uploadImportSource" && bytes !== undefined) headers.set("content-length", String(bytes.byteLength));
        init.body = new ReadableStream({ start(controller) { if (bytes !== undefined && bytes.byteLength > 0) controller.enqueue(bytes); controller.close(); } });
        init.duplex = "half";
      } else if (options.body !== undefined) init.body = typeof options.body === "string" ? options.body : options.body.slice().buffer;
      if (signal !== undefined) init.signal = signal;
      signal?.throwIfAborted();
      /** @type {Response} */
      let response;
      try { response = await this.#fetch(url, init); }
      catch (error) {
        signal?.throwIfAborted();
        // No response arrived. Only a read may be resent; a mutation's outcome is unknown.
        if (!replaySafe || attempt >= maximumReadRetries) throw new RequestTransportError(error);
        await waitForRetry(backoff(attempt), signal);
        continue;
      }
      if (signal?.aborted) {
        void response.body?.cancel().catch(() => undefined);
        signal.throwIfAborted();
      }
      if (response.type === "opaqueredirect") throw new OpaqueRedirectError();
      if (!route.successStatuses.includes(response.status)) {
        const error = await this.error(response, signal);
        if (!replaySafe || attempt >= maximumReadRetries || !transientRefusal(error)) throw error;
        // Wait at least Retry-After, plus jitter so clients refused together do not return in lockstep.
        // A wait the deadline cannot cover surfaces the refusal now.
        const delay = (error.retryAfter ?? 0) * 1000 + backoff(attempt);
        if (delay > expiresAt - Date.now()) throw error;
        await waitForRetry(delay, signal);
        continue;
      }
      return response;
    }
  }
}

/** One fixed bound for a replay-safe read, retries included; a caller's signal can only shorten it. */
const readDeadlineMs = 60_000;
/** Resends after the first attempt, for replay-safe requests only. */
const maximumReadRetries = 3;
/** Shape reads with their own stale-state publication and resume. */
const selfRecovering = new Set(["readFreeShape", "readShape", "getShapeKey"]);
/** Read-only POST operations; every other non-GET operation may mutate and is sent once. */
const readOnlyPosts = new Set(["queryDocuments", "runSql", "checkSql", "explainSql"]);
/** Producer refusals before execution that clear on their own (429 budgets, 503 busy/unavailable). */
const transientCodes = new Map([
  [429, new Set(["tenant_busy", "tenant_memory", "shape_busy"])],
  [503, new Set(["storage_busy", "unavailable", "auth_unavailable", "catalog_stale",
    "remote_region_unavailable", "confirmation_busy", "http_503"])],
  // Gateway failures carry no producer body.
  [502, new Set(["http_502"])],
  [504, new Set(["http_504"])],
]);

/** @param {TerraBaseV1Error} error @returns {boolean} */
function transientRefusal(error) {
  return transientCodes.get(error.status)?.has(error.code) === true;
}

/** Exponential backoff with full jitter. @param {number} attempt @returns {number} */
function backoff(attempt) {
  return Math.round(Math.random() * Math.min(2_000, 250 * 2 ** attempt));
}

/** @param {V1Operation} operation @returns {boolean} */
function isReplaySafe(operation) {
  return v1Route(operation).method === "GET" || readOnlyPosts.has(operation);
}

/**
 * Combines the caller's signal with the fixed deadline of a replay-safe read. The caller's abort reason is preserved.
 * Mutations keep only the caller's signal: abandoning one makes its outcome uncertain, and some legitimately run long.
 * @param {V1Operation} operation
 * @param {AbortSignal | undefined} callerSignal
 * @returns {{ signal: AbortSignal | undefined; expiresAt: number; clear: () => void }}
 */
function operationDeadline(operation, callerSignal) {
  if (!isReplaySafe(operation)) return { signal: callerSignal, expiresAt: Infinity, clear: () => {} };
  const controller = new AbortController();
  const timer = setTimeout(() => controller.abort(new DOMException(
    `TerraBase read exceeded its ${readDeadlineMs / 1000}-second deadline.`, "TimeoutError")), readDeadlineMs);
  const signal = callerSignal === undefined ? controller.signal : AbortSignal.any([callerSignal, controller.signal]);
  return { signal, expiresAt: Date.now() + readDeadlineMs, clear: () => clearTimeout(timer) };
}

/** @param {number} milliseconds @param {AbortSignal | undefined} signal @returns {Promise<void>} */
function waitForRetry(milliseconds, signal) {
  signal?.throwIfAborted();
  return new Promise((resolve, reject) => {
    const abort = () => { clearTimeout(timer); signal?.removeEventListener("abort", abort); reject(signal?.reason); };
    const timer = setTimeout(() => { signal?.removeEventListener("abort", abort); resolve(); }, milliseconds);
    signal?.addEventListener("abort", abort, { once: true });
  });
}
