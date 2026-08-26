// Shared vocabulary for the documents pages (DB-03): the canonical typed
// query texts, typed row parsing, pretty-JSON truncation, bounded pagination
// windows, filters, and route hrefs. Client-safe by construction — the only
// generated-client imports are types — so the server routes and the client
// islands speak exactly one vocabulary.
//
// The listing surface rides the contract's single query input, the
// v1-compatible text form (QueryInput kind "v1"), until SE-08 lands the typed
// QuerySpec form: the catalog is the conformance vector `query-v1-ok` text,
// and per-collection pages carry explicit LIMIT/OFFSET windows in the same
// grammar. Server-side search/filter does not exist in the typed request, so
// the pages filter client-side over bounded loaded data and say so.

import type { CollectionSummary, DocumentRecord, Json, QueryResult } from "@/api/generated/client";

/** The catalog query text fixed by the `query-v1-ok` conformance vector. */
export const COLLECTIONS_QUERY_TEXT = "FROM collections";

/** Rows per document-list page; also the incremental load-more window. */
export const DOCUMENT_PAGE_SIZE = 10;

/** Bounded view window: pages retained client-side before Load more disables. */
export const MAX_LOADED_PAGES = 10;

/** Character limit for the rendered pretty JSON; larger documents truncate. */
export const PRETTY_JSON_CHAR_LIMIT = 4000;

/** Character limit for the single-line document preview in list rows. */
export const PREVIEW_CHAR_LIMIT = 120;

/** One page window over a collection's documents. */
export interface PageWindow {
  readonly limit: number;
  readonly offset: number;
}

/**
 * Builds the per-collection listing query text for one page window.
 *
 * The collection name is embedded in the v1 text grammar (the typed request
 * has no structured collection field); an empty name is rejected rather than
 * silently queried.
 */
export function documentPageQueryText(collection: string, pageWindow: PageWindow): string {
  const name = collection.trim();
  if (name.length === 0) {
    throw new Error("a non-empty collection name is required for a listing query");
  }
  return `FROM ${name} LIMIT ${pageWindow.limit} OFFSET ${pageWindow.offset}`;
}

export interface ParsedCollections {
  readonly summaries: CollectionSummary[];
  readonly malformed: number;
}

function isJsonObject(value: unknown): value is Record<string, unknown> {
  return typeof value === "object" && value !== null && !Array.isArray(value);
}

/**
 * Parses catalog query rows into typed collection summaries.
 *
 * Rows whose document value is not a CollectionSummary shape are counted as
 * malformed — rejected visibly, never silently dropped or coerced.
 */
export function parseCollectionSummaries(result: QueryResult): ParsedCollections {
  const summaries: CollectionSummary[] = [];
  let malformed = 0;
  for (const row of result.rows ?? []) {
    const value: unknown = row?.document?.value;
    if (
      isJsonObject(value) &&
      typeof value.name === "string" &&
      Number.isFinite(value.documents) &&
      Number.isFinite(value.sizeBytes)
    ) {
      summaries.push({
        name: value.name,
        documents: value.documents,
        sizeBytes: value.sizeBytes,
      });
    } else {
      malformed += 1;
    }
  }
  return { summaries, malformed };
}

export interface ParsedDocuments {
  readonly documents: DocumentRecord[];
  readonly malformed: number;
}

/**
 * Parses listing query rows into typed document records. Rows whose document
 * is not a DocumentRecord shape are counted as malformed, never coerced.
 */
export function parseDocumentRows(result: QueryResult): ParsedDocuments {
  const documents: DocumentRecord[] = [];
  let malformed = 0;
  for (const row of result.rows ?? []) {
    const candidate: unknown = row?.document;
    if (
      isJsonObject(candidate) &&
      typeof candidate.id === "string" &&
      typeof candidate.collection === "string" &&
      Number.isInteger(candidate.version) &&
      candidate.version >= 0 &&
      "value" in candidate
    ) {
      documents.push({
        id: candidate.id,
        collection: candidate.collection,
        version: candidate.version,
        value: (candidate.value ?? null) as Json,
      });
    } else {
      malformed += 1;
    }
  }
  return { documents, malformed };
}

export interface PrettyJson {
  readonly text: string;
  readonly totalChars: number;
  readonly truncated: boolean;
}

/**
 * Pretty-prints a document value for rendering, truncating at an explicit
 * character limit so a large document never renders (or ships to the client)
 * unbounded. `totalChars` carries the honest full size for the indicator.
 */
export function prettyJsonTruncated(value: Json, limit = PRETTY_JSON_CHAR_LIMIT): PrettyJson {
  const full = JSON.stringify(value, null, 2);
  if (full.length <= limit) {
    return { text: full, totalChars: full.length, truncated: false };
  }
  return { text: full.slice(0, limit), totalChars: full.length, truncated: true };
}

/** Single-line, length-bounded preview of a document value for list rows. */
export function documentPreview(value: Json, limit = PREVIEW_CHAR_LIMIT): string {
  const oneLine = JSON.stringify(value);
  if (oneLine.length <= limit) {
    return oneLine;
  }
  return `${oneLine.slice(0, limit - 1)}…`;
}

function matchesFilter(text: string, query: string): boolean {
  const trimmed = query.trim().toLowerCase();
  return trimmed.length === 0 || text.toLowerCase().includes(trimmed);
}

/** Case-insensitive substring filter over collection names. */
export function filterCollections(
  summaries: readonly CollectionSummary[],
  query: string,
): CollectionSummary[] {
  return summaries.filter((summary) => matchesFilter(summary.name, query));
}

/** Case-insensitive substring filter over document ids (loaded pages only). */
export function filterDocuments(
  documents: readonly DocumentRecord[],
  query: string,
): DocumentRecord[] {
  return documents.filter((document) => matchesFilter(document.id, query));
}

export type PaginationStatus =
  | {
      readonly kind: "more";
      readonly loadedRows: number;
      readonly matched: number;
      readonly nextPageOffset: number;
    }
  | { readonly kind: "complete"; readonly loadedRows: number; readonly matched: number }
  | {
      readonly kind: "window-capped";
      readonly loadedRows: number;
      readonly matched: number;
      readonly retainedPages: number;
    };

/**
 * Classifies the pagination state of a document list: more pages to load, the
 * collection fully loaded, or the bounded view window reached (the explicit
 * client-memory limit — never an unbounded accumulator).
 */
export function paginationStatus(load: {
  readonly loadedRows: number;
  readonly matched: number;
  readonly pageSize: number;
  readonly maxPages: number;
}): PaginationStatus {
  const { loadedRows, matched, pageSize, maxPages } = load;
  if (loadedRows >= matched) {
    return { kind: "complete", loadedRows, matched };
  }
  const loadedPages = Math.ceil(loadedRows / pageSize);
  if (loadedPages >= maxPages) {
    return { kind: "window-capped", loadedRows, matched, retainedPages: maxPages };
  }
  return { kind: "more", loadedRows, matched, nextPageOffset: loadedRows };
}

/** Route href for one collection's document list. */
export function collectionHref(collection: string): string {
  return `/documents/${encodeURIComponent(collection)}`;
}

/** Route href for one document viewer route. */
export function documentHref(collection: string, documentId: string): string {
  return `${collectionHref(collection)}/${encodeURIComponent(documentId)}`;
}
