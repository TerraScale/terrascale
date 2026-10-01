/**
 * @typedef {(
 *   | "authentication"
 *   | "authorization"
 *   | "conflict"
 *   | "domain"
 *   | "invalid-response"
 *   | "not-found"
 *   | "rate-limit"
 *   | "server"
 *   | "transport"
 *   | "unavailable"
 *   | "validation"
 * )} TerraScaleErrorKind
 */

/**
 * The stable category names published by the TerraScale error envelope.
 * @typedef {(
 *   | "Validation"
 *   | "Authentication"
 *   | "Authorization"
 *   | "NotFound"
 *   | "Conflict"
 *   | "RateLimit"
 *   | "Schema"
 *   | "Query"
 *   | "Placement"
 *   | "AutoFix"
 *   | "AutoIndex"
 *   | "Sandbox"
 *   | "System"
 * )} TerraScaleErrorCategory
 */

/**
 * @typedef {{
 *   readonly path?: string;
 *   readonly message: string;
 *   readonly code?: string;
 * }} TerraScaleValidationIssue
 */

/**
 * `fix` is optional producer-provided remediation from the published error body.
 * `retryAfter` is the retry delay from the Retry-After header, in milliseconds,
 * when the server sent one.
 * @typedef {{
 *   readonly kind: TerraScaleErrorKind;
 *   readonly code: string;
 *   readonly message: string;
 *   readonly fix?: string | undefined;
 *   readonly retryAfter?: number | undefined;
 *   readonly category?: TerraScaleErrorCategory | undefined;
 *   readonly detail?: string | undefined;
 *   readonly suggestedAction?: string | undefined;
 *   readonly docsUrl?: string | undefined;
 *   readonly traceId?: string | undefined;
 *   readonly status?: number | undefined;
 *   readonly target?: string | undefined;
 *   readonly requestId?: string | undefined;
 *   readonly validation?: readonly TerraScaleValidationIssue[] | undefined;
 *   readonly details?: unknown | undefined;
 * }} TerraScaleError
 */

/**
 * @template TValue
 * @typedef {{
 *   readonly ok: true;
 *   readonly routeName: string;
 *   readonly status: number;
 *   readonly headers: Headers;
 *   readonly value: TValue;
 * }} TerraScaleSuccess
 */

/**
 * @typedef {{
 *   readonly ok: false;
 *   readonly routeName: string;
 *   readonly status?: number | undefined;
 *   readonly headers?: Headers | undefined;
 *   readonly error: TerraScaleError;
 *   readonly body?: unknown | undefined;
 * }} TerraScaleFailure
 */

/**
 * @template TValue
 * @typedef {TerraScaleFailure | TerraScaleSuccess<TValue>} TerraScaleResult
 */

/**
 * @template TValue
 * @param {string} routeName
 * @param {number} status
 * @param {Headers} headers
 * @param {TValue} value
 * @returns {TerraScaleSuccess<TValue>}
 */
export function ok(routeName, status, headers, value) {
  return {
    ok: true,
    routeName,
    status,
    headers,
    value,
  };
}

/**
 * @param {string} routeName
 * @param {TerraScaleError} error
 * @param {number} [status]
 * @param {Headers} [headers]
 * @param {unknown} [body]
 * @returns {TerraScaleFailure}
 */
export function err(routeName, error, status, headers, body) {
  return {
    ok: false,
    routeName,
    status,
    headers,
    error: /** @type {TerraScaleError} */ (sanitizeDiagnostic(error)),
    ...(body === undefined ? {} : { body: sanitizeDiagnostic(body) }),
  };
}

const redactedDiagnostic = "[redacted]";
const redactedUrl = "[redacted URL]";
const sensitiveKeyPattern =
  /(?:^|[._-])(api[._-]?key|access[._-]?token|refresh[._-]?token|session(?:[._-]?token)?|authorization|bearer|password|passwd|secret|credential|cookie|jwt|private[._-]?key)(?:$|[._-])/iu;
