// Shared vocabulary for the data-families pages (DB-06,
// TerraScale/TerraBaseV2#20): the closed FamilyKind set mirrored from the
// engine, the family scope-selector grammar the typed `family.read` operation
// is addressed with pre-SE-08, typed state parsing, bounded entry paging, the
// time-series range view bound, and the engine's semantics labels. Client-safe
// by construction — the only generated-client imports are types — so the
// server routes and the client islands speak exactly one vocabulary.
//
// The typed family surface today (contract terrabase.api.v2) carries exactly
// two request fields, `kind` and `scope` (a string): the pre-SE-08 way a
// per-document family panel addresses one bounded entry window is a scope
// selector over the document identity (`doc/<collection>/<documentId>` plus a
// query window), exactly as the documents pages carried explicit LIMIT/OFFSET
// windows in the v1 query text before SE-08 lands typed paging fields. The
// families fixture composes the unmodified fixture core with this grammar; the
// real host owns the semantics when it lands, and any drift reconciles there.
//
// The semantics labels are the engine's public family payload docs verbatim
// (src/families/payloads.rs, guarded by tests/families-page.test.mts) — the
// panels never paraphrase family semantics.

import type { Json } from "@/api/generated/client";

/** The closed FamilyKind wire set, engine family_key order. */
export const FAMILY_KINDS = [
  "attachments",
  "counters",
  "time-series",
  "revisions",
  "compare-exchange",
  "identities",
  "lifecycle",
  "archival",
  "validation",
  "tombstones",
] as const;

export type FamilyKindWire = (typeof FAMILY_KINDS)[number];

/** The kinds this page renders through typed family reads. */
export const VIEWABLE_FAMILY_KINDS: readonly FamilyKindWire[] = [
  "attachments",
  "counters",
  "time-series",
];

/** The remaining kinds: explicitly not yet viewable, never silently missing. */
export const NOT_YET_VIEWABLE_FAMILY_KINDS: readonly FamilyKindWire[] = FAMILY_KINDS.filter(
  (kind) => !VIEWABLE_FAMILY_KINDS.includes(kind),
).sort() as readonly FamilyKindWire[];

/** The honest placeholder label for the not-yet-viewable kinds. */
export const NOT_YET_VIEWABLE_LABEL = "Not yet viewable";

// Engine semantics labels, verbatim from src/families/payloads.rs.

/** CounterOperation::Increment doc. */
export const COUNTER_INCREMENT_SEMANTICS =
  "Add a signed delta to a counter, creating it from zero.";

/** CounterOperation::Delete doc. */
export const COUNTER_DELETE_SEMANTICS = "Delete a counter by name.";

/** TimeSeriesRange struct doc. */
export const TIME_SERIES_RANGE_SEMANTICS = "An inclusive timestamp range in epoch milliseconds.";

/** AttachmentMutation::Store doc (first sentence). */
export const ATTACHMENT_STORE_SEMANTICS = "Record attachment metadata.";

/** AttachmentMutation::Store doc (duplicate-rule sentence). */
export const ATTACHMENT_DUPLICATE_SEMANTICS =
  "A name that already exists is a duplicate unless `replace` is set.";

/** Entries per family panel page; also the incremental load-more window. */
export const FAMILY_ENTRY_PAGE_SIZE = 10;

/** Bounded view window: entry pages retained client-side before Load more disables. */
export const MAX_LOADED_ENTRY_PAGES = 10;

/** Upper bound a scope window may request (the fixture clamps above this). */
export const MAX_ENTRY_WINDOW_LIMIT = 500;

/** View bound on a selected time-series range span (7 days), an explicit studio bound. */
export const TIME_SERIES_RANGE_VIEW_BOUND_MS = 604_800_000;

/** One bounded entry window over a family's entries. */
export interface EntryWindow {
  readonly limit: number;
  readonly offset: number;
}

/** An inclusive epoch-millisecond range selection (the engine's TimeSeriesRange). */
export interface TimeSeriesRangeSelection {
  readonly start: number;
  readonly end: number;
}

function requireIdentity(kind: string, value: string): string {
  const trimmed = value.trim();
  if (trimmed.length === 0) {
    throw new Error(`a non-empty ${kind} is required for a family scope`);
  }
  return trimmed;
}

