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
export type TerraScaleRouteName =
  | `access.${string}`
  | `admin.${string}`
  | `apiKeys.${string}`
  | `audit.${string}`
  | `auth.${string}`
  | `backups.${string}`
  | `billing.${string}`
  | `databaseBranches.${string}`
  | `databases.${string}`
  | `documents.${string}`
  | `m2m.${string}`
  | `notifications.${string}`
  | `onboarding.${string}`
  | `organizations.${string}`
  | `pricing.${string}`
  | `query.${string}`
  | `queues.${string}`
  | `regions.${string}`
  | `repositories.${string}`
  | `savedQueries.${string}`
  | `schema.${string}`
  | `security.${string}`
  | `settings.${string}`
  | `usage.${string}`;

export type TerraScaleBase = "api" | "auth";
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
  readonly signal?: AbortSignal | undefined;
  readonly headerPolicy?: "authenticated" | "anonymous" | undefined;
  readonly idempotencyKey?: string | false | undefined;
  readonly consistency?: "read" | "write" | undefined;
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
  readonly #sessionConsistencyTokens = new Map<string, string>();

  constructor(config: TerraScaleRuntimeConfig) {
    this.config = resolveRuntimeConfig(config);
  }

  async request<TResponse, TBody = unknown>(
    options: TerraScaleRequestOptions<TBody>,
  ): Promise<TerraScaleResult<TResponse>> {
    const base = options.base ?? "api";
    const startedAt = performance.now();
    let idempotencyKey: string | undefined;

    try {
      idempotencyKey = this.resolveIdempotencyKey(options);
      let attempt = 0;
      while (true) {
        let responseReceived = false;
        let fetchStarted = false;
        try {
          const url = buildUrl(this.baseUrl(base), options.path, options.query);
          const headers = await this.buildHeaders(options, base, idempotencyKey);
          const requestInit: RequestInit = {
            method: options.method,
            headers,
          };
          if (options.signal !== undefined) {
            requestInit.signal = options.signal;
          }

          if (options.body !== undefined) {
            headers.set("content-type", headers.get("content-type") ?? "application/json");
            requestInit.body = JSON.stringify(options.body);
          }

          const request = new Request(url, requestInit);
          fetchStarted = true;
          const response = await this.config.fetch(request);
          responseReceived = true;
          const body = await readResponseBody(response);
          this.recordRequestMetric(base, options.routeName, response.status, performance.now() - startedAt);

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

            if (options.consistency === "write" && this.config.sessionConsistency === "automatic") {
              this.captureSessionConsistencyTokens(options.path, body.value);
            }

            return ok(options.routeName, response.status, response.headers, body.value as TResponse);
          }

          const error = errorFromResponse(response, body.ok ? body.value : body.raw);
          if (this.canRetry(options, idempotencyKey, attempt, response.status)) {
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
          if (fetchStarted && !responseReceived && this.canRetry(options, idempotencyKey, attempt)) {
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

  private baseUrl(base: TerraScaleBase): string {
    return base === "auth" ? this.config.authBaseUrl : this.config.apiBaseUrl;
  }

  private async buildHeaders<TBody>(
    options: TerraScaleRequestOptions<TBody>,
    base: TerraScaleBase,
    idempotencyKey: string | undefined,
  ): Promise<Headers> {
    const anonymous = options.headerPolicy === "anonymous";
    const headers = new Headers(anonymous ? undefined : this.config.defaultHeaders);

    headers.set("accept", headers.get("accept") ?? "application/json");
    if (!anonymous) {
      if (this.config.projectId !== undefined) {
        headers.set("x-terrascale-project-id", this.config.projectId);
      }

      if (
        base === "api" &&
        this.config.regionAffinity === "failover" &&
        this.config.preferredRegion !== undefined
      ) {
        headers.set("x-terrascale-preferred-region", this.config.preferredRegion);
      }

      if (
        options.consistency === "read" &&
        this.config.sessionConsistency === "automatic" &&
        this.#sessionConsistencyTokens.has(options.path)
      ) {
        headers.set(
          "x-terrascale-session-consistency-token",
          this.#sessionConsistencyTokens.get(options.path)!,
        );
      }

      const token = await resolveAccessToken(this.config.accessToken);

      if (token !== undefined && token.length > 0) {
        headers.set("authorization", token.startsWith("Bearer ") ? token : `Bearer ${token}`);
      }
    }

    if (options.headers !== undefined) {
      for (const [key, value] of new Headers(options.headers).entries()) {
        headers.set(key, value);
      }
    }

    if (anonymous) {
      headers.delete("authorization");
      headers.delete("x-terrascale-project-id");
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

    const key = options.idempotencyKey ?? this.config.idempotencyKeyFactory?.();
    if (key === undefined) {
      return undefined;
    }

    const normalized = key.trim();
    if (normalized.length === 0 || normalized.length > 128) {
      throw new TypeError("Idempotency keys must be nonblank and contain at most 128 characters.");
    }
    return normalized;
  }

  private canRetry<TBody>(
    options: TerraScaleRequestOptions<TBody>,
    idempotencyKey: string | undefined,
    attempt: number,
    status?: number,
  ): boolean {
    if (attempt >= this.config.maxRetryAttempts || options.signal?.aborted) {
      return false;
    }

    const retryableOperation = options.consistency === "read" || idempotencyKey !== undefined;
    if (!retryableOperation) {
      return false;
    }

    return status === undefined || retryableStatuses.has(status);
  }

  private captureSessionConsistencyTokens(requestPath: string, value: unknown): void {
    if (!isRecord(value)) {
      return;
    }

    const values = Array.isArray(value.results)
      ? value.results.flatMap(result => {
        if (!isRecord(result)) {
          return [];
        }

        const document = isRecord(result.document) ? result.document : undefined;
        const deleted = isRecord(result.deleted) ? result.deleted : undefined;
        return [document, deleted].filter((item): item is Record<string, unknown> => item !== undefined);
      })
      : [value];

    for (const result of values) {
      const token = result.sessionConsistencyToken;
      const scope = sessionConsistencyScope(requestPath, result);
      if (
        scope !== undefined &&
        typeof token === "string" &&
        token.length > 0 &&
        token.length <= 4096
      ) {
        this.#sessionConsistencyTokens.set(scope, token);
      }
    }
  }

  private recordRequestMetric(
    base: TerraScaleBase,
    routeName: TerraScaleRouteName,
    status: number | undefined,
    durationMs: number,
  ): void {
    try {
      this.config.requestMetricRecorder?.({ base, routeName, status, durationMs });
    } catch {
      return;
    }
  }
}

function sessionConsistencyScope(requestPath: string, value: Record<string, unknown>): string | undefined {
  const batchPath = requestPath.endsWith("/documents/batch")
    ? requestPath.slice(0, -"/batch".length)
    : requestPath;
  if (batchPath !== requestPath) {
    const documentId = value.id;
    return typeof documentId === "string" && documentId.length > 0
      ? `${batchPath}/${encodeURIComponent(documentId)}`
      : undefined;
  }

  if (/\/documents\/[^/]+$/u.test(requestPath)) {
    return requestPath;
  }

  if (!batchPath.endsWith("/documents")) {
    return undefined;
  }

  const documentId = value.id;
  return typeof documentId === "string" && documentId.length > 0
    ? `${batchPath}/${encodeURIComponent(documentId)}`
    : undefined;
}

function isMutation(method: TerraScaleHttpMethod): boolean {
  return method === "DELETE" || method === "PATCH" || method === "POST" || method === "PUT";
}

const retryableStatuses = new Set([408, 429, 500, 502, 503, 504]);

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

async function delayForRetry(delayMs: number, signal: AbortSignal | undefined): Promise<void> {
  signal?.throwIfAborted();
  if (delayMs === 0) {
    return;
  }

  await new Promise<void>((resolve, reject) => {
    let timer: ReturnType<typeof setTimeout> | undefined;
    let settled = false;
    const cleanup = () => {
      if (timer !== undefined) {
        clearTimeout(timer);
      }
      signal?.removeEventListener("abort", abort);
    };
    const abort = () => {
      if (settled) {
        return;
      }
      settled = true;
      cleanup();
      reject(signal?.reason ?? new DOMException("The request was aborted.", "AbortError"));
    };
    signal?.addEventListener("abort", abort, { once: true });
    if (signal?.aborted) {
      abort();
      return;
    }
    timer = setTimeout(() => {
      if (settled) {
        return;
      }
      settled = true;
      cleanup();
      resolve();
    }, delayMs);
  });
}

type ParsedBody =
  | { readonly ok: true; readonly value: unknown; readonly raw: string }
  | { readonly ok: false; readonly errorMessage: string; readonly raw: string };

function buildUrl(baseUrl: string, path: string, query: QueryRecord | undefined): URL {
  const base = new URL(`${baseUrl}/`);
  const url = new URL(path.replace(/^\/+/, ""), base);
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

  return {
    kind: kindFromStatus(response.status, validation, code),
    code,
    message:
      stringProperty(source, "message") ??
      stringProperty(source, "detail") ??
      stringProperty(source, "title") ??
      (response.statusText || "The TerraScale API request failed."),
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
