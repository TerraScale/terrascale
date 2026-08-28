// GENERATED FILE — DO NOT EDIT BY HAND.
//
// Source of truth: contract/terrabase-api.contract.json
// Regenerate with: vp run generate:client
//
// Contract: terrabase.api.v2 (stub-first) — the pre-landing shape of
// the SE-08 C# API host surface, authored from the SE-04 frame vocabulary and
// the engine PublicOperation set in src/operations.rs. Fail closed: unknown
// envelope versions, unknown frame types, correlation mismatches, digest
// mismatches, and non-monotonic delta sequence numbers are rejected, never
// silently accepted. No credentials exist on this surface (local operator
// tool, charter PD-2026-08-26-11); the host-enforced operator session is
// honored only through the typed session.unauthenticated error and its
// loginUrl redirect.

export const TERRABASE_API_CONTRACT_NAME = "terrabase.api.v2";

export const TERRABASE_API_CONTRACT_VERSION = 2;

export type Json = null | boolean | number | string | Json[] | { [key: string]: Json };

/**
 * Replication role of the engine process the host is supervising.
 */
export type EngineRole = "single" | "leader" | "follower";

export interface StorageStats {
  /**
   * Number of collections in the catalog.
   */
  collections: number;
  /**
   * Number of documents across collections.
   */
  documents: number;
  /**
   * Current write-ahead log size in bytes.
   */
  walBytes: number;
}

export interface OperationCounters {
  reads: number;
  writes: number;
  queries: number;
  familyOperations: number;
}

export interface EngineStatus {
  /**
   * Stable engine process identity.
   */
  engineId: string;
  /**
   * Engine build version.
   */
  engineVersion: string;
  role: EngineRole;
  /**
   * ISO-8601 UTC engine start time.
   */
  startedAt: string;
  uptimeMs: number;
  storage: StorageStats;
  counters: OperationCounters;
}

export interface CollectionSummary {
  name: string;
  documents: number;
  sizeBytes: number;
}

export interface DocumentRecord {
  id: string;
  collection: string;
  /**
   * Published version from DocumentMetadata.
   */
  version: number;
  value: Json;
}

export interface DocumentReadRequest {
  collection: string;
  documentId: string;
}

export interface DocumentReadResult {
  /**
   * Absent (null) when the document does not exist.
   */
  document?: DocumentRecord | null;
}

export interface DocumentWriteInput {
  id: string;
  collection: string;
  value: Json;
}

export interface DocumentWriteRequest {
  document: DocumentWriteInput;
  /**
   * ExpectedVersion precondition; absent means create-or-replace.
   */
  expectedVersion?: number | null;
  /**
   * Stable OperationId for retry identity.
   */
  operationId: string;
}

/**
 * CommitOutcome projected to the public surface.
 */
export interface CommitReceipt {
  operationId: string;
  collection: string;
  documentId: string;
  version: number;
  /**
   * ISO-8601 UTC commit time.
   */
  committedAt: string;
}

/**
 * The engine accepts Typed(QuerySpec) and V1Compatible text; the pre-landing shape carries
 * the v1 text form only and records the typed form as a contract TODO for SE-08.
 */
export type QueryInput =
  | { kind: "v1"; text: string; };

export interface QueryRequest {
  input: QueryInput;
}

export interface QueryRowResult {
  document: DocumentRecord;
  /**
   * Named typed expression projections (never arbitrary code).
   */
  projected: Projection[];
}

export interface Projection {
  name: string;
  value: Json;
}

export interface StaleIndexObservation {
  indexId: string;
  reason: string;
}

export interface QueryResult {
  rows: QueryRowResult[];
  matched: number;
  staleness: StaleIndexObservation[];
}

/**
 * The closed set of engine Data Family kinds. The wire names are the engine's reserved
 * family record keys (src/families/state.rs family_key): identities=Identity ranges,
 * lifecycle=ExpirationRefresh, archival=Archival, validation=SchemaValidation
 * (PD-2026-08-26-04), tombstones=ConflictTombstones. Unknown kinds fail closed.
 */
export type FamilyKind = "attachments" | "counters" | "time-series" | "revisions" | "compare-exchange" | "identities" | "lifecycle" | "archival" | "validation" | "tombstones";

export interface FamilyReadRequest {
  kind: FamilyKind;
  /**
   * Family scope selector (e.g. collection or counter group).
   */
  scope: string;
}

export interface FamilyReadResult {
  kind: FamilyKind;
  scope: string;
  state: Json;
}

export interface FamilyWriteRequest {
  kind: FamilyKind;
  scope: string;
  /**
   * Typed family mutation payload (validated per kind).
   */
  operation: Json;
  operationId: string;
}

export interface ApiError {
  /**
   * Stable machine-readable error code.
   */
  code: string;
  /**
   * Human-readable; never contains credentials or document bodies.
   */
  message: string;
  retryable: boolean;
  /**
   * Present only on session.unauthenticated: the host-enforced login flow entry; the studio
   * redirects there.
   */
  loginUrl?: string | null;
}

/**
 * The closed change-kind vocabulary the feed renders. `put`/`delete` are the forms the
 * TBEP wire carries today (crates/terrabase-tbep schema change_kind);
 * `document`/`index`/`counter`/`time-series`/`operation`/`database`/`conflict` are the
 * engine backbone's closed selector set (src/streaming/mod.rs ChangeKind, SE-03). Unknown
 * kinds fail closed.
 */
export type StreamChangeKind = "put" | "delete" | "document" | "index" | "counter" | "time-series" | "operation" | "database" | "conflict";

/**
 * The content-free metadata a conflict event carries (src/replication/mod.rs
 * ConflictRecord): identities, members, clocks, and the recorded outcome. No document body
 * ever travels with a conflict event; this type has no body-bearing field.
 */
export interface StreamConflictRecord {
  tenant: string;
  operations: string[];
  members: string[];
  clocks: string[];
  /**
   * Recorded policy outcome (winner/deposed-owner-retained/untrusted-clock) naming the
   * converged Member.
   */
  outcome: string;
}

/**
 * One change event of a batch. Content-free: collection and document identities, never
 * bodies.
 */
export interface StreamChangeEvent {
  sequence: number;
  operationId: string;
  kind: StreamChangeKind;
  /**
   * Absent (null) for conflict events: a conflict observation carries no collection identity
   * (src/streaming/mod.rs ChangeEvent::collection).
   */
  collection?: string | null;
  /**
   * Absent (null) for conflict events.
   */
  document?: string | null;
  /**
   * Present only on kind=conflict events: the ConflictRecord-shaped metadata, never a
   * document body.
   */
  conflict?: StreamConflictRecord | null;
}

