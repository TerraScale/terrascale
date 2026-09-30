import {
  decodeDatabaseCommitReceipt, decodeDatabasePoint, decodeDatabaseQueryPage,
  decodeDatabaseSubscription, decodeDatabaseStatus, encodeDatabaseStatus, encodeDatabaseCommit, encodeDatabaseQuery,
  encodeDatabaseSubscription, encodeDatabaseAuthority, parseDatabaseJson, requireDatabaseU64, requireDatabaseUuid,
} from "./database-codec.js";
import type {
  DatabaseAuthority, DatabaseCommit, DatabaseCommitReceipt, DatabasePoint,
  DatabaseQuery, DatabaseQueryPage, DatabaseResult, DatabaseScope,
  DatabaseSubscriptionRequest, DatabaseSubscriptionResponse, DatabaseTransactionIdentity,
  DatabaseTransactionStatus,
} from "./database-types.js";
import type { FetchLike } from "./config.js";
import { TerraBaseDiscoveryError, TerraBaseDiscoveryRouter, terraBaseRegionalOrigin, type TerraBaseRoutingCapability, type TerraBaseRoute } from "./discovery.js";

const maximumResponseBytes = 4 * 1024 * 1024;

export type TerraBaseClientConfig = DatabaseScope & {
  /** Native service key used for authenticated Atlas discovery and native data routing. */
  readonly apiKey: string | (() => string | Promise<string>);
  /** A provisioned direct origin. Omit for Atlas discovery with hosted proxy fallback. */
  readonly endpoint?: string;
  /** Bounds the canonical Atlas request within the complete operation deadline. */
  readonly discoveryTimeoutMs?: number;
  readonly requestTimeoutMs?: number;
  readonly fetch?: FetchLike;
};

export type DatabaseRequestOptions = { readonly signal?: AbortSignal };

declare const preparedBrand: unique symbol;
export type PreparedDatabaseCommit = DatabaseTransactionIdentity & { readonly [preparedBrand]: true };
const preparedRequests = new WeakMap<PreparedDatabaseCommit, { readonly client: TerraBaseClient; readonly scope: string; readonly endpoint: string | undefined; readonly body: string; readonly capabilities: readonly TerraBaseRoutingCapability[] }>();

