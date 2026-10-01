import { parseDatabaseJson, requireDatabaseU64, requireDatabaseUuid } from "./database-codec.js";
import { nativeRegionalOrigin, requireNativeRegion, terraScaleServiceDomain } from "./native-origin.js";
/** @import { FetchLike } from "./config.js" */
/** @import { DatabaseScope } from "./database-types.js" */

/** Frozen authenticated native receiver; no alternate metadata origin is admitted. */
export const terraBaseDiscoveryOrigin = /** @type {const} */ (`https://atlas.${terraScaleServiceDomain}`);
const maximumResponseBytes = 256 * 1024;
const maximumEndpoints = 20;
const nilUuid = "00000000-0000-0000-0000-000000000000";
const capabilityNames = /** @type {const} */ (["client_v1", "read", "query", "subscriptions", "write", "conditional_mutation", "atomic_groups", "security_state"]);

/** @typedef {typeof capabilityNames[number]} TerraBaseRoutingCapability */
/** @typedef {Readonly<Record<TerraBaseRoutingCapability, boolean>>} TerraBaseRoutingCapabilities */
/**
 * @typedef {{
 *   readonly region: string;
 *   readonly url: string;
 *   readonly priority: number;
 *   readonly catalog: { readonly digest: string };
 *   readonly capabilities: TerraBaseRoutingCapabilities;
 *   readonly security_writer: null;
 * }} TerraBaseRoutingEndpoint
 */
/**
 * @typedef {{
 *   readonly schema_version: 1;
 *   readonly scope: DatabaseScope;
 *   readonly authority: {
 *     readonly deployment_id: string;
 *     readonly cell: string;
 *     readonly publisher_id: string;
 *     readonly publisher_generation: bigint;
 *   };
 *   readonly revision: bigint;
 *   readonly published_at: number;
 *   readonly expires_at: number;
 *   readonly refreshSeconds: number;
 *   readonly residency: readonly string[] | null;
 *   readonly endpoints: readonly TerraBaseRoutingEndpoint[];
 * }} TerraBaseRoutingDocument
 */

export class TerraBaseDiscoveryError extends Error {
  /**
   * @readonly
   * @type {string}
   */
  code;
  /**
   * @readonly
   * @type {number | undefined}
   */
  status;

  /**
   * @param {string} code
   * @param {number} [status]
   */
  constructor(code, status) {
    super(code);
    this.code = code;
    this.status = status;
    this.name = "TerraBaseDiscoveryError";
  }
}

/**
 * @param {unknown} value
 * @param {readonly string[]} fields
 * @returns {Record<string, unknown>}
 */
function closed(value, fields) {
  if (value === null || typeof value !== "object" || Array.isArray(value)) throw new TypeError("Expected a discovery object.");
  const entry = /** @type {Record<string, unknown>} */ (value);
  if (fields.some(key => !Object.hasOwn(entry, key)) || Object.keys(entry).some(key => !fields.includes(key))) {
    throw new TypeError("Discovery object has missing or unknown fields.");
  }
  return entry;
}

/**
 * @param {unknown} value
 * @returns {string}
 */
function uuid(value) {
  const id = requireDatabaseUuid(value);
  if (id === nilUuid) throw new TypeError("Discovery UUID must be nonnil.");
  return id;
}

/**
 * @param {unknown} value
 * @returns {bigint}
 */
function decimal(value) {
  if (typeof value !== "string" || !/^(0|[1-9][0-9]{0,19})$/u.test(value)) throw new TypeError("Discovery u64 must be canonical decimal.");
  return requireDatabaseU64(BigInt(value), "discovery u64", true);
}

/**
 * @param {unknown} value
 * @param {boolean} [token]
 * @returns {string}
 */
function identity(value, token = false) {
  if (typeof value !== "string" || !(token ? /^[a-z0-9][a-z0-9-]{0,62}$/u : /^[A-Za-z0-9][A-Za-z0-9._:-]{0,127}$/u).test(value)) {
    throw new TypeError("Invalid discovery authority identity.");
  }
  return value;
}

