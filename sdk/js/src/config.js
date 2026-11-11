import { authService, serviceOrigin } from "./origin.js";

/**
 * Shared fetch signature; native database and hosted identity credentials stay distinct.
 * @typedef {(input: RequestInfo | URL, init?: RequestInit) => Promise<Response>} FetchLike
 */
/**
 * @typedef {string | (() => Promise<string | undefined> | string | undefined)} AccessTokenProvider
 */

/**
 * `requestTimeoutMs`: one deadline covers discovery, credentials, requests, bodies and retry delays.
 * @typedef {{
 *   readonly apiBaseUrl: string;
 *   readonly accessToken?: AccessTokenProvider;
 *   readonly fetch?: FetchLike;
 *   readonly maxRetryAttempts?: number | undefined;
 *   readonly retryDelayMs?: number | undefined;
 *   readonly requestTimeoutMs?: number | undefined;
 * }} TsAuthRuntimeConfig
 */

/**
 * @typedef {{
 *   readonly apiBaseUrl: string;
 *   readonly maxRetryAttempts: number;
 *   readonly retryDelayMs: number;
 *   readonly requestTimeoutMs: number;
 * }} ResolvedTsAuthRuntimeConfig
 */

/**
 * @param {TsAuthRuntimeConfig} config
 * @returns {ResolvedTsAuthRuntimeConfig}
 */
export function resolveTsAuthRuntimeConfig(config) {
  if (Object.hasOwn(config, "defaultHeaders"))
    throw new TypeError("defaultHeaders is not supported.");
  const maxRetryAttempts = config.maxRetryAttempts ?? 0;
  if (
    !Number.isInteger(maxRetryAttempts) ||
    maxRetryAttempts < 0 ||
    maxRetryAttempts > 8
  ) {
    throw new TypeError("maxRetryAttempts must be an integer between 0 and 8.");
  }
  const retryDelayMs = config.retryDelayMs ?? 250;
  if (
    !Number.isFinite(retryDelayMs) ||
    retryDelayMs < 0 ||
    retryDelayMs > 30_000
  ) {
    throw new TypeError(
      "retryDelayMs must be a finite number between 0 and 30000.",
    );
  }
  const requestTimeoutMs = config.requestTimeoutMs ?? 30_000;
  if (
    !Number.isInteger(requestTimeoutMs) ||
    requestTimeoutMs < 0 ||
    requestTimeoutMs > 300_000
  ) {
    throw new TypeError(
      "requestTimeoutMs must be an integer between 0 and 300000.",
    );
  }
  const apiBaseUrl = serviceOrigin(config.apiBaseUrl, "apiBaseUrl", authService);
  return { apiBaseUrl, maxRetryAttempts, retryDelayMs, requestTimeoutMs };
}

/**
 * @param {AccessTokenProvider | undefined} provider
 * @returns {Promise<string | undefined>}
 */
export async function resolveAccessToken(provider) {
  return typeof provider === "function" ? await provider() : provider;
}
