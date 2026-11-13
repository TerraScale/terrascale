/* Read-only Data API probe. DATA_API_CANARY maps endpoint ULIDs to a
   database-scoped ts-auth client credential; the probe reads the fixture
   row scripts/recover-canaries.py keeps in the canary table. */

import { isId } from "../ids";
import { logEvent } from "../log";

import { settle } from "../domain/consensus";
import { CANARY_CREDENTIAL_INVALID, CANARY_QUOTA_EXHAUSTED, SLOW_MS, type TimelineState } from "../domain/status";
import { MAX_BODY_BYTES, USER_AGENT } from "./check";
import type { Sample } from "./round";

/** Each canary request slower than this fails. */
export const CANARY_TIMEOUT_MS = 5_000;

/** The `v` a canary result is stored under: it is not a vantage. */
export const CANARY_VANTAGE = "canary";

export interface CanaryConfig {
  /** The public API origin, e.g. https://api.terrascale.tech. */
  origin: string;
  /** The deployment region, enforced by TerraBase's region pin. */
  region: string;
  project: string;
  database: string;
  /** Platform issuer, including /terrascale. */
  issuer: string;
  /** A ts-auth project key narrowed to this database: sql:read for the
      Data API canary, plus sql:write for the SQL API probe. */
  key: string;
}

export interface CanaryResult {
  ok: boolean | null;
  /** The slowest TerraBase request; null when the canary failed. */
  latencyMs: number | null;
  error: string | null;
}

const NAME = /^[a-z][a-z0-9_-]{0,62}$/;
const VERSION = /^tbv1-[0-9a-f]{32}$/;
const REGION_CODE = /^[a-z]{3}$/;

/** A three-letter deployment region code, e.g. gru or sjc. */
export const isRegionCode = (value: unknown): value is string => typeof value === "string" && REGION_CODE.test(value);

const projectKeyBody = (key: string): string => (key.startsWith("sk_") ? key.slice(3) : key);

export const projectKeyId = (key: string): string => projectKeyBody(key).slice(0, 26);

const isProjectKey = (key: string): boolean => {
  const body = projectKeyBody(key);
  return body.length === 79 && isId(body.slice(0, 26)) && /^_[0-9a-hjkmnp-tv-z]{52}$/.test(body.slice(26));
};

/** The canary table, quoted, and its fixture row's id (scripts/recover-canaries.py). */
export const CANARY_TABLE = '"canary"';
export const CANARY_ROW = "00000000000000000000000007";

export function isCanaryConfig(value: unknown): value is CanaryConfig {
  if (typeof value !== "object" || value === null || Array.isArray(value)) return false;
  const entry = value as Record<string, unknown>;
  try {
    const url = new URL(String(entry.origin));
    const issuer = new URL(String(entry.issuer));
    return (
      url.protocol === "https:" &&
      url.origin === entry.origin &&
      issuer.protocol === "https:" &&
      `${issuer.origin}/terrascale` === entry.issuer &&
      isRegionCode(entry.region) &&
      typeof entry.project === "string" &&
      isId(entry.project) &&
      typeof entry.database === "string" &&
      NAME.test(entry.database) &&
      typeof entry.key === "string" &&
      isProjectKey(entry.key)
    );
  } catch {
    return false;
  }
}

/** The database's SQL route: canaries read and write their own table. */
export const sqlPath = (config: Pick<CanaryConfig, "project" | "database">): string =>
  `/v1/projects/${config.project}/databases/${config.database}/sql/execute`;

/** The rows of the `index`th statement of a SQL result, or null when the body is not one. */
export const sqlRows = (value: unknown, index = 0): unknown[][] | null => {
  const results = (value as { results?: unknown } | null)?.results;
  const rows = Array.isArray(results) ? (results[index] as { rows?: unknown } | undefined)?.rows : undefined;
  return Array.isArray(rows) && rows.every(Array.isArray) ? (rows as unknown[][]) : null;
};

/** Whether a fixture read returned exactly the row `id`; no response content reaches public errors. */
export const holdsRow = (value: unknown, id: string): boolean => {
  const rows = sqlRows(value);
  return isId(id) && rows !== null && rows.length === 1 && rows[0]![0] === id;
};

/** Reads the canary secret, dropping malformed entries; empty when unset. */
export function parseCanaries(secret: string | undefined): Record<string, CanaryConfig> {
  if (!secret) return {};
  let value: unknown;
  try {
    value = JSON.parse(secret);
  } catch {
    logEvent("error", "DATA_API_CANARY is not JSON", { component: "canary" });
    return {};
  }
  if (typeof value !== "object" || value === null || Array.isArray(value)) return {};
  return Object.fromEntries(
    Object.entries(value).filter((entry): entry is [string, CanaryConfig] => {
      const valid = isId(entry[0]) && isCanaryConfig(entry[1]);
      if (!valid) logEvent("error", "malformed DATA_API_CANARY entry", { component: "canary" });
      return valid;
    }),
  );
}