function requireWindow(window: EntryWindow): EntryWindow {
  const { limit, offset } = window;
  if (!Number.isInteger(limit) || limit < 1 || limit > MAX_ENTRY_WINDOW_LIMIT) {
    throw new Error(`the entry window limit must be an integer in [1, ${MAX_ENTRY_WINDOW_LIMIT}]`);
  }
  if (!Number.isInteger(offset) || offset < 0) {
    throw new Error("the entry window offset must be a non-negative integer");
  }
  return { limit, offset };
}

function requireRange(range: TimeSeriesRangeSelection): TimeSeriesRangeSelection {
  const { start, end } = range;
  if (!Number.isInteger(start) || !Number.isInteger(end)) {
    throw new Error("the time-series range bounds must be epoch-millisecond integers");
  }
  if (start > end) {
    throw new Error("the time-series range start must not be later than its end");
  }
  return { start, end };
}

/**
 * The document scope selector every family read of this page addresses:
 * `doc/<encoded collection>/<encoded document id>`.
 */
export function documentScope(collection: string, documentId: string): string {
  const name = requireIdentity("collection", collection);
  const id = requireIdentity("document id", documentId);
  return `doc/${encodeURIComponent(name)}/${encodeURIComponent(id)}`;
}

/**
 * The scope for one bounded window of a document's counter group
 * (`family.read` kind "counters").
 */
export function counterEntriesScope(
  collection: string,
  documentId: string,
  window: EntryWindow,
): string {
  const bounded = requireWindow(window);
  return `${documentScope(collection, documentId)}?limit=${bounded.limit}&offset=${bounded.offset}`;
}

/**
 * The scope for one bounded window of a document's attachment metadata
 * (`family.read` kind "attachments"): the same document window scope; the
 * request's kind distinguishes the family.
 */
export function attachmentEntriesScope(
  collection: string,
  documentId: string,
  window: EntryWindow,
): string {
  return counterEntriesScope(collection, documentId, window);
}

/** The scope for a document's time-series catalog (`family.read` kind "time-series"). */
export function timeSeriesCatalogScope(collection: string, documentId: string): string {
  return `${documentScope(collection, documentId)}/series`;
}

/**
 * The scope for one bounded window of one series' entries, with the selected
 * inclusive range riding the scope when one is set (an absent range reads the
 * whole series, bounded by the window).
 */
export function timeSeriesEntriesScope(
  collection: string,
  documentId: string,
  seriesName: string,
  window: EntryWindow,
  range?: TimeSeriesRangeSelection | null,
): string {
  const name = requireIdentity("series name", seriesName);
  const bounded = requireWindow(window);
  const selected = range === undefined || range === null ? null : requireRange(range);
  const base = `${documentScope(collection, documentId)}/series/${encodeURIComponent(name)}`;
  if (selected === null) {
    return `${base}?limit=${bounded.limit}&offset=${bounded.offset}`;
  }
  return `${base}?limit=${bounded.limit}&offset=${bounded.offset}&start=${selected.start}&end=${selected.end}`;
}

/** Route href for one document's family panels. */
export function familiesHref(collection: string, documentId: string): string {
  return `/families/${encodeURIComponent(collection.trim())}/${encodeURIComponent(documentId.trim())}`;
}

/** The span of a range selection in milliseconds. */
export function rangeSpanMs(range: TimeSeriesRangeSelection): number {
  return range.end - range.start;
}

/** Whether a selected range stays within the view's explicit span bound. */
export function rangeWithinViewBound(range: TimeSeriesRangeSelection): boolean {
  return rangeSpanMs(range) <= TIME_SERIES_RANGE_VIEW_BOUND_MS;
}

/**
 * Strictly parses an epoch-millisecond input ("1690000000000"): trimmed
 * integers only, no exponent or fractional forms. `null` when unparseable.
 */
export function parseEpochMsInput(text: string): number | null {
  const trimmed = text.trim();
  if (!/^-?\d+$/.test(trimmed)) {
    return null;
  }
  const value = Number(trimmed);
  return Number.isSafeInteger(value) ? value : null;
}

/**
 * Checks a typed family read's echo (client islands use this for their
 * incremental reads): returns a failure reason when the answer does not name
 * the requested kind and scope, `null` when the echo is exact.
 */
