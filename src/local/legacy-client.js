// Deprecated: internal transport for the retired SQLite emulator only.
import {
  decodeDatabaseCommitReceipt, decodeDatabasePoint, decodeDatabaseQueryPage,
  decodeDatabaseSubscription, decodeDatabaseStatus, encodeDatabaseStatus, encodeDatabaseCommit, encodeDatabaseQuery,
  encodeDatabaseSubscription, encodeDatabaseAuthority, parseDatabaseJson, requireDatabaseU64, requireDatabaseUuid,
} from "../database-codec.js";
/**
 * @import {
 *   DatabaseAuthority, DatabaseCommit, DatabaseCommitReceipt, DatabasePoint,
 *   DatabaseQuery, DatabaseQueryPage, DatabaseResult, DatabaseScope,
 *   DatabaseSubscriptionRequest, DatabaseSubscriptionResponse, DatabaseTransactionIdentity,
 *   DatabaseTransactionStatus,
 * } from "../database-types.js"
 * @import { FetchLike } from "../config.js"
 */

const maximumResponseBytes = 4 * 1024 * 1024;

/**
 * Internal options for the deprecated local emulator. Remote origins are refused.
 * @typedef {DatabaseScope & {
 *   readonly apiKey: string | (() => string | Promise<string>);
 *   readonly endpoint?: string;
 *   readonly discoveryTimeoutMs?: number;
 *   readonly requestTimeoutMs?: number;
 *   readonly fetch?: FetchLike;
 * }} TerraBaseClientConfig
 */

/** @typedef {{ readonly signal?: AbortSignal }} DatabaseRequestOptions */

// Type-level brand only; the symbol is never attached to a value.
const preparedBrand = Symbol("preparedBrand");
/** @typedef {DatabaseTransactionIdentity & { readonly [preparedBrand]: true }} PreparedDatabaseCommit */
/** @type {WeakMap<PreparedDatabaseCommit, { readonly client: TerraBaseClient; readonly scope: string; readonly endpoint: string | undefined; readonly body: string }>} */
const preparedRequests = new WeakMap();

/**
 * @param {number | undefined} value
 * @param {number} defaultValue
 * @param {number} maximum
 * @returns {number}
 */
function timeout(value, defaultValue, maximum) {
  const resolved = value ?? defaultValue;
  if (!Number.isInteger(resolved) || resolved < 1 || resolved > maximum) throw new TypeError("Timeout is outside its bounds.");
  return resolved;
}

/**
 * @param {DatabaseAuthority} authority
 * @returns {DatabaseAuthority}
 */
function identityAuthority(authority) {
  return authority.mode === "leaderless" ? Object.freeze({ ...authority })
    : Object.freeze({ ...authority, writer: Object.freeze({ ...authority.writer }) });
}

/**
 * @param {DatabaseScope} scope
 * @returns {string}
 */
function scopePath(scope) {
  return `/v1/tenants/${requireDatabaseUuid(scope.tenant, "tenant")}/databases/${requireDatabaseUuid(scope.database, "database")}/shards/${requireDatabaseU64(scope.shard, "shard")}`;
}

/**
 * @param {unknown} raw
 * @returns {string}
 */
function routeError(raw) {
  if (typeof raw !== "object" || raw === null || Array.isArray(raw) || Object.keys(raw).some(key => key !== "error")) return "invalid_response";
  const error = /** @type {Record<string, unknown>} */ (raw).error;
  if (typeof error !== "object" || error === null || Array.isArray(error)) return "invalid_response";
  const value = /** @type {Record<string, unknown>} */ (error);
  if (Object.keys(value).some(key => key !== "code")) return "invalid_response";
  return typeof value.code === "string" && /^[a-z][a-z0-9_]{0,63}$/u.test(value.code) ? value.code : "invalid_response";
}

/**
 * @param {Response} response
 * @param {AbortSignal} signal
 * @returns {Promise<unknown>}
 */