export interface StreamOpenedPayload {
  subscriptionId: number;
  /**
   * The sequence the engine started serving from (this stream's resume point).
   */
  startSequence: number;
  /**
   * The collection filter, or null for every collection.
   */
  collections?: string[] | null;
}

export interface StreamBatchPayload {
  subscriptionId: number;
  fromSequence: number;
  events: StreamChangeEvent[];
  nextSequence: number;
}

/**
 * Content-free trigger notification (PD-2026-08-26-13 D3): push the hint, pull the delta.
 */
export interface StreamTriggerPayload {
  subscriptionId: number;
  collection: string;
  sequence: number;
  operationId: string;
}

export interface StreamClosedPayload {
  subscriptionId: number;
  /**
   * Machine-readable close reason (snake_case).
   */
  reason: string;
  /**
   * The stream's dropped-frame count at close.
   */
  dropped: number;
  /**
   * Present on engine-generation loss: the sequence a reconnect may resume from.
   */
  resumeFrom?: number | null;
}

/**
 * In-band backpressure honesty: the host shed frames at its queue bound; resumeFrom is the
 * oldest sequence of the dropped region (rewind there on reconnect, never past it).
 */
export interface StreamDropPayload {
  subscriptionId: number;
  resumeFrom: number;
  /**
   * Cumulative dropped count this subscription (the accounting of record).
   */
  dropped: number;
}

export interface SessionOpenedPayload {
  sessionId: string;
  /**
   * Whether the session retains frames for checkpoint resume.
   */
  resumable: boolean;
  heartbeatIntervalMs: number;
}

export interface SubscriptionEstablishedPayload {
  subscriptionId: string;
  /**
   * Sync Checkpoint the consumer may resume from.
   */
  checkpoint: string;
  /**
   * High-water frame seq at establishment: on a resume, replayed deltas carry seq in
   * (checkpoint seq, lastSeq] and live deltas carry seq > lastSeq, so consumers can classify
   * replay vs live exactly.
   */
  lastSeq: number;
}

/**
 * The trivial tick feed proving live plumbing before real data flows (DB-02).
 */
export interface TickDeltaPayload {
  seq: number;
  emittedAt: string;
  counter: number;
}

export interface HeartbeatPayload {
  at: string;
}

export interface ResyncRequiredPayload {
  reason: string;
  lastSeq: number;
}

export interface FatalErrorPayload {
  code: string;
  message: string;
}

export interface SchemaStateRequest {
  /**
   * The collection whose schema state is read.
   */
  collection: string;
}

export interface SchemaStateResult {
  /**
   * The collection.
   */
  collection: string;
  /**
   * The enforced Schema Version, or null for a schema-free collection.
   */
  enforcedVersion?: number | null;
  /**
   * The recorded rule count of the enforced Document Schema.
   */
  ruleCount: number;
  /**
   * The validation status in the schema model's vocabulary: enforced or none.
   */
  validation: string;
  /**
   * The Schema Migration state: none, in_progress, rolling_back, or complete.
   */
  migrationKind: string;
  /**
   * The manifest digest of the recorded drive, when one exists.
   */
  migrationManifest?: string | null;
  /**
   * The drive's from-version, when one exists.
   */
  migrationFromVersion?: number | null;
  /**
   * The drive's to-version, when one exists.
   */
  migrationToVersion?: number | null;
  /**
   * The documents the drive scanned so far.
   */
  migrationDocuments?: number | null;
  /**
   * The documents the drive transformed so far.
   */
  migrationTransformed?: number | null;
  /**
   * The pages the drive committed so far.
   */
  migrationPages?: number | null;
}

const KNOWN_FRAME_TYPES = [
  "session.opened",
  "subscription.established",
  "delta",
  "heartbeat",
  "resync.required",
  "fatal.error",
] as const;

export type SyncFrameType = (typeof KNOWN_FRAME_TYPES)[number];

export interface SyncFrame {
  v: number;
  type: string;
  seq: number;
  correlationId: string | null;
  causationId: string | null;
  idempotencyKey: string | null;
  digest: string;
  payload: Json;
}

export interface TicksSubscriptionHandlers {
  onState?: (state: TicksConnectionState, snapshot: TicksSubscriptionSnapshot) => void;
  onTick: (tick: TickDeltaPayload) => void;
  onReplayed?: (replayedCount: number) => void;
  onRejected?: (rejectedCount: number, reason: string) => void;
  onResync?: (reason: string) => void;
  onFatal?: (error: TerrabaseApiError) => void;
}

export interface TicksSubscriptionOptions {
  /**
   * Force a reconnect when no frame (including heartbeats) arrives within
   * this window. Defaults to 10000ms.
   */
  staleMs?: number;
}

export type TicksConnectionState = "connecting" | "live" | "reconnecting" | "failed";

export interface TicksSubscriptionSnapshot {
  state: TicksConnectionState;
  tickCount: number;
  replayedCount: number;
  rejectedCount: number;
  reconnectCount: number;
  resyncCount: number;
  duplicateCount: number;
  lastSeq: number | null;
  lastError: string | null;
}

export interface TicksSubscription {
  snapshot(): TicksSubscriptionSnapshot;
  close(): void;
}

export interface BackendContext {
  principal: string;
  tenant: string;
}