export function familyEchoMismatch(
  result: { readonly kind: string; readonly scope: string },
  kind: FamilyKindWire,
  scope: string,
): string | null {
  if (result.kind !== kind) {
    return `The family read sent for kind ${kind} was answered as kind ${JSON.stringify(result.kind)}.`;
  }
  if (result.scope !== scope) {
    return `The family read for kind ${kind} was answered for a different scope than the one requested.`;
  }
  return null;
}

// ---------------------------------------------------------------------------
// Typed state parsing
// ---------------------------------------------------------------------------

/** One parsed family state: recognized with a typed value, or unrecognized (fail closed). */
export type FamilyStateParse<TState> =
  | { readonly kind: "recognized"; readonly state: TState; readonly malformed: number }
  | { readonly kind: "unrecognized" };

function isJsonObject(value: unknown): value is Record<string, unknown> {
  return typeof value === "object" && value !== null && !Array.isArray(value);
}

function isNonNegativeInteger(value: unknown): value is number {
  return typeof value === "number" && Number.isInteger(value) && value >= 0;
}

/** One counter name with its signed value, or its absence (CounterEntry). */
export interface CounterFamilyEntry {
  readonly name: string;
  readonly value: number | null;
}

export interface CounterFamilyState {
  readonly entries: readonly CounterFamilyEntry[];
  readonly matched: number;
}

/**
 * Parses a counters family state. Entries that are not the typed shape are
 * counted as malformed — rejected visibly, never silently dropped or coerced;
 * a state that is not the window shape at all is unrecognized.
 */
export function parseCounterFamilyState(state: Json): FamilyStateParse<CounterFamilyState> {
  if (
    !isJsonObject(state) ||
    !Array.isArray(state.entries) ||
    !isNonNegativeInteger(state.matched)
  ) {
    return { kind: "unrecognized" };
  }
  const entries: CounterFamilyEntry[] = [];
  let malformed = 0;
  for (const candidate of state.entries) {
    if (
      isJsonObject(candidate) &&
      typeof candidate.name === "string" &&
      (candidate.value === null ||
        (typeof candidate.value === "number" && Number.isInteger(candidate.value)))
    ) {
      entries.push({ name: candidate.name, value: candidate.value });
    } else {
      malformed += 1;
    }
  }
  return { kind: "recognized", state: { entries, matched: state.matched }, malformed };
}

/** One attachment's metadata (AttachmentMetadata). */
export interface AttachmentFamilyEntry {
  readonly name: string;
  readonly contentType: string;
  readonly size: number;
  readonly hash: string;
}

export interface AttachmentFamilyState {
  readonly entries: readonly AttachmentFamilyEntry[];
  readonly matched: number;
}

/** Parses an attachments family state with the same fail-closed discipline. */
export function parseAttachmentFamilyState(state: Json): FamilyStateParse<AttachmentFamilyState> {
  if (
    !isJsonObject(state) ||
    !Array.isArray(state.entries) ||
    !isNonNegativeInteger(state.matched)
  ) {
    return { kind: "unrecognized" };
  }
  const entries: AttachmentFamilyEntry[] = [];
  let malformed = 0;
  for (const candidate of state.entries) {
    if (
      isJsonObject(candidate) &&
      typeof candidate.name === "string" &&
      typeof candidate.contentType === "string" &&
      isNonNegativeInteger(candidate.size) &&
      typeof candidate.hash === "string"
    ) {
      entries.push({
        name: candidate.name,
        contentType: candidate.contentType,
        size: candidate.size,
        hash: candidate.hash,
      });
    } else {
      malformed += 1;
    }
  }
  return { kind: "recognized", state: { entries, matched: state.matched }, malformed };
}

/** One series of a document's time-series catalog with its entry count. */
export interface TimeSeriesSeriesSummary {
  readonly name: string;
  readonly matched: number;
}

export interface TimeSeriesCatalogState {
  readonly series: readonly TimeSeriesSeriesSummary[];
}

