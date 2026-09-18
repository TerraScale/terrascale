import type { FetchLike } from "./config.js";
import { isRegionCode, terraScaleGlobalApiHostname, type TerraScaleRegionCode } from "./regions.js";

/**
 * SDK-driven routing discovery (ADR 0050). Discovery replaces the global
 * proxy as the default first contact: one metadata GET per routing scope,
 * then direct regional connections. The proxy remains only as a fallback.
 */

export type TerraScaleRoutingCapability = "read" | "write" | "sql" | "reactive";

const routingCapabilities: readonly TerraScaleRoutingCapability[] = ["read", "write", "sql", "reactive"];

export type TerraScaleRoutingEndpoint = {
  readonly region: TerraScaleRegionCode;
  readonly url: string;
  readonly priority: number;
  readonly capabilities: readonly TerraScaleRoutingCapability[];
};

export type TerraScaleRoutingDocument = {
  readonly database: string;
  readonly branch: string;
  readonly revision: number;
  /** ADR 0043 §1 semantics: label, explicit region list, or null. */
  readonly residency: string | readonly string[] | null;
  readonly placement: "known" | "unknown";
  readonly refreshSeconds: number;
  readonly endpoints: readonly TerraScaleRoutingEndpoint[];
};

/** The routing scope a cached document is keyed by (ADR 0050 §5.2). */
export type TerraScaleRoutingScope = {
  readonly origin: string;
  readonly database: string;
  readonly branch: string;
  readonly projectId?: string | undefined;
  readonly tenantId?: string | undefined;
};

export type TerraScaleRoutingOptions = {
  /** Override the discovery origin (tests and local development). */
  readonly baseUrl?: string | undefined;
  /** Force discovery off even for the global API origin. */
  readonly enabled?: boolean | undefined;
  /** Bounded discovery request timeout; default 3000 ms. */
  readonly timeoutMs?: number | undefined;
};

export const defaultDiscoveryTimeoutMs = 3_000;
export const defaultRequestTimeoutMs = 30_000;

const minRefreshSeconds = 60;
const maxRefreshSeconds = 3_600;
const maxEndpoints = 20;

/**
 * Discovery is the default only for the public global origin. Every other
 * origin (dashboard service bindings, loopback dev workers, test servers,
 * the local emulator) keeps its existing ADR 0043 behaviour unless the
 * caller opts in with `routing.baseUrl` or `routing.enabled`.
 */
export function discoveryActive(options: TerraScaleRoutingOptions | undefined, apiBaseUrl: string): boolean {
  if (options?.enabled === false) return false;
  if (options?.baseUrl !== undefined || options?.enabled === true) return true;
  let hostname: string;
  try {
    hostname = new URL(`${apiBaseUrl}/`).hostname.toLowerCase();
  } catch {
    return false;
  }
  return hostname === terraScaleGlobalApiHostname;
}

function parseRoutingDatabaseName(value: unknown): string | null {
  if (typeof value !== "string") return null;
  return /^[a-z0-9](?:[a-z0-9-]{0,61}[a-z0-9])?$/u.test(value) ? value : null;
}

/** Loopback and `*.localhost` dev origins: `terrascale dev` serves its pinned regional members there. */
function isDevOrigin(url: URL): boolean {
  return url.hostname === "localhost" || url.hostname.endsWith(".localhost") ||
    url.hostname === "127.0.0.1" || url.hostname === "[::1]";
}

/** A user cancellation, as opposed to a genuine network/HTTP discovery failure. */
function isAbortError(value: unknown): boolean {
  return value instanceof Error && value.name === "AbortError";
}

/**
 * Validates a discovered endpoint URL at the trust boundary. A hostile or
 * sloppy document must never steer traffic anywhere outside the regional
 * API family (or dev origins when the client itself runs against loopback).
 */
