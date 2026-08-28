// Server-side construction of the typed studio API client.
//
// Server-only by convention: pages and server actions import this module, never
// a client component. The base URL is read at request time so one build serves
// any local host or fixture.

import { TerrabaseStudioClient, type BackendContext } from "@/api/generated/client";

/** Default base URL: the in-repo dev fixture server (pre-SE-08). */
export const DEFAULT_API_BASE_URL = "http://127.0.0.1:3032";

/** Resolves the API host base URL from the environment, without a trailing slash. */
export function apiBaseUrl(): string {
  const configured = process.env.STUDIO_API_BASE_URL?.trim();
  return (
    configured === undefined || configured.length === 0 ? DEFAULT_API_BASE_URL : configured
  ).replace(/\/$/, "");
}

/**
 * The backend-supplied context the real host's local gate requires
 * (X-TB-Principal/X-TB-Tenant, PD-2026-08-26-11 D1): the caller is the
 * trusted local backend — the studio is a local operator tool with no
 * credentials on this surface, so fixed local identities (overridable per
 * environment) are the honest shape.
 */
export function studioBackendContext(): BackendContext {
  return {
    principal: process.env.STUDIO_API_PRINCIPAL?.trim() || "studio-operator",
    tenant: process.env.STUDIO_API_TENANT?.trim() || "tenant-a",
  };
}

/** Creates a typed client against the C# API host (or the dev fixture). */
export function createStudioClient(options: { timeoutMs?: number } = {}): TerrabaseStudioClient {
  return new TerrabaseStudioClient({
    baseUrl: apiBaseUrl(),
    timeoutMs: options.timeoutMs,
  });
}