export class CanaryError extends Error {
  constructor(
    message: string,
    readonly status?: number,
    readonly code?: string,
  ) {
    super(message);
  }
}

/** The monitor itself could not run the check: no evidence either way. */
export class CanaryMonitorError extends CanaryError {}

export class CanaryCredentialError extends CanaryMonitorError {
  constructor() {
    super(CANARY_CREDENTIAL_INVALID);
  }
}

/** TerraBase refuses the monitor's writes once its Free-plan monthly
    allowance is spent; reads and deletes still run. */
export class CanaryQuotaError extends CanaryMonitorError {
  constructor() {
    super(CANARY_QUOTA_EXHAUSTED, 429);
  }
}

/** The body as text, failing beyond MAX_BODY_BYTES: a canary response is small. */
async function readBounded(response: Response): Promise<string> {
  if (!response.body) return "";
  const reader = response.body.getReader();
  const decoder = new TextDecoder();
  let text = "";
  let size = 0;
  for (;;) {
    const { done, value } = await reader.read();
    if (done) return text + decoder.decode();
    size += value.byteLength;
    if (size > MAX_BODY_BYTES) {
      await reader.cancel().catch(() => {});
      throw new CanaryError("Response too large");
    }
    text += decoder.decode(value, { stream: true });
  }
}

/** Only known error codes reach public samples; never echoed credentials. */
const ERROR_CODES = new Set([
  "invalid_request",
  // TerraBase's SQL errors.
  "sql_error",
  "syntax_error",
  "undefined_table",
  "undefined_column",
  "primary_key_violation",
  "serialization_conflict",
  "quota_exceeded",
  "sql_sandbox",
  "sql_conflict",
  "sql_too_large",
  "too_large",
  "wrong_region",
  "invalid_query",
  "invalid_cursor",
  "invalid_patch",
  "ttl_invalid",
  "sort_requires_index",
  "assert_requires_index",
  "idempotency_key_required",
  "invalid_schema",
  "invalid_key",
  "invalid_token",
  "key_revoked",
  "key_expired",
  "invalid_target",
  "insufficient_scope",
  "forbidden_scope",
  "not_found",
  "unknown_database",
  "unknown_table",
  "unknown_route",
  "method_not_allowed",
  "conflict_unresolved",
  "unique_violation",
  "patch_conflict",
  "index_limit_exceeded",
  "idempotency_conflict",
  "idempotency_expired",
  "schema_changed",
  "must_refetch",
  "name_taken",
  "index_protected",
  "codec_conversion_unsupported",
  "limit_exceeded",
  "precondition_failed",
  "payload_too_large",
  "transaction_too_large",
  "unsupported_media_type",
  "home_region_required",
  "rate_limited",
  "internal_error",
  "upstream_unavailable",
  "capacity",
  "storage_unavailable",
  "stored_corruption",
  "commit_uncertain",
  "dependencies_unavailable",
  "tenant_busy",
  "tenant_memory",
  "operation_allowance",
  // ts-auth's OAuth errors.
  "invalid_client",
  "invalid_grant",
  "invalid_scope",
  "unauthorized_client",
  "temporarily_unavailable",
  "server_error",
]);

/** One canary request: the body of a 2xx response (or of one of
    `statuses`) as text. Anything else throws a CanaryError naming it, with the API's
    error code when it sent one. */
export async function canaryText(
  url: string,
  init: RequestInit,
  fetcher: typeof fetch,
  statuses?: readonly number[],
): Promise<string> {
  // The timer covers the body too, so a stalled response cannot hold the round.
  const controller = new AbortController();
  const timer = setTimeout(() => controller.abort(), CANARY_TIMEOUT_MS);
  try {
    let response: Response;
    try {
      response = await fetcher(url, {
        ...init,
        // A followed redirect would forward the key to its destination.
        redirect: "manual",
        signal: controller.signal,
      });
    } catch {
      throw new CanaryError(controller.signal.aborted ? "Timed out" : "Unreachable");
    }
    const text = await readBounded(response).catch((error: unknown) => {
      if (error instanceof CanaryError) throw error;
      throw new CanaryError(controller.signal.aborted ? "Timed out" : "Unreachable");
    });
    if (!response.ok || (statuses !== undefined && !statuses.includes(response.status))) {
      let code = "";
      try {
        const body = JSON.parse(text) as { error?: unknown } | null;
        // TerraBase nests its code; ts-auth's OAuth errors are a bare string.
        const named = typeof body?.error === "string" ? body.error : (body?.error as { code?: unknown } | null)?.code;
        // Only a documented code is kept: an echoed key must never reach a
        // public sample or incident.
        if (typeof named === "string" && ERROR_CODES.has(named)) code = ` ${named}`;
      } catch {
        // Not JSON: the status alone names it.
      }
      // A spent plan allowance (quota_exceeded on SQL) leaves the canary unmeasured, not down.
      if (response.status === 429 && (code === " operation_allowance" || code === " quota_exceeded"))
        throw new CanaryQuotaError();
      throw new CanaryError(`HTTP ${response.status}${code}`, response.status, code.trim());
    }
    return text;
  } finally {
    clearTimeout(timer);
  }
}

