// The DB-04 live changes model: the pure seam behind every Live View that
// renders the backbone's change stream. Plain TypeScript, no imports, so
// node:test loads it through Node's type stripping.
//
// What lives here:
// - the closed change-kind vocabulary the feed renders: the TBEP wire
//   forms (`put`/`delete`) plus the engine backbone's selector set
//   (`document`/`index`/`counter`/`time-series`/`operation`/`database`/
//   `conflict` — src/streaming/mod.rs `ChangeKind`, SE-03). Anything else
//   fails closed, never renders as an unknown row.
// - the V1 Changes API selector mapping the filter UI offers (V1
//   `src/TerraBase.Client/Documents/Changes/`: IDocumentChanges,
//   IIndexChanges, ICounterChanges, ITimeSeriesChanges,
//   IOperationChanges; the conflict selector is V1
//   `DocumentChangeTypes.Conflict`).
// - the structural ConflictRecord content restriction: a conflict event
//   renders exactly the record's metadata keys — tenant, operations,
//   members, clocks, outcome (src/replication/mod.rs ConflictRecord) —
//   and no document body can cross, because the projection allowlist has
//   no body-bearing field.
// - the bounded, at-least-once feed list: dedup by sequence, oldest
//   evicted at the bound, duplicates counted.
//
// Non-claims: no ordering claim beyond per-subscription sequence
// monotonicity (enforced by the client); no completeness claim across a
// host shed (the drop notice is the honest statement); conflict events
// carry no collection identity, so a collection-refined filter does not
// observe them (SE-03's recorded limit, mirrored here).

/** The closed change-kind vocabulary the feed renders. */
export const CHANGE_KINDS = [
  "put",
  "delete",
  "document",
  "index",
  "counter",
  "time-series",
  "operation",
  "database",
  "conflict",
] as const;

/** One change kind of the closed vocabulary. */
export type ChangeKind = (typeof CHANGE_KINDS)[number];

/** The selector groups the filter UI offers (V1 Changes API parity). */
export const FILTER_KINDS: readonly {
  readonly id: ChangeKind;
  readonly label: string;
  readonly v1Selector: string;
}[] = [
  {
    id: "document",
    label: "Documents",
    v1Selector: "ForAllDocuments / ForDocument / ForDocumentsStartingWith / ForDocumentsInCollection",
  },
  { id: "index", label: "Indexes", v1Selector: "ForAllIndexes / ForIndex" },
  { id: "counter", label: "Counters", v1Selector: "ForAllCounters / ForCounter" },
  { id: "time-series", label: "Time series", v1Selector: "ForAllTimeSeries / ForTimeSeries" },
  { id: "operation", label: "Operations", v1Selector: "ForAllOperations / ForOperationId" },
  { id: "database", label: "Database", v1Selector: "database-level changes" },
  { id: "conflict", label: "Conflicts", v1Selector: "DocumentChangeTypes.Conflict" },
];

/** The ConflictRecord metadata keys a conflict event may surface — the allowlist. */
export const CONFLICT_METADATA_KEYS = [
  "tenant",
  "operations",
  "members",
  "clocks",
  "outcome",
] as const;

/** The conflict metadata a conflict event carries: content-free by construction. */
export interface ConflictMetadata {
  readonly tenant: string;
  readonly operations: readonly string[];
  readonly members: readonly string[];
  readonly clocks: readonly string[];
  readonly outcome: string;
}

/** One normalized change event of the feed. Content-free: identities only. */
export interface FeedEvent {
  readonly sequence: number;
  readonly operationId: string;
  readonly kind: ChangeKind;
  readonly collection: string | null;
  readonly document: string | null;
  readonly conflict: ConflictMetadata | null;
}

/** The bound of the rendered feed list: the newest identities, oldest evicted. */
export const FEED_BOUND = 200;

type NormalizeResult =
  | { readonly ok: true; readonly event: FeedEvent }
  | { readonly ok: false; readonly reason: string };

function isChangeKind(value: unknown): value is ChangeKind {
  return typeof value === "string" && (CHANGE_KINDS as readonly string[]).includes(value);
}

function asStringArray(value: unknown): readonly string[] | null {
  if (!Array.isArray(value)) {
    return null;
  }
  return value.every((item) => typeof item === "string") ? value : null;
}

/**
 * Projects one raw conflict payload onto exactly the ConflictRecord
 * metadata keys. Structural content restriction: every other key —
 * including any document body a hostile or future payload might carry —
 * is dropped, because nothing outside the allowlist is ever read.
 */
export function projectConflict(raw: unknown): ConflictMetadata {
  const record = (raw ?? {}) as Record<string, unknown>;
  const projected: { [key in (typeof CONFLICT_METADATA_KEYS)[number]]: unknown } = {
    tenant: record.tenant,
    operations: record.operations,
    members: record.members,
    clocks: record.clocks,
    outcome: record.outcome,
  };
  return {
    tenant: typeof projected.tenant === "string" ? projected.tenant : "",
    operations: asStringArray(projected.operations) ?? [],
    members: asStringArray(projected.members) ?? [],
    clocks: asStringArray(projected.clocks) ?? [],
    outcome: typeof projected.outcome === "string" ? projected.outcome : "",
  };
}