const KNOWN_CHANGE_KINDS = [
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

const KNOWN_STREAM_EVENTS = [
  "opened",
  "batch",
  "trigger",
  "closed",
  "drop",
] as const;

export type StreamEventName = (typeof KNOWN_STREAM_EVENTS)[number];

export interface ChangesSubscriptionHandlers {
  onState?: (state: ChangesConnectionState, snapshot: ChangesSubscriptionSnapshot) => void;
  onOpened?: (opened: StreamOpenedPayload) => void;
  onEvent?: (event: StreamChangeEvent, meta: { readonly replayed: boolean }) => void;
  onTrigger?: (trigger: StreamTriggerPayload) => void;
  onDrop?: (drop: StreamDropPayload) => void;
  onClosed?: (closed: StreamClosedPayload) => void;
  onRejected?: (rejectedCount: number, reason: string) => void;
  onFatal?: (error: TerrabaseApiError) => void;
}

export interface ChangesSubscriptionOptions {
  /**
   * The collection filter (engine-side): absent subscribes to every collection.
   */
  collections?: readonly string[];
  /**
   * An explicit inclusive replay start (checkpoint resume). Absent opens live-only.
   */
  fromSequence?: number;
  /**
   * Backend-supplied context for the host's local gate. Falls back to the
   * client options; when neither is set the request carries no context and
   * the host's gate answers unauthorized (surfaced as a fatal error).
   */
  backendContext?: BackendContext;
  /**
   * Force a reconnect when no event arrives within this window. DISABLED by
   * default: the /v1/streams surface carries no heartbeat, so an idle stream
   * is indistinguishable from a quiet one in-band — only transport errors
   * (or this opt-in watchdog) drive reconnects.
   */
  staleMs?: number;
  /**
   * The delay before a reconnect attempt. Defaults to 500ms.
   */
  reconnectDelayMs?: number;
  /**
   * How long a connect attempt may hang before it is aborted and retried
   * (a TCP connect or header wait that never completes). Defaults to
   * 15000ms. Once the response opens, the stream itself is bounded only
   * by transport errors or the opt-in staleMs watchdog.
   */
  connectTimeoutMs?: number;
}

export type ChangesConnectionState = "connecting" | "live" | "reconnecting" | "failed";

export interface ChangesSubscriptionSnapshot {
  state: ChangesConnectionState;
  eventCount: number;
  replayedCount: number;
  duplicateCount: number;
  rejectedCount: number;
  reconnectCount: number;
  dropCount: number;
  droppedTotal: number | null;
  lastSeq: number | null;
  checkpoint: number | null;
  lastError: string | null;
}

export interface ChangesSubscription {
  snapshot(): ChangesSubscriptionSnapshot;
  /**
   * The deliberately-slow-consumer control (the DB-04 probe): stops reading
   * the response body — the connection stays open, the host's bounded queue
   * absorbs the backlog, and a full queue sheds counted frames.
   */
  pause(): void;
  resume(): void;
  close(): void;
}

class ChangesSubscriptionImpl implements ChangesSubscription {
  readonly #clientOptions: StudioClientOptions;
  readonly #handlers: ChangesSubscriptionHandlers;
  readonly #options: ChangesSubscriptionOptions;
  readonly #staleMs: number | null;
  readonly #reconnectDelayMs: number;
  readonly #connectTimeoutMs: number;

  #controller: AbortController | null = null;
  #reader: ReadableStreamDefaultReader<Uint8Array> | null = null;
  #decoder: TextDecoder | null = null;
  #watchdog: ReturnType<typeof setInterval> | null = null;
  #state: ChangesConnectionState = "connecting";
  #openCount = 0;
  #eventCount = 0;
  #replayedCount = 0;
  #duplicateCount = 0;
  #rejectedCount = 0;
  #dropMarkers = 0;
  #droppedTotal: number | null = null;
  #lastSeq: number | null = null;
  #lastError: string | null = null;
  #closed = false;
  #paused = false;
  #pending: Uint8Array | null = null;
  // The highest identity ever surfaced, never rewound: the top of a drop
  // marker's at-risk range (the dedup watermark below DOES rewind).
  #maxSeen = 0;
  // The at-least-once identity ledger: bounded, sorted, disjoint ranges of
  // every SURFACED sequence. Normal delivery dedups on the #lastSeq
  // watermark (the host is monotonic); after a drop marker rewinds the
  // watermark, dedup switches to this ledger — a re-delivered sequence the
  // consumer genuinely accepted is a duplicate, a re-delivered sequence in
  // the shed gap was never seen and MUST surface.
  #seen: Array<[number, number]> = [];

  // The resume cursor: the last sequence this consumer accepted, minus the
  // rewind a drop marker imposes. Reconnects ask from = checkpoint + 1.
  #checkpoint: number | null = null;
  // Drop-marker recovery (the host's Dropped discipline, BackboneHub.cs:
  // a checkpoint at or past the boundary cannot be trusted for the gap):
  // while set, the checkpoint is FROZEN at the boundary (post-marker batch
  // cursors cannot erase the rewind) and re-delivered identities dedup on
  // the #seen ledger, until the gap is covered.
  #gapBoundary: number | null = null;
  // The highest identity surfaced when the marker arrived. At or past the
  // boundary it bounds the gap; below the boundary nothing the consumer
  // saw bounds it and only a resumed connection's replay batch covers it.
  #gapHighWater = 0;
  // The highest identity processed since the marker (surfaced or counted
  // duplicate): recovery ends when it reaches the top of the gap.
  #gapCoveredTo = 0;
  // The from value this connection opened with: null when live-only, and
  // the marker that classifies the first batch of a resumed connection as
  // the replayed backlog (the host replays from exactly this point).
  #openedFrom: number | null = null;
  #firstBatchOnConnection = false;
  #lastFrameAt = Date.now();
  #carry = "";

  constructor(
    clientOptions: StudioClientOptions,
    handlers: ChangesSubscriptionHandlers,
    options: ChangesSubscriptionOptions,
  ) {
    this.#clientOptions = clientOptions;
    this.#handlers = handlers;
    this.#options = options;
    this.#staleMs = options.staleMs ?? null;
    this.#reconnectDelayMs = options.reconnectDelayMs ?? 500;
    this.#connectTimeoutMs = options.connectTimeoutMs ?? 15_000;
    this.#open();
    if (this.#staleMs !== null) {
      this.#watchdog = setInterval(() => this.#checkStale(), 1_000);
    }
  }

  snapshot(): ChangesSubscriptionSnapshot {
    return {
      state: this.#state,
      eventCount: this.#eventCount,
      replayedCount: this.#replayedCount,
      duplicateCount: this.#duplicateCount,
      rejectedCount: this.#rejectedCount,
      reconnectCount: Math.max(0, this.#openCount - 1),
      dropCount: this.#dropMarkers,
      droppedTotal: this.#droppedTotal,
      lastSeq: this.#lastSeq,
      checkpoint: this.#checkpoint,
      lastError: this.#lastError,
    };
  }

  pause(): void {
    this.#paused = true;
  }

  resume(): void {
    if (!this.#paused) {
      return;
    }
    this.#paused = false;
    if (this.#pending !== null) {
      const value = this.#pending;
      this.#pending = null;
      this.#lastFrameAt = Date.now();
      this.#ingest((this.#decoder as TextDecoder).decode(value, { stream: true }));
    }
    this.#pump();
  }

  close(): void {
    this.#closed = true;
    if (this.#watchdog !== null) {
      clearInterval(this.#watchdog);
      this.#watchdog = null;
    }
    this.#detach();
  }

  #backendContext(): BackendContext | null {
    return this.#options.backendContext ?? this.#clientOptions.backendContext ?? null;
  }

  async #open(): Promise<void> {
    if (this.#closed) {
      return;
    }
    this.#openCount += 1;
    this.#firstBatchOnConnection = true;
    const from = this.#checkpoint === null ? this.#options.fromSequence ?? null : this.#checkpoint + 1;
    this.#openedFrom = from;
    const query = new URLSearchParams();
    if (this.#options.collections !== undefined && this.#options.collections.length > 0) {
      query.set("collections", this.#options.collections.join(","));
    }
    if (from !== null && from !== undefined) {
      query.set("from", String(from));
    }
    const base = this.#clientOptions.baseUrl.replace(/\/$/, "");
    const qs = query.toString();
    const url = qs.length === 0 ? base + "/v1/streams" : base + "/v1/streams" + "?" + qs;
    const headers: Record<string, string> = { accept: "text/event-stream" };
    const context = this.#backendContext();
    if (context !== null) {
      headers["x-tb-principal"] = context.principal;
      headers["x-tb-tenant"] = context.tenant;
    }
    const controller = new AbortController();
    this.#controller = controller;
    // A connect that never completes (hung TCP, silent header wait) must
    // surface as a reconnect, not sit in connecting-limbo: abort the
    // attempt when the response has not opened within the window. The
    // timer stops at the first byte of the response — the stream itself
    // is bounded by transport errors or the opt-in staleMs watchdog.
    const connectTimer = setTimeout(() => {
      controller.abort();
    }, this.#connectTimeoutMs);
    try {
      const doFetch = this.#clientOptions.fetchImpl ?? fetch;
      const response = await doFetch(url, {
        method: "GET",
        headers,
        signal: controller.signal,
        credentials: "omit",
        cache: "no-store",
      });
      clearTimeout(connectTimer);
      if (!response.ok || response.body === null) {
        const code = response.status;
        let message = "the stream was refused before it opened";
        try {
          const body = (await response.json()) as {
            detail?: unknown;
            error?: { message?: unknown };
          };
          if (typeof body.detail === "string") {
            // The real host's ErrorDto (System.Text.Json Web defaults).
            message = body.detail;
          } else if (typeof body.error?.message === "string") {
            message = body.error.message;
          }
        } catch {
          // A non-JSON refusal keeps the generic message.
        }
        if (code === 401 || code === 403) {
          // The gate's refusals are not retryable in-band: surface them.
          this.#lastError = message;
          this.close();
          this.#setState("failed");
          this.#handlers.onFatal?.(new TerrabaseApiError("stream.refused", message, { status: code }));
          return;
        }
        this.#scheduleReconnect("the stream was refused (HTTP " + code + "): " + message);
        return;
      }
      this.#read(response.body);
    } catch (cause) {
      clearTimeout(connectTimer);
      if (this.#closed || this.#controller !== controller) {
        // close(), the watchdog, or a scheduled reconnect already aborted
        // this attempt — it owns no further transitions.
        return;
      }
      this.#scheduleReconnect(
        controller.signal.aborted
          ? "the stream connect timed out after " + this.#connectTimeoutMs + "ms"
          : "the stream connection failed: " + String(cause),
      );
    }
  }

  #read(body: ReadableStream<Uint8Array>): void {
    this.#reader = body.getReader();
    this.#decoder ??= new TextDecoder();
    this.#pump();
  }

  #pump(): void {
    const reader = this.#reader;
    if (reader === null || this.#closed) {
      return;
    }
    void reader
      .read()
      .then(({ done, value }) => {
        if (done) {
          if (!this.#closed) {
            this.#scheduleReconnect("the stream ended");
          }
          return;
        }
        if (this.#paused) {
          // A read that resolved while paused is stashed undelivered: the
          // deliberately-slow consumer processes nothing until resume().
          this.#pending = value;
          return;
        }
        this.#lastFrameAt = Date.now();
        this.#ingest((this.#decoder as TextDecoder).decode(value, { stream: true }));
        if (!this.#paused && !this.#closed) {
          this.#pump();
        }
      })
      .catch((cause: unknown) => {
        if (!this.#closed) {
          this.#scheduleReconnect("the stream read failed: " + String(cause));
        }
      });
  }

  #ingest(chunk: string): void {
    let text = this.#carry + chunk;
    // The SSE spec allows CRLF (and lone CR) line endings; the real host
    // and the fixture write LF. Normalize so CRLF frames dispatch instead
    // of stalling silently — but a trailing CR may be the first half of a
    // CRLF torn across chunks, so it stays in the carry, unnormalized.
    let tornCr = "";
    if (text.endsWith("\r")) {
      tornCr = "\r";
      text = text.slice(0, -1);
    }
    const normalized = text.replace(/\r\n?/g, "\n");
    const parts = normalized.split("\n\n");
    this.#carry = (parts.pop() ?? "") + tornCr;
    for (const block of parts) {
      if (block.trim().length === 0) {
        continue;
      }
      let id: number | null = null;
      let name: string | null = null;
      let data = "";
      for (const line of block.split("\n")) {
        if (line.startsWith("id:")) {
          const parsed = Number.parseInt(line.slice(3).trim(), 10);
          id = Number.isFinite(parsed) ? parsed : null;
        } else if (line.startsWith("event:")) {
          name = line.slice(6).trim();
        } else if (line.startsWith("data:")) {
          data += line.slice(5).trim();
        }
      }
      if (name === null) {
        continue;
      }
      this.#dispatch(name, data);
      void id;
    }
  }

  #dispatch(name: string, data: string): void {
    if (!(KNOWN_STREAM_EVENTS as readonly string[]).includes(name)) {
      this.#reject("unknown event " + JSON.stringify(name));
      return;
    }
    let payload: unknown;
    try {
      payload = JSON.parse(data);
    } catch {
      this.#reject("event " + name + " carries invalid JSON");
      return;
    }
    if (typeof payload !== "object" || payload === null) {
      this.#reject("event " + name + " is not an object");
      return;
    }
    switch (name as StreamEventName) {
      case "opened": {
        this.#setState("live");
        this.#handlers.onOpened?.(payload as unknown as StreamOpenedPayload);
        return;
      }
      case "batch": {
        this.#handleBatch(payload as unknown as StreamBatchPayload);
        return;
      }
      case "trigger": {
        const trigger = payload as unknown as StreamTriggerPayload;
        // A trigger is a content-free push hint, never an accepted
        // identity: its sequence may name a batch the host's bounded queue
        // shed while the trigger itself survived. Advancing the dedup
        // watermark here would suppress the re-delivered gap as
        // "duplicates" — only surfaced batch events move the watermark.
        this.#handlers.onTrigger?.(trigger);
        return;
      }
      case "drop": {
        const drop = payload as unknown as StreamDropPayload;
        if (typeof drop.resumeFrom !== "number" || !Number.isFinite(drop.resumeFrom)) {
          this.#reject("drop event carries no finite resume boundary");
          return;
        }
        this.#dropMarkers += 1;
        if (typeof drop.dropped === "number") {
          this.#droppedTotal = drop.dropped;
        }
        this.#lastError = null;
        // The host's Dropped discipline (BackboneHub.cs): the boundary is
        // the oldest shed sequence; a checkpoint at or past it cannot be
        // trusted for the gap, so the consumer rewinds — AND re-anchors
        // the dedup watermark, because the re-delivered gap must surface
        // while identities the consumer genuinely accepted (the #seen
        // ledger) stay duplicates.
        const boundary = drop.resumeFrom;
        if (this.#checkpoint === null || boundary - 1 < this.#checkpoint) {
          this.#checkpoint = boundary - 1;
        }
        if (this.#lastSeq === null || boundary - 1 < this.#lastSeq) {
          this.#lastSeq = boundary - 1;
        }
        this.#gapBoundary = boundary;
        this.#gapHighWater = this.#maxSeen;
        this.#gapCoveredTo = boundary - 1;
        this.#handlers.onDrop?.(drop);
        return;
      }
      case "closed": {
        const closed = payload as unknown as StreamClosedPayload;
        this.#handlers.onClosed?.(closed);
        if (closed.reason === "engine-generation-lost") {
          if (typeof closed.resumeFrom === "number" && Number.isFinite(closed.resumeFrom)) {
            // Same discipline as a drop marker: rewind AND re-anchor — the
            // engine's replay from the boundary is at-least-once.
            const boundary = closed.resumeFrom;
            if (this.#checkpoint === null || boundary - 1 < this.#checkpoint) {
              this.#checkpoint = boundary - 1;
            }
            if (this.#lastSeq === null || boundary - 1 < this.#lastSeq) {
              this.#lastSeq = boundary - 1;
            }
            this.#gapBoundary = boundary;
            this.#gapHighWater = this.#maxSeen;
            this.#gapCoveredTo = boundary - 1;
          }
          this.#scheduleReconnect("the engine generation was lost");
          return;
        }
        this.#lastError = "the stream closed: " + String(closed.reason);
        this.close();
        this.#setState("failed");
        return;
      }
      default: {
        this.#reject("unhandled event " + JSON.stringify(name));
      }
    }
  }

  #handleBatch(batch: StreamBatchPayload): void {
    if (!Array.isArray(batch.events)) {
      this.#reject("batch carries no events array");
      return;
    }
    const replayedBatch = this.#firstBatchOnConnection && this.#openedFrom !== null;
    this.#firstBatchOnConnection = false;
    for (const raw of batch.events) {
      const record = raw as unknown as Record<string, unknown>;
      if (
        typeof record.sequence !== "number" ||
        !Number.isFinite(record.sequence) ||
        typeof record.operationId !== "string" ||
        typeof record.kind !== "string" ||
        !(KNOWN_CHANGE_KINDS as readonly string[]).includes(record.kind)
      ) {
        this.#reject("batch carries an invalid event (sequence, operationId, or closed kind)");
        continue;
      }
      const sequence = record.sequence;
      // At-least-once identity. Normal delivery: the monotonic watermark.
      // Drop recovery: the #seen ledger — the watermark rewound at the
      // marker and must not swallow the re-delivered gap.
      const duplicate =
        this.#gapBoundary !== null
          ? this.#wasSeen(sequence)
          : this.#lastSeq !== null && sequence <= this.#lastSeq;
      if (duplicate) {
        this.#duplicateCount += 1;
      } else {
        if (this.#lastSeq === null || sequence > this.#lastSeq) {
          this.#lastSeq = sequence;
        }
        if (sequence > this.#maxSeen) {
          this.#maxSeen = sequence;
        }
        this.#rememberSeen(sequence);
        this.#eventCount += 1;
        if (replayedBatch) {
          this.#replayedCount += 1;
        }
        this.#handlers.onEvent?.(raw, { replayed: replayedBatch });
      }
      if (this.#gapBoundary !== null) {
        if (sequence > this.#gapCoveredTo) {
          this.#gapCoveredTo = sequence;
        }
        if (
          this.#gapHighWater >= this.#gapBoundary &&
          this.#gapCoveredTo >= this.#gapHighWater
        ) {
          // The gap tops out at the pre-marker high water and the stream
          // has re-delivered past it: covered.
          this.#endGapRecovery();
        }
      }
    }
    if (
      this.#gapBoundary !== null &&
      replayedBatch &&
      this.#gapCoveredTo >= this.#gapBoundary
    ) {
      // Nothing the consumer saw bounds the gap (the watermark was behind
      // the boundary): the resumed connection's replay batch — the host's
      // single re-delivery from the resume point (D-4) — covers it.
      this.#endGapRecovery();
    }
    if (
      typeof batch.nextSequence === "number" &&
      Number.isFinite(batch.nextSequence) &&
      batch.nextSequence > 0
    ) {
      // The batch cursor is the authoritative checkpoint (not the SSE id
      // line, which a drop marker deliberately rewinds) — but NEVER while
      // a drop gap is open: the marker discipline says a cursor at or
      // past the boundary cannot be trusted for the gap, so the rewind
      // stays sticky until the gap is covered.
      const cursor = batch.nextSequence - 1;
      if (this.#gapBoundary === null && (this.#checkpoint === null || cursor >= this.#checkpoint)) {
        this.#checkpoint = cursor;
      }
    }
  }

  #endGapRecovery(): void {
    // The gap is covered: every identity at or below #gapCoveredTo has now
    // been processed (surfaced or counted duplicate). The checkpoint and
    // the watermark resume from the covered top; batch cursors govern the
    // checkpoint again.
    if (this.#gapCoveredTo > (this.#checkpoint ?? 0)) {
      this.#checkpoint = this.#gapCoveredTo;
    }
    if (this.#gapCoveredTo > (this.#lastSeq ?? 0)) {
      this.#lastSeq = this.#gapCoveredTo;
    }
    this.#gapBoundary = null;
  }

  #wasSeen(sequence: number): boolean {
    for (const [start, end] of this.#seen) {
      if (sequence >= start && sequence <= end) {
        return true;
      }
    }
    return false;
  }

  #rememberSeen(sequence: number): void {
    const ranges = this.#seen;
    for (let index = 0; index < ranges.length; index += 1) {
      const range = ranges[index] as [number, number];
      if (sequence + 1 < range[0]) {
        ranges.splice(index, 0, [sequence, sequence]);
        this.#trimSeen();
        return;
      }
      if (sequence - 1 <= range[1]) {
        range[0] = Math.min(range[0], sequence);
        range[1] = Math.max(range[1], sequence);
        const follow = ranges[index + 1] as [number, number] | undefined;
        if (follow !== undefined && range[1] + 1 >= follow[0]) {
          range[1] = Math.max(range[1], follow[1]);
          ranges.splice(index + 1, 1);
        }
        return;
      }
    }
    ranges.push([sequence, sequence]);
    this.#trimSeen();
  }

  #trimSeen(): void {
    // A monotonic stream merges into ONE range forever; hostile
    // fragmentation beyond the cap sheds the OLDEST range only — a
    // far-past identity may then re-surface as an at-least-once
    // duplicate, never a silent loss.
    if (this.#seen.length > 128) {
      this.#seen.shift();
    }
  }

  #setState(state: ChangesConnectionState): void {
    if (this.#state === state) {
      return;
    }
    this.#state = state;
    this.#handlers.onState?.(state, this.snapshot());
  }

  #reject(reason: string): void {
    this.#rejectedCount += 1;
    this.#lastError = reason;
    this.#handlers.onRejected?.(this.#rejectedCount, reason);
  }

  #scheduleReconnect(reason: string): void {
    if (this.#closed || this.#state === "failed") {
      return;
    }
    this.#setState("reconnecting");
    this.#lastError = reason;
    this.#detach();
    setTimeout(() => {
      void this.#open();
    }, this.#reconnectDelayMs);
  }

  #detach(): void {
    if (this.#controller !== null) {
      this.#controller.abort();
      this.#controller = null;
    }
    this.#reader = null;
    this.#carry = "";
  }

  #checkStale(): void {
    if (this.#closed || this.#state === "failed" || this.#staleMs === null) {
      return;
    }
    if (Date.now() - this.#lastFrameAt > this.#staleMs) {
      this.#lastFrameAt = Date.now();
      this.#scheduleReconnect("no events within the staleness window; forcing reconnect");
    }
  }
}

