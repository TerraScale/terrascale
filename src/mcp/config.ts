import { toUlid, type ProjectId } from "../ids.js";

export type TerraScaleMcpConfig = {
  readonly apiBaseUrl: string;
  readonly authBaseUrl?: string;
  readonly accessToken: string;
  readonly projectId?: ProjectId;
};

export function loadMcpConfig(environment: NodeJS.ProcessEnv = process.env): TerraScaleMcpConfig {
  const apiBaseUrl = requiredUrl(environment.TERRASCALE_API_BASE_URL, "TERRASCALE_API_BASE_URL");
  const accessToken = requiredValue(environment.TERRASCALE_ACCESS_TOKEN, "TERRASCALE_ACCESS_TOKEN");
  const authBaseUrl = optionalUrl(environment.TERRASCALE_AUTH_API_BASE_URL, "TERRASCALE_AUTH_API_BASE_URL");
  const project = environment.TERRASCALE_PROJECT_ID?.trim();

  return {
    apiBaseUrl,
    ...(authBaseUrl === undefined ? {} : { authBaseUrl }),
    accessToken,
    ...(project === undefined || project.length === 0 ? {} : { projectId: toUlid(project) }),
  };
}

function requiredValue(value: string | undefined, name: string): string {
  const trimmed = value?.trim();
  if (trimmed === undefined || trimmed.length === 0) {
    throw new TypeError(`${name} must be set.`);
  }
  return trimmed;
}

function requiredUrl(value: string | undefined, name: string): string {
  return normalizeUrl(requiredValue(value, name), name);
}

function optionalUrl(value: string | undefined, name: string): string | undefined {
  const trimmed = value?.trim();
  return trimmed === undefined || trimmed.length === 0 ? undefined : normalizeUrl(trimmed, name);
}

function normalizeUrl(value: string, name: string): string {
  let parsed: URL;
  try {
    parsed = new URL(value);
  } catch {
    throw new TypeError(`${name} must be an absolute http(s) URL.`);
  }
  if (parsed.protocol !== "http:" && parsed.protocol !== "https:") {
    throw new TypeError(`${name} must be an absolute http(s) URL.`);
  }
  return value.replace(/\/+$/, "");
}