async function responseJson(response, signal) {
  const mediaType = response.headers.get("content-type")?.split(";", 1)[0]?.trim().toLowerCase();
  if (mediaType !== "application/json") {
    void response.body?.cancel().catch(() => undefined);
    throw new TypeError("Expected application/json.");
  }
  const reader = response.body?.getReader();
  if (reader === undefined) throw new TypeError("Response has no JSON body.");
  /** @type {Uint8Array[]} */
  const parts = [];
  let size = 0;
  try {
    while (true) {
      /** @type {ReadableStreamReadResult<Uint8Array>} */
      let part;
      try { part = await withCancellation(reader.read(), signal); }
      catch (error) {
        if (signal.aborted) throw error;
        throw new Error("Native response transport failed.", { cause: error });
      }
      if (part.done) break;
      size += part.value.length;
      if (size > maximumResponseBytes) throw new TypeError("Response exceeds its byte limit.");
      parts.push(part.value);
    }
  } catch (error) {
    void reader.cancel().catch(() => undefined);
    throw error;
  } finally { reader.releaseLock(); }
  const bytes = new Uint8Array(size);
  let offset = 0;
  for (const part of parts) { bytes.set(part, offset); offset += part.length; }
  return parseDatabaseJson(bytes);
}

/**
 * @template T
 * @param {Promise<T>} promise
 * @param {AbortSignal} signal
 * @param {(value: T) => void} [disposeLateValue]
 * @returns {Promise<T>}
 */
function withCancellation(promise, signal, disposeLateValue) {
  return new Promise((resolve, reject) => {
    /** @returns {void} */
    const cleanup = () => signal.removeEventListener("abort", abort);
    /** @returns {void} */
    const abort = () => {
      cleanup();
      reject(signal.reason);
    };
    if (signal.aborted) abort();
    else signal.addEventListener("abort", abort, { once: true });
    // Observe the started operation even if it aborted synchronously before
    // this wrapper ran; a late fetch result still owns a response body.
    void promise.then(
      value => {
        cleanup();
        if (signal.aborted) {
          try {
            disposeLateValue?.(value);
          } catch {
            // Cleanup cannot replace the terminal cancellation.
          }
          reject(signal.reason);
        } else resolve(value);
      },
      error => {
        cleanup();
        reject(signal.aborted ? signal.reason : error);
      },
    );
  });
}

/** Current scoped document client. All mutations are server commits in one declared shard. */
export class TerraBaseClient {
  /**
   * @readonly
   * @type {DatabaseScope}
   */
  #scope;
  /**
   * @readonly
   * @type {TerraBaseClientConfig}
   */
  #config;
  /**
   * @readonly
   * @type {FetchLike}
   */
  #fetch;
  /**
   * @readonly
   * @type {string}
   */
  #path;
  /**
   * @readonly
   * @type {string | undefined}
   */
  #endpoint;
  /**
   * @readonly
   * @type {number}
   */
  #requestTimeoutMs;

  /** @returns {DatabaseScope} */
  get scope() { return this.#scope; }

  /** @param {TerraBaseClientConfig} config */
  constructor(config) {
    this.#scope = Object.freeze({ tenant: requireDatabaseUuid(config.tenant, "tenant"), database: requireDatabaseUuid(config.database, "database"), shard: requireDatabaseU64(config.shard, "shard") });
    this.#config = { ...config };
    this.#fetch = config.fetch ?? globalThis.fetch.bind(globalThis);
    this.#path = scopePath(this.#scope);
    const endpoint = new URL(config.endpoint ?? "http://127.0.0.1");
    if (endpoint.protocol !== "http:" || !["127.0.0.1", "localhost", "[::1]"].includes(endpoint.hostname) || endpoint.username || endpoint.password || endpoint.search || endpoint.hash || endpoint.pathname !== "/")
      throw new TypeError("The retired SQLite client only supports a local emulator origin.");
    this.#endpoint = endpoint.origin;
    this.#requestTimeoutMs = timeout(config.requestTimeoutMs, 30_000, 300_000);
    if (typeof config.apiKey !== "function") this.#validateKey(config.apiKey);
  }

  /**
   * @param {string} collection
   * @param {string} document
   * @param {DatabaseRequestOptions} [options]
   * @returns {Promise<DatabaseResult<DatabasePoint>>}
   */
  read(collection, document, options = {}) {
    const path = `${this.#collectionPath(collection)}/documents/${requireDatabaseUuid(document, "document")}`;
    return this.#request(path, "GET", undefined, raw => this.#scopedPoint(decodeDatabasePoint(raw)), options);
  }

  /**
   * @param {string} collection
   * @param {DatabaseQuery} query
   * @param {DatabaseRequestOptions} [options]
   * @returns {Promise<DatabaseResult<DatabaseQueryPage>>}
   */
  query(collection, query, options = {}) {
    const limit = query.limit;
    const body = encodeDatabaseQuery(query);
    return this.#request(`${this.#collectionPath(collection)}/documents/query`, "POST", body, raw => {
      const page = decodeDatabaseQueryPage(raw);
      if (page.rows.length > limit) throw new TypeError("Query page exceeds the requested limit.");
      page.rows.forEach(row => this.#scopedPoint(row.point));
      return page;
    }, options);
  }