function validateEndpointUrl(raw: unknown, region: TerraScaleRegionCode, loopbackClient: boolean): string | null {
  if (typeof raw !== "string" || raw.length === 0 || raw.length > 2_048) return null;
  let url: URL;
  try {
    url = new URL(raw);
  } catch {
    return null;
  }
  if (url.username !== "" || url.password !== "") return null;
  if (url.hash !== "" || url.search !== "") return null;
  const expectedHostname = `${region.toLowerCase()}.${terraScaleGlobalApiHostname}`;
  if (url.protocol === "https:" && url.hostname.toLowerCase() === expectedHostname) {
    return `${url.protocol}//${expectedHostname}`;
  }
  if (loopbackClient && url.protocol === "http:" && isDevOrigin(url)) {
    return `${url.protocol}//${url.host}`;
  }
  return null;
}

/**
 * Strictly validates a discovery response. Any structural doubt rejects the
 * document (the SDK falls back to the ADR 0043 proxy path); routing never
 * widens on malformed input. `loopbackClient` admits http dev-origin
 * endpoint URLs (loopback and `*.localhost`), which a dev worker publishes
 * from its DEV_ORIGINS.
 */
export function parseRoutingDocument(
  payload: unknown,
  expected: { database: string; branch: string },
  loopbackClient = false,
): TerraScaleRoutingDocument | null {
  if (!payload || typeof payload !== "object" || Array.isArray(payload)) return null;
  const doc = payload as Record<string, unknown>;
  if (doc.v !== 1) return null;
  if (doc.database !== expected.database || doc.branch !== expected.branch) return null;
  const residencyRaw = doc.residency;
  let residency: string | readonly string[] | null = null;
  if (typeof residencyRaw === "string") {
    if (residencyRaw.length === 0 || residencyRaw.length > 256) return null;
    residency = residencyRaw;
  } else if (Array.isArray(residencyRaw)) {
    if (residencyRaw.length === 0 || residencyRaw.length > 20) return null;
    const regions: string[] = [];
    for (const item of residencyRaw) {
      if (!isRegionCode(item) || regions.includes(item)) return null;
      regions.push(item);
    }
    residency = regions;
  } else if (residencyRaw !== undefined && residencyRaw !== null) {
    return null;
  }
  const placement = doc.placement === "unknown" ? "unknown" : doc.placement === "known" ? "known" : null;
  if (placement === null) return null;
  const revision = doc.revision;
  if (typeof revision !== "number" || !Number.isSafeInteger(revision) || revision < 0) return null;
  const refreshSeconds = doc.refreshSeconds;
  if (typeof refreshSeconds !== "number" || !Number.isSafeInteger(refreshSeconds) || refreshSeconds < 1 || refreshSeconds > 86_400) {
    return null;
  }
  const rawEndpoints = doc.endpoints;
  if (!Array.isArray(rawEndpoints) || rawEndpoints.length === 0 || rawEndpoints.length > maxEndpoints) return null;
  const endpoints: TerraScaleRoutingEndpoint[] = [];
  const seenRegions = new Set<string>();
  for (const raw of rawEndpoints) {
    if (!raw || typeof raw !== "object" || Array.isArray(raw)) return null;
    const entry = raw as Record<string, unknown>;
    if (!isRegionCode(entry.region)) return null;
    const region = entry.region;
    if (seenRegions.has(region)) return null;
    seenRegions.add(region);
    if (typeof entry.priority !== "number" || !Number.isSafeInteger(entry.priority) || entry.priority < 1 || entry.priority > maxEndpoints) {
      return null;
    }
    const url = validateEndpointUrl(entry.url, region, loopbackClient);
    if (url === null) return null;
    if (!Array.isArray(entry.capabilities) || entry.capabilities.length === 0) return null;
    const capabilities = new Set<TerraScaleRoutingCapability>();
    for (const capability of entry.capabilities) {
      if (typeof capability !== "string" || !routingCapabilities.includes(capability as TerraScaleRoutingCapability)) return null;
      capabilities.add(capability as TerraScaleRoutingCapability);
    }
    endpoints.push({ region, url, priority: entry.priority, capabilities: [...capabilities] });
  }
  if (!endpoints.some((endpoint) => endpoint.priority === 1)) return null;
  return {
    database: expected.database,
    branch: expected.branch,
    revision,
    residency,
    placement,
    refreshSeconds: Math.min(Math.max(refreshSeconds, minRefreshSeconds), maxRefreshSeconds),
    endpoints,
  };
}