function validConflictRecord(raw: unknown): boolean {
  if (typeof raw !== "object" || raw === null) {
    return false;
  }
  const record = raw as Record<string, unknown>;
  return (
    typeof record.tenant === "string" &&
    record.tenant.length > 0 &&
    asStringArray(record.operations) !== null &&
    (asStringArray(record.operations) as readonly string[]).length > 0 &&
    asStringArray(record.members) !== null &&
    asStringArray(record.clocks) !== null &&
    typeof record.outcome === "string" &&
    record.outcome.length > 0
  );
}

/**
 * Normalizes one raw stream event. Fails closed — with a reason the UI
 * counts and surfaces — on an unknown kind, a non-finite sequence, a
 * missing operation identity, a conflict event claiming key identity, a
 * conflict event without a conflict record, and a non-conflict event
 * carrying one.
 */
export function normalizeStreamEvent(raw: unknown): NormalizeResult {
  if (typeof raw !== "object" || raw === null || Array.isArray(raw)) {
    return { ok: false, reason: "event is not an object" };
  }
  const record = raw as Record<string, unknown>;
  if (typeof record.sequence !== "number" || !Number.isFinite(record.sequence)) {
    return { ok: false, reason: "event sequence is not a finite number" };
  }
  if (typeof record.operationId !== "string" || record.operationId.length === 0) {
    return { ok: false, reason: "event operation id is missing" };
  }
  if (!isChangeKind(record.kind)) {
    return { ok: false, reason: `unknown change kind ${JSON.stringify(record.kind)}` };
  }
  const collection = typeof record.collection === "string" ? record.collection : null;
  const document = typeof record.document === "string" ? record.document : null;
  const carriesConflict = record.conflict !== undefined && record.conflict !== null;
  if (record.kind === "conflict") {
    if (collection !== null || document !== null) {
      return {
        ok: false,
        reason: "a conflict event carries no collection or document identity",
      };
    }
    if (!carriesConflict || !validConflictRecord(record.conflict)) {
      return { ok: false, reason: "a conflict event requires a conflict record" };
    }
    return {
      ok: true,
      event: {
        sequence: record.sequence,
        operationId: record.operationId,
        kind: record.kind,
        collection: null,
        document: null,
        conflict: projectConflict(record.conflict),
      },
    };
  }
  if (carriesConflict) {
    return { ok: false, reason: "only a conflict event carries a conflict record" };
  }
  return {
    ok: true,
    event: {
      sequence: record.sequence,
      operationId: record.operationId,
      kind: record.kind,
      collection,
      document,
      conflict: null,
    },
  };
}

/**
 * The selector group one kind belongs to. The TBEP wire's `put`/`delete`
 * document forms select as documents (V1 ForAllDocuments parity); every
 * backbone selector kind is its own group.
 */
export function selectorOf(kind: ChangeKind): ChangeKind {
  return kind === "put" || kind === "delete" ? "document" : kind;
}

/** Whether one event's selector group is in the selected set (empty = all). */
export function kindMatchesFilter(
  event: { readonly kind: ChangeKind },
  selected: ReadonlySet<ChangeKind>,
): boolean {
  return selected.size === 0 || selected.has(selectorOf(event.kind));
}

/**
 * Whether one event matches the collection text filter (a prefix match,
 * mirroring the V1 collection selector's scoping). Conflict events carry
 * no collection identity, so a set filter does not observe them — SE-03's
 * recorded limit, mirrored honestly in the UI.
 */
export function collectionMatches(
  event: { readonly collection: string | null },
  filter: string,
): boolean {
  const needle = filter.trim().toLowerCase();
  if (needle.length === 0) {
    return true;
  }
  return event.collection !== null && event.collection.toLowerCase().startsWith(needle);
}

export interface FeedAppend {
  readonly feed: readonly FeedEvent[];
  readonly duplicate: boolean;
}

/**
 * Appends one event to the bounded feed list: a sequence already present
 * is a counted duplicate (at-least-once identities), never re-surfaced;
 * the oldest event is evicted at the bound.
 */
export function appendFeedEvent(
  feed: readonly FeedEvent[],
  event: FeedEvent,
  bound: number = FEED_BOUND,
): FeedAppend {
  if (feed.some((present) => present.sequence === event.sequence)) {
    return { feed, duplicate: true };
  }
  const next = [...feed, event];
  return { feed: next.length > bound ? next.slice(next.length - bound) : next, duplicate: false };
}

/** Counts the feed by selector group (the wire forms count as documents). */
export function feedKindCounts(feed: readonly FeedEvent[]): Record<string, number> {
  const counts: Record<string, number> = {};
  for (const kind of FILTER_KINDS) {
    counts[kind.id] = 0;
  }
  for (const event of feed) {
    const group = selectorOf(event.kind);
    counts[group] = (counts[group] ?? 0) + 1;
  }
  return counts;
}

/** The honest in-band drop statement: the count and the resume boundary. */
export function dropNotice(drop: { readonly dropped: number; readonly resumeFrom: number }): string {
  return `${drop.dropped} notification${drop.dropped === 1 ? "" : "s"} dropped by the host's bounded queue — resuming from sequence ${drop.resumeFrom} re-delivers the missed range.`;
}