const sensitiveQueryKeyPattern =
  /^(?:api[._-]?key|access[._-]?token|refresh[._-]?token|session(?:[._-]?token)?|token|authorization|bearer|password|passwd|secret|credential|cookie|query|sql|statement|parameters?)$/iu;
const bearerPattern = /\b(bearer|basic)\s+[A-Za-z0-9._~+/=-]+/giu;
const keyValuePattern =
  /((?:api[._-]?key|access[._-]?token|refresh[._-]?token|session(?:[._-]?token)?|authorization|bearer|password|passwd|secret|credential|cookie|query|sql|statement|parameters?)\s*[:=]\s*)(["']?)([^\s,;)}\]"']+)/giu;
const urlPattern = /https?:\/\/[^\s<>"']+/giu;

/**
 * Makes values safe to retain in a public result. Error bodies and transport
 * failures are producer-controlled and may contain credentials even when the
 * published error contract does not. Keep this deliberately conservative:
 * sensitive fields are replaced and secret-bearing URLs are never preserved.
 * @param {unknown} value
 * @returns {unknown}
 */
export function sanitizeDiagnostic(value) {
  return sanitizeDiagnosticValue(value, "", new WeakSet(), 0);
}

/**
 * @param {unknown} value
 * @param {string} key
 * @param {WeakSet<object>} seen
 * @param {number} depth
 * @returns {unknown}
 */
function sanitizeDiagnosticValue(value, key, seen, depth) {
  if (isSensitiveKey(key)) return redactedDiagnostic;
  if (typeof value === "string") return sanitizeDiagnosticText(value);
  if (value === null || typeof value !== "object") return value;
  if (depth >= 12) return redactedDiagnostic;
  if (seen.has(value)) return redactedDiagnostic;
  seen.add(value);

  if (value instanceof Error) {
    return {
      name: sanitizeDiagnosticText(value.name),
      message: sanitizeDiagnosticText(value.message),
    };
  }
  if (value instanceof URL) return sanitizeUrl(value);
  if (value instanceof Headers) {
    /** @type {Record<string, unknown>} */
    const headers = {};
    value.forEach((entry, name) => {
      headers[name] = sanitizeDiagnosticValue(entry, name, seen, depth + 1);
    });
    return headers;
  }
  if (Array.isArray(value)) {
    return value.map((entry) =>
      sanitizeDiagnosticValue(entry, "", seen, depth + 1),
    );
  }

  /** @type {Record<string, unknown>} */
  const output = {};
  for (const [entryKey, entryValue] of Object.entries(value)) {
    try {
      output[entryKey] = sanitizeDiagnosticValue(
        entryValue,
        entryKey,
        seen,
        depth + 1,
      );
    } catch {
      output[entryKey] = redactedDiagnostic;
    }
  }
  return output;
}

/**
 * @param {string} key
 * @returns {boolean}
 */
function isSensitiveKey(key) {
  return sensitiveKeyPattern.test(key) || sensitiveQueryKeyPattern.test(key);
}

/**
 * @param {string} value
 * @returns {string}
 */
function sanitizeDiagnosticText(value) {
  const withUrls = value.replace(urlPattern, (candidate) => {
    try {
      const url = new URL(candidate.replace(/[).,;]+$/u, ""));
      return hasSensitiveUrlPart(url) ? redactedUrl : candidate;
    } catch {
      return candidate;
    }
  });
  return withUrls
    .replace(bearerPattern, "$1 [redacted]")
    .replace(keyValuePattern, "$1$2[redacted]");
}

/**
 * @param {URL} url
 * @returns {string}
 */
function sanitizeUrl(url) {
  return hasSensitiveUrlPart(url) ? redactedUrl : url.toString();
}

/**
 * @param {URL} url
 * @returns {boolean}
 */
function hasSensitiveUrlPart(url) {
  if (url.username !== "" || url.password !== "") return true;
  for (const key of url.searchParams.keys()) {
    if (sensitiveQueryKeyPattern.test(key)) return true;
  }
  return false;
}
