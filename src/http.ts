import {
  resolveAccessToken,
  resolveTsAuthRuntimeConfig,
  type ResolvedTsAuthRuntimeConfig,
  type TsAuthRuntimeConfig,
  type FetchLike,
} from "./config.js";
import {
  err,
  ok,
  sanitizeDiagnostic,
  type TerraScaleError,
  type TerraScaleErrorCategory,
  type TerraScaleResult,
  type TerraScaleValidationIssue,
} from "./result.js";
import { TsAuthEndpointDiscovery } from "./ts-auth-discovery.js";
import { parseDatabaseJson } from "./database-codec.js";

type TsAuthRouteName = `tsauth.${string}`;
type TsAuthHttpMethod = "DELETE" | "GET" | "PATCH" | "POST" | "PUT";
type TsAuthRequest<TBody> = {
  readonly method: TsAuthHttpMethod;
  readonly path: string;
  readonly routeName: TsAuthRouteName;
  readonly query?: object | undefined;
  readonly body?: TBody | undefined;
  readonly formBody?: URLSearchParams | string | undefined;
  readonly userinfoAccessToken?: string | undefined;
  readonly resourceIntrospectionBasic?: string | undefined;
  readonly signal?: AbortSignal | undefined;
};
const retryableStatuses = new Set([429, 502, 503, 504]);
const maximumResponseBytes = 4 * 1024 * 1024;

/** Private transport for the named hosted identity and OIDC operations. */
export class TsAuthHttp {
  readonly config: ResolvedTsAuthRuntimeConfig;
  readonly #fetch: FetchLike;
  readonly #accessToken: TsAuthRuntimeConfig["accessToken"];
  readonly #discovery: TsAuthEndpointDiscovery | undefined;

  constructor(
    config: TsAuthRuntimeConfig,
    options?: { readonly authReverseProxy?: boolean },
  ) {
    this.config = Object.freeze(resolveTsAuthRuntimeConfig(config));
    const fetchHandler = config.fetch ?? globalThis.fetch?.bind(globalThis);
    if (fetchHandler === undefined)
      throw new TypeError("TsAuthClient requires a fetch implementation.");
    this.#fetch = fetchHandler;
    this.#accessToken = config.accessToken;
    this.#discovery =
      options?.authReverseProxy !== true &&
      this.config.apiBaseUrl === "https://auth.terrascale.tech"
        ? new TsAuthEndpointDiscovery(fetchHandler)
        : undefined;
  }

