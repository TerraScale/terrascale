/* TS-603 draft: prepared against the TS-598 platform credential contract.
   Wire into round.ts only after TS-589 and TS-592 ship. The currently
   published SDK rejects JWTs and has no platform grant/management methods. */

import { isId } from "../ids";
import { createTerraBaseClient, isTerraBaseV1Error, v1Filter, type V1Collection } from "@terrascale/sdk";
import { CANARY_TIMEOUT_MS, CanaryError, type CanaryResult } from "./canary";
import { MAX_BODY_BYTES, USER_AGENT } from "./check";

export type SdkProbe = "documents" | "management";

export interface SdkProbeConfig {
  origin: string;
  /** Regional platform issuer, including /terrascale. */
  issuer: string;
  project: string;
  /** A ts-auth project API key, exchanged at ts-auth; never sent to TerraBase. */
  key: string;
  database?: string;
  collection?: string;
  document?: string;
}

const NAME = /^[a-z][a-z0-9_-]{0,62}$/;
const KEY = /^tsk_[0-9a-hjkmnp-tv-z]{20}_[0-9a-hjkmnp-tv-z]{52}$/;

export function isSdkProbeConfig(probe: SdkProbe, value: unknown): value is SdkProbeConfig {
  if (typeof value !== "object" || value === null || Array.isArray(value)) return false;
  const entry = value as Record<string, unknown>;
  try {
    const origin = new URL(String(entry.origin));
    const issuer = new URL(String(entry.issuer));
    return (
      origin.protocol === "https:" &&
      origin.origin === entry.origin &&
      issuer.protocol === "https:" &&
      `${issuer.origin}/terrascale` === entry.issuer &&
      typeof entry.project === "string" &&
      isId(entry.project) &&
      typeof entry.key === "string" &&
      KEY.test(entry.key) &&
      entry.userId === undefined &&
      entry.organization === undefined &&
      (probe === "management" ||
        (typeof entry.database === "string" &&
          NAME.test(entry.database) &&
          typeof entry.collection === "string" &&
          NAME.test(entry.collection) &&
          (entry.document === undefined || (typeof entry.document === "string" && isId(entry.document)))))
    );
  } catch {
    return false;
  }
}

/** Named arguments for the TS-592 SDK platform grant, once admitted. No raw HTTP fallback. */
export function probeCredentials(probe: SdkProbe, config: SdkProbeConfig) {
  if (!isSdkProbeConfig(probe, config)) throw new CanaryError("Invalid probe config");
  return {
    issuer: config.issuer,
    clientId: config.key.slice(0, 24),
    clientSecret: config.key,
    request: {
      grant_type: "client_credentials" as const,
      project: config.project,
      scope: probe === "documents" ? "sql:read sql:write" : "databases:read",
    },
  };
}

/** JWT comes from the SDK platform grant. The draft pin still rejects it. */
export function probeCollection(
  config: SdkProbeConfig,
  accessToken: string,
  fetcher: typeof fetch = fetch,
): V1Collection {
  if (!isSdkProbeConfig("documents", config)) throw new CanaryError("Invalid probe config");
  // shortcut: SDK 0.0.3 accepts any bearer token; refuse JWTs here so none is dispatched, until the platform grant lands.
  if (!accessToken.startsWith("tb_")) throw new TypeError("token must be a tb_ API key");
  const client = createTerraBaseClient({
    baseUrl: config.origin,
    project: config.project,
    token: accessToken,
    maxResponseBytes: MAX_BODY_BYTES,
    fetch: async (input, init) => {
      const request = new Request(input, init);
      request.headers.set("user-agent", USER_AGENT);
      try {
        return await fetcher(request, { cache: "no-store", redirect: "manual" });
      } catch {
        throw new CanaryError(request.signal.aborted ? "Timed out" : "Unreachable");
      }
    },
  });
  return client.database(config.database!).collection(config.collection!);
}

/** Public errors contain no SDK messages or credentials. */
function probeError(error: unknown): string {
  if (error instanceof CanaryError) return error.message;
  if (isTerraBaseV1Error(error)) {
    if (error.status === 0) return "Unreachable";
    return `HTTP ${error.status}`;
  }
  if (error instanceof Error && ["TimeoutError", "AbortError"].includes(error.name)) return "Timed out";
  return "Invalid response";
}

/** SDK-decoded acknowledgements still must prove the intended durable write. */
function acknowledged(write: { id: string; durability: string }, id: string): void {
  if (write.id !== id || write.durability !== "local_durable") throw new CanaryError("Invalid response");
}

/** Retained canary: write/read. Document probe: write/read/query/delete,
    with deletion attempted even when the write's answer was lost. */
export async function runSdkDocuments(
  collection: V1Collection,
  id: string,
  marker: string,
  cleanup: boolean,
): Promise<CanaryResult> {
  if (!isId(id)) return { ok: false, latencyMs: null, error: "Invalid probe config" };
  const options = () => ({ signal: AbortSignal.timeout(CANARY_TIMEOUT_MS) });
  const started = Date.now();
  let failure: unknown;
  try {
    acknowledged(await collection.put(id, { canary: marker }, options()), id);
    const read = await collection.get(id, options());
    if (read.id !== id || read.state !== "value" || read.document?.canary !== marker)
      throw new CanaryError("Write not readable");
    if (cleanup) {
      const found = await collection.query({ filter: v1Filter.eq("$id", id), limit: 1 }, options());
      if (!found.items.some((item) => item.id === id && item.state === "value" && item.document?.canary === marker))
        throw new CanaryError("Query missed the document");
    }
  } catch (error) {
    failure = error;
  } finally {
    if (cleanup) {
      try {
        acknowledged(await collection.delete(id, options()), id);
      } catch (error) {
        failure ??= error;
      }
    }
  }
  return failure === undefined
    ? { ok: true, latencyMs: Date.now() - started, error: null }
    : { ok: false, latencyMs: null, error: probeError(failure) };
}
