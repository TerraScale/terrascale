// Internal typed transport for the TerraBase v1 client. It is not exported
// from any package entrypoint: callers reach it only through named operations.
import { parseV1Error } from "./errors.js";
import { requireDatabaseSelector, requireProject } from "./ids.js";
import { object } from "./decode.js";
import { parseExactJson } from "./json.js";
import { region as regionId } from "./catalog.js";
import { v1Route, v1RoutePath } from "./routes.js";
import { V1SqlSocket, sqlSocketProtocol } from "./sql-socket.js";
import { apiService, authService, isLoopbackHost, serviceOrigin } from "../origin.js";
import { ShapeHints, shapeHintProtocol } from "./shape-hints.js";
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
const publicBaseUrl = "https://api.terrascale.tech";
const publicAuthOrigin = "https://auth.terrascale.tech";
/**
 * Resolver input contains no credential. A successful URL is cached for this client/database.
 * @typedef {{ readonly project: string | undefined; readonly database?: string | undefined; readonly signal?: AbortSignal | undefined }} V1EndpointContext
 */
/** @typedef {(context: V1EndpointContext) => string | Promise<string>} V1EndpointResolver */
/** A fresh access token for each authenticated request, including stream reconnects. @typedef {() => string | Promise<string>} V1TokenProvider */
/** @typedef {new (url: string, init: string[] | { protocols: string[]; headers: Record<string, string> }) => import("./sql-socket.js").SqlSocketLike} SqlSocketConstructor */
/**
 * @typedef {object} V1CallOptions
 * @property {string | Uint8Array} [body]
 * @property {string} [contentType]
 * @property {Readonly<Record<string, string>>} [headers] Contract headers only (If-Match, If-None-Match).
 * @property {URLSearchParams | undefined} [query]
 * @property {AbortSignal | undefined} [signal]
 * @property {"follow" | undefined} [redirect] Only table shape reads.
 */

/**
 * @typedef {object} V1CallResult
 * @property {number} status
 * @property {Headers} headers
 * @property {JsonValue | undefined} value
 * @property {string | undefined} [redirectedTo] The final URL after a followed redirect.
 */

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
  return serviceOrigin(raw, "baseUrl", apiService);
}

function keyExchangeOrigin(/** @type {unknown} */ authBaseUrl, /** @type {string | undefined} */ base) {
  if (authBaseUrl !== undefined) return serviceOrigin(/** @type {string} */ (authBaseUrl), "authBaseUrl", authService);
  if (base !== publicBaseUrl)
    throw new TypeError("Set authBaseUrl to the ts-auth that exchanges the apiKey: with a TerraBase origin other than https://api.terrascale.tech the key is not sent to production ts-auth.");
  return publicAuthOrigin;
}

/** @param {unknown} token @returns {string} */
function requireToken(token) {
  if (typeof token !== "string" || token.trim() !== token || !tokenPattern.test(token))
    throw new TypeError("token must be a compact access JWT or a legacy C# tb_ API key.");
  return token;
}