/** One canary request whose 2xx response must be a JSON object. */
export async function canaryJson(
  url: string,
  init: RequestInit,
  fetcher: typeof fetch,
  statuses?: readonly number[],
): Promise<unknown> {
  let body: unknown;
  try {
    body = JSON.parse(await canaryText(url, init, fetcher, statuses));
  } catch (error) {
    if (error instanceof CanaryError) throw error;
    throw new CanaryError("Invalid JSON");
  }
  if (body === null || typeof body !== "object") throw new CanaryError("Invalid JSON");
  return body;
}

/** Exchanged tokens per fetcher, reused while they outlast a round's use:
    a token exchange takes seconds, and a canary judges TerraBase, not ts-auth.
    A write round sends at most four timed requests with one token, so a
    token needs that long left. ts-auth issues one-minute tokens: a larger
    margin made every round exchange the key inside its own timeout. */
const tokens = new WeakMap<typeof fetch, Map<string, { token: string; expires: number }>>();
const TOKEN_MARGIN_MS = 4 * CANARY_TIMEOUT_MS;
/** Exchanges in flight, shared by concurrent probes of the same key and scope. */
const exchanges = new WeakMap<typeof fetch, Map<string, Promise<string>>>();

/** Forget a config's token, so a failed round exchanges the key again. */
export function forgetToken(config: Pick<CanaryConfig, "key">, fetcher: typeof fetch): void {
  const cached = tokens.get(fetcher);
  if (cached) for (const name of cached.keys()) if (name.endsWith(` ${config.key}`)) cached.delete(name);
}

/** Exchange only at ts-auth; the long-lived key is never sent to TerraBase. */
export async function readToken(
  config: Pick<CanaryConfig, "issuer" | "project" | "key">,
  fetcher: typeof fetch,
  scope = "sql:read",
): Promise<string> {
  const name = `${config.issuer} ${config.project} ${scope} ${config.key}`;
  const hit = tokens.get(fetcher)?.get(name);
  if (hit && hit.expires > Date.now()) return hit.token;
  // Every region's probe misses in the same round; ts-auth serialises one key's
  // exchanges, so a shared exchange keeps the later probes inside their timeout.
  let pending = exchanges.get(fetcher);
  if (!pending) exchanges.set(fetcher, (pending = new Map()));
  let exchange = pending.get(name);
  if (!exchange) {
    const shared = pending;
    exchange = exchangeToken(config, fetcher, scope, name).finally(() => shared.delete(name));
    pending.set(name, exchange);
  }
  return exchange;
}

const CREDENTIAL_ERRORS: ReadonlySet<string> = new Set([
  "invalid_client",
  "invalid_key",
  "invalid_token",
  "key_revoked",
  "key_expired",
  "invalid_scope",
  "insufficient_scope",
  "forbidden_scope",
  "invalid_target",
  "unauthorized_client",
]);

function tokenError(error: unknown): never {
  if (
    error instanceof CanaryError &&
    [400, 401, 403].includes(error.status ?? 0) &&
    CREDENTIAL_ERRORS.has(error.code ?? "")
  )
    throw new CanaryCredentialError();
  throw error;
}

async function exchangeToken(
  config: Pick<CanaryConfig, "issuer" | "project" | "key">,
  fetcher: typeof fetch,
  scope: string,
  name: string,
): Promise<string> {
  const body = (await canaryJson(
    `${config.issuer}/oauth/token`,
    {
      method: "POST",
      headers: { "content-type": "application/x-www-form-urlencoded", "user-agent": USER_AGENT },
      body: new URLSearchParams({
        grant_type: "client_credentials",
        client_id: projectKeyId(config.key),
        client_secret: config.key,
        project: config.project,
        scope,
      }).toString(),
      cache: "no-store",
    },
    fetcher,
    [200],
  )
    .catch(tokenError)
    .catch((error: unknown) => {
      if (error instanceof CanaryMonitorError) throw error;
      if (error instanceof CanaryError) throw new CanaryError(`Token exchange: ${error.message}`, error.status);
      throw error;
    })) as { access_token?: unknown; token_type?: unknown; expires_in?: unknown };
  if (
    typeof body.access_token !== "string" ||
    !/^[A-Za-z0-9_-]+\.[A-Za-z0-9_-]+\.[A-Za-z0-9_-]+$/.test(body.access_token) ||
    body.token_type !== "Bearer" ||
    typeof body.expires_in !== "number" ||
    !Number.isSafeInteger(body.expires_in) ||
    body.expires_in <= 0
  )
    throw new CanaryError("Token exchange: invalid response");
  let cached = tokens.get(fetcher);
  if (!cached) tokens.set(fetcher, (cached = new Map()));
  cached.set(name, { token: body.access_token, expires: Date.now() + body.expires_in * 1000 - TOKEN_MARGIN_MS });
  return body.access_token;
}