export class TerrabaseApiError extends Error {
  readonly code: string;
  readonly retryable: boolean;
  readonly status: number;
  readonly correlationId: string | null;
  readonly loginUrl: string | null;

  constructor(
    code: string,
    message: string,
    options: { retryable?: boolean; status?: number; correlationId?: string | null; loginUrl?: string | null } = {},
  ) {
    super(message);
    this.name = "TerrabaseApiError";
    this.code = code;
    this.retryable = options.retryable ?? false;
    this.status = options.status ?? 0;
    this.correlationId = options.correlationId ?? null;
    this.loginUrl = options.loginUrl ?? null;
  }
}

function canonicalJson(value: Json): string {
  if (value === null || typeof value === "number" || typeof value === "boolean" || typeof value === "string") {
    return JSON.stringify(value);
  }
  if (Array.isArray(value)) {
    return `[${value.map((item) => canonicalJson(item)).join(",")}]`;
  }
  const keys = Object.keys(value).sort();
  return `{${keys.map((key) => `${JSON.stringify(key)}:${canonicalJson(value[key])}`).join(",")}}`;
}

async function digestOf(payload: Json): Promise<string> {
  const bytes = new TextEncoder().encode(canonicalJson(payload));
  const digest = await crypto.subtle.digest("SHA-256", bytes);
  const hex = Array.from(new Uint8Array(digest), (byte) => byte.toString(16).padStart(2, "0")).join("");
  return `sha256:${hex}`;
}

