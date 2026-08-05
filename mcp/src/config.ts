import type { FetchLike } from "@terrascale/sdk";

export type McpRuntimeConfig = {
  readonly apiBaseUrl: string;
  readonly accessToken: string;
  readonly requestTimeoutMs: number;
};

export type McpHttpConfig = Omit<McpRuntimeConfig, "accessToken">;

const defaultRequestTimeoutMs = 10_000;

export function readStdioConfig(environment: NodeJS.ProcessEnv): McpRuntimeConfig {
  return readRuntimeConfig({
    apiBaseUrl: environment.TERRASCALE_API_BASE_URL,
    accessToken: environment.TERRASCALE_ACCESS_TOKEN,
    requestTimeoutMs: environment.TERRASCALE_MCP_REQUEST_TIMEOUT_MS,
  });
}

export function readHttpConfig(environment: NodeJS.ProcessEnv): McpHttpConfig {
  const { accessToken: _accessToken, ...config } = readRuntimeConfig({
    apiBaseUrl: environment.TERRASCALE_API_BASE_URL,
    accessToken: "http-caller-provided",
    requestTimeoutMs: environment.TERRASCALE_MCP_REQUEST_TIMEOUT_MS,
  });
  return config;
}

export function readRuntimeConfig(input: {
  readonly apiBaseUrl: string | undefined;
  readonly accessToken: string | undefined;
  readonly requestTimeoutMs?: string | number | undefined;
}): McpRuntimeConfig {
  const apiBaseUrl = requireApiBaseUrl(input.apiBaseUrl);
  const accessToken = requireAccessToken(input.accessToken);
  const requestTimeoutMs = readTimeout(input.requestTimeoutMs);

  return { apiBaseUrl, accessToken, requestTimeoutMs };
}

export function callerAccessToken(authorization: string | undefined): string | undefined {
  if (authorization === undefined || authorization.length > 8_192) {
    return undefined;
  }

  const token = authorization.trim();
  return token.length === 0 ? undefined : token;
}

export function timeoutFetch(timeoutMs: number, callerSignal?: () => AbortSignal | undefined): FetchLike {
  return async (input, init) => {
    const controller = new AbortController();
    const timer = setTimeout(() => controller.abort(), timeoutMs);

    try {
      const signal = callerSignal?.();
      return await globalThis.fetch(input, {
        ...init,
        signal: signal === undefined ? controller.signal : AbortSignal.any([signal, controller.signal]),
      });
    } finally {
      clearTimeout(timer);
    }
  };
}

function requireApiBaseUrl(value: string | undefined): string {
  if (value === undefined || value.trim().length === 0) {
    throw new McpConfigurationError("api_base_url_required");
  }

  let url: URL;
  try {
    url = new URL(value);
  } catch {
    throw new McpConfigurationError("api_base_url_invalid");
  }

  const localHttp = url.protocol === "http:" && ["localhost", "127.0.0.1", "::1"].includes(url.hostname);
  if (url.protocol !== "https:" && !localHttp) {
    throw new McpConfigurationError("api_base_url_requires_https");
  }

  return url.toString().replace(/\/+$/, "");
}

function requireAccessToken(value: string | undefined): string {
  const token = value?.trim();
  if (token === undefined || token.length === 0 || token.length > 8_192) {
    throw new McpConfigurationError("access_token_required");
  }
  return token;
}

function readTimeout(value: string | number | undefined): number {
  if (value === undefined || value === "") {
    return defaultRequestTimeoutMs;
  }

  const timeout = typeof value === "number" ? value : Number(value);
  if (!Number.isInteger(timeout) || timeout < 100 || timeout > 30_000) {
    throw new McpConfigurationError("request_timeout_invalid");
  }
  return timeout;
}

export class McpConfigurationError extends Error {
  readonly code: string;

  constructor(code: string) {
    super(code);
    this.code = code;
  }
}
