import { resolveAccessToken, resolveTsAuthRuntimeConfig } from "./config.js";
import { err, ok, sanitizeDiagnostic } from "./result.js";
import { parseStrictJson } from "./strict-json.js";
import { validateTsAuthIds } from "./ts-auth-ids.js";
import { isValidId } from "./ids.js";
/** @import { ResolvedTsAuthRuntimeConfig, TsAuthRuntimeConfig, FetchLike } from "./config.js" */
/** @import { TerraScaleError, TerraScaleErrorCategory, TerraScaleResult, TerraScaleValidationIssue } from "./result.js" */

/** @typedef {`tsauth.${string}`} TsAuthRouteName */
/** @typedef {"DELETE" | "GET" | "PATCH" | "POST" | "PUT"} TsAuthHttpMethod */
/**
 * @template TBody
 * @typedef {{
 *   readonly method: TsAuthHttpMethod;
 *   readonly path: string;
 *   readonly routeName: TsAuthRouteName;
 *   readonly query?: object | undefined;
 *   readonly body?: TBody | undefined;
 *   readonly formBody?: URLSearchParams | string | undefined;
 *   readonly userinfoAccessToken?: string | undefined;
 *   readonly resourceIntrospectionBasic?: string | undefined;
 *   readonly deletionReceipt?: string | undefined;
 *   readonly signal?: AbortSignal | undefined;
 * }} TsAuthRequest
 */
const retryableStatuses = new Set([429, 502, 503, 504]);
const maximumResponseBytes = 4 * 1024 * 1024;

/** Private transport for the named hosted identity and OIDC operations. */
export class TsAuthHttp {
  /**
   * @readonly
   * @type {ResolvedTsAuthRuntimeConfig}
   */
  config;
  /** @readonly @type {FetchLike} */
  #fetch;
  /** @readonly @type {TsAuthRuntimeConfig["accessToken"]} */
  #accessToken;
  /** @param {TsAuthRuntimeConfig} config */
  constructor(config) {
    this.config = Object.freeze(resolveTsAuthRuntimeConfig(config));
    const fetchHandler = config.fetch ?? globalThis.fetch?.bind(globalThis);
    if (fetchHandler === undefined)
      throw new TypeError("TsAuthClient requires a fetch implementation.");
    this.#fetch = fetchHandler;
    this.#accessToken = config.accessToken;
  }