/**
 * @param {unknown} value
 * @returns {number}
 */
function unix(value) {
  if (typeof value !== "number" || !Number.isSafeInteger(value) || value < 1 || value > 253402300799) throw new TypeError("Invalid discovery timestamp.");
  return value;
}

/**
 * Parse only the current closed native8 descriptor, without widening its lease or capabilities.
 * @param {unknown} payload
 * @param {DatabaseScope} expected
 * @param {number} [nowMs]
 * @returns {TerraBaseRoutingDocument}
 */
export function parseTerraBaseRoutingDocument(payload, expected, nowMs = Date.now()) {
  const doc = closed(payload, ["schema_version", "scope", "authority", "revision", "published_at", "expires_at", "refreshSeconds", "residency", "endpoints"]);
  if (doc.schema_version !== 1) throw new TypeError("Unsupported discovery schema.");
  const target = closed(doc.scope, ["tenant_id", "database_id", "shard_id"]);
  const scope = Object.freeze({ tenant: uuid(target.tenant_id), database: uuid(target.database_id), shard: decimal(target.shard_id) });
  if (scope.tenant !== expected.tenant || scope.database !== expected.database || scope.shard !== expected.shard) throw new TypeError("Discovery belongs to another native scope.");
  const source = closed(doc.authority, ["deployment_id", "cell", "publisher_id", "publisher_generation"]);
  const authority = Object.freeze({ deployment_id: identity(source.deployment_id), cell: identity(source.cell, true), publisher_id: identity(source.publisher_id, true), publisher_generation: decimal(source.publisher_generation) });
  const published_at = unix(doc.published_at), expires_at = unix(doc.expires_at);
  if (!Number.isFinite(nowMs) || published_at * 1000 > nowMs || expires_at * 1000 <= nowMs || expires_at <= published_at || expires_at - published_at > 10) {
    throw new TypeError("Discovery interval is expired, future, or outside its bound.");
  }
  const refreshSeconds = doc.refreshSeconds;
  if (typeof refreshSeconds !== "number" || !Number.isInteger(refreshSeconds) || refreshSeconds < 1 || refreshSeconds > 10) throw new TypeError("Invalid discovery refresh interval.");
  /** @type {readonly string[] | null} */
  let residency = null;
  if (doc.residency !== null) {
    if (!Array.isArray(doc.residency) || doc.residency.length < 1 || doc.residency.length > maximumEndpoints) throw new TypeError("Invalid native residency.");
    const codes = Array.from(doc.residency).map(requireNativeRegion);
    if (new Set(codes).size !== codes.length) throw new TypeError("Duplicate residency region.");
    residency = Object.freeze(codes);
  }
  if (!Array.isArray(doc.endpoints) || doc.endpoints.length < 1 || doc.endpoints.length > maximumEndpoints) throw new TypeError("Invalid discovery endpoint list.");
  /** @type {Set<string>} */
  const regions = new Set();
  /** @type {Set<number>} */
  const priorities = new Set();
  const count = doc.endpoints.length;
  const endpoints = Array.from(doc.endpoints).map(raw => {
    const endpoint = closed(raw, ["region", "url", "priority", "catalog", "capabilities", "security_writer"]);
    const code = requireNativeRegion(endpoint.region), priority = endpoint.priority;
    if (regions.has(code) || typeof priority !== "number" || !Number.isInteger(priority) || priority < 1 || priority > count || priorities.has(priority)) throw new TypeError("Invalid or duplicate endpoint rank.");
    regions.add(code); priorities.add(priority);
    if (endpoint.url !== nativeRegionalOrigin(code) || (residency !== null && !residency.includes(code))) throw new TypeError("Endpoint is not an eligible canonical native origin.");
    const catalog = closed(endpoint.catalog, ["digest"]);
    if (typeof catalog.digest !== "string" || !/^[0-9a-f]{64}$/u.test(catalog.digest)) throw new TypeError("Invalid catalog digest.");
    const caps = closed(endpoint.capabilities, capabilityNames);
    for (const name of capabilityNames) if (typeof caps[name] !== "boolean") throw new TypeError("Discovery capabilities must be explicit booleans.");
    if (caps.client_v1 !== true || (caps.read !== true && caps.write !== true) || caps.conditional_mutation !== false || caps.atomic_groups !== false || caps.security_state !== false || endpoint.security_writer !== null) {
      throw new TypeError("Unsupported or ineligible native capability descriptor.");
    }
    return /** @type {TerraBaseRoutingEndpoint} */ (Object.freeze({ region: code, url: endpoint.url, priority, catalog: Object.freeze({ digest: catalog.digest }), capabilities: /** @type {TerraBaseRoutingCapabilities} */ (Object.freeze({ ...caps })), security_writer: null }));
  });
  return Object.freeze({ schema_version: 1, scope, authority, revision: decimal(doc.revision), published_at, expires_at, refreshSeconds, residency, endpoints: Object.freeze(endpoints) });
}

