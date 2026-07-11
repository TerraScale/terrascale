import type { ProjectId, TenantId, Ulid } from "./ids.js";

export type FetchLike = (input: RequestInfo | URL, init?: RequestInit) => Promise<Response>;

export type AccessTokenProvider = string | (() => Promise<string | undefined> | string | undefined);

export type DashboardLogicalDatabaseKey =
  | "audit"
  | "auth"
  | "billing"
  | "notifications"
  | "primary"
  | "settings"
  | "usage";

export type DashboardLogicalDatabaseIds = Partial<Record<DashboardLogicalDatabaseKey, Ulid>> &
  Record<string, Ulid | undefined>;

export type TerraScaleRuntimeConfig = {
  readonly apiBaseUrl: string;
  readonly authBaseUrl?: string;
  readonly dashboardClientId: string;
  readonly tenantId?: TenantId;
  readonly projectId?: ProjectId;
  readonly dashboardDatabaseIds?: DashboardLogicalDatabaseIds;
  readonly accessToken?: AccessTokenProvider;
  readonly defaultHeaders?: HeadersInit;
  readonly fetch?: FetchLike;
  /** Generates a stable key once per mutating request. Disabled by default for public callers. */
  readonly idempotencyKeyFactory?: (() => string) | undefined;
};

export type ResolvedTerraScaleRuntimeConfig = {
  readonly apiBaseUrl: string;
  readonly authBaseUrl: string;
  readonly dashboardClientId: string;
  readonly tenantId?: TenantId;
  readonly projectId?: ProjectId;
  readonly dashboardDatabaseIds: DashboardLogicalDatabaseIds;
  readonly accessToken?: AccessTokenProvider;
  readonly defaultHeaders?: HeadersInit;
  readonly fetch: FetchLike;
  readonly idempotencyKeyFactory?: (() => string) | undefined;
};

export function resolveRuntimeConfig(
  config: TerraScaleRuntimeConfig,
): ResolvedTerraScaleRuntimeConfig {
  const fetchHandler = config.fetch ?? globalThis.fetch?.bind(globalThis);

  if (fetchHandler === undefined) {
    throw new TypeError("TerraScaleClient requires a fetch implementation.");
  }

  return {
    ...config,
    apiBaseUrl: normalizeBaseUrl(config.apiBaseUrl),
    authBaseUrl: normalizeBaseUrl(config.authBaseUrl ?? config.apiBaseUrl),
    dashboardDatabaseIds: config.dashboardDatabaseIds ?? {},
    fetch: fetchHandler,
  };
}

export async function resolveAccessToken(
  provider: AccessTokenProvider | undefined,
): Promise<string | undefined> {
  if (provider === undefined) {
    return undefined;
  }

  return typeof provider === "function" ? await provider() : provider;
}

function normalizeBaseUrl(baseUrl: string): string {
  const trimmed = baseUrl.trim();

  if (trimmed.length === 0) {
    throw new TypeError("TerraScaleClient base URLs cannot be empty.");
  }

  return trimmed.endsWith("/") ? trimmed.slice(0, -1) : trimmed;
}
