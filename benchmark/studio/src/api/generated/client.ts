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
}
