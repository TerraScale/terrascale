// Operator session bootstrap for the studio (DB-02).
//
// Host-enforced by contract: the studio performs exactly one typed read and
// reacts to the host's answer. Under charter PD-2026-08-26-11 the default local
// host surface is ungated, so this bootstrap normally passes through. A host
// that does enforce an operator session answers typed operations with the
// `session.unauthenticated` error carrying a `loginUrl`; the studio then
// redirects there — it never authenticates on its own, never stores or sends
// credentials, and never logs credential-shaped values.

import {
  TerrabaseApiError,
  type EngineStatus,
  type TerrabaseStudioClient,
} from "@/api/generated/client";

export type OperatorSession = OperatorSessionOpen | OperatorSessionRedirect;

export interface OperatorSessionOpen {
  readonly kind: "open";
  readonly status: EngineStatus;
}

export interface OperatorSessionRedirect {
  readonly kind: "redirect";
  readonly loginUrl: string;
}

/** Resolves a host loginUrl (absolute, or relative to the API host) to a usable URL. */
export function resolveLoginUrl(baseUrl: string, loginUrl: string): string {
  return new URL(loginUrl, `${baseUrl}/`).toString();
}

/**
 * Bootstraps the operator session with one typed read.
 *
 * Returns the engine status when the session is accepted, or the host's login
 * flow URL when the host answers `session.unauthenticated`. Any other failure
 * propagates to the route's error state — the bootstrap adds no logic of its
 * own beyond the redirect.
 */
export async function bootstrapOperatorSession(
  client: TerrabaseStudioClient,
): Promise<OperatorSession> {
  let status: EngineStatus;
  try {
    status = await client.engineStatus();
  } catch (error) {
    if (
      error instanceof TerrabaseApiError &&
      error.code === "session.unauthenticated" &&
      error.loginUrl !== null &&
      error.loginUrl.length > 0
    ) {
      return { kind: "redirect", loginUrl: resolveLoginUrl(client.baseUrl, error.loginUrl) };
    }
    throw error;
  }
  return { kind: "open", status };
}