export interface StudioClientOptions {
  baseUrl: string;
  fetchImpl?: typeof fetch;
  timeoutMs?: number;
  backendContext?: BackendContext;
}

export interface OperationOptions {
  idempotencyKey?: string;
  causationId?: string;
  timeoutMs?: number;
}

async function callOperation(
  clientOptions: StudioClientOptions,
  op: string,
  request: Json | null,
  options: OperationOptions,
): Promise<unknown> {
  const correlationId = crypto.randomUUID();
  const envelope = {
    v: TERRABASE_API_CONTRACT_VERSION,
    op,
    correlationId,
    causationId: options.causationId ?? null,
    idempotencyKey: options.idempotencyKey ?? null,
    request: request ?? {},
  };
  const doFetch = clientOptions.fetchImpl ?? fetch;
  const controller = new AbortController();
  const timeoutMs = options.timeoutMs ?? clientOptions.timeoutMs ?? 8000;
  const timeout = setTimeout(() => controller.abort(), timeoutMs);
  try {
    const response = await doFetch(`${clientOptions.baseUrl.replace(/\/$/, "")}/api/v2/operations/${op}`, {
      method: "POST",
      headers: { "content-type": "application/json" },
      body: JSON.stringify(envelope),
      signal: controller.signal,
      credentials: "omit",
    });
    let body: unknown;
    try {
      body = await response.json();
    } catch {
      throw new TerrabaseApiError("protocol.body-invalid", `Response for ${op} is not JSON.`, {
        status: response.status,
        correlationId,
      });
    }
    if (typeof body !== "object" || body === null || Array.isArray(body)) {
      throw new TerrabaseApiError("protocol.envelope-invalid", `Response for ${op} is not an envelope object.`, {
        status: response.status,
        correlationId,
      });
    }
    const record = body as Record<string, unknown>;
    if (record.v !== TERRABASE_API_CONTRACT_VERSION) {
      throw new TerrabaseApiError(
        "protocol.version-unsupported",
        `Response for ${op} carries envelope version ${JSON.stringify(record.v)}.`,
        { status: response.status, correlationId },
      );
    }
    if (record.correlationId !== correlationId) {
      throw new TerrabaseApiError(
        "protocol.correlation-mismatch",
        `Response for ${op} did not echo the request correlationId.`,
        { status: response.status, correlationId },
      );
    }
    if (record.error !== undefined && record.error !== null) {
      const error = record.error as { code?: unknown; message?: unknown; retryable?: unknown; loginUrl?: unknown };
      throw new TerrabaseApiError(
        typeof error.code === "string" ? error.code : "error.code-missing",
        typeof error.message === "string" ? error.message : "The host returned an untyped error.",
        {
          retryable: error.retryable === true,
          status: response.status,
          correlationId,
          loginUrl: typeof error.loginUrl === "string" ? error.loginUrl : null,
        },
      );
    }
    if (response.status < 200 || response.status >= 300) {
      throw new TerrabaseApiError("protocol.status-invalid", `Unexpected HTTP ${response.status} for ${op}.`, {
        status: response.status,
        correlationId,
      });
    }
    return record.result;
  } catch (cause) {
    if (cause instanceof TerrabaseApiError) {
      throw cause;
    }
    const aborted = cause instanceof Error && cause.name === "AbortError";
    throw new TerrabaseApiError(
      aborted ? "transport.timeout" : "transport.unreachable",
      aborted ? `Request for ${op} timed out after ${timeoutMs}ms.` : `Request for ${op} failed: ${String(cause)}`,
      { retryable: true, correlationId },
    );
  } finally {
    clearTimeout(timeout);
  }
}

