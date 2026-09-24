import {
  type ResolvedTerraScaleRuntimeConfig,
  resolveAccessToken,
  resolveRuntimeConfig,
  type TerraScaleRuntimeConfig,
} from "./config.js";
import {
  composeAbortSignals,
  TerraScaleDiscoveryRouter,
  discoveryActive,
  isProductRoute,
  isRegionCatalogRoute,
  parseRoutingDatabase,
} from "./discovery.js";
import {
  err,
  ok,
  type TerraScaleError,
  type TerraScaleErrorCategory,
  type TerraScaleResult,
  type TerraScaleValidationIssue,
  sanitizeDiagnostic,
} from "./result.js";
import {
  canonicalRegionCode,
  regionalApiBaseUrl,
  terraScaleDiscoveryBaseUrl,
  type TerraScaleRegionCode,
} from "./regions.js";
import { TsAuthEndpointDiscovery } from "./ts-auth-discovery.js";
export type TerraScaleRouteName =
  | `access.${string}`
  | `admin.${string}`
  | `audit.${string}`
  | `backups.${string}`
  | `databaseDocuments.${string}`
  | `databaseBranches.${string}`
  | `databases.${string}`
  | `documents.${string}`
  | `reactive.${string}`
  | `sql.${string}`
  | `notifications.${string}`
  | `onboarding.${string}`
  | `organizations.${string}`
  | `health.${string}`
  | `regions.${string}`
  | `savedQueries.${string}`
  | `settings.${string}`
  | `usage.${string}`
  | `tsauth.${string}`;

export type TerraScaleBase = "api";
export type TerraScaleHttpMethod = "DELETE" | "GET" | "PATCH" | "POST" | "PUT";
export type QueryScalar = boolean | Date | number | string;
export type QueryValue = QueryScalar | readonly QueryScalar[] | null | undefined;
export type QueryRecord = object;

export type TerraScaleRequestOptions<TBody = unknown> = {
  readonly base?: TerraScaleBase | undefined;
  readonly method: TerraScaleHttpMethod;
  readonly path: string;
  readonly routeName: TerraScaleRouteName;
  /** Per-request project scope; falls back to the client configuration. */
  readonly projectId?: string | undefined;
  readonly query?: QueryRecord | undefined;
  readonly body?: TBody | undefined;
  /** Pre-encoded form body, sent as application/x-www-form-urlencoded. */
  readonly formBody?: URLSearchParams | string | undefined;
  readonly headers?: HeadersInit | undefined;
  readonly idempotencyKey?: string | false | undefined;
  /** Opt in only for named read operations whose POST body is safe to repeat. */
  readonly retryableRead?: boolean | undefined;
  /** Abort the underlying fetch, including while a retry is pending. */
  readonly signal?: AbortSignal | undefined;
  /** A per-request REST Region hint. It is never used for reactive traffic. */
  readonly regionHint?: TerraScaleRegionCode | undefined;
};

export type TerraScaleRequestMetric = {
  readonly base: TerraScaleBase;
  readonly routeName: TerraScaleRouteName;
  readonly status?: number | undefined;
  readonly durationMs: number;
};

export type TerraScaleRequestMetricRecorder = (metric: TerraScaleRequestMetric) => void;

export class TerraScaleHttp {
  readonly config: ResolvedTerraScaleRuntimeConfig;
  readonly #fetch: (input: RequestInfo | URL, init?: RequestInit) => Promise<Response>;
  readonly #accessToken: TerraScaleRuntimeConfig["accessToken"];
  readonly #idempotencyKeyFactory: TerraScaleRuntimeConfig["idempotencyKeyFactory"];
  readonly #requestMetricRecorder: TerraScaleRuntimeConfig["requestMetricRecorder"];
  #pinnedRegion: TerraScaleRegionCode | undefined;
  #sessionToken: string | undefined;
  /** SDK-driven discovery router (ADR 0050); undefined keeps the ADR 0043 path. */
  readonly #router: TerraScaleDiscoveryRouter | undefined;
  readonly #authDiscovery: TsAuthEndpointDiscovery | undefined;

  constructor(config: TerraScaleRuntimeConfig, options?: { readonly authReverseProxy?: boolean }) {
    this.config = resolveRuntimeConfig(config);
    validateCredentialEndpoint(this.config.apiBaseUrl, config.fetch !== undefined);
    const fetchHandler = config.fetch ?? globalThis.fetch?.bind(globalThis);
    if (fetchHandler === undefined) {
      throw new TypeError("TerraScaleClient requires a fetch implementation.");
    }
    this.#fetch = fetchHandler;
    this.#authDiscovery = options?.authReverseProxy !== true &&
      this.config.apiBaseUrl === "https://auth.terrascale.tech"
      ? new TsAuthEndpointDiscovery(fetchHandler)
      : undefined;
    this.#accessToken = config.accessToken;
    this.#idempotencyKeyFactory = config.idempotencyKeyFactory;
    this.#requestMetricRecorder = config.requestMetricRecorder;
    this.#pinnedRegion = undefined;
    const configuredSession = (config as TerraScaleRuntimeConfig & { readonly sessionToken?: unknown }).sessionToken;
    this.#sessionToken = validateSessionToken(configuredSession) ? configuredSession.trim() : undefined;
    this.#router = this.config.regionAffinity !== "strict" && discoveryActive(this.config.routing, this.config.apiBaseUrl)
      ? new TerraScaleDiscoveryRouter({
        origin: this.config.apiBaseUrl,
        // The discovery origin is the metadata host, independent of the
        // product `apiBaseUrl`; `routing.baseUrl` overrides it for tests/dev.
        discoveryBaseUrl: this.config.routing.baseUrl ?? terraScaleDiscoveryBaseUrl,
        fetch: fetchHandler,
        timeoutMs: this.config.requestTimeoutMs,
        discoveryTimeoutMs: this.config.routing.timeoutMs ?? 3_000,
        ...(this.config.projectId === undefined ? {} : { projectId: this.config.projectId }),
        ...(this.config.tenantId === undefined ? {} : { tenantId: this.config.tenantId }),
        ...(this.config.preferredRegion === undefined ? {} : { preferredRegion: this.config.preferredRegion }),
      })
      : undefined;
  }

