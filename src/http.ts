import {
  type ResolvedTerraScaleRuntimeConfig,
  resolveAccessToken,
  resolveRuntimeConfig,
  type TerraScaleRuntimeConfig,
} from "./config.js";
import {
  err,
  ok,
  type TerraScaleError,
  type TerraScaleResult,
  type TerraScaleValidationIssue,
} from "./result.js";
import {
  canonicalRegionCode,
  regionalApiBaseUrl,
  type TerraScaleRegionCode,
} from "./regions.js";
export type TerraScaleRouteName =
  | `access.${string}`
  | `admin.${string}`
  | `audit.${string}`
  | `backups.${string}`
  | `databaseBranches.${string}`
  | `databases.${string}`
  | `documents.${string}`
  | `reactive.${string}`
  | `sql.${string}`
  | `notifications.${string}`
  | `onboarding.${string}`
  | `organizations.${string}`
  | `regions.${string}`
  | `savedQueries.${string}`
  | `settings.${string}`
  | `usage.${string}`;

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
  readonly query?: QueryRecord | undefined;
  readonly body?: TBody | undefined;
  readonly headers?: HeadersInit | undefined;
  readonly idempotencyKey?: string | false | undefined;
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

  constructor(config: TerraScaleRuntimeConfig) {
    this.config = resolveRuntimeConfig(config);
    validateCredentialEndpoint(this.config.apiBaseUrl, config.fetch !== undefined);
    const fetchHandler = config.fetch ?? globalThis.fetch?.bind(globalThis);
    if (fetchHandler === undefined) {
      throw new TypeError("TerraScaleClient requires a fetch implementation.");
    }
    this.#fetch = fetchHandler;
    this.#accessToken = config.accessToken;
    this.#idempotencyKeyFactory = config.idempotencyKeyFactory;
    this.#requestMetricRecorder = config.requestMetricRecorder;
    this.#pinnedRegion = undefined;
    const configuredSession = (config as TerraScaleRuntimeConfig & { readonly sessionToken?: unknown }).sessionToken;
    this.#sessionToken = validateSessionToken(configuredSession) ? configuredSession.trim() : undefined;
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

    try {
      idempotencyKey = this.resolveIdempotencyKey(options);
      let attempt = 0;
      let repaired421 = false;
      while (true) {
        let responseReceived = false;
        let fetchStarted = false;
        try {
          const requestBase = this.baseUrl(options.regionHint);
          const globalFirstContact = this.isGlobalFirstContact(requestBase, options.regionHint);
          const url = buildUrl(requestBase, options.path, options.query);
          const headers = await this.buildHeaders(options, idempotencyKey);
          const requestInit: RequestInit = {
            method: options.method,
            headers,
            ...(options.signal === undefined ? {} : { signal: options.signal }),
          };
          if (options.body !== undefined) {
            headers.set("content-type", headers.get("content-type") ?? "application/json");
            requestInit.body = JSON.stringify(options.body);
          }

          const request = new Request(url, requestInit);
          fetchStarted = true;
          const response = await this.#fetch(request);
          responseReceived = true;
          const body = await readResponseBody(response);
          this.observeSession(response.headers);
          this.recordRequestMetric(base, options.routeName, response.status, performance.now() - startedAt);

          // Conditional reads use 304 as a successful cache hit. There is no
          // representation body, so callers receive `undefined` and can use
          // the status/headers to distinguish it from a normal 200 response.
          if (response.status === 304) {
            return ok(options.routeName, response.status, response.headers, undefined as TResponse);
          }

          if (response.ok) {
            if (globalFirstContact) this.observeServingRegion(response.headers);
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

          if (response.status === 421 && !repaired421) {
            const repaired = placementRegion(response.headers);
            if (repaired !== undefined && repaired !== this.#pinnedRegion) {
              this.#pinnedRegion = repaired;
              repaired421 = true;
              // Placement is authoritative for this one replay, including
              // when the original request supplied a per-call hint.
              attempt++;
              continue;
            }
          }

          const error = errorFromResponse(response, body.ok ? body.value : body.raw);
          if (this.canRetry(options.method, idempotencyKey, attempt, response.status)) {
            await delayForRetry(retryDelay(response, this.config.retryDelayMs, attempt), options.signal);
            attempt++;
            continue;
          }

          return err(
            options.routeName,
            error,
            response.status,
            response.headers,
            body.ok ? body.value : body.raw,
          );
        } catch (error) {
          if (fetchStarted && !responseReceived && !options.signal?.aborted && this.canRetry(options.method, idempotencyKey, attempt)) {
            await delayForRetry(retryDelay(undefined, this.config.retryDelayMs, attempt), options.signal);
            attempt++;
            continue;
          }

          throw error;
        }
      }
    } catch (error) {
      this.recordRequestMetric(base, options.routeName, undefined, performance.now() - startedAt);
      return err(options.routeName, {
        kind: "transport",
        code: "transport_error",
        message:
          error instanceof Error
            ? error.message
            : "The request failed before a response was received.",
        details: error,
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

  private isGlobalFirstContact(baseUrl: string, regionHint?: TerraScaleRegionCode): boolean {
    return this.config.regionAffinity === "failover" &&
      this.#pinnedRegion === undefined &&
      regionHint === undefined &&
      baseUrl === this.config.apiBaseUrl;
  }

  private async buildHeaders<TBody>(
    options: TerraScaleRequestOptions<TBody>,
    idempotencyKey: string | undefined,
  ): Promise<Headers> {
    validateInternalHeaders(options.headers);
    const headers = new Headers();

    headers.set("accept", headers.get("accept") ?? "application/json");
    if (this.config.projectId !== undefined) {
      headers.set("x-terrascale-project-id", this.config.projectId);
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

    const token = await resolveAccessToken(this.#accessToken);

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
    if (!isMutation(options.method) || options.idempotencyKey === false) {
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
  ): boolean {
    if (attempt >= this.config.maxRetryAttempts) {
      return false;
    }

    // GET is safe to repeat by contract. Mutations require the caller's
    // stable idempotency identity; other POSTs (SQL/reactive) are not
    // assumed idempotent merely because they use HTTP POST.
    const retryableOperation = method === "GET" || (isMutation(method) && idempotencyKey !== undefined);
    if (!retryableOperation) {
      return false;
    }

    return status === undefined || retryableStatuses.has(status);
  }

  private observeServingRegion(headers: Headers): void {
    const region = servingRegion(headers);
    if (region !== undefined && this.#pinnedRegion === undefined) {
      this.#pinnedRegion = region;
    }
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
  "if-match",
  "if-none-match",
  "x-terrascale-durability",
  "x-terrascale-session",
  "x-terrascale-allow-stale",
  "x-terrascale-delete-confirmation",
  "x-terrascale-preferred-region",
]);

function validateInternalHeaders(headers: HeadersInit | undefined): void {
  if (headers === undefined) {
    return;
  }

  for (const [name] of new Headers(headers).entries()) {
    if (!internalHeaderNames.has(name)) {
      throw new TypeError(
        `Header "${name}" is not configurable; use the named TerraScale SDK operation option instead.`,
      );
    }
  }
}

function isMutation(method: TerraScaleHttpMethod): boolean {
  return method === "DELETE" || method === "PATCH" || method === "POST" || method === "PUT";
}

const retryableStatuses = new Set([408, 429, 500, 502, 503, 504]);

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

function servingRegion(headers: Headers): TerraScaleRegionCode | undefined {
  return canonicalRegionCode(headers.get("x-terrascale-serving-region") ?? headers.get("x-terrascale-region") ?? undefined);
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

function retryDelay(response: Response | undefined, baseDelayMs: number, attempt: number): number {
  const retryAfter = response?.headers.get("retry-after");
  if (retryAfter !== null && retryAfter !== undefined) {
    const seconds = Number(retryAfter);
    if (Number.isFinite(seconds) && seconds >= 0) {
      return Math.min(seconds * 1_000, 30_000);
    }

    const date = Date.parse(retryAfter);
    if (Number.isFinite(date)) {
      return Math.min(Math.max(0, date - Date.now()), 30_000);
    }
  }

  return Math.min(baseDelayMs * (2 ** attempt), 30_000);
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

function buildUrl(baseUrl: string, path: string, query: QueryRecord | undefined): URL {
  const normalizedPath = path.replace(/^\/+/, "");
  if (!/^api\/v1(?:\/|$)/u.test(normalizedPath)) {
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

async function readResponseBody(response: Response): Promise<ParsedBody> {
  const raw = await response.text();

  if (raw.length === 0) {
    return { ok: true, value: undefined, raw };
  }

  const contentType = response.headers.get("content-type") ?? "";

  if (!contentType.toLowerCase().includes("json")) {
    return response.ok ? { ok: true, value: raw, raw } : { ok: true, value: { message: raw }, raw };
  }

  try {
    return { ok: true, value: JSON.parse(raw) as unknown, raw };
  } catch (error) {
    return {
      ok: false,
      errorMessage:
        error instanceof Error ? error.message : "The response body was not valid JSON.",
      raw,
    };
  }
}

function errorFromResponse(response: Response, body: unknown): TerraScaleError {
  const source = unwrapErrorBody(body);
  const code =
    stringProperty(source, "code") ??
    stringProperty(source, "errorCode") ??
    defaultCode(response.status);
  const validation = validationIssues(source);
  const fix = stringProperty(source, "fix");

  return {
    kind: kindFromStatus(response.status, validation, code),
    code,
    message:
      stringProperty(source, "message") ??
      stringProperty(source, "error") ??
      stringProperty(source, "detail") ??
      stringProperty(source, "title") ??
      (response.statusText || "The TerraScale API request failed."),
    ...(fix === undefined ? {} : { fix }),
    status: response.status,
    target: stringProperty(source, "target"),
    requestId:
      stringProperty(source, "requestId") ??
      stringProperty(source, "traceId") ??
      requestIdFrom(response.headers),
    validation,
    details: body,
  };
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
