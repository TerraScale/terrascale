import { isDataApiHostname } from "./native-origin.js";

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
  const apiBaseUrl = config.apiBaseUrl.trim().replace(/\/+$/u, "");
  /** @type {URL} */
  let url;
  try {
    url = new URL(apiBaseUrl);
  } catch {
    return { apiBaseUrl, maxRetryAttempts, retryDelayMs, requestTimeoutMs };
  }
  if (isDataApiHostname(url.hostname)) {
    throw new TypeError("Database API hosts cannot be used as an auth origin.");
  }
  for (const key of url.searchParams.keys()) {
    if (
      /(?:auth|cookie|credential|key|pass(?:word)?|secret|session|token)/iu.test(
        key,
      )
    )
      throw new TypeError(
        "ts-auth origins cannot contain secret-bearing query parameters.",
      );
  }
  if (
    url.username !== "" ||
    url.password !== "" ||
    url.pathname !== "/" ||
    url.search !== "" ||
    url.hash !== ""
  ) {
    throw new TypeError(
      "The ts-auth apiBaseUrl must be a bare origin without credentials, a path, query, or fragment.",
    );
  }
  const hostname = url.hostname.toLowerCase();
  const loopback = ["localhost", "127.0.0.1", "[::1]"].includes(hostname);
  const testTransport =
    config.fetch !== undefined &&
    (hostname === "test" || hostname.endsWith(".test"));
  if (
    url.protocol !== "https:" &&
    !(url.protocol === "http:" && (loopback || testTransport))
  ) {
    throw new TypeError(
      "ts-auth endpoints require HTTPS except explicit loopback/test transports.",
    );
  }
  return { apiBaseUrl, maxRetryAttempts, retryDelayMs, requestTimeoutMs };
}

/**
 * @param {AccessTokenProvider | undefined} provider
 * @returns {Promise<string | undefined>}
 */
export async function resolveAccessToken(provider) {
  return typeof provider === "function" ? await provider() : provider;
}