class TicksSubscriptionImpl implements TicksSubscription {
  readonly #clientOptions: StudioClientOptions;
  readonly #handlers: TicksSubscriptionHandlers;
  readonly #staleMs: number;
  readonly #path = "/api/v2/subscriptions/ticks";

  #source: EventSource | null = null;
  #watchdog: ReturnType<typeof setInterval> | null = null;
  #queue: Promise<void> = Promise.resolve();
  #state: TicksConnectionState = "connecting";
  #openCount = 0;
  #connectionLosses = 0;
  #tickCount = 0;
  #replayedCount = 0;
  #rejectedCount = 0;
  #resyncCount = 0;
  #duplicateCount = 0;
  #lastSeq: number | null = null;
  #lastError: string | null = null;
  #lastFrameAt = Date.now();
  #closed = false;

  // Resume identity, reconstructed from the frames themselves (the SSE id
  // line is not exposed to script): session id from session.opened, plus the
  // seq of the last frame accepted on this subscription.
  #sessionId: string | null = null;
  #lastFrameSeq: number | null = null;
  // Delta-only monotonicity: control frames on a resumed connection carry
  // fresh seq identities past the replayed backlog and must not advance it.
  #lastDeltaSeq: number | null = null;
  // Set from subscription.established.lastSeq on a reconnect: replayed
  // deltas carry seq <= this high-water mark, live deltas above it.
  #replayHighWater: number | null = null;