/** The issuer's token route, the URL of the Authentication token probe. */
export const isTokenRoute = (url: string): boolean => url.endsWith("/oauth/token");

/** A fresh client-credentials exchange, uncached: how long ts-auth takes to
    issue the token a customer's server asks for. */
export async function runTokenProbe(config: CanaryConfig, fetcher: typeof fetch = fetch): Promise<CanaryResult> {
  const started = Date.now();
  try {
    const body = (await canaryJson(
      `${config.issuer}/oauth/token`,
      {
        method: "POST",
        headers: { "content-type": "application/x-www-form-urlencoded", "user-agent": USER_AGENT },
        body: new URLSearchParams({
          grant_type: "client_credentials",
          client_id: projectKeyId(config.key),
          client_secret: config.key,
          project: config.project,
          scope: "sql:read",
        }).toString(),
        cache: "no-store",
      },
      fetcher,
      [200],
    ).catch(tokenError)) as { access_token?: unknown; token_type?: unknown };
    if (typeof body.access_token !== "string" || body.token_type !== "Bearer")
      throw new CanaryError("Invalid response");
    return { ok: true, latencyMs: Date.now() - started, error: null };
  } catch (error) {
    if (!(error instanceof CanaryError)) throw error;
    return { ok: error instanceof CanaryMonitorError ? null : false, latencyMs: null, error: error.message };
  }
}

/** A fetcher that keeps its slowest request: a canary's latency, so each
    TerraBase request is judged like a single check, not the sum of its steps. */
export function timed(fetcher: typeof fetch): { fetch: typeof fetch; slowest: () => number } {
  let slowest = 0;
  return {
    fetch: async (input, init) => {
      const started = Date.now();
      try {
        return await fetcher(input, init);
      } finally {
        slowest = Math.max(slowest, Date.now() - started);
      }
    },
    slowest: () => slowest,
  };
}

/** Read a pre-created fixture; the round marker remains in the coordinator's call signature. */
export async function runCanary(
  config: CanaryConfig,
  _marker: string,
  fetcher: typeof fetch = fetch,
): Promise<CanaryResult> {
  try {
    if (!isCanaryConfig(config)) throw new CanaryError("Invalid probe config");
    const token = await readToken(config, fetcher);
    const terrabase = timed(fetcher);
    const read = await canaryJson(
      config.origin + sqlPath(config),
      {
        method: "POST",
        headers: {
          authorization: `Bearer ${token}`,
          "content-type": "application/json",
          "user-agent": USER_AGENT,
          "X-TerraScale-Region-Pin": config.region,
        },
        body: JSON.stringify({
          statements: [{ sql: `SELECT id FROM ${CANARY_TABLE} WHERE id = ?`, args: [CANARY_ROW] }],
        }),
        cache: "no-store",
      },
      terrabase.fetch,
      [200],
    );
    if (!holdsRow(read, CANARY_ROW)) throw new CanaryError("Invalid response");
    return { ok: true, latencyMs: terrabase.slowest(), error: null };
  } catch (error) {
    if (!(error instanceof CanaryError)) throw error;
    forgetToken(config, fetcher);
    return { ok: error instanceof CanaryMonitorError ? null : false, latencyMs: null, error: error.message };
  }
}

/** The round's sample of a canary endpoint, with hysteresis like any other:
    one failed round shows as degraded, two as an outage. */
export function canarySample(
  endpointId: string,
  result: CanaryResult,
  streak: number,
): { sample: Sample; streak: number } {
  let round: TimelineState;
  if (result.ok === null) round = "none";
  else if (!result.ok) round = "major_outage";
  else if ((result.latencyMs ?? 0) > SLOW_MS) round = "degraded";
  else round = "operational";
  const settled = settle(round, streak);
  return {
    sample: {
      endpointId,
      state: settled.state,
      ok: result.ok ? 1 : 0,
      total: result.ok === null ? 0 : 1,
      latencyMs: result.latencyMs,
      results: [
        { v: CANARY_VANTAGE, ok: result.ok === null ? null : result.ok ? 1 : 0, ms: result.latencyMs, e: result.error },
      ],
      values: null,
    },
    streak: settled.streak,
  };
}
