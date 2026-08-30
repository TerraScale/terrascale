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
    error,
    body,
    ...(routing === undefined ? {} : { routing }),
  };
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