/**
 * @param {string} key
 * @returns {void}
 */
function credential(key) {
  // The unused trailing bits must be zero in unpadded base64url16/base64url32.
  if (!/^tbk1\.[A-Za-z0-9_-]{21}[AQgw]\.[A-Za-z0-9_-]{42}[AEIMQUYcgkosw048]$/u.test(key)) {
    throw new TerraBaseDiscoveryError("invalid_discovery_credential");
  }
}

/**
 * @template T
 * @param {Promise<T>} promise
 * @param {AbortSignal} signal
 * @returns {Promise<T>}
 */
async function cancellable(promise, signal) {
  signal.throwIfAborted();
  return new Promise((resolve, reject) => {
    /** @returns {void} */
    const abort = () => reject(signal.reason);
    signal.addEventListener("abort", abort, { once: true });
    promise.then(resolve, reject).finally(() => signal.removeEventListener("abort", abort));
  });
}

/**
 * @param {Response} response
 * @param {AbortSignal} signal
 * @returns {Promise<unknown>}
 */
async function responseJson(response, signal) {
  if (response.headers.get("content-type")?.split(";", 1)[0]?.trim().toLowerCase() !== "application/json") throw new TypeError("Expected discovery JSON.");
  const cache = response.headers.get("cache-control")?.toLowerCase().split(",").map(part => part.trim());
  if (!cache?.includes("private") || !cache.includes("no-store") || cache.includes("public")) throw new TypeError("Discovery response must be private, no-store.");
  const reader = response.body?.getReader();
  if (reader === undefined) throw new TypeError("Discovery response has no body.");
  /** @type {Uint8Array[]} */
  const parts = []; let size = 0;
  try {
    while (true) {
      const part = await cancellable(reader.read(), signal);
      if (part.done) break;
      size += part.value.length;
      if (size > maximumResponseBytes) throw new TypeError("Discovery body exceeds its bound.");
      parts.push(part.value);
    }
  } catch (error) { void reader.cancel().catch(() => undefined); throw error; }
  finally { reader.releaseLock(); }
  const bytes = new Uint8Array(size); let offset = 0;
  for (const part of parts) { bytes.set(part, offset); offset += part.length; }
  return parseDatabaseJson(bytes);
}

/**
 * @param {unknown} payload
 * @param {number} status
 * @returns {TerraBaseDiscoveryError}
 */
function failure(payload, status) {
  const error = closed(closed(payload, ["error"]).error, ["code", "message", "fix"]);
  /** @type {Readonly<Record<number, readonly string[]>>} */
  const admitted = { 400: ["invalid_request", "invalid_scope", "invalid_cursor"], 403: ["discovery_forbidden"], 503: ["discovery_unconfigured", "no_eligible_region", "routing_observation_regressed"] };
  if (typeof error.code !== "string" || !admitted[status]?.includes(error.code) || typeof error.message !== "string" || typeof error.fix !== "string") throw new TypeError("Invalid discovery error envelope.");
  return new TerraBaseDiscoveryError(error.code, status);
}