  /** The discovery router, shared with the reactive client for one config. */
  get discoveryRouter(): TerraScaleDiscoveryRouter | undefined {
    return this.#router;
  }

  /** Region learned from trusted first-contact/421 response headers. */
  get pinnedRegion(): TerraScaleRegionCode | undefined {
    return this.#pinnedRegion ?? this.config.preferredRegion;
  }

  /** Current opaque causal session frontier, if one has been observed. */
  get sessionToken(): string | undefined {
    return this.#sessionToken;
  }

  /** Explicitly install a causal session frontier for subsequent requests. */
  setSessionToken(token: string | undefined): void {
    if (token === undefined) {
      this.#sessionToken = undefined;
      return;
    }
    if (!validateSessionToken(token)) {
      throw new TypeError("Session tokens must be nonblank, contain no control characters, and be at most 16384 characters.");
    }
    this.#sessionToken = token.trim();
  }

  /** Explicitly pin REST traffic to one registered Region. */
  pinRegion(region: TerraScaleRegionCode): void {
    this.#pinnedRegion = canonicalRegionCode(region);
    if (this.#pinnedRegion === undefined) {
      throw new TypeError("region must be exactly one of GRU, SFO, or FRA.");
    }
  }

  clearRegionPin(): void {
    this.#pinnedRegion = undefined;
  }

