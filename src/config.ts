import type { ProjectId } from "./ids.js";

export type FetchLike = (input: RequestInfo | URL, init?: RequestInit) => Promise<Response>;

export type AccessTokenProvider = string | (() => Promise<string | undefined> | string | undefined);

import type { TerraScaleRequestMetricRecorder } from "./http.js";

export type TerraScaleRuntimeConfig = {
  readonly apiBaseUrl: string;
  readonly authBaseUrl?: string;
  readonly projectId?: ProjectId;
  readonly accessToken?: AccessTokenProvider;
  readonly defaultHeaders?: HeadersInit;
  readonly fetch?: FetchLike;
  /** Records metrics for this client instance only. Recorder failures are ignored. */
  readonly requestMetricRecorder?: TerraScaleRequestMetricRecorder;
  /** Generates a stable key once per mutating request. Disabled by default for public callers. */
  readonly idempotencyKeyFactory?: (() => string) | undefined;
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

  return trimmed.replace(/\/+$/, "");
}