  /**
   * @template TResponse
   * @template [TBody=unknown]
   * @param {TsAuthRequest<TBody>} options
   * @returns {Promise<TerraScaleResult<TResponse>>}
   */
  async request(options) {
    const deadline =
      this.config.requestTimeoutMs > 0
        ? AbortSignal.timeout(this.config.requestTimeoutMs)
        : undefined;
    const signals = [options.signal, deadline].filter(
      /** @returns {signal is AbortSignal} */
      (signal) => signal !== undefined,
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
      let attempt = 0;
      while (true) {
        signal?.throwIfAborted();
        let responseReceived = false;
        let fetchStarted = false;
        try {
          const headers = new Headers({ accept: "application/json" });
          const receiptRead = accountReceiptRead(options);
          if (options.deletionReceipt !== undefined) {
            if ((!receiptRead && !(options.routeName === "tsauth.platform.delete_account" && options.method === "DELETE" &&
                /^\/?v1\/terrascale\/account$/u.test(options.path))) ||
                !/^[A-Za-z0-9_-]{42}[AEIMQUYcgkosw048]$/u.test(options.deletionReceipt))
              throw new TypeError("Deletion receipts belong only to named account deletion operations.");
            headers.set("X-TerraScale-Deletion-Receipt", options.deletionReceipt);
          }
          if (receiptRead && (options.deletionReceipt === undefined || this.#accessToken !== undefined ||
              options.userinfoAccessToken !== undefined || options.resourceIntrospectionBasic !== undefined))
            throw new TypeError("Account deletion reads require a receipt without bearer credentials.");
          const oidc = options.routeName.startsWith("tsauth.oidc.");
          const token = oidc
            ? undefined
            : await withCancellation(
                resolveAccessToken(this.#accessToken),
                signal,
              );
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
          /** @type {RequestInit & { duplex?: "half" }} */
          const init = {
            method: options.method,
            headers,
            redirect: "manual",
            ...(signal === undefined ? {} : { signal }),
            ...(body === undefined ? {} : { body }),
          };
          if (options.method !== "GET" && typeof process !== "undefined" && process.release?.name === "node") {
            // Native Node fetch replays buffered mutations on 421, even with SDK retries disabled.
            // Empty DELETEs also need a one-shot body. Keep JSON/form bytes and media types unchanged.
            const bytes = body === undefined ? undefined : new TextEncoder().encode(body);
            init.body = new ReadableStream({ start(controller) {
              if (bytes !== undefined && bytes.byteLength > 0) controller.enqueue(bytes);
              controller.close();
            } });
            init.duplex = "half";
          }
          const request = new Request(
            buildUrl(
              this.config.apiBaseUrl,
              options.path,
              options.query,
            ),
            init,
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
            options,
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
            try {
              validateTsAuthIds(parsed.value, options.routeName);
            } catch {
              return err(options.routeName, {
                kind: "invalid-response", code: "invalid_response",
                message: "ts-auth returned a noncanonical entity ID.", status: response.status,
                requestId: requestIdFrom(response.headers),
              }, response.status, response.headers);
            }
            return ok(
              options.routeName,
              response.status,
              response.headers,
              /** @type {TResponse} */ (parsed.value),
            );
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
      if (signal?.aborted) {
        const timedOut = deadline?.aborted === true && signal.reason === deadline.reason;
        let cause;
        try { cause = sanitizeHttpDiagnostic(signal.reason); }
        catch { cause = "[redacted]"; }
        return err(options.routeName, {
          kind: "transport",
          code: timedOut ? "request_timeout" : "request_aborted",
          message: timedOut ? "The request deadline expired." : "The request was aborted.",
          details: { cause },
        });
      }
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

/**
 * @param {string} origin
 * @param {string} path
 * @param {object} [query]
 * @returns {URL}
 */
function buildUrl(origin, path, query) {
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

/**
 * @typedef {{ readonly ok: true; readonly value: unknown }
 *   | { readonly ok: false; readonly errorMessage: string }} ParsedBody
 */

/**
 * @param {TsAuthRouteName} routeName
 * @returns {boolean}
 */
function strictOauthRoute(routeName) {
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

/** @param {TsAuthRequest<unknown>} request */
function accountReceiptRead(request) {
  return request.method === "GET" && (
    request.routeName === "tsauth.platform.account_deletion_status" && /^\/?v1\/terrascale\/account\/deletion$/u.test(request.path) ||
    request.routeName === "tsauth.platform.account_deletion_history" && /^\/?v1\/terrascale\/account\/deletion\/history$/u.test(request.path));
}

/**
 * @param {Response} response
 * @param {TsAuthRequest<unknown>} request
 * @param {AbortSignal} [signal]
 * @returns {Promise<ParsedBody>}
 */
async function readResponseBody(response, request, signal) {
  const routeName = request.routeName;
  const reader = response.body?.getReader();
  /** @type {Uint8Array[]} */
  const chunks = [];
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
  // Published closure/status operations return empty 202 while still pending.
  // Keep exceptions bound to exact named paths, methods and status.
  const closure = /^\/?v1\/terrascale\/organizations\/([^/]+)\/projects\/([^/]+)$/u.exec(request.path);
  const pendingClosure = request.method === "DELETE" && (
    routeName === "tsauth.platform.delete_project" && isValidId(closure?.[1]) && isValidId(closure?.[2]) ||
    routeName === "tsauth.platform.delete_account" && /^\/?v1\/terrascale\/account$/u.test(request.path));
  const pendingReceipt = routeName === "tsauth.platform.account_deletion_status" && accountReceiptRead(request);
  if (raw.length === 0 && response.status === 202 && (pendingClosure || pendingReceipt))
    return { ok: true, value: undefined };
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
      // Bound syntax depth and reject duplicate members before OAuth decoding.
      parseStrictJson(bytes);
    }
    /** @type {unknown} */
    const value = strictOauth
      ? JSON.parse(
          raw,
          /**
           * @param {string} key
           * @param {unknown} value
           * @param {{ readonly source?: string }} [context]
           * @returns {unknown}
           */
          (key, value, context) => {
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

/**
 * @param {Response | undefined} response
 * @returns {number | undefined}
 */
function retryAfterMs(response) {
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

/**
 * @param {Response | undefined} response
 * @param {number} baseDelayMs
 * @param {number} attempt
 * @returns {number}
 */
function retryDelay(response, baseDelayMs, attempt) {
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

/**
 * @template T
 * @param {Promise<T>} operation
 * @param {AbortSignal} [signal]
 * @param {(value: T) => void} [disposeLateValue]
 * @returns {Promise<T>}
 */
function withCancellation(operation, signal, disposeLateValue) {
  if (signal === undefined) return operation;
  return new Promise((resolve, reject) => {
    /** @returns {void} */
    const onAbort = () =>
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

/**
 * @param {number} delayMs
 * @param {AbortSignal} [signal]
 * @returns {Promise<void>}
 */
function delayForRetry(delayMs, signal) {
  if (signal?.aborted)
    return Promise.reject(
      signal.reason ??
        new DOMException("The operation was aborted", "AbortError"),
    );
  if (delayMs === 0) {
    return Promise.resolve();
  }

  return new Promise((resolve, reject) => {
    const timer = setTimeout(() => {
      signal?.removeEventListener("abort", onAbort);
      resolve();
    }, delayMs);
    /** @returns {void} */
    const onAbort = () => {
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

/**
 * @param {Response} response
 * @param {unknown} body
 * @param {TsAuthRouteName} routeName
 * @returns {TerraScaleError}
 */
function errorFromResponse(response, body, routeName) {
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

/**
 * Apply HTTP-specific rules before error fields are extracted.
 * @param {unknown} value
 * @returns {unknown}
 */
function sanitizeHttpDiagnostic(value) {
  return sanitizeHttpDiagnosticValue(
    sanitizeDiagnostic(value),
    "",
    new WeakSet(),
    0,
  );
}

/**
 * @param {unknown} value
 * @param {string} key
 * @param {WeakSet<object>} seen
 * @param {number} depth
 * @returns {unknown}
 */
function sanitizeHttpDiagnosticValue(value, key, seen, depth) {
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
  /** @type {Record<string, unknown>} */
  const output = {};
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

/**
 * @param {Record<string, unknown>} source
 * @returns {TerraScaleErrorCategory | undefined}
 */
function errorCategory(source) {
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

/**
 * @param {unknown} body
 * @returns {Record<string, unknown>}
 */
function unwrapErrorBody(body) {
  if (!isRecord(body)) {
    return {};
  }

  const error = body.error;
  return isRecord(error) ? error : body;
}

/**
 * @param {Record<string, unknown>} source
 * @param {string} key
 * @returns {string | undefined}
 */
function stringProperty(source, key) {
  const value = source[key];
  return typeof value === "string" && value.length > 0 ? value : undefined;
}

/**
 * @param {Record<string, unknown>} source
 * @returns {readonly TerraScaleValidationIssue[] | undefined}
 */
function validationIssues(source) {
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
      .filter(
        /** @returns {item is TerraScaleValidationIssue} */
        (item) => item !== undefined,
      );
  }

  if (isRecord(raw)) {
    return Object.entries(raw).flatMap(([path, value]) => {
      const messages = Array.isArray(value) ? value : [value];
      return messages
        .filter(
          /** @returns {message is string} */
          (message) => typeof message === "string",
        )
        .map((message) => ({ path, message }));
    });
  }

  return undefined;
}

/**
 * @param {number} status
 * @param {readonly TerraScaleValidationIssue[] | undefined} validation
 * @param {string} code
 */
function kindFromStatus(status, validation, code) {
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

/**
 * @param {number} status
 * @returns {string}
 */
function defaultCode(status) {
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

/**
 * @param {Headers} headers
 * @returns {string | undefined}
 */
function requestIdFrom(headers) {
  return headers.get("x-request-id") ?? headers.get("traceparent") ?? undefined;
}

/**
 * @param {unknown} value
 * @returns {value is Record<string, unknown>}
 */
function isRecord(value) {
  return typeof value === "object" && value !== null && !Array.isArray(value);
}
