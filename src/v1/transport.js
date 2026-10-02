// Internal typed transport for the TerraBase v1 client. It is not exported
// from any package entrypoint: callers reach it only through named operations.
import { parseV1Error } from "./errors.js";
import { newIdempotencyKey } from "./ids.js";
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
const defaultMaximumResponseBytes = 64 * 1024 * 1024;
const defaultMaxRetries = 3;
const maximumRetryWaitMs = 30_000;
const tokenPattern = /^tbk1\.[A-Za-z0-9_-]{22}\.[A-Za-z0-9_-]{43}$/u;
const idempotencyKeyPattern = /^[A-Za-z0-9_.-]{1,64}$/u;
const transient503 = new Set([
  "capacity",
  "storage_unavailable",
  "dependencies_unavailable",
]);

/**
 * - `baseUrl`: the TerraBase origin, for example `https://gru.terrabase.example`.
 *   HTTPS is required; plain HTTP is accepted only for loopback hosts. A path
 *   prefix is kept; credentials, a query or a fragment are rejected.
 * - `token`: the `tbk1.<id>.<secret>` data key. It is held privately and sent
 *   only as the `Authorization` header to `baseUrl`.
 * - `fetch`: a fetch implementation; defaults to `globalThis.fetch`.
 * - `maxRetries`: retry budget for 429 and retryable 503s (default 3, 0 disables).
 * - `maxResponseBytes`: bound for one response body (default 64 MiB).
 * - `autoIdempotencyKey`: give every mutation (put, create, patch, delete,
 *   transaction commit) that has no `idempotencyKey` a fresh random one, so
 *   transport failures, transient 503s, `commit_uncertain` and
 *   `internal_error` are retried with the same key (default false).
 * @typedef {object} TerraBaseV1ClientConfig
 * @property {string} baseUrl
 * @property {string} token
 * @property {FetchLike | undefined} [fetch]
 * @property {number | undefined} [maxRetries]
 * @property {number | undefined} [maxResponseBytes]
 * @property {boolean | undefined} [autoIdempotencyKey]
 */

/**
 * @typedef {object} V1CallOptions
 * @property {string} [body]
 * @property {string} [contentType]
 * @property {Readonly<Record<string, string>>} [headers] Contract headers only (If-Match, If-None-Match).
 * @property {string | undefined} [idempotencyKey]
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
 * @param {string | undefined} key
 * @returns {string | undefined}
 */
export function requireIdempotencyKey(key) {
  if (key !== undefined && (typeof key !== "string" || !idempotencyKeyPattern.test(key)))
    throw new TypeError("Idempotency-Key must be 1-64 characters of [A-Za-z0-9_.-].");
  return key;
}

/**
 * @param {number} attempt
 * @returns {number} Milliseconds.
 */
export function backoffMs(attempt) {
  const ceiling = Math.min(100 * 2 ** Math.min(attempt, 5), 2000);
  return ceiling / 2 + Math.random() * (ceiling / 2);
}

/**
 * @param {number} ms
 * @param {AbortSignal | undefined} signal
 * @returns {Promise<void>}
 */
export function sleep(ms, signal) {
  return new Promise((resolve, reject) => {
    if (signal?.aborted) {
      reject(signal.reason);
      return;
    }
    /** @returns {void} */
    const abort = () => {
      clearTimeout(timer);
      reject(signal?.reason);
    };
    const timer = setTimeout(() => {
      signal?.removeEventListener("abort", abort);
      resolve();
    }, Math.max(0, ms));
    signal?.addEventListener("abort", abort, { once: true });
  });
}

/**
 * The contract retry policy: 429 is always retried (the request was not
 * admitted); capacity, storage_unavailable and dependencies_unavailable only
 * for replay-safe requests (reads, queries, the shape gatekeeper, shape reads
 * and keyed writes); commit_uncertain (503) and internal_error (500) only with
 * an Idempotency-Key. Nothing else is retried here. A `Retry-After` longer
 * than 30 seconds is not shortened: the error is returned with `retryAfter`
 * so the caller can decide whether to wait that long.
 * @param {TerraBaseV1Error} error
 * @param {number} attempt
 * @param {{ readonly replaySafe: boolean; readonly keyed: boolean }} request
 * @returns {number | undefined} Delay in milliseconds, or undefined.
 */
export function retryDelayMs(error, attempt, request) {
  const retryable =
    error.status === 429 ||
    (error.status === 503 &&
      ((transient503.has(error.code) && (request.replaySafe || request.keyed)) ||
        (error.code === "commit_uncertain" && request.keyed))) ||
    (error.status === 500 && error.code === "internal_error" && request.keyed);
  return retryable ? retryAfterDelayMs(error, attempt) : undefined;
}

/**
 * The delay before retrying a retryable error: its `Retry-After`, or backoff.
 * A `Retry-After` above 30 seconds is honored by not retrying at all.
 * @param {TerraBaseV1Error} error
 * @param {number} attempt
 * @returns {number | undefined}
 */
