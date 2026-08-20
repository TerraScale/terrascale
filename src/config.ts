import type { ProjectId } from "./ids.js";

export type FetchLike = (input: RequestInfo | URL, init?: RequestInit) => Promise<Response>;

export type AccessTokenProvider = string | (() => Promise<string | undefined> | string | undefined);

import type { TerraScaleRequestMetricRecorder } from "./http.js";

export type TerraScaleRuntimeConfig = {
  /** Public API root. Defaults to https://api.terrascale.tech. */
  readonly apiBaseUrl?: string;
  readonly authBaseUrl?: string;
  readonly projectId?: ProjectId;
  readonly accessToken?: AccessTokenProvider;
  readonly defaultHeaders?: HeadersInit;
  readonly fetch?: FetchLike;
  /** Records metrics for this client instance only. Recorder failures are ignored. */
  readonly requestMetricRecorder?: TerraScaleRequestMetricRecorder;
  /** Generates a stable key once per mutating request. Disabled by default for public callers. */
  readonly idempotencyKeyFactory?: (() => string) | undefined;
  /** Selected region preference. Failover affinity sends this as a routing hint. */
  readonly preferredRegion?: string | undefined;
  /** Strict affinity uses the selected region's production hostname and never uses a failover hint. */
  readonly regionAffinity?: "failover" | "strict" | undefined;
  /** Stores write tokens inside this client instance and sends the latest token on later reads. */
  readonly sessionConsistency?: "automatic" | "none" | undefined;
  /** Number of transient retry attempts after the initial request. Disabled by default. */
  readonly maxRetryAttempts?: number | undefined;
  /** Base delay in milliseconds when a transient response has no Retry-After header. */
  readonly retryDelayMs?: number | undefined;
};

export type ResolvedTerraScaleRuntimeConfig = {
  readonly apiBaseUrl: string;
  readonly authBaseUrl: string;
  readonly projectId?: ProjectId;
  readonly accessToken?: AccessTokenProvider;
  readonly defaultHeaders?: HeadersInit;
  readonly fetch: FetchLike;
  readonly requestMetricRecorder?: TerraScaleRequestMetricRecorder;
  readonly idempotencyKeyFactory?: (() => string) | undefined;
  readonly preferredRegion?: string | undefined;
  readonly regionAffinity: "failover" | "strict";
  readonly sessionConsistency: "automatic" | "none";
  readonly maxRetryAttempts: number;
  readonly retryDelayMs: number;
};

export function resolveRuntimeConfig(
  config: TerraScaleRuntimeConfig,
): ResolvedTerraScaleRuntimeConfig {
  const fetchHandler = config.fetch ?? globalThis.fetch?.bind(globalThis);

  if (fetchHandler === undefined) {
    throw new TypeError("TerraScaleClient requires a fetch implementation.");
  }

  const preferredRegion = normalizeRegion(config.preferredRegion);
  const regionAffinity = config.regionAffinity ?? "failover";
  if (regionAffinity !== "failover" && regionAffinity !== "strict") {
    throw new TypeError('regionAffinity must be either "failover" or "strict".');
  }
  if (regionAffinity === "strict" && preferredRegion === undefined) {
    throw new TypeError("Strict regional affinity requires a preferredRegion.");
  }

  const sessionConsistency = config.sessionConsistency ?? "none";
  if (sessionConsistency !== "automatic" && sessionConsistency !== "none") {
    throw new TypeError('sessionConsistency must be either "automatic" or "none".');
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
    ...config,
    apiBaseUrl,
    authBaseUrl: normalizeBaseUrl(config.authBaseUrl ?? globalApiBaseUrl),
    fetch: fetchHandler,
    preferredRegion,
    regionAffinity,
    sessionConsistency,
    maxRetryAttempts,
    retryDelayMs,
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

  return trimmed.replace(/\/+$/, "");
}

function normalizeRegion(region: string | undefined): string | undefined {
  if (region === undefined) {
    return undefined;
  }

  const normalized = region.trim().toLowerCase();
  if (!/^[a-z0-9][a-z0-9-]{1,31}$/.test(normalized)) {
    throw new TypeError("preferredRegion must be a valid TerraScale region code.");
  }

  return normalized;
}

function strictRegionalBaseUrl(baseUrl: string, region: string): string {
  const url = new URL(`${baseUrl}/`);
  if (url.hostname === "api.terrascale.tech") {
    url.hostname = `${region}.api.terrascale.tech`;
  } else if (url.hostname !== `${region}.api.terrascale.tech`) {
    throw new TypeError(
      "Strict regional affinity requires the global TerraScale API URL or the selected regional URL.",
    );
  }

  return url.toString().replace(/\/$/, "");
}