  async request<TResponse, TBody = unknown>(
    options: TsAuthRequest<TBody>,
  ): Promise<TerraScaleResult<TResponse>> {
    const deadline =
      this.config.requestTimeoutMs > 0
        ? AbortSignal.timeout(this.config.requestTimeoutMs)
        : undefined;
    const signals = [options.signal, deadline].filter(
      (signal): signal is AbortSignal => signal !== undefined,
    );
    const signal =
      signals.length === 0
        ? undefined
        : signals.length === 1
          ? signals[0]
          : AbortSignal.any(signals);
    try {
      signal?.throwIfAborted();
      if (options.body !== undefined && options.formBody !== undefined)
        throw new TypeError("Requests accept either JSON or form data.");
      const body =
        options.body !== undefined
          ? JSON.stringify(options.body)
          : options.formBody?.toString();
      const endpoints = await withCancellation(
        this.#discovery?.endpoints(signal) ?? Promise.resolve(undefined),
        signal,
      );
      let endpointIndex = 0;
      let attempt = 0;
      let refreshed = false;
      let refreshedToken: string | undefined;
      while (true) {
        signal?.throwIfAborted();
        let responseReceived = false;
        let fetchStarted = false;
        try {
          const headers = new Headers({ accept: "application/json" });
          const oidc = options.routeName.startsWith("tsauth.oidc.");
          const token = oidc
            ? undefined
            : (refreshedToken ??
              (await withCancellation(
                resolveAccessToken(this.#accessToken),
                signal,
              )));
          if (token !== undefined && token.length > 0)
            headers.set(
              "authorization",
              token.startsWith("Bearer ") ? token : `Bearer ${token}`,
            );
          if (options.userinfoAccessToken !== undefined) {
            if (
              options.routeName !== "tsauth.oidc.userinfo" ||
              options.method !== "GET"
            )
              throw new TypeError(
                "Bearer access tokens belong to named OIDC userinfo only.",
              );
            headers.set(
              "authorization",
              `Bearer ${options.userinfoAccessToken}`,
            );
          }
          if (options.resourceIntrospectionBasic !== undefined) {
            if (
              options.routeName !== "tsauth.oidc.resourceIntrospect" ||
              options.method !== "POST" ||
              !/^[^/]+\/oauth\/introspect$/u.test(options.path) ||
              options.formBody === undefined ||
              options.userinfoAccessToken !== undefined ||
              !/^Basic [A-Za-z0-9+/]+={0,2}$/u.test(
                options.resourceIntrospectionBasic,
              )
            )
              throw new TypeError(
                "Resource Basic credentials belong to named resource introspection only.",
              );
            headers.set("authorization", options.resourceIntrospectionBasic);
          }
          if (body !== undefined)
            headers.set(
              "content-type",
              options.formBody === undefined
                ? "application/json"
                : "application/x-www-form-urlencoded",
            );
          const request = new Request(
            buildUrl(
              endpoints?.[endpointIndex] ?? this.config.apiBaseUrl,
              options.path,
              options.query,
            ),
            {
              method: options.method,
              headers,
              redirect: "manual",
              ...(signal === undefined ? {} : { signal }),
              ...(body === undefined ? {} : { body }),
            },
          );
          signal?.throwIfAborted();
          fetchStarted = true;
          const response = await withCancellation(
            this.#fetch(request),
            signal,
            (response) => {
              void response.body?.cancel().catch(() => undefined);
            },
          );
          if (signal?.aborted) {
            void response.body?.cancel().catch(() => undefined);
            signal.throwIfAborted();
          }
          responseReceived = true;
          if (
            options.method === "GET" &&
            endpoints !== undefined &&
            endpointIndex < endpoints.length - 1 &&
            [502, 503, 504].includes(response.status)
          ) {
            void response.body?.cancel().catch(() => undefined);
            endpointIndex += 1;
            continue;
          }
          if (
            response.type === "opaqueredirect" ||
            (response.status >= 300 && response.status < 400)
          ) {
            void response.body?.cancel().catch(() => undefined);
            throw new TypeError(
              "ts-auth returned an unsupported HTTP redirect.",
            );
          }
          const parsed = await readResponseBody(
            response,
            options.routeName,
            signal,
          );
          signal?.throwIfAborted();
          if (response.ok) {
            if (!parsed.ok)
              return err(
                options.routeName,
                {
                  kind: "invalid-response",
                  code: "invalid_response",
                  message: parsed.errorMessage,
                  status: response.status,
                  requestId: requestIdFrom(response.headers),
                },
                response.status,
                response.headers,
              );
            return ok(
              options.routeName,
              response.status,
              response.headers,
              parsed.value as TResponse,
            );
          }
          if (
            response.status === 401 &&
            !oidc &&
            !refreshed &&
            typeof this.#accessToken === "function"
          ) {
            const next = await withCancellation(
              resolveAccessToken(this.#accessToken),
              signal,
            );
            if (next !== undefined && next.length > 0 && next !== token) {
              refreshed = true;
              refreshedToken = next;
              continue;
            }
          }
          if (
            options.method === "GET" &&
            attempt < this.config.maxRetryAttempts &&
            retryableStatuses.has(response.status)
          ) {
            const delay = retryDelay(
              response,
              this.config.retryDelayMs,
              attempt,
            );
            if (delay <= 30_000) {
              await delayForRetry(delay, signal);
              attempt += 1;
              continue;
            }
          }
          const errorBody = sanitizeHttpDiagnostic(
            parsed.ok ? parsed.value : undefined,
          );
          return err(
            options.routeName,
            errorFromResponse(response, errorBody, options.routeName),
            response.status,
            response.headers,
            errorBody,
          );
        } catch (error) {
          if (
            fetchStarted &&
            !responseReceived &&
            !signal?.aborted &&
            options.method === "GET"
          ) {
            if (
              endpoints !== undefined &&
              endpointIndex < endpoints.length - 1
            ) {
              endpointIndex += 1;
              continue;
            }
            if (attempt < this.config.maxRetryAttempts) {
              await delayForRetry(
                retryDelay(undefined, this.config.retryDelayMs, attempt),
                signal,
              );
              attempt += 1;
              continue;
            }
          }
          throw error;
        }
      }
    } catch (error) {
      return err(options.routeName, {
        kind: "transport",
        code: "transport_error",
        message:
          error instanceof Error
            ? String(sanitizeHttpDiagnostic(error.message))
            : "The request failed before a response was received.",
        details: sanitizeHttpDiagnostic(error),
      });
    }
  }
}

function buildUrl(origin: string, path: string, query?: object): URL {
  const normalized = path.replace(/^\/+/, "");
  if (
    !/^v1\//u.test(normalized) &&
    !/^[^/]+\/(?:\.well-known|oauth)\//u.test(normalized)
  ) {
    throw new TypeError(
      "ts-auth requests require named management or instance OIDC routes.",
    );
  }
  const url = new URL(normalized, `${origin}/`);
  if (url.origin !== new URL(origin).origin)
    throw new TypeError("ts-auth paths cannot escape their origin.");
  for (const [name, value] of Object.entries(query ?? {})) {
    if (value !== null && value !== undefined)
      url.searchParams.set(
        name,
        value instanceof Date ? value.toISOString() : String(value),
      );
  }
  return url;
}

type ParsedBody =
  | { readonly ok: true; readonly value: unknown }
  | { readonly ok: false; readonly errorMessage: string };

function strictOauthRoute(routeName: TsAuthRouteName): boolean {
  return (
    routeName.startsWith("tsauth.oauthClients.") ||
    routeName.startsWith("tsauth.oauthResources.") ||
    [
      "tsauth.oidc.token",
      "tsauth.oidc.introspect",
      "tsauth.oidc.resourceIntrospect",
      "tsauth.oidc.deviceAuthorization",
    ].includes(routeName)
  );
}

async function readResponseBody(
  response: Response,
  routeName: TsAuthRouteName,
  signal?: AbortSignal,
): Promise<ParsedBody> {
  const reader = response.body?.getReader();
  const chunks: Uint8Array[] = [];
  let length = 0;
  try {
    if (reader !== undefined) {
      while (true) {
        const item = await withCancellation(reader.read(), signal);
        if (item.done) break;
        length += item.value.byteLength;
        if (length > maximumResponseBytes)
          throw new TypeError("ts-auth response exceeds the body limit.");
        chunks.push(item.value);
      }
    }
  } catch (error) {
    void reader?.cancel().catch(() => undefined);
    throw error;
  } finally {
    reader?.releaseLock();
  }
  const bytes = new Uint8Array(length);
  let offset = 0;
  for (const chunk of chunks) {
    bytes.set(chunk, offset);
    offset += chunk.byteLength;
  }
  const raw = new TextDecoder("utf-8", { fatal: true }).decode(bytes);
  const jsonRequired =
    response.ok &&
    response.status !== 204 &&
    routeName !== "tsauth.oidc.revoke";
  if (raw.length === 0)
    return jsonRequired
      ? {
          ok: false,
          errorMessage: "ts-auth returned an empty response body.",
        }
      : { ok: true, value: undefined };
  const contentType = response.headers.get("content-type") ?? "";
  if (!contentType.toLowerCase().includes("json"))
    return jsonRequired
      ? {
          ok: false,
          errorMessage: "ts-auth returned a non-JSON response body.",
        }
      : { ok: true, value: undefined };
  try {
    const strictOauth = strictOauthRoute(routeName);
    if (strictOauth) {
      // Reuse the bounded duplicate-member lexer. Native domain decoding is not
      // involved; this is the package's private strict JSON syntax primitive.
      parseDatabaseJson(bytes);
    }
    const value: unknown = strictOauth
      ? JSON.parse(
          raw,
          (
            key: string,
            value: unknown,
            context?: { readonly source?: string },
          ) => {
            if (
              typeof value === "number" &&
              [
                "auth_epoch",
                "created_at",
                "updated_at",
                "exp",
                "iat",
                "expires_in",
                "interval",
              ].includes(key)
            ) {
              if (
                !Number.isSafeInteger(value) ||
                context?.source === undefined ||
                !/^(0|[1-9][0-9]*)$/u.test(context.source) ||
                BigInt(context.source) !== BigInt(value)
              )
                throw new TypeError(
                  "OAuth integer exceeds the exact JSON contract.",
                );
            }
            return value;
          },
        )
      : JSON.parse(raw);
    return { ok: true, value };
  } catch {
    // JSON parser messages may quote secret-bearing response bytes. Only
    // decoded JSON can enter diagnostics, where sensitive fields are redacted.
    return {
      ok: false,
      errorMessage: "ts-auth returned an invalid JSON response body.",
    };
  }
}

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

function retryDelay(
  response: Response | undefined,
  baseDelayMs: number,
  attempt: number,
): number {
  const retryAfter = retryAfterMs(response);
  if (retryAfter !== undefined) {
    return retryAfter;
  }

  // Computed backoff carries jitter (ADR 0050 §5.9) so concurrent clients
  // that fail together do not retry in lockstep; server-provided
  // Retry-After timing is honoured exactly and never jittered.
  const exponential = Math.min(baseDelayMs * 2 ** attempt, 30_000);
  const jitterRatio = 0.8 + Math.random() * 0.4;
  return Math.round(Math.min(exponential * jitterRatio, 30_000));
}

function withCancellation<T>(
  operation: Promise<T>,
  signal?: AbortSignal,
  disposeLateValue?: (value: T) => void,
): Promise<T> {
  if (signal === undefined) return operation;
  return new Promise<T>((resolve, reject) => {
    const onAbort = (): void =>
      reject(
        signal.reason ??
          new DOMException("The operation was aborted", "AbortError"),
      );
    if (signal.aborted) onAbort();
    else signal.addEventListener("abort", onAbort, { once: true });
    void operation
      .then((value) => {
        if (signal.aborted) {
          try {
            disposeLateValue?.(value);
          } catch {
            // Cleanup failure cannot change a terminal cancellation.
          }
          onAbort();
        } else resolve(value);
      }, reject)
      .finally(() => signal.removeEventListener("abort", onAbort));
  });
}

function delayForRetry(delayMs: number, signal?: AbortSignal): Promise<void> {
  if (signal?.aborted)
    return Promise.reject(
      signal.reason ??
        new DOMException("The operation was aborted", "AbortError"),
    );
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
      reject(
        signal?.reason ??
          new DOMException("The operation was aborted", "AbortError"),
      );
    };
    signal?.addEventListener("abort", onAbort, { once: true });
    if (signal?.aborted) onAbort();
  });
}