/** @returns {boolean} */
function sendsUpgradeHeaders() {
  const runtime = /** @type {{ process?: { release?: { name?: string } }; Bun?: unknown; Deno?: unknown }} */ (globalThis);
  return runtime.process?.release?.name === "node" || runtime.Bun !== undefined || runtime.Deno !== undefined;
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

/** A browser hid a 307 behind an opaque redirect; only a table shape read may follow it. */
export class OpaqueRedirectError extends TypeError {
  constructor() {
    super("TerraBase answered with a redirect this runtime cannot follow.");
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
  /** @type {string | undefined} */
  #apiKeyBasic;
  /** @type {{ token: string; refreshAt: number; expiresAt: number } | undefined} */
  #cachedKeyToken;
  /** @type {Promise<string> | undefined} */
  #tokenFlight;
  /** @type {unknown} */
  #keyRefusal;
  /** @type {FetchLike} */
  #fetch;
  /** @type {number} */
  #maxResponseBytes;
  /** @type {SqlSocketConstructor | undefined} */
  #webSocket;
  #regionPin = "";
  #authOrigin = "";

  /** @param {TerraBaseV1ClientConfig | (import("./system.js").TerraBaseSystemClientConfig & { project?: undefined; endpointResolver?: undefined })} config */
  constructor(config) {
    if (typeof config !== "object" || config === null) throw new TypeError("TerraBase v1 client config is required.");
    this.#project = config.project === undefined ? undefined : requireProject(config.project);
    if (config.baseUrl !== undefined && config.endpointResolver !== undefined)
      throw new TypeError("Supply at most one of baseUrl or endpointResolver.");
    if (config.endpointResolver !== undefined && typeof config.endpointResolver !== "function")
      throw new TypeError("endpointResolver must be a function.");
    this.#resolver = config.endpointResolver ?? staticBaseUrlResolver(config.baseUrl ?? publicBaseUrl);
    const apiKey = "apiKey" in config ? config.apiKey : undefined;
    const authBaseUrl = "authBaseUrl" in config ? config.authBaseUrl : undefined;
    if (apiKey !== undefined) {
      if (config.token !== undefined) throw new TypeError("Supply exactly one of token or apiKey.");
      if (!/^[0-7][0-9A-HJKMNP-TV-Z]{25}_[0-9a-hjkmnp-tv-z]{52}$/u.test(apiKey))
        throw new TypeError("apiKey must be a canonical ULID, underscore and 52 lowercase Base32 characters.");
      this.#authOrigin = keyExchangeOrigin(authBaseUrl, config.endpointResolver === undefined ? canonicalBaseUrl(config.baseUrl ?? publicBaseUrl) : undefined);
      this.#apiKeyBasic = `Basic ${btoa(`${apiKey.slice(0, 26)}:${apiKey}`)}`;
    } else if (authBaseUrl !== undefined) throw new TypeError("authBaseUrl exchanges an apiKey; set apiKey or remove authBaseUrl.");
    if (apiKey === undefined && (config.project !== undefined || config.token !== undefined) && typeof config.token !== "function")
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
    const webSocket = "WebSocket" in config ? config.WebSocket : undefined;
    if (webSocket !== undefined && typeof webSocket !== "function") throw new TypeError("WebSocket must be a constructor.");
    this.#webSocket = webSocket ?? /** @type {SqlSocketConstructor | undefined} */ (/** @type {unknown} */ (globalThis.WebSocket));
    if ("region" in config && config.region !== undefined && (typeof config.region !== "string" || config.region.trim() !== "")) this.#regionPin = regionId(config.region);
  }

  /**
   * Opens the SQL WebSocket. The credential goes in the upgrade's Authorization header where the runtime allows it
   * (Node, Bun, Deno), and otherwise in the first frame (browsers); never in the URL.
   * @param {Readonly<Record<string, string>>} params
   * @param {AbortSignal | undefined} signal
   * @returns {Promise<import("./sql-socket.js").V1SqlSocket>}
   */
  async openSqlSocket(params, signal) {
    const WebSocketClass = this.#webSocket;
    if (WebSocketClass === undefined) throw new TypeError("This runtime has no WebSocket; pass one as the WebSocket client option.");
    const url = new URL(await this.url("connectSql", params, signal));
    url.protocol = url.protocol === "https:" ? "wss:" : "ws:";
    const token = await this.#accessToken(signal);
    if (sendsUpgradeHeaders()) {
      const headers = { authorization: `Bearer ${token}` };
      if (this.#regionPin !== "") Object.assign(headers, { "X-TerraScale-Region-Pin": this.#regionPin });
      return V1SqlSocket.open(new WebSocketClass(url.href, { protocols: [sqlSocketProtocol], headers }), signal);
    }
    return V1SqlSocket.open(new WebSocketClass(url.href, [sqlSocketProtocol]), signal, token);
  }

  /** @param {Readonly<Record<string, string>>} params @param {URLSearchParams} query @param {AbortSignal} signal */
  async openShapeHints(params, query, signal) {
    const WebSocketClass = this.#webSocket;
    if (WebSocketClass === undefined) return undefined;
    const url = new URL(await this.url("connectShapeHints", params, signal));
    url.search = query.toString();
    url.protocol = url.protocol === "https:" ? "wss:" : "ws:";
    const token = await this.#accessToken(signal);
    if (sendsUpgradeHeaders()) {
      const headers = { authorization: `Bearer ${token}` };
      if (this.#regionPin !== "") Object.assign(headers, { "X-TerraScale-Region-Pin": this.#regionPin });
      return ShapeHints.open(new WebSocketClass(url.href, { protocols: [shapeHintProtocol], headers }), signal, undefined);
    }
    return ShapeHints.open(new WebSocketClass(url.href, [shapeHintProtocol]), signal, token);
  }

  /** Internal credential lifetime metadata; never exposes the bearer value. */
  get hasTokenProvider() { return typeof this.#token === "function" || this.#apiKeyBasic !== undefined; }

  /** @param {AbortSignal | undefined} signal @returns {Promise<string>} */
  async #accessToken(signal) {
    signal?.throwIfAborted();
    if (this.#apiKeyBasic !== undefined) {
      if (this.#keyRefusal !== undefined) throw this.#keyRefusal;
      const now = performance.now();
      const cached = this.#cachedKeyToken;
      const valid = cached !== undefined && now < cached.expiresAt;
      if (valid && now < cached.refreshAt) return cached.token;
      if (this.#tokenFlight === undefined) {
        this.#tokenFlight = Promise.resolve().then(() => this.#exchangeKey()).finally(() => { this.#tokenFlight = undefined; });
        // A failed background exchange is observed. Missing/expired callers
        // still await the same result, independently of caller cancellation.
        void this.#tokenFlight.catch(() => {});
      }
      if (valid) return cached.token;
      return this.#waitForToken(this.#tokenFlight, signal);
    }
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

  /** @returns {Promise<string>} */
  async #exchangeKey() {
    const started = performance.now();
    try {
      const response = await this.#authRequest("/terrascale/oauth/token", new URLSearchParams({
        grant_type: "client_credentials", project: requireProject(this.#project),
      }).toString(), "application/x-www-form-urlencoded", undefined);
      const raw = object(response, "API key token");
      if (typeof raw.expires_in !== "number" || !Number.isSafeInteger(raw.expires_in) || raw.expires_in <= 0)
        throw new TypeError("ts-auth returned an invalid token lifetime.");
      const lifetime = raw.expires_in * 1000;
      const expiresAt = started + lifetime;
      if (!Number.isFinite(expiresAt) || performance.now() >= expiresAt)
        throw new TypeError("ts-auth's token expired before it arrived.");
      const token = requireToken(raw.access_token);
      if (this.#keyRefusal !== undefined) throw this.#keyRefusal;
      this.#cachedKeyToken = { token, refreshAt: started + lifetime - Math.min(60_000, lifetime / 2), expiresAt };
      return token;
    } catch (error) {
      if (this.#cachedKeyToken !== undefined) this.#cachedKeyToken.refreshAt = performance.now() + 1000;
      throw error;
    }
  }

  /** @param {Promise<string>} pending @param {AbortSignal | undefined} signal @returns {Promise<string>} */
  async #waitForToken(pending, signal) {
    if (signal === undefined) return pending;
    /** @type {() => void} */
    let abort = () => {};
    try {
      return await new Promise((resolve, reject) => {
        abort = () => reject(signal.reason);
        signal.addEventListener("abort", abort, { once: true });
        if (signal.aborted) abort();
        void pending.then(resolve, reject);
      });
    } finally {
      signal.removeEventListener("abort", abort);
    }
  }

  /** @param {string} body @param {AbortSignal | undefined} signal @returns {Promise<JsonValue | undefined>} */
  createScopedKey(body, signal) {
    return this.#authRequest("/v1/terrascale/api-keys/self/scoped-keys", body, "application/json", signal);
  }

  /** @param {string} path @param {string} body @param {string} contentType @param {AbortSignal | undefined} signal @returns {Promise<JsonValue | undefined>} */
  async #authRequest(path, body, contentType, signal) {
    if (this.#apiKeyBasic === undefined) throw new TypeError("createScopedKey requires the client's apiKey.");
    const bounded = signal === undefined ? AbortSignal.timeout(30_000) : AbortSignal.any([signal, AbortSignal.timeout(30_000)]);
    bounded.throwIfAborted();
    const bytes = new TextEncoder().encode(body);
    /** @type {RequestInit & { duplex?: "half" }} */
    const init = { method: "POST", redirect: "manual", signal: bounded,
      headers: { authorization: this.#apiKeyBasic, "content-type": contentType, accept: "application/json" }, body };
    if (typeof process !== "undefined" && process.release?.name === "node") {
      init.body = new ReadableStream({ start(controller) { controller.enqueue(bytes); controller.close(); } });
      init.duplex = "half";
    }
    const response = await this.#fetch(`${this.#authOrigin}${path}`, init);
    const text = await this.readText(response, bounded, 64 * 1024);
    if (response.status !== (path.endsWith("scoped-keys") ? 201 : 200)) {
      const error = parseV1Error(response.status, response.headers, text);
      if (path.endsWith("/token") && response.status === 401) {
        this.#keyRefusal = error;
        this.#cachedKeyToken = undefined;
      }
      throw error;
    }
    return V1Transport.#decodeJson(response, text);
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
   * @param {number} [maximumBytes]
   * @returns {Promise<string>}
   */
  async readText(response, signal, maximumBytes = this.#maxResponseBytes) {
    const bytes = await this.readBytes(response, signal, maximumBytes);
    return bytes.byteLength === 0 ? "" : new TextDecoder("utf-8", { fatal: true, ignoreBOM: true }).decode(bytes);
  }

  /**
   * Reads a response body as owned bytes within the response byte bound. A
   * connection failure while reading is a BodyTransportError.
   * @param {Response} response
   * @param {AbortSignal | undefined} [signal]
   * @param {number} [maximumBytes]
   * @returns {Promise<Uint8Array>}
   */
  async readBytes(response, signal, maximumBytes = this.#maxResponseBytes) {
    signal?.throwIfAborted();
    const reader = response.body?.getReader();
    if (reader === undefined) return new Uint8Array(0);
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
        if (size > maximumBytes)
          throw new TypeError(`TerraBase response exceeds ${maximumBytes} bytes.`);
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
    return bytes;
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
      return { status: response.status, headers: response.headers, value: response.status === 307 ? undefined : await this.readJson(response, scoped.signal),
        ...(response.redirected ? { redirectedTo: response.url } : {}) };
    } finally {
      deadline.clear();
    }
  }

  /**
   * Calls a named producer route whose success body is owned raw bytes instead of JSON,
   * with the original status and media type. Non-2xx refusals throw TerraBaseV1Error.
   * @param {V1Operation} operation
   * @param {Readonly<Record<string, string>>} params
   * @param {V1CallOptions} [options]
   * @returns {Promise<{ status: number; contentType: string; body: Uint8Array }>}
   */
  async raw(operation, params, options = {}) {
    const response = await this.#send(operation, params, options, Infinity);
    return { status: response.status, contentType: response.headers.get("content-type") ?? "", body: await this.readBytes(response, options.signal) };
  }

  /**
   * Calls a named producer route that answers with UTF-8 text, not JSON, and returns it whole.
   * @param {V1Operation} operation
   * @param {Readonly<Record<string, string>>} params
   * @param {V1CallOptions} [options]
   * @returns {Promise<string>}
   */
  async text(operation, params, options = {}) {
    const deadline = operationDeadline(operation, options.signal);
    try {
      const scoped = { ...options, signal: deadline.signal };
      const response = await this.#send(operation, params, scoped, deadline.expiresAt);
      const mediaType = response.headers.get("content-type")?.split(";", 1)[0]?.trim().toLowerCase();
      if (mediaType !== "text/plain")
        throw new TypeError(`TerraBase returned ${mediaType ?? "no media type"} instead of text/plain.`);
      return await this.readText(response, scoped.signal);
    } finally {
      deadline.clear();
    }
  }

  /**
   * Calls a named producer route that answers with a body to stream, not JSON. The
   * caller consumes and cancels the returned body; only header-time failures are retried here.
   * @param {V1Operation} operation
   * @param {Readonly<Record<string, string>>} params
   * @param {AbortSignal | undefined} [signal]
   * @returns {Promise<ReadableStream<Uint8Array>>}
   */
  async stream(operation, params, signal) {
    const deadline = operationDeadline(operation, signal);
    try {
      const response = await this.#send(operation, params, { signal: deadline.signal }, deadline.expiresAt);
      if (response.body === null) throw new TypeError("TerraBase returned no stream body.");
      return response.body;
    } finally {
      deadline.clear();
    }
  }

  /** @param {string} url @param {number} size @param {AbortSignal} signal */
  async readShapeObject(url, size, signal) {
    const target = new URL(url);
    if (target.protocol !== "https:" || target.username !== "" || target.password !== "" || target.hash !== "" || !target.pathname.startsWith("/v1/shapes/")
      || !Number.isSafeInteger(size) || size < 90 || size > 8 * 1024 * 1024 + 250 || size > this.#maxResponseBytes)
      throw new TypeError("Invalid sealed shape object.");
    const bounded = AbortSignal.any([signal, AbortSignal.timeout(30_000)]);
    const response = await this.#fetch(target.href, { method: "GET", credentials: "omit", redirect: "error", referrerPolicy: "no-referrer", signal: bounded });
    if (response.status !== 200) {
      await response.body?.cancel();
      throw new TypeError("The sealed shape object is unavailable.");
    }
    const reader = response.body?.getReader();
    if (reader === undefined) throw new TypeError("The sealed shape object is empty.");
    const bytes = new Uint8Array(size);
    let at = 0;
    try {
      for (;;) {
        bounded.throwIfAborted();
        const part = await reader.read();
        if (part.done) break;
        if (at + part.value.length > size) throw new TypeError("The sealed shape object exceeds its size.");
        bytes.set(part.value, at);
        at += part.value.length;
      }
      if (at !== size) throw new TypeError("The sealed shape object has the wrong size.");
      return bytes;
    } finally {
      await reader.cancel().catch(() => undefined);
      reader.releaseLock();
    }
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
      if (route.permission !== "anonymous" && (this.#token !== undefined || this.#apiKeyBasic !== undefined))
        headers.set("authorization", `Bearer ${await this.#accessToken(signal)}`);
      if (route.responseMediaType !== "") headers.set("accept", route.responseMediaType);
      if (options.body !== undefined) headers.set("content-type", options.contentType ?? "application/json");
      if (this.#regionPin !== "") headers.set("X-TerraScale-Region-Pin", this.#regionPin);
      /** @type {RequestInit & { duplex?: "half" }} */
      const init = { method: route.method, headers, redirect: options.redirect ?? "manual" };
      if (route.permission.endsWith(":write")) {
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
        if (response.status === 401 && this.#apiKeyBasic !== undefined) {
          if (["key_revoked", "key_expired", "invalid_key"].includes(error.code)) this.#keyRefusal = error;
          if (headers.get("authorization") === `Bearer ${this.#cachedKeyToken?.token}`) this.#cachedKeyToken = undefined;
        }
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
const selfRecovering = new Set(["readTableShape"]);
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
  return v1Route(operation).method === "GET";
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
