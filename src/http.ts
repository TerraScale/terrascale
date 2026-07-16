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
  | `dashboard.${string}`
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
  /** Controls whether TerraScale identity and configured default headers may be attached. */
  readonly headerPolicy?: "authenticated" | "anonymous" | undefined;
  /** Overrides automatic generation. Set to false to explicitly opt out. */
  readonly idempotencyKey?: string | false | undefined;
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

  constructor(config: TerraScaleRuntimeConfig | ResolvedTerraScaleRuntimeConfig) {
    this.config =
      "fetch" in config && typeof config.fetch === "function" && "authBaseUrl" in config
        ? ({
            ...config,
            authBaseUrl: config.authBaseUrl ?? config.apiBaseUrl,
            dashboardDatabaseIds: config.dashboardDatabaseIds ?? {},
          } as ResolvedTerraScaleRuntimeConfig)
        : resolveRuntimeConfig(config as TerraScaleRuntimeConfig);
  }

  async request<TResponse, TBody = unknown>(
    options: TerraScaleRequestOptions<TBody>,
  ): Promise<TerraScaleResult<TResponse>> {
    const base = options.base ?? "api";
    const startedAt = performance.now();

    try {
      const url = buildUrl(this.baseUrl(base), options.path, options.query);
      const headers = await this.buildHeaders(options);
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
      const response = await this.config.fetch(request);
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

        return ok(options.routeName, response.status, response.headers, body.value as TResponse);
      }

      return err(
        options.routeName,
        errorFromResponse(response, body.ok ? body.value : body.raw),
        response.status,
        response.headers,
        body.ok ? body.value : body.raw,
      );
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

  dashboardDatabaseId(name: string): string | undefined {
    return this.config.dashboardDatabaseIds[name];
  }

  private baseUrl(base: TerraScaleBase): string {
    return base === "auth" ? this.config.authBaseUrl : this.config.apiBaseUrl;
  }

  private async buildHeaders<TBody>(options: TerraScaleRequestOptions<TBody>): Promise<Headers> {
    const anonymous = options.headerPolicy === "anonymous";
    const headers = new Headers(anonymous ? undefined : this.config.defaultHeaders);
    const requestHeaders = new Headers(options.headers);

    headers.set("accept", headers.get("accept") ?? "application/json");
    if (!anonymous) {
      headers.set("x-terrascale-dashboard-client-id", this.config.dashboardClientId);

      if (this.config.tenantId !== undefined) {
        headers.set("x-terrascale-tenant-id", this.config.tenantId);
      }

      if (this.config.projectId !== undefined) {
        headers.set("x-terrascale-project-id", this.config.projectId);
      }

      const token = await resolveAccessToken(this.config.accessToken);

      if (token !== undefined && token.length > 0) {
        headers.set("authorization", token.startsWith("Bearer ") ? token : `Bearer ${token}`);
      }
    }

    for (const [key, value] of requestHeaders.entries()) {
      headers.set(key, value);
    }

    if (anonymous) {
      headers.delete("authorization");
      headers.delete("x-terrascale-dashboard-client-id");
      headers.delete("x-terrascale-project-id");
      headers.delete("x-terrascale-tenant-id");
    }

    if (isMutation(options.method) && options.idempotencyKey !== false) {
      const key = options.idempotencyKey ?? this.config.idempotencyKeyFactory?.();
      if (key !== undefined) {
        const normalized = key.trim();
        if (normalized.length === 0 || normalized.length > 128) {
          throw new TypeError("Idempotency keys must be nonblank and contain at most 128 characters.");
        }
        headers.set("idempotency-key", normalized);
      }
    }

    return headers;
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
      // Observability must never change request behavior.
    }
  }
}

function isMutation(method: TerraScaleHttpMethod): boolean {
  return method === "POST" || method === "PATCH";
}

type ParsedBody =
  | { readonly ok: true; readonly value: unknown; readonly raw: string }
  | { readonly ok: false; readonly errorMessage: string; readonly raw: string };

function buildUrl(baseUrl: string, path: string, query: QueryRecord | undefined): URL {
  const url = new URL(path.replace(/^\/+/, ""), `${baseUrl}/`);

  for (const [key, value] of Object.entries(query ?? {}) as [string, QueryValue][]) {
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
    kind: kindFromStatus(response.status, validation),
    code,
    message:
      stringProperty(source, "message") ??
      stringProperty(source, "detail") ??
      stringProperty(source, "title") ??
      response.statusText ??
      "The TerraScale API request failed.",
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
  const raw = source.validationErrors ?? source.errors;

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
          code: stringProperty(item, "code"),
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