function errorFromResponse(
  response: Response,
  body: unknown,
  routeName: TsAuthRouteName,
): TerraScaleError {
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
  const flatOAuthPair =
    flatError !== undefined && typeof source.error_description === "string";
  const oauthCode = routeName.startsWith("tsauth.")
    ? flatError
    : flatOAuthPair
      ? flatError
      : undefined;
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
      (flatOAuthPair
        ? stringProperty(source, "error_description")
        : undefined) ??
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
  return sanitizeHttpDiagnosticValue(
    sanitizeDiagnostic(value),
    "",
    new WeakSet<object>(),
    0,
  );
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
        return url.search !== "" || url.hash !== ""
          ? "[redacted URL]"
          : candidate;
      } catch {
        return candidate;
      }
    });
  }
  if (value === null || typeof value !== "object") return value;
  if (depth >= 12 || seen.has(value)) return "[redacted]";
  seen.add(value);
  if (Array.isArray(value))
    return value.map((entry) =>
      sanitizeHttpDiagnosticValue(entry, "", seen, depth + 1),
    );
  const output: Record<string, unknown> = {};
  for (const [entryKey, entryValue] of Object.entries(value)) {
    output[entryKey] = sanitizeHttpDiagnosticValue(
      entryValue,
      entryKey,
      seen,
      depth + 1,
    );
  }
  return output;
}

function errorCategory(
  source: Record<string, unknown>,
): TerraScaleErrorCategory | undefined {
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

function stringProperty(
  source: Record<string, unknown>,
  key: string,
): string | undefined {
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

        const message =
          stringProperty(item, "message") ??
          stringProperty(item, "errorMessage");

        if (message === undefined) {
          return undefined;
        }

        return {
          path: stringProperty(item, "path") ?? stringProperty(item, "field"),
          message,
          code:
            stringProperty(item, "code") ?? stringProperty(item, "constraint"),
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