/**
 * A routing document synthesized from a member's authoritative placement
 * headers (ADR 0043 §1). A 421 or a first-contact placement header teaches
 * the placement without a discovery round trip; eligibility is unknown, so
 * every placement region is fully capable until discovery says otherwise.
 * Synthesis rewrites only the public global origin into its regional form:
 * custom and dev origins would cache production URLs that ignore them, so
 * every other origin declines and keeps the header-hint repair path (the
 * pin travels as a request header, which leaves custom origins untouched).
 */
export function routingDocumentFromPlacement(
  scope: TerraScaleRoutingScope,
  regions: readonly TerraScaleRegionCode[],
  revision: number,
): TerraScaleRoutingDocument | null {
  try {
    if (new URL(`${scope.origin}/`).hostname !== terraScaleGlobalApiHostname) return null;
  } catch {
    return null;
  }
  const unique: TerraScaleRegionCode[] = [];
  for (const region of regions) {
    if (!isRegionCode(region)) continue;
    if (!unique.includes(region)) unique.push(region);
  }
  if (unique.length === 0) return null;
  const endpoints = unique.slice(0, maxEndpoints).map((region, index) => ({
    region,
    url: `https://${region.toLowerCase()}.${terraScaleGlobalApiHostname}`,
    priority: index + 1,
    capabilities: [...routingCapabilities],
  }));
  return {
    database: scope.database,
    branch: scope.branch,
    revision,
    residency: null,
    placement: "known",
    refreshSeconds: minRefreshSeconds,
    endpoints,
  };
}

export function endpointSupports(endpoint: TerraScaleRoutingEndpoint, capability: TerraScaleRoutingCapability): boolean {
  return endpoint.capabilities.includes(capability);
}

function scopeKey(scope: TerraScaleRoutingScope): string {
  return `${scope.origin}|${scope.database}|${scope.branch}|p=${scope.projectId ?? ""}|t=${scope.tenantId ?? ""}`;
}

interface CacheEntry {
  readonly document: TerraScaleRoutingDocument;
  readonly expiresAtMs: number;
}

/**
 * Per-realm routing cache shared by every SDK client in the process, keyed
 * by the full authorization scope so routing learned for one project,
 * tenant, database, or branch never serves another (ADR 0050 §5.2).
 * Discovery must not run before every query: entries outlive their refresh
 * policy and concurrent lookups for one scope share a single request.
 */
export class TerraScaleRoutingCache {
  readonly #entries = new Map<string, CacheEntry>();
  readonly #inflight = new Map<string, Promise<TerraScaleRoutingDocument>>();
  readonly #lastFailureAtMs = new Map<string, number>();
  /** Demoted regions per scope: routing errors push the region to the back. */
  readonly #demoted = new Map<string, Set<string>>();
  /** Scopes whose next discovery request must be corrective (`stale=1`). */
  readonly #stale = new Set<string>();
  /** Opaque per-scope generation tokens, bumped whenever cached routing changes. */
  readonly #generations = new Map<string, object>();
  #nowMs: () => number = () => Date.now();

  /** Tests inject a deterministic clock. */
  setClock(nowMs: () => number): void {
    this.#nowMs = nowMs;
  }

  /**
   * The scope's current generation token. A coalesced discovery result may
   * only be applied while the token it started under is still current:
   * `set`/`invalidate`/`demoteRegion` in between means fresher authoritative
   * routing landed mid-flight and the fetched document is stale.
   */
  generation(scope: TerraScaleRoutingScope): object {
    const key = scopeKey(scope);
    let token = this.#generations.get(key);
    if (token === undefined) {
      token = {};
      this.#generations.set(key, token);
    }
    return token;
  }