  /**
   * @param {string} collection
   * @param {DatabaseSubscriptionRequest} request
   * @param {DatabaseRequestOptions} [options]
   * @returns {Promise<DatabaseResult<DatabaseSubscriptionResponse>>}
   */
  subscription(collection, request, options = {}) {
    const phase = request.phase;
    const limit = request.phase === "snapshot" ? request.limit : undefined;
    const body = encodeDatabaseSubscription(request);
    return this.#request(`${this.#collectionPath(collection)}/documents/subscriptions`, "POST", body, raw => {
      const result = decodeDatabaseSubscription(raw);
      if (result.phase !== phase) throw new TypeError("Subscription response belongs to another phase.");
      if (result.phase === "snapshot" && limit !== undefined && result.rows.length > limit) throw new TypeError("Snapshot page exceeds the requested limit.");
      if (result.phase === "snapshot") result.rows.forEach(row => this.#scopedPoint(row.point));
      else result.groups.forEach(group => group.effects.forEach(effect => { if (effect.point !== null) this.#scopedPoint(effect.point); }));
      return result;
    }, options);
  }

  /**
   * Prepare once and retain this identity for exact retry and durable status resolution.
   * @param {DatabaseCommit} commit
   * @returns {Promise<PreparedDatabaseCommit>}
   */
  async prepareCommit(commit) {
    const transaction_id = Object.freeze({ ...commit.transaction_id });
    const authority = identityAuthority(commit.authority);
    const encoded = await encodeDatabaseCommit(this.#scope, commit);
    const prepared = /** @type {PreparedDatabaseCommit} */ (Object.freeze({ transaction_id, authority, effect_digest: encoded.effect_digest }));
    preparedRequests.set(prepared, { client: this, scope: this.#path, endpoint: this.#endpoint, body: encoded.body });
    return prepared;
  }

  /**
   * @param {PreparedDatabaseCommit} prepared
   * @param {DatabaseRequestOptions} [options]
   * @returns {Promise<DatabaseResult<DatabaseCommitReceipt>>}
   */
  commit(prepared, options = {}) {
    const request = preparedRequests.get(prepared);
    if (request === undefined || request.client !== this || request.scope !== this.#path) throw new TypeError("Prepared commit belongs to another client or scope, or was not prepared by this SDK.");
    if (request.endpoint !== this.#endpoint) throw new TypeError("Prepared commit belongs to another endpoint.");
    return this.#request(`${this.#path}/transactions`, "POST", request.body, raw => {
      const receipt = decodeDatabaseCommitReceipt(raw);
      if (receipt.event.database !== this.#scope.database || receipt.event.shard !== this.#scope.shard ||
        receipt.effect_digest !== prepared.effect_digest || JSON.stringify(encodeDatabaseAuthority(receipt.authority)) !== JSON.stringify(encodeDatabaseAuthority(prepared.authority)) ||
        receipt.transaction_id.nonce !== prepared.transaction_id.nonce || receipt.transaction_id.expires_at_unix_ms !== prepared.transaction_id.expires_at_unix_ms) {
        throw new TypeError("Receipt belongs to another operation or database scope.");
      }
      return receipt;
    }, options, prepared);
  }

  /**
   * Unknown, pending and expired outcomes remain unresolved; an absent document proves no abort.
   * @param {DatabaseTransactionIdentity} identity
   * @param {DatabaseRequestOptions} [options]
   * @returns {Promise<DatabaseResult<DatabaseTransactionStatus>>}
   */
  transactionStatus(identity, options = {}) {
    const snapshot = Object.freeze({ ...identity, transaction_id: Object.freeze({ ...identity.transaction_id }), authority: identityAuthority(identity.authority) });
    const body = encodeDatabaseStatus(snapshot);
    return this.#request(`${this.#path}/transactions/status`, "POST", body, raw => {
      const status = decodeDatabaseStatus(raw);
      if (status.effect_digest !== snapshot.effect_digest || status.transaction_id.nonce !== snapshot.transaction_id.nonce || status.transaction_id.expires_at_unix_ms !== snapshot.transaction_id.expires_at_unix_ms || JSON.stringify(encodeDatabaseAuthority(status.authority)) !== JSON.stringify(encodeDatabaseAuthority(snapshot.authority))) {
        throw new TypeError("Status belongs to another operation.");
      }
      if (status.state === "committed" && (status.outcome.receipt.event.database !== this.#scope.database || status.outcome.receipt.event.shard !== this.#scope.shard)) throw new TypeError("Status receipt belongs to another database scope.");
      return status;
    }, options);
  }

  /**
   * @param {string} collection
   * @returns {string}
   */
  #collectionPath(collection) { return `${this.#path}/collections/${requireDatabaseUuid(collection, "collection")}`; }

  /**
   * @param {DatabasePoint} point
   * @returns {DatabasePoint}
   */
  #scopedPoint(point) {
    if (point.siblings.some(sibling => sibling.event.database !== this.#scope.database || sibling.event.shard !== this.#scope.shard)) {
      throw new TypeError("Point belongs to another database scope.");
    }
    return point;
  }

  /**
   * @param {unknown} key
   * @returns {string}
   */
  #validateKey(key) {
    if (typeof key !== "string" || key.length === 0 || key.length > 4096 || /[\s\p{Cc}]/u.test(key)) throw new TypeError("Native API key must be nonblank and bounded.");
    return key;
  }

  /**
   * @template T
   * @param {string} path
   * @param {"GET" | "POST"} method
   * @param {string | undefined} body
   * @param {(raw: unknown) => T} decode
   * @param {DatabaseRequestOptions} options
   * @param {DatabaseTransactionIdentity} [transaction]
   * @returns {Promise<DatabaseResult<T>>}
   */
  async #request(path, method, body, decode, options, transaction) {
    let sent = false;
    /** @type {number | undefined} */
    let responseStatus;
    const signal = AbortSignal.any([AbortSignal.timeout(this.#requestTimeoutMs), ...(options.signal === undefined ? [] : [options.signal])]);
    try {
      signal.throwIfAborted();
      const apiKey = this.#validateKey(typeof this.#config.apiKey === "function" ? await withCancellation(Promise.resolve(this.#config.apiKey()), signal) : this.#config.apiKey);
      const origin = /** @type {string} */ (this.#endpoint);
      signal.throwIfAborted();
      const headers = new Headers({ accept: "application/json", authorization: `Bearer ${apiKey}` });
      if (body !== undefined) headers.set("content-type", "application/json");
      const request = new Request(new URL(path, origin), { method, headers, credentials: "omit", redirect: "manual", cache: "no-store", referrerPolicy: "no-referrer", signal, ...(body === undefined ? {} : { body }) });
      sent = true;
      /** @type {Response} */
      let response;
      try {
        response = await withCancellation(this.#fetch(request), signal, late => {
          void late.body?.cancel().catch(() => undefined);
        });
      } catch (error) {
        if (signal.aborted) throw error;
        throw new Error("Native request transport failed.", { cause: error });
      }
      responseStatus = response.status;
      if (signal.aborted) {
        void response.body?.cancel().catch(() => undefined);
        signal.throwIfAborted();
      }
      if (response.redirected) {
        void response.body?.cancel().catch(() => undefined);
        throw new TypeError("Native redirects are not admitted.");
      }
      if (response.ok && response.status !== 200) {
        void response.body?.cancel().catch(() => undefined);
        throw new TypeError("The native protocol admits only HTTP 200 success.");
      }
      const raw = await responseJson(response, signal);
      signal.throwIfAborted();
      if (!response.ok) {
        const code = routeError(raw);
        /** @type {"uncertain" | "rejected" | "unknown"} */
        let mutationOutcome = "unknown";
        if (code === "commit_uncertain") mutationOutcome = "uncertain";
        else if (code === "precondition_failed" && response.status === 412) mutationOutcome = "rejected";
        // Authentication, cancellation and changed-effect errors do not resolve an earlier attempt.
        // Only the producer's retained precondition rejection is a terminal mutation outcome.
        return { ok: false, error: { code, status: response.status, ...(transaction === undefined ? {} : { transaction, mutation_outcome: mutationOutcome }) } };
      }
      return { ok: true, status: response.status, value: decode(raw) };
    } catch (error) {
      let code = "transport_unavailable";
      if (signal.aborted) code = "cancelled";
      else if (error instanceof TypeError || error instanceof SyntaxError) code = "invalid_response";
      const status = responseStatus;
      return { ok: false, error: { code, ...(status === undefined ? {} : { status }), ...(transaction === undefined ? {} : { transaction, mutation_outcome: sent ? "uncertain" : "unknown" }) } };
    }
  }
}

/**
 * @param {TerraBaseClientConfig} config
 * @returns {TerraBaseClient}
 */
export function createTerraBaseClient(config) { return new TerraBaseClient(config); }