  constructor(
    clientOptions: StudioClientOptions,
    handlers: TicksSubscriptionHandlers,
    options: TicksSubscriptionOptions,
  ) {
    this.#clientOptions = clientOptions;
    this.#handlers = handlers;
    this.#staleMs = options.staleMs ?? 10_000;
    this.#open();
    this.#watchdog = setInterval(() => this.#checkStale(), 1_000);
  }

  snapshot(): TicksSubscriptionSnapshot {
    return {
      state: this.#state,
      tickCount: this.#tickCount,
      replayedCount: this.#replayedCount,
      rejectedCount: this.#rejectedCount,
      reconnectCount: Math.max(0, this.#openCount - 1, this.#connectionLosses),
      resyncCount: this.#resyncCount,
      duplicateCount: this.#duplicateCount,
      lastSeq: this.#lastSeq,
      lastError: this.#lastError,
    };
  }

  close(): void {
    this.#closed = true;
    if (this.#watchdog !== null) {
      clearInterval(this.#watchdog);
      this.#watchdog = null;
    }
    this.#detach();
  }

  #checkpoint(): string | null {
    if (this.#sessionId === null || this.#lastFrameSeq === null) {
      return null;
    }
    return `${this.#sessionId}:${this.#lastFrameSeq}`;
  }

  #open(): void {
    if (this.#closed) {
      return;
    }
    this.#openCount += 1;
    // The browser's own reconnect path reuses the EventSource and re-sends
    // Last-Event-ID automatically; a source this client had to recreate
    // carries the checkpoint as a query parameter instead.
    const checkpoint = this.#checkpoint();
    const url =
      checkpoint === null
        ? `${this.#clientOptions.baseUrl.replace(/\/$/, "")}${this.#path}`
        : `${this.#clientOptions.baseUrl.replace(/\/$/, "")}${this.#path}?lastEventId=${encodeURIComponent(checkpoint)}`;
    const source = new EventSource(url, { withCredentials: false });
    this.#source = source;
    source.onopen = () => {
      this.#lastFrameAt = Date.now();
    };
    source.onerror = () => {
      if (this.#closed) {
        return;
      }
      // The browser's automatic retry reuses the source (no manual open), so
      // connection losses are counted here rather than from #openCount alone.
      if (this.#state !== "reconnecting") {
        this.#connectionLosses += 1;
      }
      this.#setState("reconnecting");
      this.#lastError = "connection lost; resuming from checkpoint";
      if (source.readyState === EventSource.CLOSED) {
        this.#detach();
        setTimeout(() => this.#open(), 1_000);
      }
      // readyState CONNECTING: the browser is retrying this same source and
      // will re-send Last-Event-ID itself — leave it alone.
    };
    source.onmessage = (event: MessageEvent<string>) => {
      this.#lastFrameAt = Date.now();
      // Serialize async digest validation so frame order is preserved.
      this.#queue = this.#queue
        .then(() => this.#processFrame(event.data))
        .catch(() => {
          // #processFrame records its own failures; never let one reject the chain.
        });
    };
  }

  #detach(): void {
    if (this.#source !== null) {
      this.#source.close();
      this.#source.onopen = null;
      this.#source.onerror = null;
      this.#source.onmessage = null;
      this.#source = null;
    }
  }

  #setState(state: TicksConnectionState): void {
    if (this.#state === state) {
      return;
    }
    this.#state = state;
    this.#handlers.onState?.(state, this.snapshot());
  }