  #bump(key: string): void {
    this.#generations.set(key, {});
  }

  get(scope: TerraScaleRoutingScope): TerraScaleRoutingDocument | undefined {
    const key = scopeKey(scope);
    const entry = this.#entries.get(key);
    if (entry === undefined) return undefined;
    if (entry.expiresAtMs <= this.#nowMs()) {
      this.#entries.delete(key);
      return undefined;
    }
    const demoted = this.#demoted.get(key);
    if (demoted === undefined || demoted.size === 0) return entry.document;
    const endpoints = entry.document.endpoints.filter((endpoint) => !demoted.has(endpoint.region));
    if (endpoints.length === 0) return entry.document;
    return { ...entry.document, endpoints: endpoints.map((endpoint, index) => ({ ...endpoint, priority: index + 1 })) };
  }

  set(scope: TerraScaleRoutingScope, document: TerraScaleRoutingDocument): void {
    const key = scopeKey(scope);
    this.#entries.set(key, { document, expiresAtMs: this.#nowMs() + document.refreshSeconds * 1_000 });
    this.#demoted.delete(key);
    this.#bump(key);
  }

  /** Invalidate after a rejected route; the next lookup rediscovers with `stale=1`. */
  invalidate(scope: TerraScaleRoutingScope): void {
    const key = scopeKey(scope);
    this.#entries.delete(key);
    this.#demoted.delete(key);
    this.#stale.add(key);
    this.#bump(key);
  }

  /** Marks the scope for one corrective lookup; consumed by the next fetch. */
  markStale(scope: TerraScaleRoutingScope): void {
    this.#stale.add(scopeKey(scope));
  }

  consumeStale(scope: TerraScaleRoutingScope): boolean {
    return this.#stale.delete(scopeKey(scope));
  }

  /** Record a routing failure against one region; it moves behind the alternatives. */
  demoteRegion(scope: TerraScaleRoutingScope, region: TerraScaleRegionCode): void {
    const key = scopeKey(scope);
    let demoted = this.#demoted.get(key);
    if (demoted === undefined) {
      demoted = new Set<string>();
      this.#demoted.set(key, demoted);
    }
    demoted.add(region);
    this.#bump(key);
  }

  noteFailure(scope: TerraScaleRoutingScope): void {
    this.#lastFailureAtMs.set(scopeKey(scope), this.#nowMs());
  }

  /** Discovery backoff: a scope that just failed waits before trying again. */
  failureBackoffRemainingMs(scope: TerraScaleRoutingScope, backoffMs: number): number {
    const failedAt = this.#lastFailureAtMs.get(scopeKey(scope));
    if (failedAt === undefined) return 0;
    return Math.max(0, failedAt + backoffMs - this.#nowMs());
  }

  /** Single-flight: concurrent discovery for one scope shares one request. */
  async coalesce(scope: TerraScaleRoutingScope, initiate: () => Promise<TerraScaleRoutingDocument>): Promise<TerraScaleRoutingDocument> {
    const key = scopeKey(scope);
    const existing = this.#inflight.get(key);
    if (existing !== undefined) return existing;
    const promise = initiate().finally(() => {
      this.#inflight.delete(key);
    });
    this.#inflight.set(key, promise);
    return promise;
  }
}

const sharedRoutingCache = new TerraScaleRoutingCache();

/** The process-wide routing cache; one client per application shares it. */
export function sharedRoutingCacheInstance(): TerraScaleRoutingCache {
  return sharedRoutingCache;
}

export type DiscoveryFetchInput = {
  readonly origin: string;
  readonly database: string;
  readonly branch: string;
  readonly revision?: number | undefined;
  readonly stale?: boolean | undefined;
  /** Measured client-to-region round trips sent as `X-TerraScale-Region-Rtt`. */
  readonly measuredRttMs?: ReadonlyMap<TerraScaleRegionCode, number> | undefined;
  readonly fetch: FetchLike;
  readonly timeoutMs: number;
  readonly signal?: AbortSignal | undefined;
};

/**
 * Composes abort signals without relying on `AbortSignal.any` availability.
 * `dispose` detaches the listeners once the caller's operation settles, so a
 * long-lived caller signal does not accumulate listeners across requests.
 */
export function composeAbortSignals(signals: readonly (AbortSignal | undefined)[]): { signal: AbortSignal | undefined; dispose: () => void } {
  const live = signals.filter((signal): signal is AbortSignal => signal !== undefined);
  if (live.length === 0) return { signal: undefined, dispose: () => undefined };
  const controller = new AbortController();
  const detach = (): void => {
    for (const signal of live) signal.removeEventListener("abort", abort);
  };
  const abort = (): void => {
    if (!controller.signal.aborted) {
      const reason = live.find((signal) => signal.aborted)?.reason;
      controller.abort(reason);
    }
    detach();
  };
  for (const signal of live) {
    if (signal.aborted) {
      abort();
      break;
    }
    signal.addEventListener("abort", abort, { once: true });
  }
  return { signal: controller.signal, dispose: detach };
}

