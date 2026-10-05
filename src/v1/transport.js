// Internal typed transport for the TerraBase v1 client. It is not exported
// from any package entrypoint: callers reach it only through named operations.
import { parseV1Error } from "./errors.js";
import { requireName, requireProject } from "./ids.js";
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
const tokenPattern = /^tb_[A-Za-z0-9_-]{43}$/u;
/**
 * Resolver input contains no credential. A successful URL is cached for this client/database.
 * @typedef {{ readonly project: string; readonly database: string; readonly signal?: AbortSignal | undefined }} V1EndpointContext
 */
/** @typedef {(context: V1EndpointContext) => string | Promise<string>} V1EndpointResolver */
/**
 * Project-scoped C# Documents API client. Supply exactly one of baseUrl or endpointResolver.
 * URLs must use HTTPS or loopback HTTP. Mutations are never automatically replayed.
 * @typedef {object} TerraBaseV1ClientConfig
 * @property {string} project
 * @property {string} token A tb_ API key; held privately.
 * @property {string | undefined} [baseUrl]
 * @property {V1EndpointResolver | undefined} [endpointResolver]
 * @property {FetchLike | undefined} [fetch]
 * @property {number | undefined} [maxResponseBytes] Default 64 MiB, including query framing and escaping.
 */

/**
 * @typedef {object} V1CallOptions
 * @property {string} [body]
 * @property {string} [contentType]
 * @property {Readonly<Record<string, string>>} [headers] Contract headers only (If-Match, If-None-Match).
 * @property {URLSearchParams | undefined} [query]
 * @property {AbortSignal | undefined} [signal]
 */

/**
 * @typedef {object} V1CallResult
 * @property {number} status
 * @property {Headers} headers
 * @property {JsonValue | undefined} value
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

/** A response body that failed in transit after its headers arrived. */
export class BodyTransportError extends Error {
  /** @param {unknown} cause */
  constructor(cause) {
    super("TerraBase response body failed in transit.", { cause });
    this.name = "BodyTransportError";
  }
}

/** Internal transport bound to one data key. */
export class V1Transport {
  /** @type {string} */
  #project;
  /** @type {V1EndpointResolver} */
  #resolver;
  /** @type {Map<string, { promise: Promise<string>; timer: ReturnType<typeof setTimeout>; waiters: number }>} */
  #bases = new Map();
  /** @type {string} */
  #token;
  /** @type {FetchLike} */
  #fetch;
  /** @type {number} */
  #maxResponseBytes;

  /** @param {TerraBaseV1ClientConfig} config */
  constructor(config) {
    if (typeof config !== "object" || config === null) throw new TypeError("TerraBase v1 client config is required.");
    this.#project = requireProject(config.project);
    if ((config.baseUrl === undefined) === (config.endpointResolver === undefined))
      throw new TypeError("Supply exactly one of baseUrl or endpointResolver.");
    if (config.endpointResolver !== undefined && typeof config.endpointResolver !== "function")
      throw new TypeError("endpointResolver must be a function.");
    this.#resolver = config.endpointResolver ?? staticBaseUrlResolver(/** @type {string} */ (config.baseUrl));
    if (typeof config.token !== "string" || !tokenPattern.test(config.token))
      throw new TypeError("token must be a tb_ API key with 43 base64url characters.");
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

  /**
   * @param {V1Operation} operation
   * @param {Readonly<Record<string, string>>} params
   * @param {AbortSignal | undefined} [signal]
   * @returns {Promise<string>}
   */
  async url(operation, params, signal) {
    signal?.throwIfAborted();
    const database = requireName("database", params.database);
    let base = this.#bases.get(database);
    if (base === undefined) {
      const controller = new AbortController();
      const timer = setTimeout(() => controller.abort(new DOMException(
        "TerraBase endpoint resolution timed out after 30 seconds.", "TimeoutError",
      )), 30_000);
      /** @type {Promise<string>} */
      const promise = new Promise((resolve, reject) => {
        controller.signal.addEventListener("abort", () => reject(controller.signal.reason), { once: true });
        void Promise.resolve().then(() => this.#resolver(Object.freeze({
          project: this.#project, database, signal: controller.signal,
        }))).then(canonicalBaseUrl).then(resolve, reject).finally(() => clearTimeout(timer));
      });
      base = { promise, timer, waiters: 0 };
      this.#bases.set(database, base);
      // A failed resolution is not a session; a later operation may resolve again.
      void promise.catch(() => { if (this.#bases.get(database) === base) this.#bases.delete(database); });
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
      return `${resolved}${v1RoutePath(v1Route(operation), { ...params, project: this.#project })}`;
    } finally {
      // Keep Node alive only while callers are waiting for this resolution.
      if (--base.waiters === 0 && typeof base.timer === "object") base.timer.unref();
    }
  }

  /**
   * Reads a response body as UTF-8 text within the response byte bound. A
   * connection failure while reading is a BodyTransportError.
   * @param {Response} response
   * @returns {Promise<string>}
   */
  async readText(response) {
    const reader = response.body?.getReader();
    if (reader === undefined) return "";
    /** @type {Uint8Array[]} */
    const parts = [];
    let size = 0;
    try {
      while (true) {
        /** @type {ReadableStreamReadResult<Uint8Array>} */
        let part;
        try {
          part = await reader.read();
        } catch (error) {
          throw new BodyTransportError(error);
        }
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
    return new TextDecoder("utf-8", { fatal: true }).decode(bytes);
  }

  /**
   * Parses a JSON response body exactly.
   * @param {Response} response
   * @returns {Promise<JsonValue | undefined>}
   */
  async readJson(response) {
    return V1Transport.#decodeJson(response, await this.readText(response));
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
   * @returns {Promise<TerraBaseV1Error>}
   */
  async error(response) {
    let text = "";
    try {
      text = await this.readText(response);
    } catch {
      text = "";
    }
    return parseV1Error(response.status, response.headers, text);
  }

  /**
   * Calls only a named producer route. Redirects and failed mutations are never replayed.
   * @param {V1Operation} operation
   * @param {Readonly<Record<string, string>>} params
   * @param {V1CallOptions} [options]
   * @returns {Promise<V1CallResult>}
   */
  async call(operation, params, options = {}) {
    const route = v1Route(operation);
    if (options.body !== undefined && new TextEncoder().encode(options.body).byteLength > maximumRequestBytes)
      throw new TypeError(`TerraBase request bodies are limited to ${maximumRequestBytes} bytes.`);
    let url = await this.url(operation, params, options.signal);
    if (options.query !== undefined && options.query.size > 0) url += `?${options.query}`;
    const headers = new Headers(options.headers);
    headers.set("authorization", `Bearer ${this.#token}`);
    headers.set("accept", route.responseMediaType);
    if (options.body !== undefined) headers.set("content-type", options.contentType ?? "application/json");
    /** @type {RequestInit} */
    const init = { method: route.method, headers, redirect: "manual" };
    if (options.body !== undefined) init.body = options.body;
    if (options.signal !== undefined) init.signal = options.signal;
    const response = await this.#fetch(url, init);
    if (response.type === "opaqueredirect")
      throw new TypeError("Fresh shape bootstrap requires server fetch; obtain a handle and snapshot offset with collection.shape() on your server.");
    if (!route.successStatuses.includes(response.status)) throw await this.error(response);
    if (response.status === 307) {
      await response.body?.cancel();
      return { status: response.status, headers: response.headers, value: undefined };
    }
    return { status: response.status, headers: response.headers, value: await this.readJson(response) };
  }
}