/** Parses a document's time-series catalog state with the same discipline. */
export function parseTimeSeriesCatalogState(state: Json): FamilyStateParse<TimeSeriesCatalogState> {
  if (!isJsonObject(state) || !Array.isArray(state.series)) {
    return { kind: "unrecognized" };
  }
  const series: TimeSeriesSeriesSummary[] = [];
  let malformed = 0;
  for (const candidate of state.series) {
    if (
      isJsonObject(candidate) &&
      typeof candidate.name === "string" &&
      isNonNegativeInteger(candidate.matched)
    ) {
      series.push({ name: candidate.name, matched: candidate.matched });
    } else {
      malformed += 1;
    }
  }
  return { kind: "recognized", state: { series }, malformed };
}

/** One time-series entry (TimeSeriesEntry): timestamp, values, optional tag. */
export interface TimeSeriesFamilyEntry {
  readonly timestamp: number;
  readonly values: readonly number[];
  readonly tag: string | null;
}

export interface TimeSeriesRangeEcho {
  readonly start: number;
  readonly end: number;
}

export interface TimeSeriesPageState {
  readonly name: string;
  readonly entries: readonly TimeSeriesFamilyEntry[];
  readonly matched: number;
  readonly range: TimeSeriesRangeEcho | null;
}

function parseRangeEcho(value: unknown): TimeSeriesRangeEcho | "invalid" | "absent" {
  if (value === null || value === undefined) {
    return "absent";
  }
  if (
    isJsonObject(value) &&
    Number.isInteger(value.start) &&
    Number.isInteger(value.end) &&
    value.start <= value.end
  ) {
    return { start: value.start, end: value.end };
  }
  return "invalid";
}

/** Parses one series' time-series page state with the same discipline. */
export function parseTimeSeriesPageState(state: Json): FamilyStateParse<TimeSeriesPageState> {
  if (
    !isJsonObject(state) ||
    typeof state.name !== "string" ||
    !Array.isArray(state.entries) ||
    !isNonNegativeInteger(state.matched)
  ) {
    return { kind: "unrecognized" };
  }
  const range = parseRangeEcho(state.range);
  if (range === "invalid") {
    return { kind: "unrecognized" };
  }
  const entries: TimeSeriesFamilyEntry[] = [];
  let malformed = 0;
  for (const candidate of state.entries) {
    if (
      isJsonObject(candidate) &&
      Number.isInteger(candidate.timestamp) &&
      Array.isArray(candidate.values) &&
      candidate.values.length > 0 &&
      candidate.values.every((value) => typeof value === "number" && Number.isFinite(value)) &&
      (candidate.tag === null || candidate.tag === undefined || typeof candidate.tag === "string")
    ) {
      entries.push({
        timestamp: candidate.timestamp,
        values: candidate.values,
        tag: typeof candidate.tag === "string" ? candidate.tag : null,
      });
    } else {
      malformed += 1;
    }
  }
  return {
    kind: "recognized",
    state: {
      name: state.name,
      entries,
      matched: state.matched,
      range: range === "absent" ? null : range,
    },
    malformed,
  };
}

// ---------------------------------------------------------------------------
// Bounded entry paging
// ---------------------------------------------------------------------------

export type EntryPagingStatus =
  | {
      readonly kind: "more";
      readonly loadedEntries: number;
      readonly matched: number;
      readonly nextOffset: number;
    }
  | { readonly kind: "complete"; readonly loadedEntries: number; readonly matched: number }
  | {
      readonly kind: "window-capped";
      readonly loadedEntries: number;
      readonly matched: number;
      readonly retainedPages: number;
    };

/**
 * Classifies a family panel's paging state: more windows to load, every
 * matched entry loaded, or the bounded view window reached (the explicit
 * client-memory limit — never an unbounded accumulator).
 */
export function entryPagingStatus(load: {
  readonly loadedEntries: number;
  readonly matched: number;
  readonly pageSize: number;
  readonly maxPages: number;
}): EntryPagingStatus {
  const { loadedEntries, matched, pageSize, maxPages } = load;
  if (loadedEntries >= matched) {
    return { kind: "complete", loadedEntries, matched };
  }
  const loadedPages = Math.ceil(loadedEntries / pageSize);
  if (loadedPages >= maxPages) {
    return { kind: "window-capped", loadedEntries, matched, retainedPages: maxPages };
  }
  return { kind: "more", loadedEntries, matched, nextOffset: loadedEntries };
}