function measuredRttHeader(measured: ReadonlyMap<TerraScaleRegionCode, number> | undefined): string | undefined {
  if (measured === undefined || measured.size === 0) return undefined;
  const parts: string[] = [];
  for (const [region, ms] of measured) {
    if (!isRegionCode(region)) continue;
    if (!Number.isFinite(ms) || ms < 1 || ms > 60_000) continue;
    if (parts.length >= 20) break;
    parts.push(`${region}=${Math.round(ms)}`);
  }
  return parts.length === 0 ? undefined : parts.join(",");
}

/**
 * One bounded discovery request (ADR 0050 §2). Never authenticated, never
 * cacheable by the browser (`no-store`); the SDK routing cache owns caching.
 */
export async function fetchRoutingDocument(input: DiscoveryFetchInput): Promise<TerraScaleRoutingDocument> {
  const url = new URL("/api/v1/discovery", `${input.origin}/`);
  url.searchParams.set("database", input.database);
  url.searchParams.set("branch", input.branch);
  if (input.revision !== undefined) url.searchParams.set("rev", String(input.revision));
  if (input.stale) url.searchParams.set("stale", "1");
  const rttHeader = measuredRttHeader(input.measuredRttMs);
  const headers = new Headers({ accept: "application/json" });
  if (rttHeader !== undefined) headers.set("x-terrascale-region-rtt", rttHeader);

  const timeoutSignal = AbortSignal.timeout(input.timeoutMs);
  const composed = composeAbortSignals([input.signal, timeoutSignal]);
  const signal = composed.signal;
  try {
    return await dispatchDiscoveryFetch(input, url, headers, signal);
  } finally {
    composed.dispose();
  }
}

async function dispatchDiscoveryFetch(
  input: DiscoveryFetchInput,
  url: URL,
  headers: Headers,
  signal: AbortSignal | undefined,
): Promise<TerraScaleRoutingDocument> {
  const response = await input.fetch(new Request(url, {
    method: "GET",
    headers,
    redirect: "manual",
    ...(signal === undefined ? {} : { signal }),
  }));
  if (response.status === 404) {
    throw new TerraScaleDiscoveryError("not_found", `Database ${input.database} is unknown to discovery.`);
  }
  if (!response.ok) {
    throw new TerraScaleDiscoveryError(
      "unavailable",
      `Discovery answered ${response.status}.`,
    );
  }
  const payload: unknown = await response.json();
  let loopbackClient = false;
  try {
    loopbackClient = isDevOrigin(new URL(`${input.origin}/`));
  } catch {
    loopbackClient = false;
  }
  const document = parseRoutingDocument(payload, { database: input.database, branch: input.branch }, loopbackClient);
  if (document === null) {
    throw new TerraScaleDiscoveryError("invalid_document", "Discovery returned a malformed routing document.");
  }
  return document;
}

export type TerraScaleDiscoveryErrorCode = "not_found" | "unavailable" | "invalid_document" | "timeout" | "disabled";

export class TerraScaleDiscoveryError extends Error {
  readonly code: TerraScaleDiscoveryErrorCode;

  constructor(code: TerraScaleDiscoveryErrorCode, message: string) {
    super(message);
    this.name = "TerraScaleDiscoveryError";
    this.code = code;
  }
}

/**
 * The fixed path table for database-scoped requests, mirroring the edge's
 * parseDb exactly (ts-proxy design §4): `/api/v1/databases/{name}…`. The
 * database name is the routing scope; database-less routes never discover.
 */
export function parseRoutingDatabase(path: string): string | null {
  const segments = path.replace(/^\/+/, "").split("/").filter(Boolean);
  if (segments[0] !== "api" || segments[1] !== "v1") return null;
  if (segments[2] !== "databases" || segments.length < 4) return null;
  const database = segments[3];
  return database !== undefined && parseRoutingDatabaseName(database) !== null ? database : null;
}

export const defaultRoutingBranch = "main";

/**
 * Rounds a measured round trip into a bounded exponential moving average so
 * one slow outlier cannot permanently demote a healthy region.
 */