function bareOrigin(raw: string, local: boolean): string {
  const url = new URL(raw);
  if (url.username !== "" || url.password !== "" || url.search !== "" || url.hash !== "" || url.pathname !== "/") {
    throw new TypeError("Service origin must be bare and contain no credentials.");
  }
  const authority = raw.slice(raw.indexOf("//") + 2).split(/[/?#]/u, 1)[0]!;
  const rawHostname = authority.startsWith("[") ? authority.slice(0, authority.indexOf("]") + 1) : authority.split(":", 1)[0];
  if (rawHostname !== url.hostname) throw new TypeError("Service origins must use a canonical ASCII hostname.");
  const hostname = url.hostname.replace(/\.+$/u, "");
  if (url.hostname !== hostname) {
    throw new TypeError("Service origins must use a canonical hostname.");
  }
  if (hostname !== "api.terrascale.tech" && hostname.endsWith(".terrascale.tech")) {
    const code = hostname.slice(0, -".api.terrascale.tech".length).toUpperCase();
    if (url.hostname !== new URL(terraBaseRegionalOrigin(code)).hostname) throw new TypeError("Native regional origins must use <region>.api.terrascale.tech.");
  }
  const loopback = ["localhost", "127.0.0.1", "[::1]"].includes(url.hostname) || url.hostname.endsWith(".localhost");
  if (url.protocol !== "https:" && !(local && url.protocol === "http:" && loopback)) {
    throw new TypeError("Service origins must use HTTPS, except loopback development.");
  }
  return url.origin;
}

function timeout(value: number | undefined, defaultValue: number, maximum: number): number {
  const resolved = value ?? defaultValue;
  if (!Number.isInteger(resolved) || resolved < 1 || resolved > maximum) throw new TypeError("Timeout is outside its bounds.");
  return resolved;
}

function identityAuthority(authority: DatabaseAuthority): DatabaseAuthority {
  return authority.mode === "leaderless" ? Object.freeze({ ...authority })
    : Object.freeze({ ...authority, writer: Object.freeze({ ...authority.writer }) });
}

function scopePath(scope: DatabaseScope): string {
  return `/v1/tenants/${requireDatabaseUuid(scope.tenant, "tenant")}/databases/${requireDatabaseUuid(scope.database, "database")}/shards/${requireDatabaseU64(scope.shard, "shard")}`;
}

function routeError(raw: unknown): string {
  if (typeof raw !== "object" || raw === null || Array.isArray(raw) || Object.keys(raw).some(key => key !== "error")) return "invalid_response";
  const error = (raw as Record<string, unknown>).error;
  if (typeof error !== "object" || error === null || Array.isArray(error)) return "invalid_response";
  const value = error as Record<string, unknown>;
  if (Object.keys(value).some(key => key !== "code")) return "invalid_response";
  return typeof value.code === "string" && /^[a-z][a-z0-9_]{0,63}$/u.test(value.code) ? value.code : "invalid_response";
}

async function responseJson(response: Response, signal: AbortSignal): Promise<unknown> {
  const mediaType = response.headers.get("content-type")?.split(";", 1)[0]?.trim().toLowerCase();
  if (mediaType !== "application/json") {
    void response.body?.cancel().catch(() => undefined);
    throw new TypeError("Expected application/json.");
  }
  const reader = response.body?.getReader();
  if (reader === undefined) throw new TypeError("Response has no JSON body.");
  const parts: Uint8Array[] = [];
  let size = 0;
  try {
    while (true) {
      let part: ReadableStreamReadResult<Uint8Array>;
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

function withCancellation<T>(
  promise: Promise<T>,
  signal: AbortSignal,
  disposeLateValue?: (value: T) => void,
): Promise<T> {
  return new Promise<T>((resolve, reject) => {
    const cleanup = (): void => signal.removeEventListener("abort", abort);
    const abort = (): void => {
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

async function waitForRetry(response: Response, signal: AbortSignal): Promise<void> {
  const value = response.headers.get("retry-after")?.trim();
  if (value === undefined) return;
  const delay = /^[0-9]+$/u.test(value) ? Number(value) * 1000 : Date.parse(value) - Date.now();
  if (!(delay > 0)) return;
  await new Promise<void>((resolve, reject) => {
    const abort = (): void => { clearTimeout(timer); reject(signal.reason); };
    const timer = setTimeout(() => { signal.removeEventListener("abort", abort); resolve(); }, Math.min(delay, 2_147_483_647));
    signal.addEventListener("abort", abort, { once: true });
    if (signal.aborted) abort();
  });
}

/** Current scoped document client. All mutations are server commits in one declared shard. */
export class TerraBaseClient {
  readonly #scope: DatabaseScope;
  readonly #config: TerraBaseClientConfig;
  readonly #fetch: FetchLike;
  readonly #path: string;
  readonly #endpoint: string | undefined;
  readonly #discovery: TerraBaseDiscoveryRouter | undefined;
  readonly #requestTimeoutMs: number;

  get scope(): DatabaseScope { return this.#scope; }

  constructor(config: TerraBaseClientConfig) {
    this.#scope = Object.freeze({ tenant: requireDatabaseUuid(config.tenant, "tenant"), database: requireDatabaseUuid(config.database, "database"), shard: requireDatabaseU64(config.shard, "shard") });
    this.#config = { ...config };
    this.#fetch = config.fetch ?? globalThis.fetch.bind(globalThis);
    this.#path = scopePath(this.#scope);
    this.#endpoint = config.endpoint === undefined ? undefined : bareOrigin(config.endpoint, true);
    this.#requestTimeoutMs = timeout(config.requestTimeoutMs, 30_000, 300_000);
    this.#discovery = this.#endpoint === undefined ? new TerraBaseDiscoveryRouter(this.#scope, this.#fetch, config.discoveryTimeoutMs) : undefined;
    if (typeof config.apiKey !== "function") this.#validateKey(config.apiKey);
  }

  read(collection: string, document: string, options: DatabaseRequestOptions = {}): Promise<DatabaseResult<DatabasePoint>> {
    const path = `${this.#collectionPath(collection)}/documents/${requireDatabaseUuid(document, "document")}`;
    return this.#request(path, "GET", undefined, raw => this.#scopedPoint(decodeDatabasePoint(raw)), options, ["client_v1", "read"]);
  }

  query(collection: string, query: DatabaseQuery, options: DatabaseRequestOptions = {}): Promise<DatabaseResult<DatabaseQueryPage>> {
    const limit = query.limit;
    const body = encodeDatabaseQuery(query);
    return this.#request(`${this.#collectionPath(collection)}/documents/query`, "POST", body, raw => {
      const page = decodeDatabaseQueryPage(raw);
      if (page.rows.length > limit) throw new TypeError("Query page exceeds the requested limit.");
      page.rows.forEach(row => this.#scopedPoint(row.point));
      return page;
    }, options, ["client_v1", "read", "query"]);
  }

  subscription(collection: string, request: DatabaseSubscriptionRequest, options: DatabaseRequestOptions = {}): Promise<DatabaseResult<DatabaseSubscriptionResponse>> {
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
    }, options, ["client_v1", "read", "subscriptions"]);
  }

  /** Prepare once and retain this identity for exact retry and durable status resolution. */
  async prepareCommit(commit: DatabaseCommit): Promise<PreparedDatabaseCommit> {
    const transaction_id = Object.freeze({ ...commit.transaction_id });
    const authority = identityAuthority(commit.authority);
    const capabilities: TerraBaseRoutingCapability[] = ["client_v1", "write"];
    if (commit.writes.some(write => write.condition.kind !== "none")) capabilities.push("conditional_mutation");
    if (commit.writes.length > 1) capabilities.push("atomic_groups");
    if (authority.mode === "single_writer") capabilities.push("security_state");
    const encoded = await encodeDatabaseCommit(this.#scope, commit);
    const prepared = Object.freeze({ transaction_id, authority, effect_digest: encoded.effect_digest }) as PreparedDatabaseCommit;
    preparedRequests.set(prepared, { client: this, scope: this.#path, endpoint: this.#endpoint, body: encoded.body, capabilities: Object.freeze(capabilities) });
    return prepared;
  }

  commit(prepared: PreparedDatabaseCommit, options: DatabaseRequestOptions = {}): Promise<DatabaseResult<DatabaseCommitReceipt>> {
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
    }, options, request.capabilities, prepared);
  }

  /** Unknown, pending and expired outcomes remain unresolved; an absent document proves no abort. */
  transactionStatus(identity: DatabaseTransactionIdentity, options: DatabaseRequestOptions = {}): Promise<DatabaseResult<DatabaseTransactionStatus>> {
    const snapshot = Object.freeze({ ...identity, transaction_id: Object.freeze({ ...identity.transaction_id }), authority: identityAuthority(identity.authority) });
    const body = encodeDatabaseStatus(snapshot);
    return this.#request(`${this.#path}/transactions/status`, "POST", body, raw => {
      const status = decodeDatabaseStatus(raw);
      if (status.effect_digest !== snapshot.effect_digest || status.transaction_id.nonce !== snapshot.transaction_id.nonce || status.transaction_id.expires_at_unix_ms !== snapshot.transaction_id.expires_at_unix_ms || JSON.stringify(encodeDatabaseAuthority(status.authority)) !== JSON.stringify(encodeDatabaseAuthority(snapshot.authority))) {
        throw new TypeError("Status belongs to another operation.");
      }
      if (status.state === "committed" && (status.outcome.receipt.event.database !== this.#scope.database || status.outcome.receipt.event.shard !== this.#scope.shard)) throw new TypeError("Status receipt belongs to another database scope.");
      return status;
    }, options, snapshot.authority.mode === "single_writer" ? ["client_v1", "write", "security_state"] : ["client_v1", "write"]);
  }

  #collectionPath(collection: string): string { return `${this.#path}/collections/${requireDatabaseUuid(collection, "collection")}`; }

  #scopedPoint(point: DatabasePoint): DatabasePoint {
    if (point.siblings.some(sibling => sibling.event.database !== this.#scope.database || sibling.event.shard !== this.#scope.shard)) {
      throw new TypeError("Point belongs to another database scope.");
    }
    return point;
  }

  #validateKey(key: unknown): string {
    if (typeof key !== "string" || key.length === 0 || key.length > 4096 || /[\s\p{Cc}]/u.test(key)) throw new TypeError("Native API key must be nonblank and bounded.");
    return key;
  }

  async #request<T>(path: string, method: "GET" | "POST", body: string | undefined, decode: (raw: unknown) => T, options: DatabaseRequestOptions, capabilities: readonly TerraBaseRoutingCapability[], transaction?: DatabaseTransactionIdentity): Promise<DatabaseResult<T>> {
    let sent = false;
    let responseStatus: number | undefined;
    let route: TerraBaseRoute | undefined;
    const signal = AbortSignal.any([AbortSignal.timeout(this.#requestTimeoutMs), ...(options.signal === undefined ? [] : [options.signal])]);
    try {
      signal.throwIfAborted();
      const apiKey = this.#validateKey(typeof this.#config.apiKey === "function" ? await withCancellation(Promise.resolve(this.#config.apiKey()), signal) : this.#config.apiKey);
      const attempted = new Set<string>();
      let response: Response;
      let raw: unknown;
      while (true) {
        route = this.#endpoint === undefined ? await this.#discovery!.resolveHosted(apiKey, capabilities, signal) : { url: this.#endpoint };
        if (attempted.has(route.url)) throw new TerraBaseDiscoveryError("regions_unavailable");
        attempted.add(route.url);
        responseStatus = undefined;
        signal.throwIfAborted();
        const headers = new Headers({ accept: "application/json", authorization: `Bearer ${apiKey}` });
        if (body !== undefined) headers.set("content-type", "application/json");
        const request = new Request(new URL(path, route.url), { method, headers, credentials: "omit", redirect: "manual", cache: "no-store", referrerPolicy: "no-referrer", signal, ...(body === undefined ? {} : { body }) });
        if (route.endpoint !== undefined) this.#discovery!.assertCurrent(route.endpoint);
        sent = true;
        try {
          response = await withCancellation(this.#fetch(request), signal, late => { void late.body?.cancel().catch(() => undefined); });
        } catch (error) {
          if (!signal.aborted && route.endpoint !== undefined) {
            this.#discovery!.fail(route.endpoint);
            if (transaction === undefined) continue;
          }
          if (!signal.aborted) throw new Error("Native request transport failed.", { cause: error });
          throw error;
        }
        if (!signal.aborted && route.endpoint !== undefined && (response.status === 421 || response.status >= 500)) {
          this.#discovery!.fail(route.endpoint);
          if (transaction === undefined) {
            void response.body?.cancel().catch(() => undefined);
            await waitForRetry(response, signal);
            continue;
          }
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
        try { raw = await responseJson(response, signal); }
        catch (error) {
          if (!signal.aborted && route.endpoint !== undefined && !(error instanceof TypeError || error instanceof SyntaxError)) {
            this.#discovery!.fail(route.endpoint);
            if (transaction === undefined && response.status === 200) continue;
          }
          throw error;
        }
        break;
      }
      signal.throwIfAborted();
      if (!response.ok) {
        const code = routeError(raw);
        // Authentication, cancellation and changed-effect errors do not resolve an earlier attempt.
        // Only the producer's retained precondition rejection is a terminal mutation outcome.
        return { ok: false, error: { code, status: response.status, ...(transaction === undefined ? {} : { transaction, mutation_outcome: code === "commit_uncertain" ? "uncertain" : code === "precondition_failed" && response.status === 412 ? "rejected" : "unknown" }) } };
      }
      return { ok: true, status: response.status, value: decode(raw) };
    } catch (error) {
      const code = signal.aborted ? "cancelled" : error instanceof TerraBaseDiscoveryError ? error.code : error instanceof TypeError || error instanceof SyntaxError ? "invalid_response" : "transport_unavailable";
      const status = responseStatus ?? (error instanceof TerraBaseDiscoveryError ? error.status : undefined);
      return { ok: false, error: { code, ...(status === undefined ? {} : { status }), ...(transaction === undefined ? {} : { transaction, mutation_outcome: sent ? "uncertain" : "unknown" }) } };
    }
  }
}

export function createTerraBaseClient(config: TerraBaseClientConfig): TerraBaseClient { return new TerraBaseClient(config); }
