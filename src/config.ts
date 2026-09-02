import type { ProjectId } from "./ids.js";
import { isRegisteredLocalRuntimeFetch } from "./local-runtime-capability.js";
import { parseRegionCode, type TerraScaleRegionCode } from "./regions.js";

export type FetchLike = (input: RequestInfo | URL, init?: RequestInit) => Promise<Response>;

export type AccessTokenProvider = string | (() => Promise<string | undefined> | string | undefined);

export type TerraScaleSqliteRuntimeConfig = {
  readonly enabled: true;
  readonly databasePath: string;
};

import type { TerraScaleRequestMetricRecorder } from "./http.js";

export type TerraScaleRuntimeConfig = {
  readonly apiBaseUrl?: string;
  readonly projectId?: ProjectId;
  readonly accessToken?: AccessTokenProvider;
  readonly fetch?: FetchLike;
  readonly requestMetricRecorder?: TerraScaleRequestMetricRecorder;
  readonly idempotencyKeyFactory?: (() => string) | undefined;
  readonly preferredRegion?: TerraScaleRegionCode | undefined;
  readonly regionAffinity?: "failover" | "strict" | undefined;
  readonly maxRetryAttempts?: number | undefined;
  readonly retryDelayMs?: number | undefined;
  readonly sqlite?: TerraScaleSqliteRuntimeConfig | undefined;
};

export type ResolvedTerraScaleRuntimeConfig = {
  readonly apiBaseUrl: string;
  readonly projectId?: ProjectId;
  readonly preferredRegion?: TerraScaleRegionCode | undefined;
  readonly regionAffinity: "failover" | "strict";
  readonly maxRetryAttempts: number;
  readonly retryDelayMs: number;
  readonly sqlite?: TerraScaleSqliteRuntimeConfig | undefined;
};

export function resolveRuntimeConfig(
  config: TerraScaleRuntimeConfig,
): ResolvedTerraScaleRuntimeConfig {
  if (Object.hasOwn(config, "defaultHeaders")) {
    throw new TypeError("defaultHeaders is not supported; use the named credential and routing options.");
  }

  if (config.sqlite !== undefined && !isLocalRuntimeConfig(config)) {
    throw new TypeError("SQLite mode is only available through the @terrascale/sdk/local entrypoint.");
  }
  const fetchHandler = config.fetch ?? globalThis.fetch?.bind(globalThis);

  if (fetchHandler === undefined) {
    throw new TypeError("TerraScaleClient requires a fetch implementation.");
  }

  const preferredRegion = config.preferredRegion === undefined
    ? undefined
    : parseRegionCode(config.preferredRegion, "preferredRegion");
  const regionAffinity = config.regionAffinity ?? "failover";
  if (regionAffinity !== "failover" && regionAffinity !== "strict") {
    throw new TypeError('regionAffinity must be either "failover" or "strict".');
  }
  if (regionAffinity === "strict" && preferredRegion === undefined) {
    throw new TypeError("Strict regional affinity requires a preferredRegion.");
  }

  const maxRetryAttempts = config.maxRetryAttempts ?? 0;
  if (!Number.isInteger(maxRetryAttempts) || maxRetryAttempts < 0 || maxRetryAttempts > 8) {
    throw new TypeError("maxRetryAttempts must be an integer between 0 and 8.");
  }

  const retryDelayMs = config.retryDelayMs ?? 250;
  if (!Number.isFinite(retryDelayMs) || retryDelayMs < 0 || retryDelayMs > 30_000) {
    throw new TypeError("retryDelayMs must be a finite number between 0 and 30000.");
  }

  const globalApiBaseUrl = normalizeBaseUrl(config.apiBaseUrl ?? "https://api.terrascale.tech");
  const apiBaseUrl = regionAffinity === "strict"
    ? strictRegionalBaseUrl(globalApiBaseUrl, preferredRegion!)
    : globalApiBaseUrl;

  return {
    apiBaseUrl,
    ...(config.projectId === undefined ? {} : { projectId: config.projectId }),
    preferredRegion,
    regionAffinity,
    maxRetryAttempts,
    retryDelayMs,
    ...(config.sqlite === undefined ? {} : { sqlite: config.sqlite }),
  };
}

function isLocalRuntimeConfig(config: TerraScaleRuntimeConfig): boolean {
  return config.apiBaseUrl === "http://terrascale.local"
    && isRegisteredLocalRuntimeFetch(config.fetch);
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

  const normalized = trimmed.replace(/\/+$/, "");
  let url: URL;
  try {
    url = new URL(`${normalized}/`);
  } catch {
    // Preserve request-time transport errors for malformed URLs.
    return normalized;
  }
  if (
    /(?:^|[.-])(?:tbep|terrabase)(?:[.-]|$)/iu.test(url.hostname) ||
    /(?:^|\/)(?:tbep|terrabase)(?:\/|$)/iu.test(url.pathname)
  ) {
    throw new TypeError("TerraScale SDK clients may target only the public API, not TBEP or TerraBase.");
  }

  return normalized;
}

function strictRegionalBaseUrl(baseUrl: string, region: TerraScaleRegionCode): string {
  const url = new URL(`${baseUrl}/`);
  const regionalHostname = `${region.toLowerCase()}.api.terrascale.tech`;
  if (url.hostname === "api.terrascale.tech") {
    url.hostname = regionalHostname;
  } else if (url.hostname !== regionalHostname) {
    throw new TypeError(
      "Strict regional affinity requires the global TerraScale API URL or the selected regional URL.",
    );
  }

  return url.toString().replace(/\/$/, "");
}
