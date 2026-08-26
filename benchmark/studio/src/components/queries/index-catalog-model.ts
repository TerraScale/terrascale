// Pure view model for the index catalog view (DB-05).
//
// No imports: the module is loaded by node:test through Node's type stripping
// (tests/queries.test.mjs), so it stays dependency-free and erasable. The
// interfaces are structural subsets of the generated client types in
// src/api/generated/client.ts.

import type { ConsoleRowLike } from "./query-console-model";

/**
 * The catalog read is one typed `query.execute` operation over a reserved,
 * operator-visible query text. The studio API contract has no dedicated index
 * catalog operation yet (the engine's `IndexCatalog` in src/index/mod.rs is
 * crate-private), so the view rides the query operation the contract does
 * carry — the same way the DB-02 fixture surfaces collection summaries — and
 * renders whatever the host returns, including nothing.
 */
export const CATALOG_QUERY_TEXT = "from __index_catalog__";

/** The collection the catalog read returns rows from. */
export const CATALOG_COLLECTION = "__index_catalog__";

/** Index states the view labels. Anything else is unknown, never coerced. */
export type IndexStatus = "fresh" | "stale" | "rebuilding" | "unknown";

/** Operator-facing labels, one per status, all distinct. */
export const INDEX_STATUS_LABEL: Readonly<Record<IndexStatus, string>> = {
  fresh: "Fresh",
  stale: "Stale",
  rebuilding: "Rebuilding",
  unknown: "Unknown",
};

/** One index as the catalog view renders it. */
export interface IndexCatalogEntry {
  readonly namespace: string;
  readonly status: IndexStatus;
  /** The host's own status value, carried even when unrecognized. */
  readonly rawStatus: string;
  /** The host's evidence for a non-fresh state, verbatim, or null. */
  readonly reason: string | null;
}

/** The parsed catalog plus an honest count of rows that did not parse. */
export interface IndexCatalog {
  readonly entries: readonly IndexCatalogEntry[];
  readonly unparsedCount: number;
  readonly statusCounts: Readonly<Record<IndexStatus, number>>;
}

/**
 * Classifies a host status value. Only the exact known spellings classify;
 * anything else — including near misses and different casing — is unknown so
 * the view can show the host's own value instead of guessing a state.
 */
export function classifyIndexStatus(raw: unknown): IndexStatus {
  if (typeof raw !== "string") {
    return "unknown";
  }
  if (raw === "fresh" || raw === "stale" || raw === "rebuilding") {
    return raw;
  }
  return "unknown";
}

/**
 * Parses one catalog row. A row without a non-empty string namespace does not
 * parse; the caller counts it as unparsed rather than dropping it silently.
 */
export function parseCatalogRow(
  row: ConsoleRowLike,
): { ok: true; entry: IndexCatalogEntry } | { ok: false; reason: "namespace-missing" } {
  const value = row.document.value;
  if (typeof value !== "object" || value === null || Array.isArray(value)) {
    return { ok: false, reason: "namespace-missing" };
  }
  const namespace = (value as Record<string, unknown>).namespace;
  if (typeof namespace !== "string" || namespace.trim().length === 0) {
    return { ok: false, reason: "namespace-missing" };
  }
  const record = value as Record<string, unknown>;
  const rawStatus = typeof record.status === "string" ? record.status : "";
  const reason =
    typeof record.reason === "string" && record.reason.length > 0 ? record.reason : null;
  return {
    ok: true,
    entry: {
      namespace,
      status: classifyIndexStatus(rawStatus),
      rawStatus,
      reason,
    },
  };
}

/** Builds the catalog view model from the rows a catalog read returned. */
export function buildIndexCatalog(rows: readonly ConsoleRowLike[]): IndexCatalog {
  const entries: IndexCatalogEntry[] = [];
  let unparsedCount = 0;
  const statusCounts: Record<IndexStatus, number> = {
    fresh: 0,
    stale: 0,
    rebuilding: 0,
    unknown: 0,
  };
  for (const row of rows) {
    const parsed = parseCatalogRow(row);
    if (!parsed.ok) {
      unparsedCount += 1;
      continue;
    }
    entries.push(parsed.entry);
    statusCounts[parsed.entry.status] += 1;
  }
  return { entries, unparsedCount, statusCounts };
}