/**
 * @param {TerraBaseRoutingDocument} document
 * @param {TerraBaseRoutingDocument} floor
 * @returns {boolean}
 */
function observationRegressed(document, floor) {
  const current = document.authority, previous = floor.authority;
  return current.deployment_id !== previous.deployment_id || current.cell !== previous.cell ||
    current.publisher_generation < previous.publisher_generation || document.revision < floor.revision ||
    (current.publisher_generation === previous.publisher_generation && document.revision === floor.revision && current.publisher_id !== previous.publisher_id);
}

/** Per-scope observation floors survive lease expiry and credential rotation for this client. */
export class TerraBaseDiscoveryRouter {
  /** @readonly @type {DatabaseScope} */
  #scope;
  /** @readonly @type {FetchLike} */
  #fetch;
  /** @readonly @type {number} */
  #timeoutMs;
  /** @type {TerraBaseRoutingDocument | undefined} */
  #observation;
  /** @type {{ readonly fingerprint: string; readonly document: TerraBaseRoutingDocument; readonly wallDeadline: number; readonly monotonicDeadline: number } | undefined} */
  #cache;
  /** @readonly @type {WeakMap<TerraBaseRoutingEndpoint, { readonly document: TerraBaseRoutingDocument; readonly wallDeadline: number; readonly monotonicDeadline: number }>} */
  #leases = new WeakMap();

  /**
   * @param {DatabaseScope} scope
   * @param {FetchLike} [fetch]
   * @param {number} [timeoutMs]
   */
  constructor(scope, fetch = globalThis.fetch.bind(globalThis), timeoutMs = 3000) {
    this.#scope = Object.freeze({ tenant: uuid(scope.tenant), database: uuid(scope.database), shard: requireDatabaseU64(scope.shard, "shard") });
    if (!Number.isInteger(timeoutMs) || timeoutMs < 1 || timeoutMs > 30000) throw new TypeError("Discovery timeout is outside its bounds.");
    this.#fetch = fetch; this.#timeoutMs = timeoutMs;
  }