interface RttSample {
  value: number;
  atMs: number;
}

export class RegionRttTracker {
  readonly #samples = new Map<TerraScaleRegionCode, RttSample>();
  readonly #halflifeMs: number;

  constructor(halflifeMs = 60_000) {
    this.#halflifeMs = Math.max(1, halflifeMs);
  }

  observe(region: TerraScaleRegionCode, rttMs: number, nowMs: number): void {
    if (!isRegionCode(region)) return;
    if (!Number.isFinite(rttMs) || rttMs < 0 || rttMs > 60_000) return;
    const previous = this.#samples.get(region);
    if (previous === undefined || previous.atMs > nowMs) {
      this.#samples.set(region, { value: rttMs, atMs: nowMs });
      return;
    }
    const decay = 2 ** (-(nowMs - previous.atMs) / this.#halflifeMs);
    this.#samples.set(region, { value: previous.value * decay + rttMs * (1 - decay), atMs: nowMs });
  }

  snapshot(): Map<TerraScaleRegionCode, number> {
    return new Map([...this.#samples].map(([region, sample]) => [region, sample.value]));
  }
}

export type ResolvedRoutingBase = {
  readonly baseUrl: string;
  readonly region: TerraScaleRegionCode;
  readonly fromDiscovery: true;
};

export type RouterConfig = {
  readonly origin: string;
  readonly discoveryBaseUrl: string | undefined;
  readonly fetch: FetchLike;
  readonly timeoutMs: number;
  readonly discoveryTimeoutMs: number;
  readonly projectId?: string | undefined;
  readonly tenantId?: string | undefined;
  readonly preferredRegion?: TerraScaleRegionCode | undefined;
};

/**
 * The routing seam the HTTP and reactive clients consult (ADR 0050 §5).
 * A healthy connection is kept: refreshes update the cache for future
 * selections, and only a routing failure or a 421 moves the client. Each
 * scope remembers its selected region; document order only fills the
 * fallback list (§5.5 — a refresh never re-pins a healthy connection).
 */
export class TerraScaleDiscoveryRouter {
  readonly #config: RouterConfig;
  readonly #cache: TerraScaleRoutingCache;
  readonly #rtt = new RegionRttTracker();
  /** Sticky selected region per scope; moves only on failure, 421, or exit from the eligible set. */
  readonly #pinned = new Map<string, TerraScaleRegionCode>();

  constructor(config: RouterConfig, cache: TerraScaleRoutingCache = sharedRoutingCacheInstance()) {
    this.#config = config;
    this.#cache = cache;
  }

  get discoveryOrigin(): string {
    return this.#config.discoveryBaseUrl ?? this.#config.origin;
  }

  scopeFor(database: string, branch: string = defaultRoutingBranch): TerraScaleRoutingScope {
    return {
      origin: this.discoveryOrigin,
      database,
      branch,
      ...(this.#config.projectId === undefined ? {} : { projectId: this.#config.projectId }),
      ...(this.#config.tenantId === undefined ? {} : { tenantId: this.#config.tenantId }),
    };
  }

  cachedDocument(database: string, branch?: string): TerraScaleRoutingDocument | undefined {
    return this.#cache.get(this.scopeFor(database, branch));
  }

  /**
   * Resolve the base URL for one database-scoped operation. Returns undefined
   * when discovery cannot answer (cache miss + failed/timeout request): the
   * caller falls back to the ADR 0043 global-origin path for that request.
   */
  async resolveBase(database: string, needsWrite: boolean): Promise<ResolvedRoutingBase | undefined> {
    const scope = this.scopeFor(database);
    let document = this.#cache.get(scope);
    if (document === undefined) {
      if (this.#cache.failureBackoffRemainingMs(scope, 1_000) > 0) return undefined;
      const stale = this.#cache.consumeStale(scope);
      // The coalesced request is bounded only by its own timeout: one joining
      // caller's cancellation must never fail (or arm the backoff for) the
      // shared single-flight discovery.
      const generation = this.#cache.generation(scope);
      try {
        document = await this.#cache.coalesce(scope, () => fetchRoutingDocument({
          origin: this.discoveryOrigin,
          database,
          branch: defaultRoutingBranch,
          ...(stale ? { stale: true } : {}),
          measuredRttMs: this.#rtt.snapshot(),
          fetch: this.#config.fetch,
          timeoutMs: this.#config.discoveryTimeoutMs,
        }));
        if (this.#cache.generation(scope) !== generation && this.#cache.get(scope) !== document) {
          // A fresher 421-learned placement or demotion landed mid-flight; the
          // fetched document is older than what the scope already holds (the
          // identical-document case is the initiator of this very single-flight
          // applying it for a joiner). Drop it — the stale-marked scope
          // refreshes correctly on the next request.
          return undefined;
        }
        this.#cache.set(scope, document);
      } catch (error) {
        // Any discovery failure — including a worker 404, which the contract
        // reserves — falls back to the ADR 0043 path; the member's response
        // stays authoritative for existence, not the discovery cache. Only
        // genuine failures arm the backoff, never a cancellation.
        if (!isAbortError(error)) this.#cache.noteFailure(scope);
        return undefined;
      }
    }
    const endpoint = this.#selectEndpoint(scope, document, needsWrite);
    if (endpoint === undefined) return undefined;
    return { baseUrl: endpoint.url, region: endpoint.region, fromDiscovery: true };
  }

  /**
   * Selects the endpoint for one operation: the scope's remembered region
   * while it is still in the document and capability-eligible, otherwise the
   * configured preference or the document's first eligible endpoint. Only a
   * selection that actually moves rewrites the remembered region.
   */
  #selectEndpoint(
    scope: TerraScaleRoutingScope,
    document: TerraScaleRoutingDocument,
    needsWrite: boolean,
  ): TerraScaleRoutingEndpoint | undefined {
    const required: TerraScaleRoutingCapability = needsWrite ? "write" : "read";
    const eligible = document.endpoints.filter((endpoint) => endpointSupports(endpoint, required));
    if (eligible.length === 0) return undefined;
    const key = scopeKey(scope);
    const pinned = this.#pinned.get(key);
    if (pinned !== undefined) {
      const kept = eligible.find((endpoint) => endpoint.region === pinned);
      if (kept !== undefined) return kept;
    }
    const preferred = this.#config.preferredRegion;
    const hinted = preferred === undefined ? undefined : eligible.find((endpoint) => endpoint.region === preferred);
    const chosen = hinted ?? eligible[0];
    if (chosen === undefined) return undefined;
    this.#pinned.set(key, chosen.region);
    return chosen;
  }

  /** A member 421 or placement header teaches placement without a round trip. */
  notePlacement(database: string, regions: readonly TerraScaleRegionCode[], revision: number): void {
    const scope = this.scopeFor(database);
    const synthesized = routingDocumentFromPlacement(scope, regions, revision);
    if (synthesized !== null) this.#cache.set(scope, synthesized);
    else this.#cache.invalidate(scope);
    // The member's authoritative placement replaces any remembered region.
    this.#pinned.delete(scopeKey(scope));
    // The synthesized document is provisional; the next refresh is corrective.
    this.#cache.markStale(scope);
  }

  /** A rejected route: drop the cache and mark the scope for a corrective lookup. */
  reportRejectedRoute(database: string): void {
    const scope = this.scopeFor(database);
    this.#cache.invalidate(scope);
    this.#pinned.delete(scopeKey(scope));
  }

  /** A regional transport failure demotes that region behind the alternatives. */
  reportRegionFailure(database: string, region: TerraScaleRegionCode): void {
    this.#cache.demoteRegion(this.scopeFor(database), region);
  }

  /**
   * A member `region_route_change` frame: leave the old region and prefer the
   * frame's advertised target while it stays eligible, instead of blindly
   * taking the next-ranked endpoint.
   */
  reportRegionMove(database: string, from: TerraScaleRegionCode | undefined, to: TerraScaleRegionCode): void {
    const scope = this.scopeFor(database);
    if (from !== undefined) this.#cache.demoteRegion(scope, from);
    this.#pinned.set(scopeKey(scope), to);
  }

  recordLatency(region: TerraScaleRegionCode, rttMs: number): void {
    this.#rtt.observe(region, rttMs, Date.now());
  }
}
