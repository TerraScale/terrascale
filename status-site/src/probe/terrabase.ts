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
  CANARY_TABLE,
  isCanaryConfig,
  isRegionCode,
  forgetToken,
  readToken,
  sqlPath,
  sqlRows,
  timed,
  type CanaryConfig,
  type CanaryResult,
} from "./canary";
import { USER_AGENT } from "./check";
import type { Sample } from "./round";

/** The SQL probe's stored endpoints.probe value. Renaming it means rebuilding
    endpoints, whose ON DELETE CASCADE children hold the status history. */
export const SQL_PROBE = "documents";

export type TerraBaseProbe = "health" | typeof SQL_PROBE;

export interface TerraBaseConfig {
  /** The public API origin, e.g. https://api.terrascale.tech. */
  origin: string;
  /** The deployment region, enforced by TerraBase's region pin. */
  region: string;
  /** Platform issuer, including /terrascale; required for SQL probes. */
  issuer?: string;
  /** Database-scoped ts-auth project key with sql:read and sql:write. */
  key?: string;
  project?: string;
  database?: string;
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

/** The write probe inserts and deletes its own canary rows. */
const SQL_SCOPES = "sql:read sql:write";

const ROUTES = {
  readyz: "/readyz",
  sql: (config: TerraBaseConfig) => sqlPath(config as CanaryConfig),
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
    case SQL_PROBE:
      return isCanaryConfig(entry);
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

/** The rows a one-statement SQL write changed, when the body is a SQL result. */
const changed = (value: unknown): unknown =>
  (value as { results?: { rows_affected?: unknown }[] } | null)?.results?.[0]?.rows_affected;

/** Readiness, or a fresh canary row inserted, read, counted and deleted
    with SQL; the delete is attempted even when an earlier step failed. */
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
      case SQL_PROBE: {
        headers.authorization = `Bearer ${await readToken(config as CanaryConfig, fetcher, SQL_SCOPES)}`;
        // Fresh per region and round, so concurrent probes never collide.
        const id = newId();
        const table = CANARY_TABLE;
        const execute = (sql: string, args: unknown[]) =>
          call(ROUTES.sql(config), {
            method: "POST",
            headers: { "content-type": "application/json" },
            body: JSON.stringify({ statements: [{ sql, args }] }),
          });
        let failure: unknown;
        try {
          if (changed(await execute(`INSERT INTO ${table} (id, canary) VALUES (?, ?)`, [id, marker])) !== 1) invalid();
          const read = sqlRows(await execute(`SELECT canary FROM ${table} WHERE id = ?`, [id]));
          if (read?.length !== 1 || read[0]![0] !== marker) throw new CanaryError("Write not readable");
          const found = sqlRows(
            await execute(`SELECT count(*) FROM ${table} WHERE id = ? AND canary = ?`, [id, marker]),
          );
          if (found?.[0]?.[0] !== 1) throw new CanaryError("Query missed the row");
        } catch (error) {
          failure = error;
        } finally {
          try {
            if (changed(await execute(`DELETE FROM ${table} WHERE id = ?`, [id])) !== 1) invalid();
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
    if (probe === SQL_PROBE) forgetToken(config as CanaryConfig, fetcher);
    return { ok: error instanceof CanaryMonitorError ? null : false, latencyMs: null, error: error.message };
  }
}