  /**
   * @param {string} apiKey
   * @param {readonly TerraBaseRoutingCapability[]} required
   * @param {AbortSignal} [callerSignal]
   * @returns {Promise<TerraBaseRoutingEndpoint>}
   */
  async resolve(apiKey, required, callerSignal) {
    const signal = AbortSignal.any([AbortSignal.timeout(this.#timeoutMs), ...(callerSignal === undefined ? [] : [callerSignal])]);
    signal.throwIfAborted(); credential(apiKey);
    if (required.some(name => !capabilityNames.includes(name))) throw new TypeError("Unknown native routing capability.");
    const hash = await cancellable(crypto.subtle.digest("SHA-256", new TextEncoder().encode(apiKey)), signal);
    const fingerprint = Array.from(new Uint8Array(hash), byte => byte.toString(16).padStart(2, "0")).join("");
    const cached = this.#cache, wallNow = Date.now(), monotonicNow = performance.now();
    let doc = cached?.fingerprint === fingerprint && cached.document.published_at * 1000 <= wallNow && wallNow < cached.wallDeadline && monotonicNow < cached.monotonicDeadline ? cached.document : undefined;
    if (doc === undefined) {
      this.#cache = undefined;
      const startedWall = Date.now(), startedMonotonic = performance.now();
      const url = new URL("/api/v1/discovery", terraBaseDiscoveryOrigin);
      url.search = new URLSearchParams({ tenant_id: this.#scope.tenant, database_id: this.#scope.database, shard_id: this.#scope.shard.toString() }).toString();
      if (this.#observation !== undefined) {
        url.searchParams.set("observed_deployment_id", this.#observation.authority.deployment_id);
        url.searchParams.set("observed_cell", this.#observation.authority.cell);
        url.searchParams.set("observed_publisher_generation", this.#observation.authority.publisher_generation.toString());
        url.searchParams.set("rev", this.#observation.revision.toString());
      }
      const request = new Request(url, { method: "GET", headers: { accept: "application/json", authorization: `Bearer ${apiKey}` }, credentials: "omit", redirect: "manual", cache: "no-store", referrerPolicy: "no-referrer", signal });
      /** @type {Response | undefined} */
      let response;
      /** @type {Promise<Response> | undefined} */
      let pendingResponse;
      try {
        pendingResponse = this.#fetch(request);
        response = await cancellable(pendingResponse, signal);
        if (response.redirected) throw new TypeError("Discovery redirects are not admitted.");
        if (response.status !== 200 && ![400, 403, 503].includes(response.status)) throw new TypeError("Unadmitted discovery HTTP status.");
        const payload = await responseJson(response, signal);
        if (response.status !== 200) throw failure(payload, response.status);
        doc = parseTerraBaseRoutingDocument(payload, this.#scope);
        const previous = this.#observation;
        if (previous !== undefined && observationRegressed(doc, previous)) {
          throw new TerraBaseDiscoveryError("routing_observation_regressed");
        }
        const wallDeadline = Math.min(doc.expires_at * 1000, startedWall + doc.refreshSeconds * 1000);
        const monotonicDeadline = Math.min(startedMonotonic + doc.refreshSeconds * 1000, performance.now() + doc.expires_at * 1000 - Date.now());
        if (Date.now() >= wallDeadline || performance.now() >= monotonicDeadline) throw new TerraBaseDiscoveryError("routing_expired");
        this.#observation = doc;
        this.#cache = { fingerprint, document: doc, wallDeadline, monotonicDeadline };
      } catch (error) {
        /**
         * @param {Response} unread
         * @returns {void}
         */
        const discard = (unread) => {
          try {
            if (!unread.bodyUsed) void unread.body?.cancel().catch(() => undefined);
          } catch {
            // Custom response properties and cancel methods may throw; cleanup is best effort.
          }
        };
        if (response !== undefined) discard(response);
        // A custom fetch can ignore abort and return later; cleanup must not delay the caller.
        else void pendingResponse?.then(discard, () => undefined).catch(() => undefined);
        if (signal.aborted) {
          if (callerSignal?.aborted) throw callerSignal.reason;
          throw new TerraBaseDiscoveryError("discovery_unavailable");
        }
        if (error instanceof TerraBaseDiscoveryError) throw error;
        if (response !== undefined && (error instanceof TypeError || error instanceof SyntaxError)) throw new TerraBaseDiscoveryError("invalid_response", response.status);
        throw new TerraBaseDiscoveryError("discovery_unavailable");
      }
    }
    signal.throwIfAborted();
    const endpoint = [...doc.endpoints].sort((a, b) => a.priority - b.priority).find(candidate => required.every(name => candidate.capabilities[name]));
    if (endpoint === undefined) throw new TerraBaseDiscoveryError("capability_unavailable");
    // Both the cached and freshly fetched paths leave the admitted lease in #cache.
    const cache = /** @type {{ readonly wallDeadline: number; readonly monotonicDeadline: number }} */ (this.#cache);
    this.#leases.set(endpoint, { document: doc, wallDeadline: cache.wallDeadline, monotonicDeadline: cache.monotonicDeadline });
    return endpoint;
  }

  /**
   * Recheck immediately before native dispatch, including concurrently advanced floors.
   * @param {TerraBaseRoutingEndpoint} endpoint
   * @returns {void}
   */
  assertCurrent(endpoint) {
    const lease = this.#leases.get(endpoint), observed = this.#observation;
    const wallNow = Date.now();
    if (lease === undefined || lease.document.published_at * 1000 > wallNow || wallNow >= lease.wallDeadline || performance.now() >= lease.monotonicDeadline) throw new TerraBaseDiscoveryError("routing_expired");
    if (observed === undefined || observationRegressed(lease.document, observed)) {
      throw new TerraBaseDiscoveryError("routing_observation_regressed");
    }
  }
}