  async request<TResponse, TBody = unknown>(
    options: TerraScaleRequestOptions<TBody>,
  ): Promise<TerraScaleResult<TResponse>> {
    const base: TerraScaleBase = "api";
    const startedAt = performance.now();
    let idempotencyKey: string | undefined;

    if (options.body !== undefined && options.formBody !== undefined) {
      throw new TypeError("Requests accept either a JSON body or a form body, not both.");
    }

    try {
      options.signal?.throwIfAborted();
      idempotencyKey = this.resolveIdempotencyKey(options);
      // One logical operation must retain the same bytes across retries,
      // even when the caller mutates its object while a request is pending.
      const requestBody = options.body !== undefined
        ? options.body instanceof Uint8Array
          ? options.body
          : JSON.stringify(options.body)
        : options.formBody === undefined
          ? undefined
          : typeof options.formBody === "string"
            ? options.formBody
            : options.formBody.toString();
      // ADR 0050 §1/§8 route classification: database-scoped requests route
      // through discovery to a direct regional endpoint; product routes target
      // the product origin; every other member route selects a regional
      // endpoint from the catalog. There is no proxy fallback anymore.
      // ts-auth is a separate service on its own origin, outside that
      // classification: discovery, catalog selection, and regional rewriting
      // never apply to its requests.
      const serviceRoute = options.routeName.startsWith("tsauth.");
      const authEndpoints = serviceRoute ? await this.#authDiscovery?.endpoints(options.signal) : undefined;
      let authRegionIndex = 0;
      const routingDatabase = !serviceRoute && this.#router !== undefined ? parseRoutingDatabase(options.path) : null;
      const catalogRoute = !serviceRoute && this.#router !== undefined && routingDatabase === null && isRegionCatalogRoute(options.path);
      const productRoute = !serviceRoute && this.#router !== undefined && routingDatabase === null && isProductRoute(options.path);
      const needsWrite = isMutation(options.method) && !(options.retryableRead === true && options.method === "POST");
      // One bounded deadline covers every attempt (ADR 0050 §5.9).
      const deadline = this.config.requestTimeoutMs > 0 ? AbortSignal.timeout(this.config.requestTimeoutMs) : undefined;
      let attempt = 0;
      let repaired421 = false;
      let authRefreshed = false;
      let requestRegionHint = options.regionHint;
      while (true) {
        let responseReceived = false;
        let fetchStarted = false;
        let routedRegion: TerraScaleRegionCode | undefined;
        let attemptStartedAt = startedAt;
        // The token this attempt actually sends; the 401 replay compares a
        // re-resolved token against it, never against a shared latest value
        // that concurrent requests keep overwriting.
        const sentToken: { value: string | undefined } = { value: undefined };
        try {
          options.signal?.throwIfAborted();
          const routedBase = routingDatabase === null || this.#router === undefined
            ? undefined
            : await this.#router.resolveBase(routingDatabase, needsWrite);
          let requestBase: string;
          if (serviceRoute) {
            requestBase = authEndpoints?.[authRegionIndex] ?? this.config.apiBaseUrl;
          } else if (this.#router === undefined) {
            requestBase = this.baseUrl(requestRegionHint);
          } else if (catalogRoute) {
            // `api/v1/regions` exactly is the catalog itself, served by the discovery origin.
            requestBase = this.#router.discoveryOrigin;
          } else if (productRoute) {
            // Dashboard-owned product routes target the product origin.
            requestBase = this.config.apiBaseUrl;
          } else {
            // Discovery failures and database-less member routes degrade to
            // catalog selection (ADR 0050 §5.8); the configured origin is the
            // last resort when the catalog cannot answer either.
            requestBase = routedBase?.baseUrl
              ?? (await this.#router.resolveCatalogBase())
              ?? this.baseUrl(requestRegionHint);
          }
          routedRegion = routedBase?.region;
          attemptStartedAt = performance.now();
          const url = buildUrl(requestBase, options.path, options.query, options.routeName);
          const headers = await withCancellation(
            this.buildHeaders({ ...options, regionHint: requestRegionHint }, idempotencyKey, sentToken),
            options.signal,
          );
          // composeAbortSignals returns the single live signal as-is (the
          // common case: a deadline, no caller signal), so the per-attempt
          // composition only allocates when both are present.
          const attemptCompose = composeAbortSignals([options.signal, deadline]);
          const attemptSignal = attemptCompose.signal;
          options.signal?.throwIfAborted();
          const requestInit: RequestInit = {
            method: options.method,
            headers,
            redirect: "manual",
            ...(attemptSignal === undefined ? {} : { signal: attemptSignal }),
          };
          if (requestBody !== undefined) {
            headers.set(
              "content-type",
              headers.get("content-type") ??
                (options.formBody === undefined ? "application/json" : "application/x-www-form-urlencoded"),
            );
            requestInit.body = requestBody instanceof Uint8Array ? requestBody as unknown as BodyInit : requestBody;
          }

          const request = new Request(url, requestInit);
          fetchStarted = true;
          let response: Response;
          let body: ParsedBody;
          try {
            response = await this.#fetch(request);
            responseReceived = true;
            if (serviceRoute && options.method === "GET" && authEndpoints !== undefined &&
                authRegionIndex < authEndpoints.length - 1 && [502, 503, 504].includes(response.status)) {
              void response.body?.cancel().catch(() => undefined);
              authRegionIndex++;
              continue;
            }
            if (response.type === "opaqueredirect" || (response.status >= 300 && response.status < 400 && response.status !== 304)) {
              // The redirected body is never consumed; drain it before failing.
              void response.body?.cancel().catch(() => undefined);
              throw new TypeError("The TerraScale API returned an unsupported HTTP redirect.");
            }
            body = await readResponseBody(response, options.routeName);
          } finally {
            // The composed signal stays attached until the body has been read:
            // disposing at header time would detach the caller signal and the
            // deadline while the stream is still in flight.
            attemptCompose.dispose();
          }
          this.observeSession(response.headers);
          if (routedRegion !== undefined && response.status < 500) {
            this.#router?.recordLatency(routedRegion, performance.now() - attemptStartedAt);
          }
          this.recordRequestMetric(base, options.routeName, response.status, performance.now() - startedAt);

          // Conditional reads use 304 as a successful cache hit. There is no
          // representation body, so callers receive `undefined` and can use
          // the status/headers to distinguish it from a normal 200 response.
          if (response.status === 304) {
            return ok(options.routeName, response.status, response.headers, undefined as TResponse);
          }

          if (response.ok) {
            if (!body.ok) {
              return err(
                options.routeName,
                {
                  kind: "invalid-response",
                  code: "invalid_response",
                  message: body.errorMessage,
                  status: response.status,
                  requestId: requestIdFrom(response.headers),
                },
                response.status,
                response.headers,
                body.raw,
              );
            }

            return ok(options.routeName, response.status, response.headers, body.value as TResponse);
          }

          // A member reports degraded liveness as HTTP 503 while retaining
          // the canonical HealthResponse body. Keep that typed observation so
          // callers can distinguish an unhealthy member from a transport or
          // API error, matching the other SDKs.
          if (response.status === 503 && options.routeName === "health.health" && body.ok) {
            return ok(options.routeName, response.status, response.headers, body.value as TResponse);
          }

          if (response.status === 421 && !repaired421 && this.config.regionAffinity !== "strict") {
            const repaired = placementRegion(response.headers);
            if (routingDatabase !== null && this.#router !== undefined) {
              // The member is authoritative (ADR 0043 §2): adopt its
              // placement immediately and schedule a corrective refresh.
              this.#router.notePlacement(routingDatabase, placementRegions(response.headers), 0);
            }
            if (repaired !== undefined && repaired !== (requestRegionHint ?? this.#pinnedRegion)) {
              this.#pinnedRegion = repaired;
              // A 421 advertises placement, but does not prove that the
              // original request had no effect. Only replay safe reads or a
              // mutation carrying the same stable identity used on the first
              // attempt. Unkeyed mutations surface the response after learning
              // the pin so a later explicit retry can choose the right member.
              if (canRepairPlacement(options.method, idempotencyKey, options.retryableRead)) {
                repaired421 = true;
                requestRegionHint = repaired;
                // Placement is authoritative for this one replay, including
                // when the original request supplied a per-call hint.
                attempt++;
                continue;
              }
            }
          }

          // Bounded authentication refresh (ADR 0050 §5.6): a 401 never
          // executed the request, so one re-resolution and replay is safe
          // for every method — but only once and only when the token moved
          // relative to the token this attempt actually sent.
          if (response.status === 401 && !authRefreshed && typeof this.#accessToken === "function") {
            const refreshed = await resolveAccessToken(this.#accessToken);
            if (refreshed !== undefined && refreshed.length > 0 && refreshed !== sentToken.value) {
              authRefreshed = true;
              attempt++;
              continue;
            }
          }

          const errorBody = sanitizeHttpDiagnostic(body.ok ? body.value : body.raw);
          const error = errorFromResponse(response, errorBody, options.routeName);
          if (retryableStatuses.has(response.status) && routedRegion !== undefined && routingDatabase !== null) {
            // A regional endpoint implicated by a retryable failure moves
            // behind the eligible alternatives (ADR 0050 §5.3).
            this.#router?.reportRegionFailure(routingDatabase, routedRegion);
          }
          if (deadline?.aborted) {
            return err(options.routeName, error, response.status, response.headers, errorBody);
          }
          if (this.canRetry(options.method, idempotencyKey, attempt, response.status, options.retryableRead)) {
            const delayMs = retryDelay(response, this.config.retryDelayMs, attempt);
            // Preserve the server response when its backoff exceeds our automatic
            // retry window. Capping Retry-After would send the request too early.
            if (delayMs <= 30_000) {
              // The deadline bounds the sleep too: aborting mid-sleep stops the
              // loop promptly instead of after the full backoff.
              const sleepCompose = composeAbortSignals([options.signal, deadline]);
              try {
                await delayForRetry(delayMs, sleepCompose.signal);
              } finally {
                sleepCompose.dispose();
              }
              attempt++;
              continue;
            }
          }

          return err(
            options.routeName,
            error,
            response.status,
            response.headers,
            errorBody,
          );
        } catch (error) {
          if (serviceRoute && options.method === "GET" && authEndpoints !== undefined &&
              authRegionIndex < authEndpoints.length - 1 && fetchStarted && !responseReceived &&
              !options.signal?.aborted && deadline?.aborted !== true) {
            authRegionIndex++;
            continue;
          }
          if (
            fetchStarted && !responseReceived && !options.signal?.aborted && deadline?.aborted !== true &&
            routingDatabase !== null && routedRegion !== undefined
          ) {
            this.#router?.reportRegionFailure(routingDatabase, routedRegion);
          }
          if (fetchStarted && !responseReceived && !options.signal?.aborted && deadline?.aborted !== true && this.canRetry(options.method, idempotencyKey, attempt, undefined, options.retryableRead)) {
            await delayForRetry(retryDelay(undefined, this.config.retryDelayMs, attempt), options.signal);
            attempt++;
            continue;
          }

          throw error;
        }
      }
    } catch (error) {
      this.recordRequestMetric(base, options.routeName, undefined, performance.now() - startedAt);
      const safeError = sanitizeHttpDiagnostic(error);
      return err(options.routeName, {
        kind: "transport",
        code: "transport_error",
        message:
          error instanceof Error
            ? sanitizeHttpDiagnostic(error.message) as string
            : "The request failed before a response was received.",
        details: safeError,
      });
    }
  }

  private baseUrl(regionHint?: TerraScaleRegionCode): string {
    const region = regionHint ?? this.#pinnedRegion;
    if (region === undefined) {
      return this.config.apiBaseUrl;
    }
    return regionalApiBaseUrl(this.config.apiBaseUrl, region);
  }

  private async buildHeaders<TBody>(
    options: TerraScaleRequestOptions<TBody>,
    idempotencyKey: string | undefined,
    sentToken: { value: string | undefined },
  ): Promise<Headers> {
    validateInternalHeaders(options.headers, options.routeName);
    const headers = new Headers();

    headers.set("accept", options.routeName === "databases.downloadExport" ? "application/octet-stream" : "application/json");
    if (this.config.tenantId !== undefined) {
      headers.set("x-terrascale-tenant-id", this.config.tenantId);
    }
    if ((options.projectId ?? this.config.projectId) !== undefined) {
      headers.set("x-terrascale-project-id", options.projectId ?? this.config.projectId!);
    }

    if (
      options.regionHint !== undefined ||
      (this.config.regionAffinity === "failover" &&
        (this.config.preferredRegion !== undefined || this.#pinnedRegion !== undefined))
    ) {
      headers.set(
        "x-terrascale-preferred-region",
        (options.regionHint ?? this.#pinnedRegion ?? this.config.preferredRegion)!,
      );
    }

    // ts-auth public OIDC endpoints authenticate with client credentials in
    // the form body (or a caller-supplied OAuth access token on userinfo),
    // never with the instance sk_ management key from the client config.
    const token = options.routeName.startsWith("tsauth.oidc.")
      ? undefined
      : await resolveAccessToken(this.#accessToken);
    sentToken.value = token;

    if (token !== undefined && token.length > 0) {
      headers.set("authorization", token.startsWith("Bearer ") ? token : `Bearer ${token}`);
    }

    if (this.#sessionToken !== undefined) {
      headers.set("x-terrascale-session", this.#sessionToken);
    }

    if (options.headers !== undefined) {
      for (const [key, value] of new Headers(options.headers).entries()) {
        headers.set(key, value);
      }
    }

    if (idempotencyKey !== undefined) {
      headers.set("idempotency-key", idempotencyKey);
    }

    return headers;
  }

  private resolveIdempotencyKey<TBody>(options: TerraScaleRequestOptions<TBody>): string | undefined {
    if ((!isMutation(options.method) && options.idempotencyKey === undefined) || options.idempotencyKey === false) {
      return undefined;
    }

    const key = options.idempotencyKey ?? this.#idempotencyKeyFactory?.();
    if (key === undefined) {
      return undefined;
    }

    const normalized = key.trim();
    if (normalized.length === 0 || normalized.length > 128) {
      throw new TypeError("Idempotency keys must be nonblank and contain at most 128 characters.");
    }
    return normalized;
  }

  private canRetry(
    method: TerraScaleHttpMethod,
    idempotencyKey: string | undefined,
    attempt: number,
    status?: number,
    retryableRead = false,
  ): boolean {
    if (attempt >= this.config.maxRetryAttempts) {
      return false;
    }

    // GET is safe to repeat by contract. Mutations require the caller's
    // stable idempotency identity; other POSTs (SQL/reactive) are not
    // assumed idempotent merely because they use HTTP POST.
    const retryableOperation = (retryableRead && method === "POST") || method === "GET" || (isMutation(method) && idempotencyKey !== undefined);
    if (!retryableOperation) {
      return false;
    }

    return status === undefined || retryableStatuses.has(status);
  }

  private observeSession(headers: Headers): void {
    const candidate = headers.get("x-terrascale-session")?.trim();
    if (candidate === undefined || !validateSessionToken(candidate)) return;
    const current = this.#sessionToken;
    if (current === undefined || sessionTokenAdvances(current, candidate)) {
      this.#sessionToken = candidate;
    }
  }

  private recordRequestMetric(
    base: TerraScaleBase,
    routeName: TerraScaleRouteName,
    status: number | undefined,
    durationMs: number,
  ): void {
    try {
      this.#requestMetricRecorder?.({ base, routeName, status, durationMs });
    } catch {
      return;
    }
  }
}

function validateCredentialEndpoint(baseUrl: string, explicitTestTransport: boolean): void {
  let url: URL;
  try {
    url = new URL(`${baseUrl}/`);
  } catch {
    // Preserve request-time transport errors for malformed URLs. The URL
    // cannot carry credentials until it parses successfully.
    return;
  }
  if (url.username !== "" || url.password !== "") {
    throw new TypeError("TerraScale API base URLs cannot contain credentials.");
  }
  for (const key of url.searchParams.keys()) {
    if (/(?:auth|cookie|credential|key|pass(?:word)?|secret|session|token)/iu.test(key)) {
      throw new TypeError("TerraScale API base URLs cannot contain secret-bearing query parameters.");
    }
  }
  if (url.protocol === "https:") return;
  const loopback = url.hostname === "localhost" || url.hostname === "127.0.0.1" || url.hostname === "[::1]";
  const testHost = url.hostname === "test" || url.hostname.endsWith(".test");
  if (url.protocol === "http:" && (loopback || (explicitTestTransport && testHost))) return;
  throw new TypeError("TerraScale API endpoints must use HTTPS, except explicit loopback/test transports.");
}

const internalHeaderNames = new Set([
  "content-type",
  "if-match",
  "if-none-match",
  "x-terrascale-durability",
  "x-terrascale-session",
  "x-terrascale-allow-stale",
  "x-terrascale-delete-confirmation",
  "x-terrascale-preferred-region",
  "x-terrascale-auth-transaction",
  "x-terrascale-sha256",
]);

function validateInternalHeaders(headers: HeadersInit | undefined, routeName: TerraScaleRouteName = "" as TerraScaleRouteName): void {
  if (headers === undefined) {
    return;
  }

  // ts-auth OAuth userinfo carries a caller-selected OAuth bearer token;
  // authorization stays non-configurable on every other surface.
  const callerBearerAllowed = routeName === "tsauth.oidc.userinfo";
  for (const [name] of new Headers(headers).entries()) {
    if (!internalHeaderNames.has(name) && !(callerBearerAllowed && name === "authorization")) {
      throw new TypeError(
        `Header "${name}" is not configurable; use the named TerraScale SDK operation option instead.`,
      );
    }
  }
}

function isMutation(method: TerraScaleHttpMethod): boolean {
  return method === "DELETE" || method === "PATCH" || method === "POST" || method === "PUT";
}

function canRepairPlacement(
  method: TerraScaleHttpMethod,
  idempotencyKey: string | undefined,
  retryableRead = false,
): boolean {
  return method === "GET" || (retryableRead && method === "POST") || idempotencyKey !== undefined;
}

// Transient failures that a bounded, idempotent retry can repair. 502/503/504
// are the standard gateway statuses; 520-524 are Cloudflare's origin
// connectivity class (520 unknown, 521 origin down, 522 connection timed out,
// 523 origin unreachable, 524 origin timeout) and are returned before the
// request reaches the Worker. 525/526 are TLS/certificate configuration errors
// and stay deterministic rather than retried.
const retryableStatuses = new Set([408, 429, 500, 502, 503, 504, 520, 521, 522, 523, 524]);

function validateSessionToken(value: unknown): value is string {
  if (typeof value !== "string") return false;
  const normalized = value.trim();
  return normalized.length > 0 && normalized.length <= 16_384 && normalized.length % 4 !== 1 &&
    /^[A-Za-z0-9_-]+$/u.test(normalized);
}

function sessionTokenAdvances(current: string, candidate: string): boolean {
  // SessionToken is intentionally opaque: only the producer can interpret
  // its causal frontier. Keep the latest valid observation rather than
  // comparing encodings as if they were numeric revisions.
  return candidate !== current;
}

function placementRegion(headers: Headers): TerraScaleRegionCode | undefined {
  const raw = headers.get("x-terrascale-placement");
  if (raw === null) return undefined;
  for (const candidate of raw.split(",")) {
    const region = canonicalRegionCode(candidate.trim());
    if (region !== undefined) return region;
  }
  return undefined;
}

/** All regions in a placement header, order preserved (home first, ADR 0043 §1). */
function placementRegions(headers: Headers): TerraScaleRegionCode[] {
  const raw = headers.get("x-terrascale-placement");
  if (raw === null) return [];
  const regions: TerraScaleRegionCode[] = [];
  for (const candidate of raw.split(",")) {
    const region = canonicalRegionCode(candidate.trim());
    if (region !== undefined && !regions.includes(region)) regions.push(region);
  }
  return regions;
}

/** Retry-After parsed to milliseconds; undefined when absent or unparseable. */
function retryAfterMs(response: Response | undefined): number | undefined {
  const retryAfter = response?.headers.get("retry-after");
  if (retryAfter === null || retryAfter === undefined) {
    return undefined;
  }

  const seconds = Number(retryAfter);
  if (Number.isFinite(seconds) && seconds >= 0) {
    return seconds * 1_000;
  }

  const date = Date.parse(retryAfter);
  return Number.isFinite(date) ? Math.max(0, date - Date.now()) : undefined;
}

function retryDelay(response: Response | undefined, baseDelayMs: number, attempt: number): number {
  const retryAfter = retryAfterMs(response);
  if (retryAfter !== undefined) {
    return retryAfter;
  }

  // Computed backoff carries jitter (ADR 0050 §5.9) so concurrent clients
  // that fail together do not retry in lockstep; server-provided
  // Retry-After timing is honoured exactly and never jittered.
  const exponential = Math.min(baseDelayMs * (2 ** attempt), 30_000);
  const jitterRatio = 0.8 + Math.random() * 0.4;
  return Math.round(Math.min(exponential * jitterRatio, 30_000));
}

function withCancellation<T>(operation: Promise<T>, signal?: AbortSignal): Promise<T> {
  if (signal === undefined) return operation;
  return new Promise<T>((resolve, reject) => {
    const onAbort = (): void => reject(signal.reason ?? new DOMException("The operation was aborted", "AbortError"));
    if (signal.aborted) onAbort();
    else signal.addEventListener("abort", onAbort, { once: true });
    void operation.then(resolve, reject).finally(() => signal.removeEventListener("abort", onAbort));
  });
}

function delayForRetry(delayMs: number, signal?: AbortSignal): Promise<void> {
  if (signal?.aborted) return Promise.reject(signal.reason ?? new DOMException("The operation was aborted", "AbortError"));
  if (delayMs === 0) {
    return Promise.resolve();
  }

  return new Promise<void>((resolve, reject) => {
    const timer = setTimeout(() => {
      signal?.removeEventListener("abort", onAbort);
      resolve();
    }, delayMs);
    const onAbort = (): void => {
      clearTimeout(timer);
      reject(signal?.reason ?? new DOMException("The operation was aborted", "AbortError"));
    };
    signal?.addEventListener("abort", onAbort, { once: true });
    if (signal?.aborted) onAbort();
  });
}

type ParsedBody =
  | { readonly ok: true; readonly value: unknown; readonly raw: string }
  | { readonly ok: false; readonly errorMessage: string; readonly raw: string };

function buildUrl(baseUrl: string, path: string, query: QueryRecord | undefined, routeName: TerraScaleRouteName): URL {
  const normalizedPath = path.replace(/^\/+/, "");
  if (routeName.startsWith("tsauth.")) {
    // ts-auth is a separate service on its own origin, not a TerraScale
    // product surface: its management API is rooted at /v1 and its public
    // OIDC endpoints hang directly under the instance slug segment. The
    // product /api/v1 guard does not apply, but a ts-auth request path must
    // still match one of those two published shapes.
    if (!/^v1\//u.test(normalizedPath) && !/^[^/]+\/(?:\.well-known|oauth)\//u.test(normalizedPath)) {
      throw new TypeError("ts-auth requests are limited to /v1 management routes and instance-scoped public OIDC routes.");
    }
  } else if (!/^api\/v1(?:\/|$)/u.test(normalizedPath)) {
    throw new TypeError("TerraScale SDK requests are limited to the public /api/v1 contract.");
  }

  const base = new URL(`${baseUrl}/`);
  const url = new URL(normalizedPath, base);
  if (url.origin !== base.origin) {
    throw new TypeError("TerraScaleClient request paths cannot escape the configured origin.");
  }

  if (query === undefined) {
    return url;
  }

  for (const [key, value] of Object.entries(query) as [string, QueryValue][]) {
    if (value === null || value === undefined) {
      continue;
    }

    if (isQueryArray(value)) {
      for (const item of value) {
        url.searchParams.append(key, queryValueToString(item));
      }

      continue;
    }

    url.searchParams.set(key, queryValueToString(value));
  }

  return url;
}

function queryValueToString(value: QueryScalar): string {
  return value instanceof Date ? value.toISOString() : String(value);
}

function isQueryArray(value: QueryValue): value is readonly QueryScalar[] {
  return Array.isArray(value);
}

async function readResponseBody(response: Response, routeName: TerraScaleRouteName): Promise<ParsedBody> {
  if (response.ok && routeName === "databases.downloadFile") {
    return { ok: true, value: new Uint8Array(await response.arrayBuffer()), raw: "" };
  }
  if (response.ok && routeName === "databases.downloadExport") {
    const mediaType = response.headers.get("content-type")?.split(";", 1)[0]?.trim().toLowerCase();
    if (mediaType !== "application/octet-stream") {
      return { ok: false, errorMessage: "Export artifacts require application/octet-stream.", raw: await response.text() };
    }
    return { ok: true, value: new Uint8Array(await response.arrayBuffer()), raw: "" };
  }
  // ts-auth JSON routes publish JSON bodies, so an empty or non-JSON 2xx is a
  // broken response, not a passthrough value. Void routes are exempt: the
  // service answers revoke with 200 plus an empty body (RFC 7009) and the
  // delete family with 204 No Content, which never carries a body.
  const tsauthJsonRoute = response.ok &&
    routeName.startsWith("tsauth.") &&
    response.status !== 204 &&
    routeName !== "tsauth.oidc.revoke";
  const raw = await response.text();

  if (raw.length === 0) {
    if (tsauthJsonRoute) {
      return { ok: false, errorMessage: "ts-auth returned an empty response body.", raw };
    }
    return { ok: true, value: undefined, raw };
  }

  const contentType = response.headers.get("content-type") ?? "";

  if (!contentType.toLowerCase().includes("json")) {
    if (tsauthJsonRoute) {
      return {
        ok: false,
        errorMessage: `ts-auth returned a non-JSON response body (${contentType === "" ? "no content type" : contentType}).`,
        raw,
      };
    }
    return response.ok ? { ok: true, value: raw, raw } : { ok: true, value: { message: raw }, raw };
  }

  try {
    return { ok: true, value: response.ok ? parseSuccessResponse(raw, routeName) : JSON.parse(raw) as unknown, raw };
  } catch (error) {
    return {
      ok: false,
      errorMessage:
        error instanceof Error ? error.message : "The response body was not valid JSON.",
      raw,
    };
  }
}

function parseSuccessResponse(raw: string, routeName: TerraScaleRouteName): unknown {
  if (routeName === "databases.getDocument") return parseDocumentValue(raw);
  const integerPaths = responseUint64Paths[routeName];
  const signedPaths = responseInt64Paths[routeName];
  const rowsKey = routeName === "databases.executeSql" ? "rows"
    : routeName === "databases.listDocuments" ? "documents" : undefined;
  if (integerPaths === undefined && signedPaths === undefined && rowsKey === undefined) {
    // Keep response metadata and ordinary DTOs on the normal JSON contract.
    return JSON.parse(raw);
  }
  // Decode the lossless view once, then derive the ordinary view from it:
  // every BigInt becomes the same lossy number a plain JSON.parse of the same
  // text would have produced, before the whitelisted paths are re-installed.
  const precise = parseDocumentValue(raw);
  const ordinary: unknown = lossyNumberView(precise);
  if (integerPaths !== undefined || signedPaths !== undefined) {
    for (const fieldPath of integerPaths ?? []) installIntegerField(ordinary, precise, fieldPath.split("."), false);
    for (const fieldPath of signedPaths ?? []) installIntegerField(ordinary, precise, fieldPath.split("."), true);
  }
  if (rowsKey === undefined || !isRecord(ordinary)) return ordinary;
  // Document list and SQL rows also carry a recursive JsonValue payload.
  // Preserve its lossless integer handling after installing typed fields.
  const rows = ordinary[rowsKey];
  if (!Array.isArray(rows)) return ordinary;
  if (!isRecord(precise)) return ordinary;
  const preciseRows: unknown = precise[rowsKey];
  if (!Array.isArray(preciseRows)) return ordinary;
  return {
    ...ordinary,
    [rowsKey]: rows.map((row: unknown, index: number) => {
      const exact: unknown = preciseRows[index];
      return isRecord(row) && isRecord(exact) ? { ...row, value: exact.value } : row;
    }),
  };
}

/** Structural copy that keeps BigInt-free subtrees shared with the precise
 * view and converts every BigInt to its nearest double, which is exactly the
 * value a plain JSON.parse of the same source text produces. */
function lossyNumberView(value: unknown): unknown {
  if (typeof value === "bigint") return Number(value);
  if (Array.isArray(value)) {
    let copy: unknown[] | undefined;
    for (let index = 0; index < value.length; index++) {
      const converted = lossyNumberView(value[index]);
      if (converted !== value[index]) (copy ??= [...value])[index] = converted;
    }
    return copy ?? value;
  }
  if (isRecord(value)) {
    let copy: Record<string, unknown> | undefined;
    for (const key of Object.keys(value)) {
      const converted = lossyNumberView(value[key]);
      if (converted !== value[key]) (copy ??= { ...value })[key] = converted;
    }
    return copy ?? value;
  }
  return value;
}

const responseUint64Paths: Readonly<Partial<Record<TerraScaleRouteName, readonly string[]>>> = {
  "databases.listOperations": ["operations.*.revision"],
  "databases.getProvisioningOperation": ["revision"],
  "databases.insertUnique": ["expiresAtMs"],
  "databases.compareAndSwap": ["expiresAtMs"],
  "databases.consumeOne": ["expiresAtMs"],
  "databases.index": ["expiresAtMs"],
  "databases.findOne": ["expiresAtMs"],
  "databases.findPage": ["expiresAtMs"],
  "databases.count": ["count", "expiresAtMs"],
  "databases.updateOne": ["count", "expiresAtMs"],
  "databases.updateMany": ["count", "expiresAtMs"],
  "databases.deleteOne": ["count", "expiresAtMs"],
  "databases.deleteMany": ["count", "expiresAtMs"],
  "databases.incrementOne": ["count", "expiresAtMs"],
  "databases.consumeMatching": ["count", "expiresAtMs"],
  "databases.beginTransaction": ["expiresAtMs"],
  "databases.commitTransaction": ["expiresAtMs"],
  "databases.rollbackTransaction": ["expiresAtMs"],
  "databases.getSchema": ["generation", "schema_version", "migration.total", "migration.completed"],
  "databases.putSchema": ["generation", "schema_version", "migration.total", "migration.completed"],
  "databases.rollbackSchema": ["generation", "schema_version", "migration.total", "migration.completed"],
  "databases.databaseUsage": ["snapshot_sequence", "storage_complete_through_millis", "reads.total_reads"],
  "databases.indexRecommendations": ["generation", "recommendations.*.generation", "recommendations.*.maintenance_cost"],
  "databases.listIndexes": ["indexes.*.backfill_remaining"],
  "databases.listWebhooks": ["webhooks.*.created_at"],
  "databases.createWebhook": ["created_at"],
  "databases.rotateSecret": ["created_at"],
  "databases.developmentFault": ["expiresAtMillis"],
  "databases.clearDevelopmentFault": ["expiresAtMillis"],
  "databases.developmentLogs": ["entries.*.sequence", "entries.*.atMillis"],
  "databases.deleteWebhook": ["created_at"],
  "databases.listDeliveries": ["deliveries.*.revision", "deliveries.*.retry_started_at", "deliveries.*.next_attempt_at"],
  "databases.replayWebhook": ["revision", "retry_started_at", "next_attempt_at"],
  "databases.getExport": ["created_at_millis", "pages", "records", "estimated_records", "manifest.length"],
  "databases.resumeExport": ["created_at_millis", "pages", "records", "estimated_records", "manifest.length"],
  "databases.cancelExport": ["created_at_millis", "pages", "records", "estimated_records", "manifest.length"],
  "databases.listExports": ["exports.*.created_at_millis", "exports.*.pages", "exports.*.records", "exports.*.estimated_records", "exports.*.manifest.length"],
  "databases.listImports": ["*.committed_records", "*.cursor", "*.input_length", "*.pending_cursor", "*.pending_records"],
  "databases.createImport": ["committed_records", "cursor", "input_length", "pending_cursor", "pending_records"],
  "databases.getImport": ["committed_records", "cursor", "input_length", "pending_cursor", "pending_records"],
  "databases.uploadImport": ["committed_records", "cursor", "input_length", "pending_cursor", "pending_records"],
  "databases.resumeImport": ["committed_records", "cursor", "input_length", "pending_cursor", "pending_records"],
};

const responseInt64Paths: Readonly<Partial<Record<TerraScaleRouteName, readonly string[]>>> = {
  "databases.authorizationLease": ["issuedAt", "validUntil"],
  "databases.listDocuments": ["documents.*.version"],
  "databases.batchDocuments": ["committed"],
  "databases.listDatabases": ["databases.*.created_at"],
  "databases.listBranches": ["branches.*.created_at"],
  "databases.createDatabase": ["created_at"],
  "databases.getDatabase": ["created_at"],
  "databases.deleteDatabase": ["created_at"],
  "databases.restoreDatabase": ["created_at"],
  "databases.createBranch": ["created_at"],
  "databases.restoreBackup": ["created_at"],
};

function installIntegerField(target: unknown, source: unknown, parts: readonly string[], signed: boolean): void {
  const [part, ...rest] = parts;
  if (part === "*") {
    if (Array.isArray(target) && Array.isArray(source)) {
      target.forEach((entry: unknown, index: number) => installIntegerField(entry, source[index], rest, signed));
    }
    return;
  }
  if (part === undefined || !isRecord(target) || !isRecord(source)) return;
  const value = source[part];
  if (rest.length > 0) {
    installIntegerField(target[part], value, rest, signed);
    return;
  }
  if (value === undefined || value === null) return;
  const minimum = signed ? -9223372036854775808n : 0n;
  const maximum = signed ? 9223372036854775807n : 18446744073709551615n;
  if (!(typeof value === "number" && Number.isSafeInteger(value) && (signed || value >= 0))
    && !(typeof value === "bigint" && value >= minimum && value <= maximum)) {
    throw new TypeError(`Response field ${part} must be ${signed ? "a signed" : "an unsigned"} 64-bit integer.`);
  }
  target[part] = value;
}

function parseDocumentValue(raw: string): unknown {
  return JSON.parse(raw, (_key: string, value: unknown, context?: { source?: string }) => {
    if (typeof value !== "number" || Number.isSafeInteger(value)) return value;
    const source = context?.source;
    if (source === undefined) {
      // Older runtimes cannot distinguish an integer that was rounded from a
      // floating-point literal. Never return a silently corrupted data value.
      if (Number.isInteger(value) || !Number.isFinite(value)) {
        throw new TypeError("Lossless data integers require JSON.parse reviver source support.");
      }
      return value;
    }
    return /^-?(?:0|[1-9][0-9]*)$/u.test(source) ? BigInt(source) : value;
  });
}

function errorFromResponse(response: Response, body: unknown, routeName: TerraScaleRouteName): TerraScaleError {
  // Error responses are producer-controlled. Sanitize before extracting any
  // published fields so hostile values cannot enter `message`, `fix`, or
  // validation metadata and rely on a later result-level pass to clean them.
  const safeBody = sanitizeHttpDiagnostic(body);
  const source = unwrapErrorBody(safeBody);
  const category = errorCategory(source);
  const traceId = stringProperty(source, "traceId");
  // RFC 6749 flat OAuth failures carry the code in `error` and the message in
  // `error_description`. On ts-auth routes a bare `error` is already the
  // published OAuth code (RFC 6749 allows omitting the description); on every
  // other surface the pair only maps when both are present, so the legacy
  // flat shape (a bare human message in `error`) keeps its meaning.
  const flatError = stringProperty(source, "error");
  const flatOAuthPair = flatError !== undefined && typeof source.error_description === "string";
  const oauthCode = routeName.startsWith("tsauth.") ? flatError : (flatOAuthPair ? flatError : undefined);
  const code =
    stringProperty(source, "code") ??
    stringProperty(source, "errorCode") ??
    oauthCode ??
    defaultCode(response.status);
  const validation = validationIssues(source);
  const fix = stringProperty(source, "fix");
  const retryAfter = retryAfterMs(response);

  return {
    kind: kindFromStatus(response.status, validation, code),
    code,
    message:
      stringProperty(source, "message") ??
      (flatOAuthPair ? stringProperty(source, "error_description") : undefined) ??
      stringProperty(source, "error") ??
      stringProperty(source, "detail") ??
      stringProperty(source, "title") ??
      (response.statusText || "The TerraScale API request failed."),
    ...(fix === undefined ? {} : { fix }),
    ...(retryAfter === undefined ? {} : { retryAfter }),
    category,
    detail: stringProperty(source, "detail"),
    suggestedAction: stringProperty(source, "suggestedAction"),
    docsUrl: stringProperty(source, "docsUrl"),
    traceId,
    status: response.status,
    target: stringProperty(source, "target"),
    requestId:
      stringProperty(source, "requestId") ??
      requestIdFrom(response.headers) ??
      (category === undefined ? traceId : undefined),
    validation,
    details: safeBody,
  };
}

const diagnosticTokenKey = /(?:^|[._-])(?:token|jwt|signature)(?:$|[._-])/iu;
const diagnosticUrl = /https?:\/\/[^\s<>"']+/giu;

/** Apply HTTP-specific rules before error fields are extracted. */
function sanitizeHttpDiagnostic(value: unknown): unknown {
  return sanitizeHttpDiagnosticValue(sanitizeDiagnostic(value), "", new WeakSet<object>(), 0);
}

function sanitizeHttpDiagnosticValue(
  value: unknown,
  key: string,
  seen: WeakSet<object>,
  depth: number,
): unknown {
  if (diagnosticTokenKey.test(key)) return "[redacted]";
  if (typeof value === "string") {
    return value.replace(diagnosticUrl, (candidate) => {
      try {
        const url = new URL(candidate.replace(/[).,;]+$/u, ""));
        return url.search !== "" || url.hash !== "" ? "[redacted URL]" : candidate;
      } catch {
        return candidate;
      }
    });
  }
  if (value === null || typeof value !== "object") return value;
  if (depth >= 12 || seen.has(value)) return "[redacted]";
  seen.add(value);
  if (Array.isArray(value)) return value.map((entry) => sanitizeHttpDiagnosticValue(entry, "", seen, depth + 1));
  const output: Record<string, unknown> = {};
  for (const [entryKey, entryValue] of Object.entries(value)) {
    output[entryKey] = sanitizeHttpDiagnosticValue(entryValue, entryKey, seen, depth + 1);
  }
  return output;
}

function errorCategory(source: Record<string, unknown>): TerraScaleErrorCategory | undefined {
  const value = stringProperty(source, "category");
  switch (value) {
    case "Validation":
    case "Authentication":
    case "Authorization":
    case "NotFound":
    case "Conflict":
    case "RateLimit":
    case "Schema":
    case "Query":
    case "Placement":
    case "AutoFix":
    case "AutoIndex":
    case "Sandbox":
    case "System":
      return value;
    default:
      return undefined;
  }
}

function unwrapErrorBody(body: unknown): Record<string, unknown> {
  if (!isRecord(body)) {
    return {};
  }

  const error = body.error;
  return isRecord(error) ? error : body;
}

function stringProperty(source: Record<string, unknown>, key: string): string | undefined {
  const value = source[key];
  return typeof value === "string" && value.length > 0 ? value : undefined;
}

function validationIssues(
  source: Record<string, unknown>,
): readonly TerraScaleValidationIssue[] | undefined {
  const raw = source.fieldErrors ?? source.validationErrors ?? source.errors;

  if (Array.isArray(raw)) {
    return raw
      .map((item) => {
        if (typeof item === "string") {
          return { message: item };
        }

        if (!isRecord(item)) {
          return undefined;
        }

        const message = stringProperty(item, "message") ?? stringProperty(item, "errorMessage");

        if (message === undefined) {
          return undefined;
        }

        return {
          path: stringProperty(item, "path") ?? stringProperty(item, "field"),
          message,
          code: stringProperty(item, "code") ?? stringProperty(item, "constraint"),
        };
      })
      .filter((item): item is TerraScaleValidationIssue => item !== undefined);
  }

  if (isRecord(raw)) {
    return Object.entries(raw).flatMap(([path, value]) => {
      const messages = Array.isArray(value) ? value : [value];
      return messages
        .filter((message): message is string => typeof message === "string")
        .map((message) => ({ path, message }));
    });
  }

  return undefined;
}

function kindFromStatus(
  status: number,
  validation: readonly TerraScaleValidationIssue[] | undefined,
  code: string,
) {
  if (validation !== undefined && validation.length > 0) {
    return "validation";
  }

  if (status === 401) {
    return "authentication";
  }

  if (status === 403) {
    return "authorization";
  }

  if (status === 404) {
    return "not-found";
  }

  if (status === 409) {
    return "conflict";
  }

  if (status === 429) {
    return "rate-limit";
  }

  if (status === 503 && code === "project_region_unavailable") {
    return "unavailable";
  }

  if (status >= 500) {
    return "server";
  }

  return "domain";
}

function defaultCode(status: number): string {
  if (status === 400) {
    return "bad_request";
  }

  if (status === 401) {
    return "unauthenticated";
  }

  if (status === 403) {
    return "forbidden";
  }

  if (status === 404) {
    return "not_found";
  }

  if (status === 409) {
    return "conflict";
  }

  if (status === 429) {
    return "rate_limited";
  }

  return status >= 500 ? "server_error" : "domain_error";
}

function requestIdFrom(headers: Headers): string | undefined {
  return headers.get("x-request-id") ?? headers.get("traceparent") ?? undefined;
}

function isRecord(value: unknown): value is Record<string, unknown> {
  return typeof value === "object" && value !== null && !Array.isArray(value);
}