  #reject(reason: string): void {
    this.#rejectedCount += 1;
    this.#lastError = reason;
    this.#handlers.onRejected?.(this.#rejectedCount, reason);
  }

  async #processFrame(raw: string): Promise<void> {
    let frame: unknown;
    try {
      frame = JSON.parse(raw);
    } catch {
      this.#reject("frame is not JSON");
      return;
    }
    if (typeof frame !== "object" || frame === null || Array.isArray(frame)) {
      this.#reject("frame is not an object");
      return;
    }
    const record = frame as Record<string, unknown>;
    if (record.v !== TERRABASE_API_CONTRACT_VERSION) {
      this.#reject(`frame envelope version ${JSON.stringify(record.v)} is unsupported`);
      return;
    }
    if (typeof record.type !== "string" || !(KNOWN_FRAME_TYPES as readonly string[]).includes(record.type)) {
      this.#reject(`unknown frame type ${JSON.stringify(record.type)}`);
      return;
    }
    if (typeof record.seq !== "number" || !Number.isFinite(record.seq)) {
      this.#reject("frame seq is not a finite number");
      return;
    }
    if (typeof record.digest !== "string") {
      this.#reject("frame digest is missing");
      return;
    }
    const payload = (record.payload ?? null) as Json;
    const digest = await digestOf(payload);
    if (digest !== record.digest) {
      this.#reject("frame digest mismatch");
      return;
    }

    switch (record.type as SyncFrameType) {
      case "session.opened": {
        const opened = payload as unknown as SessionOpenedPayload;
        if (typeof opened.sessionId === "string") {
          this.#sessionId = opened.sessionId;
        }
        this.#lastFrameSeq = record.seq;
        return;
      }
      case "subscription.established": {
        const established = payload as unknown as SubscriptionEstablishedPayload;
        // Any established frame after deltas were already consumed marks a
        // resume (the browser's automatic reconnect keeps #openCount at 1,
        // so prior history — not the open count — is the signal).
        if (this.#lastDeltaSeq !== null && typeof established.lastSeq === "number") {
          this.#replayHighWater = established.lastSeq;
        }
        this.#lastFrameSeq = record.seq;
        this.#setState("live");
        return;
      }
      case "delta": {
        const delta = payload as unknown as TickDeltaPayload;
        if (delta.seq !== record.seq) {
          this.#reject("delta payload seq does not match frame seq");
          return;
        }
        if (this.#lastDeltaSeq !== null && record.seq <= this.#lastDeltaSeq) {
          // At-least-once identity: replayed duplicates are skipped and
          // counted, never surfaced as fresh ticks.
          this.#duplicateCount += 1;
          this.#lastFrameSeq = record.seq;
          return;
        }
        this.#lastDeltaSeq = record.seq;
        this.#lastFrameSeq = record.seq;
        this.#lastSeq = record.seq;
        this.#tickCount += 1;
        if (this.#replayHighWater !== null) {
          if (record.seq <= this.#replayHighWater) {
            this.#replayedCount += 1;
          } else {
            // First live delta past the replayed backlog.
            const replayed = this.#replayedCount;
            this.#replayHighWater = null;
            this.#handlers.onReplayed?.(replayed);
          }
        }
        this.#handlers.onTick(delta);
        return;
      }
      case "heartbeat": {
        this.#lastFrameSeq = record.seq;
        return;
      }
      case "resync.required": {
        const resync = payload as unknown as ResyncRequiredPayload;
        this.#resyncCount += 1;
        this.#lastError = `resync required: ${resync.reason}`;
        this.#handlers.onResync?.(resync.reason);
        // The checkpoint is no longer resumable: drop all resume identity
        // and re-open a fresh session (never re-present the stale checkpoint).
        this.#sessionId = null;
        this.#lastFrameSeq = null;
        this.#lastDeltaSeq = null;
        this.#replayHighWater = null;
        this.#detach();
        this.#open();
        return;
      }
      case "fatal.error": {
        const failure = payload as unknown as FatalErrorPayload;
        this.#lastError = failure.message;
        this.close();
        this.#setState("failed");
        this.#handlers.onFatal?.(
          new TerrabaseApiError(failure.code, failure.message, { retryable: false }),
        );
        return;
      }
      default: {
        this.#reject(`unhandled frame type ${JSON.stringify(record.type)}`);
      }
    }
  }

  #checkStale(): void {
    if (this.#closed || this.#state === "failed") {
      return;
    }
    if (Date.now() - this.#lastFrameAt > this.#staleMs) {
      this.#lastFrameAt = Date.now();
      this.#setState("reconnecting");
      this.#lastError = "no frames within the staleness window; forcing reconnect";
      this.#detach();
      this.#open();
    }
  }
}

export class TerrabaseStudioClient {
  readonly #options: StudioClientOptions;

  constructor(options: StudioClientOptions) {
    this.#options = options;
  }

  get baseUrl(): string {
    return this.#options.baseUrl;
  }

  /**
   * Host-level typed read wrapping engine identity, storage stats, and operation counters.
   */
  async engineStatus(options: OperationOptions = {}): Promise<EngineStatus> {
    return callOperation(this.#options, "engine.status", null, options) as Promise<EngineStatus>;
  }

  /**
   * PublicOperation::DocumentRead.
   */
  async documentRead(request: DocumentReadRequest, options: OperationOptions = {}): Promise<DocumentReadResult> {
    return callOperation(this.#options, "document.read", request as unknown as Json, options) as Promise<DocumentReadResult>;
  }

  /**
   * PublicOperation::DocumentWrite (idempotent via OperationId + idempotencyKey).
   */
  async documentWrite(request: DocumentWriteRequest, options: OperationOptions = {}): Promise<CommitReceipt> {
    return callOperation(this.#options, "document.write", request as unknown as Json, options) as Promise<CommitReceipt>;
  }

  /**
   * PublicOperation::Query over the v1-compatible grammar.
   */
  async queryExecute(request: QueryRequest, options: OperationOptions = {}): Promise<QueryResult> {
    return callOperation(this.#options, "query.execute", request as unknown as Json, options) as Promise<QueryResult>;
  }

  /**
   * PublicOperation::Family(FamilyReadRequest). Unknown FamilyKind values fail closed with
   * family.kind-unknown.
   */
  async familyRead(request: FamilyReadRequest, options: OperationOptions = {}): Promise<FamilyReadResult> {
    return callOperation(this.#options, "family.read", request as unknown as Json, options) as Promise<FamilyReadResult>;
  }

  /**
   * PublicOperation::Family(FamilyWriteRequest) (idempotent via OperationId +
   * idempotencyKey).
   */
  async familyWrite(request: FamilyWriteRequest, options: OperationOptions = {}): Promise<CommitReceipt> {
    return callOperation(this.#options, "family.write", request as unknown as Json, options) as Promise<CommitReceipt>;
  }

  /**
   * PublicOperation::Schema(SchemaOperation::Status). One typed read of one collection's
   * Document Schema state and Schema Migration state; the studio's schema page reads nothing
   * else.
   */
  async schemaStatus(request: SchemaStateRequest, options: OperationOptions = {}): Promise<SchemaStateResult> {
    return callOperation(this.#options, "schema.status", request as unknown as Json, options) as Promise<SchemaStateResult>;
  }

    /**
   * Trivial live tick feed (session open/resume, delta frames, heartbeat, resync-required,
   * fatal errors) proving the studio's live plumbing end-to-end against the SE-04 envelope
   * discipline.
   */
  subscribeTicks(
    handlers: TicksSubscriptionHandlers,
    options: TicksSubscriptionOptions = {},
  ): TicksSubscription {
    return new TicksSubscriptionImpl(this.#options, handlers, options);
  }

  /**
   * The backbone changes stream (DB-04): the C# API host's /v1/streams SSE
   * surface over the SE-03 event backbone — named events (opened, batch,
   * trigger, closed, drop), checkpoint-resumed reconnect, at-least-once
   * sequence identities deduplicated client-side, host-shed drops surfaced
   * in-band, and every kind of the closed selector vocabulary rendered.
   */
  subscribeChanges(
    handlers: ChangesSubscriptionHandlers,
    options: ChangesSubscriptionOptions = {},
  ): ChangesSubscription {
    return new ChangesSubscriptionImpl(this.#options, handlers, options);
  }
}