export function retryAfterDelayMs(error, attempt) {
  if (error.retryAfter === undefined) return backoffMs(attempt);
  const requested = error.retryAfter * 1000;
  return requested > maximumRetryWaitMs ? undefined : requested;
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
  #base;
  /** @type {string} */
  #token;
  /** @type {FetchLike} */
  #fetch;
  /** @type {number} */
  #maxRetries;
  /** @type {number} */
  #maxResponseBytes;
  /** @type {boolean} */
  #autoIdempotencyKey;

  /** @param {TerraBaseV1ClientConfig} config */
  constructor(config) {
    if (typeof config !== "object" || config === null)
      throw new TypeError("TerraBase v1 client config is required.");
    const url = requireServiceUrl(config.baseUrl, "baseUrl");
    if (url.search !== "") throw new TypeError("baseUrl must not carry a query.");
    if (typeof config.token !== "string" || !tokenPattern.test(config.token))
      throw new TypeError("token must be a tbk1.<id>.<secret> key.");
    const fetchHandler = config.fetch ?? globalThis.fetch?.bind(globalThis);
    if (typeof fetchHandler !== "function")
      throw new TypeError("TerraBase v1 client requires a fetch implementation.");
    const maxRetries = config.maxRetries ?? defaultMaxRetries;
    if (!Number.isInteger(maxRetries) || maxRetries < 0 || maxRetries > 16)
      throw new TypeError("maxRetries must be an integer between 0 and 16.");
    const maxResponseBytes = config.maxResponseBytes ?? defaultMaximumResponseBytes;
    if (!Number.isInteger(maxResponseBytes) || maxResponseBytes < 1)
      throw new TypeError("maxResponseBytes must be a positive integer.");
    if (config.autoIdempotencyKey !== undefined && typeof config.autoIdempotencyKey !== "boolean")
      throw new TypeError("autoIdempotencyKey must be a boolean.");
    this.#autoIdempotencyKey = config.autoIdempotencyKey === true;
    this.#base = `${url.origin}${url.pathname.replace(/\/+$/u, "")}`;
    this.#token = config.token;
    this.#fetch = fetchHandler;
    this.#maxRetries = maxRetries;
    this.#maxResponseBytes = maxResponseBytes;
  }

  /** @returns {number} */
  get maxRetries() {
    return this.#maxRetries;
  }

  /**
   * @param {V1Operation} operation
   * @param {Readonly<Record<string, string>>} params
   * @returns {string}
   */
  url(operation, params) {
    return `${this.#base}${v1RoutePath(v1Route(operation), params)}`;
  }

  /**
   * One HTTP exchange without retries. `token` replaces the data key (edge
   * shape tokens); redirects are never followed with credentials unless
   * `redirect` says so.
   * @param {string} url
   * @param {{ method: string; accept: string; token?: string | undefined; body?: string | undefined; contentType?: string | undefined; headers?: Readonly<Record<string, string>> | undefined; idempotencyKey?: string | undefined; signal?: AbortSignal | undefined; redirect?: RequestRedirect | undefined }} init
   * @returns {Promise<Response>}
   */
  exchange(url, init) {
    const headers = new Headers(init.headers);
    headers.set("authorization", `Bearer ${init.token ?? this.#token}`);
    headers.set("accept", init.accept);
    if (init.body !== undefined)
      headers.set("content-type", init.contentType ?? "application/json");
    if (init.idempotencyKey !== undefined)
      headers.set("idempotency-key", init.idempotencyKey);
    /** @type {RequestInit} */
    const request = {
      method: init.method,
      headers,
      redirect: init.redirect ?? "manual",
    };
    if (init.body !== undefined) request.body = init.body;
    if (init.signal !== undefined) request.signal = init.signal;
    return this.#fetch(url, request);
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
   * Calls a v1 route with the contract retry policy.
   * @param {V1Operation} operation
   * @param {Readonly<Record<string, string>>} params
   * @param {V1CallOptions} [options]
   * @returns {Promise<V1CallResult>}
   */
  async call(operation, params, options = {}) {
    const route = v1Route(operation);
    const url = `${this.#base}${v1RoutePath(route, params)}`;
    if (options.body !== undefined && new TextEncoder().encode(options.body).byteLength > maximumRequestBytes)
      throw new TypeError(`TerraBase request bodies are limited to ${maximumRequestBytes} bytes.`);
    const idempotencyKey = requireIdempotencyKey(options.idempotencyKey) ??
      (this.#autoIdempotencyKey && route.idempotencyKey === "optional" ? newIdempotencyKey() : undefined);
    const keyed = idempotencyKey !== undefined;
    const replaySafe = route.replaySafe || keyed;
    for (let attempt = 0; ; attempt++) {
      /** @type {Response} */
      let response;
      try {
        response = await this.exchange(url, {
          method: route.method,
          accept: route.responseMediaType,
          body: options.body,
          contentType: options.contentType,
          headers: options.headers,
          idempotencyKey,
          signal: options.signal,
        });
      } catch (error) {
        if (options.signal?.aborted || !replaySafe || attempt >= this.#maxRetries) throw error;
        await sleep(backoffMs(attempt), options.signal);
        continue;
      }
      if (route.successStatuses.includes(response.status)) {
        /** @type {string} */
        let text;
        try {
          text = await this.readText(response);
        } catch (error) {
          // A body cut off in transit is a transport failure: replay-safe
          // requests retry it; malformed media or JSON below are not retried.
          if (options.signal?.aborted) throw options.signal.reason;
          if (!(error instanceof BodyTransportError) || !replaySafe || attempt >= this.#maxRetries) throw error;
          await sleep(backoffMs(attempt), options.signal);
          continue;
        }
        return { status: response.status, headers: response.headers, value: V1Transport.#decodeJson(response, text) };
      }
      const failure = await this.error(response);
      const delay = retryDelayMs(failure, attempt, { replaySafe: route.replaySafe, keyed });
      if (delay === undefined || attempt >= this.#maxRetries) throw failure;
      await sleep(delay, options.signal);
    }
  }
}

