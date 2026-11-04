/* Coordinator probes of the C# TerraBase contract. Unconfigured secrets
   remain unjudged; malformed configuration and contract failures fail closed. */

import { isId, newId } from "../ids";
import { logEvent } from "../log";

import { NOT_CONFIGURED } from "../domain/status";
import {
  CANARY_VANTAGE,
  CanaryError,
  CanaryMonitorError,
  canaryJson,
  collectionPath,
  holdsDocument,
  isDocumentProbeConfig,
  isRegionCode,
  forgetToken,
  readToken,
  timed,
  type DocumentProbeConfig,
  type CanaryResult,
} from "./canary";
import { USER_AGENT } from "./check";
import type { Sample } from "./round";

export type TerraBaseProbe = "health" | "documents";

export interface TerraBaseConfig {
  /** The public API origin, e.g. https://api.terrascale.tech. */
  origin: string;
  /** The deployment region, enforced by TerraBase's region pin. */
  region: string;
  /** Platform issuer, including /terrascale; required for document reads. */
  issuer?: string;
  /** Database-scoped ts-auth project key with documents:read and documents:write. */
  key?: string;
  project?: string;
  database?: string;
  collection?: string;
}

/** A not configured endpoint's sample: unjudged, so it is neither up nor
    down, counts toward no uptime and leaves any down streak behind. */
export const notConfiguredSample = (endpointId: string): Sample => ({
  endpointId,
  state: "none",
  ok: 0,
  total: 0,
  latencyMs: null,
  results: [{ v: CANARY_VANTAGE, ok: null, ms: null, e: NOT_CONFIGURED }],
  values: null,
});

const isString = (value: unknown): value is string => typeof value === "string" && value.length > 0;

/** The document probe writes and deletes its own canary documents. */
const DOCUMENT_SCOPES = "documents:read documents:write";

const ROUTES = {
  readyz: "/readyz",
  document: (config: TerraBaseConfig, id: string) =>
    `${collectionPath(config as DocumentProbeConfig)}/documents/by-key?id=${id}`,
  query: (config: TerraBaseConfig) => `${collectionPath(config as DocumentProbeConfig)}/query`,
};

/** Whether the secret configures an endpoint: it names it, with the key
    the probe needs. Whatever else it holds is checked by isTerraBaseConfig. */
export function isConfigured(probe: TerraBaseProbe, value: unknown): boolean {
  if (value === undefined) return false;
  const keyless =
    typeof value === "object" &&
    value !== null &&
    !Array.isArray(value) &&
    (value as { key?: unknown }).key === undefined;
  return probe === "health" || !keyless;
}

/** A bare https origin: no credentials, path, query or fragment, which the
    appended API path would otherwise land behind. */
export function isOrigin(value: unknown): boolean {
  if (typeof value !== "string") return false;
  try {
    const url = new URL(value);
    return url.protocol === "https:" && url.origin === value;
  } catch {
    return false;
  }
}

/** Whether `value` holds everything `probe` needs. */
export function isTerraBaseConfig(probe: TerraBaseProbe, value: unknown): value is TerraBaseConfig {
  if (typeof value !== "object" || value === null) return false;
  const entry = value as Record<string, unknown>;
  if (!isOrigin(entry.origin) || !isRegionCode(entry.region)) return false;
  switch (probe) {
    case "health":
      return entry.key === undefined || isString(entry.key);
    case "documents":
      return isDocumentProbeConfig(entry);
  }
}

/** Reads the secret's entries, unchecked: empty when unset, null when it
    is not a JSON object, so a broken secret fails every probe instead of
    passing for an absent one. */
export function parseTerraBase(secret: string | undefined): Record<string, unknown> | null {
  if (!secret) return {};
  try {
    const value: unknown = JSON.parse(secret);
    if (typeof value === "object" && value !== null && !Array.isArray(value) && Object.keys(value).every(isId))
      return value as Record<string, unknown>;
  } catch {
    // Reported below.
  }
  logEvent("error", "TERRABASE_PROBES must be a JSON object keyed by canonical endpoint ULIDs", {
    component: "terrabase",
  });
  return null;
}

const invalid = (): never => {
  throw new CanaryError("Invalid response");
};

/** A write's acknowledgement must name the canary document and a version. */
const acknowledged = (value: unknown, id: string): boolean => {
  const body = value as { id?: unknown; version?: unknown } | null;
  return body?.id === id && isString(body.version);
};

/** Readiness, or a fresh canary document written, read, queried and
    deleted; the delete is attempted even when an earlier step failed. */
export async function runTerraBase(
  probe: TerraBaseProbe,
  config: TerraBaseConfig,
  marker: string,
  fetcher: typeof fetch = fetch,
): Promise<CanaryResult> {
  const headers: Record<string, string> = {
    "user-agent": USER_AGENT,
    "X-TerraScale-Region-Pin": config.region,
  };
  const init = (extra: RequestInit = {}): RequestInit => ({ ...extra, headers: { ...headers, ...extra.headers } });
  const terrabase = timed(fetcher);
  // Every request answers 200 unless it says otherwise.
  const call = (path: string, extra?: RequestInit, statuses: readonly number[] = [200]) =>
    canaryJson(config.origin + path, init(extra), terrabase.fetch, statuses);
  try {
    if (!isTerraBaseConfig(probe, config)) throw new CanaryError("Invalid probe config");
    switch (probe) {
      case "health": {
        const ready = (await call(ROUTES.readyz, { cache: "no-store" })) as {
          status?: unknown;
          version?: unknown;
          region?: unknown;
        };
        if (ready.status !== "ready" || !isString(ready.version) || ready.region !== config.region) invalid();
        break;
      }
      case "documents": {
        headers.authorization = `Bearer ${await readToken(config as DocumentProbeConfig, fetcher, DOCUMENT_SCOPES)}`;
        // Fresh per region and round, so concurrent probes never collide.
        const id = newId();
        const json = { "content-type": "application/json" };
        let failure: unknown;
        try {
          const written = await call(
            ROUTES.document(config, id),
            { method: "PUT", headers: { ...json, "if-none-match": "*" }, body: JSON.stringify({ canary: marker }) },
            [201],
          );
          if (!acknowledged(written, id)) invalid();
          const read = (await call(ROUTES.document(config, id), { method: "GET", cache: "no-store" })) as {
            document?: { canary?: unknown };
          };
          if (!holdsDocument(read, id) || read.document?.canary !== marker) throw new CanaryError("Write not readable");
          const page = (await call(ROUTES.query(config), {
            method: "POST",
            headers: json,
            body: JSON.stringify({ filter: { eq: ["$id", id] }, limit: 1 }),
          })) as { items?: unknown };
          const items = Array.isArray(page.items) ? (page.items as { document?: { canary?: unknown } }[]) : [];
          if (items.length !== 1 || !holdsDocument(items[0], id) || items[0]?.document?.canary !== marker)
            throw new CanaryError("Query missed the document");
        } catch (error) {
          failure = error;
        } finally {
          try {
            if (!acknowledged(await call(ROUTES.document(config, id), { method: "DELETE" }), id)) invalid();
          } catch (error) {
            failure ??= error;
          }
        }
        if (failure !== undefined) throw failure;
        break;
      }
    }
    return { ok: true, latencyMs: terrabase.slowest(), error: null };
  } catch (error) {
    if (!(error instanceof CanaryError)) throw error;
    if (probe === "documents") forgetToken(config as DocumentProbeConfig, fetcher);
    return { ok: error instanceof CanaryMonitorError ? null : false, latencyMs: null, error: error.message };
  }
}
