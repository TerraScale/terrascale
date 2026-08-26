// Pure view model for the query console (DB-05).
//
// No imports: the module is loaded by node:test through Node's type stripping
// (tests/queries.test.mjs), so it must stay dependency-free and erasable. The
// interfaces below are structural subsets of the generated client types in
// src/api/generated/client.ts — the generated types satisfy them without any
// adapter.

/** A typed result row as the contract defines it (QueryRowResult). */
export interface ConsoleRowLike {
  readonly document: {
    readonly id: string;
    readonly collection: string;
    readonly version: number;
    readonly value: unknown;
  };
  readonly projected: readonly { readonly name: string; readonly value: unknown }[];
}

/** A staleness observation as the contract defines it (StaleIndexObservation). */
export interface StalenessObservationLike {
  readonly indexId: string;
  readonly reason: string;
}

/** A query result as the contract defines it (QueryResult). */
export interface QueryResultLike {
  readonly rows: readonly ConsoleRowLike[];
  readonly matched: number;
  readonly staleness: readonly StalenessObservationLike[];
}

/** A typed API error as the contract defines it (ApiError, minus loginUrl). */
export interface TypedErrorLike {
  readonly code: string;
  readonly message: string;
  readonly retryable: boolean;
}

/** Bounded page sizes the console offers; paging is never unbounded. */
export const PAGE_SIZES = [5, 10, 25] as const;

/** The page size a fresh console starts at. */
export const DEFAULT_PAGE_SIZE: number = 10;

/**
 * The collection the confirmed save-query mutation writes to, through one
 * typed `document.write`. Studio-owned operator data, never an engine
 * namespace: the engine's reserved namespaces are index namespaces, not
 * collections.
 */
export const SAVED_QUERIES_COLLECTION = "studio-saved-queries";

/** Outcome while no query has run yet. */
export interface IdleOutcome {
  readonly kind: "idle";
}

/** Outcome of one executed typed query operation. */
export interface ResultsOutcome {
  readonly kind: "results";
  readonly queryText: string;
  readonly rows: readonly ConsoleRowLike[];
  /** Rows the host actually returned (rows.length, named for the operator). */
  readonly returnedRows: number;
  /** Rows the predicate matched before paging, as the host reported it. */
  readonly matched: number;
  /** The host's per-index staleness observations, verbatim. */
  readonly staleness: readonly StalenessObservationLike[];
  /**
   * Wall-clock window of this one execution in milliseconds. It is a single
   * execution's elapsed time shown to the operator — not a benchmark, not a
   * performance claim, and never comparable across runs (charter
   * PD-2026-08-26-03).
   */
  readonly elapsedMs: number;
  readonly executedAt: string;
}

/** Outcome of a typed operation the host answered with an error. */
export interface ErrorOutcome {
  readonly kind: "error";
  readonly code: string;
  readonly message: string;
  readonly retryable: boolean;
}

/** Everything the console can render after one operation. */
export type ConsoleOutcome = IdleOutcome | ResultsOutcome | ErrorOutcome;

/** Receipt of a confirmed mutation, as the contract defines it (CommitReceipt). */
export interface MutationReceipt {
  readonly operationId: string;
  readonly collection: string;
  readonly documentId: string;
  readonly version: number;
  readonly committedAt: string;
}

/** Outcome of the confirmed save-query mutation. */
export type MutationOutcome =
  | { readonly kind: "idle" }
  | { readonly kind: "receipt"; readonly receipt: MutationReceipt }
  | ErrorOutcome;

export type QueryTextResult =
  | { readonly ok: true; readonly text: string }
  | { readonly ok: false; readonly reason: "empty" };

/**
 * Normalizes operator query text. Whitespace is trimmed; empty text is
 * rejected client-side (the engine's own `QueryInputError::EmptyText` is the
 * server-side analog) so an empty dispatch never leaves the console.
 */
export function normalizeQueryText(raw: string): QueryTextResult {
  const text = raw.trim();
  if (text.length === 0) {
    return { ok: false, reason: "empty" };
  }
  return { ok: true, text };
}

/** Resolves a page size to the closed set, falling back to the default. */
export function normalizePageSize(raw: number | null | undefined): number {
  if (typeof raw !== "number" || !Number.isFinite(raw)) {
    return DEFAULT_PAGE_SIZE;
  }
  return (PAGE_SIZES as readonly number[]).includes(raw) ? raw : DEFAULT_PAGE_SIZE;
}

/** Number of pages a row count spans; an empty result is exactly one empty page. */
export function pageCount(rowCount: number, pageSize: number): number {
  if (pageSize <= 0) {
    return 1;
  }
  return Math.max(1, Math.ceil(rowCount / pageSize));
}

/** Clamps a 1-based page number into [1, pages]. */
export function clampPage(page: number, pages: number): number {
  if (!Number.isFinite(page)) {
    return 1;
  }
  return Math.min(pages, Math.max(1, Math.trunc(page)));
}

/** The slice of rows one page shows. */
export function slicePage<T>(rows: readonly T[], pageSize: number, page: number): T[] {
  const bounded = clampPage(page, pageCount(rows.length, pageSize));
  const start = (bounded - 1) * pageSize;
  return rows.slice(start, start + pageSize);
}

/** Everything the pager renders, derived once so the UI cannot disagree with itself. */
export interface PagerDescriptor {
  readonly rowCount: number;
  readonly pageSize: number;
  readonly page: number;
  readonly pageCount: number;
  /** 1-based index of the first row shown, or null when the page is empty. */
  readonly firstRowIndex: number | null;
  /** 1-based index of the last row shown, or null when the page is empty. */
  readonly lastRowIndex: number | null;
  readonly hasPrev: boolean;
  readonly hasNext: boolean;
}

export function pagerDescriptor(rowCount: number, pageSize: number, page: number): PagerDescriptor {
  const pages = pageCount(rowCount, pageSize);
  const bounded = clampPage(page, pages);
  const empty = rowCount === 0;
  const first = empty ? null : (bounded - 1) * pageSize + 1;
  const last = empty ? null : Math.min(bounded * pageSize, rowCount);
  return {
    rowCount,
    pageSize,
    page: bounded,
    pageCount: pages,
    firstRowIndex: first,
    lastRowIndex: last,
    hasPrev: bounded > 1,
    hasNext: bounded < pages,
  };
}

/**
 * Derives the console's results state from one typed query result. Staleness
 * observations are carried verbatim: the studio never rewords the host's
 * evidence.
 */
export function executionOutcomeFromResult(
  result: QueryResultLike,
  context: { queryText: string; elapsedMs: number; executedAt: string },
): ResultsOutcome {
  return {
    kind: "results",
    queryText: context.queryText,
    rows: result.rows,
    returnedRows: result.rows.length,
    matched: result.matched,
    staleness: result.staleness,
    elapsedMs: context.elapsedMs,
    executedAt: context.executedAt,
  };
}

/**
 * Derives the console's error state from one typed API error. The host's code,
 * message, and retryability pass through unmodified; a missing code is the
 * client's own typed `error.code-missing`, matching the generated client.
 */
export function errorOutcome(error: TypedErrorLike): ErrorOutcome {
  return {
    kind: "error",
    code:
      typeof error.code === "string" && error.code.length > 0 ? error.code : "error.code-missing",
    message: typeof error.message === "string" ? error.message : "",
    retryable: error.retryable === true,
  };
}
