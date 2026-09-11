import { canonicalRegionCode, type TerraScaleRegionCode } from "./regions.js";

export type TerraScaleErrorKind =
  | "authentication"
  | "authorization"
  | "conflict"
  | "domain"
  | "invalid-response"
  | "not-found"
  | "rate-limit"
  | "server"
  | "transport"
  | "unavailable"
  | "validation";

export type TerraScaleRoutingObservation = {
  readonly edgeRegion?: TerraScaleRegionCode | undefined;
  readonly servingRegion?: TerraScaleRegionCode | undefined;
  readonly routeMode?: TerraScaleRouteMode | undefined;
  readonly failoverFromRegion?: TerraScaleRegionCode | undefined;
  readonly degraded: boolean;
};

export type TerraScaleRouteMode =
  | "global"
  | "preferred"
  | "preferred-failover"
  | "regional-strict";

export type TerraScaleValidationIssue = {
  readonly path?: string;
  readonly message: string;
  readonly code?: string;
};

export type TerraScaleError = {
  readonly kind: TerraScaleErrorKind;
  readonly code: string;
  readonly message: string;
  /** Optional producer-provided remediation from the published error body. */
  readonly fix?: string | undefined;
  readonly status?: number | undefined;
  readonly target?: string | undefined;
  readonly requestId?: string | undefined;
  readonly validation?: readonly TerraScaleValidationIssue[] | undefined;
  readonly details?: unknown | undefined;
};

export type TerraScaleSuccess<TValue> = {
  readonly ok: true;
  readonly routeName: string;
  readonly status: number;
  readonly headers: Headers;
  readonly value: TValue;
  readonly routing?: TerraScaleRoutingObservation | undefined;
};

export type TerraScaleFailure = {
  readonly ok: false;
  readonly routeName: string;
  readonly status?: number | undefined;
  readonly headers?: Headers | undefined;
  readonly error: TerraScaleError;
  readonly body?: unknown | undefined;
  readonly routing?: TerraScaleRoutingObservation | undefined;
};

export type TerraScaleResult<TValue> = TerraScaleFailure | TerraScaleSuccess<TValue>;

export function ok<TValue>(
  routeName: string,
  status: number,
  headers: Headers,
  value: TValue,
): TerraScaleSuccess<TValue> {
  const routing = routingFrom(headers);
  return {
    ok: true,
    routeName,
    status,
    headers,
    value,
    ...(routing === undefined ? {} : { routing }),
  };
}

export function err(
  routeName: string,
  error: TerraScaleError,
  status?: number,
  headers?: Headers,
  body?: unknown,
): TerraScaleFailure {
  const routing = headers === undefined ? undefined : routingFrom(headers);
  return {
    ok: false,
    routeName,
    status,
    headers,
    error: sanitizeDiagnostic(error) as TerraScaleError,
    ...(body === undefined ? {} : { body: sanitizeDiagnostic(body) }),
    ...(routing === undefined ? {} : { routing }),
  };
}

const redactedDiagnostic = "[redacted]";
const redactedUrl = "[redacted URL]";
const sensitiveKeyPattern = /(?:^|[._-])(api[._-]?key|access[._-]?token|refresh[._-]?token|session(?:[._-]?token)?|authorization|bearer|password|passwd|secret|credential|cookie|private[._-]?key)(?:$|[._-])/iu;
const sensitiveQueryKeyPattern = /^(?:api[._-]?key|access[._-]?token|refresh[._-]?token|session(?:[._-]?token)?|token|authorization|bearer|password|passwd|secret|credential|cookie|query|sql|statement|parameters?)$/iu;
const bearerPattern = /\b(bearer|basic)\s+[A-Za-z0-9._~+/=-]+/giu;
const keyValuePattern = /((?:api[._-]?key|access[._-]?token|refresh[._-]?token|session(?:[._-]?token)?|authorization|bearer|password|passwd|secret|credential|cookie|query|sql|statement|parameters?)\s*[:=]\s*)(["']?)([^\s,;)}\]"']+)/giu;
const urlPattern = /https?:\/\/[^\s<>"']+/giu;

/**
 * Makes values safe to retain in a public result. Error bodies and transport
 * failures are producer-controlled and may contain credentials even when the
 * published error contract does not. Keep this deliberately conservative:
 * sensitive fields are replaced and secret-bearing URLs are never preserved.
 */
export function sanitizeDiagnostic(value: unknown): unknown {
  return sanitizeDiagnosticValue(value, "", new WeakSet<object>(), 0);
}

function sanitizeDiagnosticValue(
  value: unknown,
  key: string,
  seen: WeakSet<object>,
  depth: number,
): unknown {
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
    const headers: Record<string, unknown> = {};
    value.forEach((entry, name) => {
      headers[name] = sanitizeDiagnosticValue(entry, name, seen, depth + 1);
    });
    return headers;
  }
  if (Array.isArray(value)) {
    return value.map((entry) => sanitizeDiagnosticValue(entry, "", seen, depth + 1));
  }

  const output: Record<string, unknown> = {};
  for (const [entryKey, entryValue] of Object.entries(value)) {
    try {
      output[entryKey] = sanitizeDiagnosticValue(entryValue, entryKey, seen, depth + 1);
    } catch {
      output[entryKey] = redactedDiagnostic;
    }
  }
  return output;
}

function isSensitiveKey(key: string): boolean {
  return sensitiveKeyPattern.test(key) || sensitiveQueryKeyPattern.test(key);
}

function sanitizeDiagnosticText(value: string): string {
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

function sanitizeUrl(url: URL): string {
  return hasSensitiveUrlPart(url) ? redactedUrl : url.toString();
}

function hasSensitiveUrlPart(url: URL): boolean {
  if (url.username !== "" || url.password !== "") return true;
  for (const key of url.searchParams.keys()) {
    if (sensitiveQueryKeyPattern.test(key)) return true;
  }
  return false;
}

function routingFrom(headers: Headers): TerraScaleRoutingObservation | undefined {
  const edgeRegion = normalizeRegion(headers.get("x-terrascale-edge-region"));
  const servingRegion = normalizeRegion(headers.get("x-terrascale-serving-region"));
  const routeMode = normalizeRouteMode(headers.get("x-terrascale-route-mode"));
  const failoverFromRegion = normalizeRegion(headers.get("x-terrascale-failover-from-region"));
  if (
    edgeRegion === undefined &&
    servingRegion === undefined &&
    routeMode === undefined &&
    failoverFromRegion === undefined
  ) {
    return undefined;
  }

  return {
    ...(edgeRegion === undefined ? {} : { edgeRegion }),
    ...(servingRegion === undefined ? {} : { servingRegion }),
    ...(routeMode === undefined ? {} : { routeMode }),
    ...(failoverFromRegion === undefined ? {} : { failoverFromRegion }),
    degraded:
      routeMode === "preferred-failover" ||
      (routeMode === undefined &&
        edgeRegion !== undefined &&
        servingRegion !== undefined &&
        edgeRegion !== servingRegion),
  };
}

function normalizeRegion(value: string | null): TerraScaleRegionCode | undefined {
  return value === null ? undefined : canonicalRegionCode(value);
}

function normalizeRouteMode(value: string | null): TerraScaleRouteMode | undefined {
  switch (value?.trim().toLowerCase()) {
    case "global":
    case "preferred":
    case "preferred-failover":
    case "regional-strict":
      return value.trim().toLowerCase() as TerraScaleRouteMode;
    default:
      return undefined;
  }
}
