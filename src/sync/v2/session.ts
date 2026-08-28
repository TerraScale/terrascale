// The native `terrascale.sync.v2` client session (SE-09): the SDK's
// live-sync surface over the TerraBase API host's HTTP/SSE endpoints
// (SE-08's `TypedApi`): session setup with durable checkpoint resume,
// delta pulls from the checkpoint, snapshot adoption on resync, the
// trigger-notification channel that drives immediate pulls (push the
// hint, pull the delta — PD-2026-08-26-13 D3), and server-ack-only
// mutations carrying an idempotency key (PD-2026-08-26-12 D2: no
// optimistic patches — the removal is the recorded v1/v2 behavioral
// difference; the mutation reaches the client's state only through the
// journal -> delta path).
//
// The wrapped-codec decision keeps `loro-delta/1` and `loro-snapshot/1`
// payload bytes opaque to the protocol; this wave's store is memory-only
// and advances the authoritative client state (the checkpoint) while
// handing payload bytes to an optional materializer.
//
// Follow-up repairs (TerraBaseV2#88, warden re-gate MINORs, both pinned
// red-then-green): the SSE line bound is evaluated only after complete
// lines are drained from the read buffer, so it measures an actual
// (undelimited) line, never a reader chunk carrying many complete events;
// and the reconnect ladder resets only after a sustained-stability
// horizon (no stream failure for longer than every gap the ladder can
// schedule), so a stream dying on any cadence inside the horizon — with a
// healthy delta endpoint — is one sustained outage that still reaches
// MaxReconnectAttempts. Neither repair changes the public options, their
// defaults, or the SSE/HTTP contract surfaces; and neither diagnoses why
// a host's stream dies — the session only bounds its own reconnection.

import {
  syncV2CanonicalCheckpoint,
  syncV2CheckpointFromBytes,
  syncV2CheckpointToBytes,
  syncV2MaxKeyBytes,
  type SyncV2Checkpoint,
  type SyncV2PayloadCodecName,
  type SyncV2Value,
} from "./frame.js";

/** The fetch surface the session speaks (injectable for tests and runtimes). */
export type SyncV2Fetch = (
  input: string,
  init?: {
    readonly method?: string | undefined;
    readonly headers?: Readonly<Record<string, string>> | undefined;
    readonly body?: string | undefined;
    readonly signal?: AbortSignal | undefined;
  },
) => Promise<Response>;

/** One parsed SSE event of the host's `/v1/streams` channel. */
export type SyncV2StreamEvent = {
  readonly event: string;
  readonly id: string | null;
  readonly data: unknown;
};

/** The push channel the session subscribes to. */
export type SyncV2Stream = {
  close(): void;
};

/** A factory for the push channel; the default parses SSE over fetch. */
export type SyncV2StreamFactory = (
  url: string,
  headers: Readonly<Record<string, string>>,
  onEvent: (event: SyncV2StreamEvent) => void,
  onEnded: (error: unknown) => void,
) => SyncV2Stream;

/** Durable checkpoint persistence the session reports every acknowledged advance to. */
export type SyncV2CheckpointStore = {
  save(checkpointBase64: string): void | Promise<void>;
};

/** The connection status of a v2 sync session. */
export type SyncV2ConnectionStatus =
  | "idle"
  | "connecting"
  | "live"
  | "resync-required"
  | "reconnecting"
  | "closed"
  | "failed";

/** The memory-only client store's observable snapshot. */
export type SyncV2ClientSnapshot = {
  readonly Status: SyncV2ConnectionStatus;
  readonly Checkpoint: SyncV2Checkpoint;
  readonly AppliedPayloadCodec: SyncV2PayloadCodecName | null;
  readonly AppliedDeltaBatches: number;
  readonly AdoptedSnapshots: number;
  readonly LastError: string | undefined;
};

/**
 * Optional document materialization from opaque payload bytes. The
 * wrapped-codec decision keeps the payload encoding owned by the named
 * payload codecs; a deployment that can decode a codec version supplies
 * the materializer, and the store exposes its documents.
 */
export type SyncV2DocumentMaterializer = (
  payload: Uint8Array,
  codec: SyncV2PayloadCodecName,
) => ReadonlyMap<string, Readonly<Record<string, SyncV2Value>>> | undefined;

/** The options of one sync session. */
export type TerraScaleSyncV2SessionOptions = {
  /** The API host base URL (for example `https://sync.terrascale.test`). */
  readonly Url: string;
  /** The collection scope the session subscribes to. */
  readonly Collection: string;
  /** The backend-supplied principal (`X-TB-Principal` on the local host boundary). */
  readonly Principal: string;
  /** The backend-supplied tenant (`X-TB-Tenant` on the local host boundary). */
  readonly Tenant: string;
  /** The fetch implementation; defaults to the global `fetch`. */
  readonly Fetch?: SyncV2Fetch | undefined;
  /** A durable checkpoint to resume from (base64, from `getCheckpointBase64`). */
  readonly ResumeCheckpoint?: string | undefined;
  /** Durable persistence notified on every acknowledged checkpoint. */
  readonly CheckpointStore?: SyncV2CheckpointStore | undefined;
  /** The push-channel factory; defaults to SSE over fetch. */
  readonly StreamFactory?: SyncV2StreamFactory | undefined;
  /** Document materialization from payload bytes (memory-only otherwise). */
  readonly Materializer?: SyncV2DocumentMaterializer | undefined;
  /** The base reconnect delay in milliseconds; the ladder doubles it per attempt, jitters, and caps at 30 000 (default 100). */
  readonly ReconnectDelayMilliseconds?: number | undefined;
  /** The reconnect-attempt bound; exhaustion fails the session terminally (default 8). */
  readonly MaxReconnectAttempts?: number | undefined;
};

/** One server-ack-only mutation request. Exactly one of `Put` or `Delete` applies. */
export type SyncV2MutationRequest = {
  readonly Document: string;
  readonly Put?: Readonly<Record<string, SyncV2Value>> | undefined;
  readonly Delete?: boolean | undefined;
};

/** The durable receipt of an applied mutation. */
export type SyncV2MutationReceipt = {
  readonly Sequence: number;
  readonly Version: number;
  readonly OperationId: string;
};

/** The outcome of a mutation: the server's acknowledgement is the only answer. */
export type SyncV2MutationResult =
  | { readonly Outcome: "applied"; readonly Receipt: SyncV2MutationReceipt }
  | { readonly Outcome: "rejected"; readonly Reason: string };

/**
 * The memory-only store of one sync session: the authoritative client
 * state is the checkpoint; payload bytes apply idempotently and reach
 * documents only through the materializer.
 */
export class TerraScaleSyncV2Store {
  readonly #listeners = new Set<() => void>();
  #snapshot: SyncV2ClientSnapshot;
  #documents = new Map<string, Readonly<Record<string, SyncV2Value>>>();

  public constructor(initial?: SyncV2ClientSnapshot) {
    this.#snapshot = freezeSnapshot(initial ?? {
      Status: "idle",
      Checkpoint: syncV2CanonicalCheckpoint([], 0n),
      AppliedPayloadCodec: null,
      AppliedDeltaBatches: 0,
      AdoptedSnapshots: 0,
      LastError: undefined,
    });
  }

  public subscribe(listener: () => void): () => void {
    this.#listeners.add(listener);
    return () => this.#listeners.delete(listener);
  }

  public getSnapshot(): SyncV2ClientSnapshot {
    return this.#snapshot;
  }

  public getServerSnapshot(): SyncV2ClientSnapshot {
    return this.#snapshot;
  }

  /** The documents the materializer produced, if one is supplied. */
  public getDocuments(): ReadonlyMap<string, Readonly<Record<string, SyncV2Value>>> {
    return this.#documents;
  }

  public setStatus(status: SyncV2ConnectionStatus, lastError?: string | undefined): void {
    this.#commit({ status, lastError });
  }

  public fail(error: unknown): void {
    this.#commit({
      status: "failed",
      lastError: error instanceof Error ? error.message : String(error),
    });
  }

  /**
   * Applies one delta batch. Idempotent by checkpoint: a batch whose next
   * checkpoint does not strictly advance the session's state is a no-op —
   * the at-least-once re-serve contract of the reconnect-replay vector.
   */
  public applyDeltaBatch(
    payload: Uint8Array,
    codec: SyncV2PayloadCodecName,
    next: SyncV2Checkpoint,
    materializer?: SyncV2DocumentMaterializer | undefined,
  ): boolean {
    if (!advances(this.#snapshot.Checkpoint, next)) {
      return false;
    }
    const documents = materializer?.(payload, codec);
    if (documents !== undefined) {
      this.#documents = new Map(documents);
    }
    this.#commit({
      status: undefined,
      checkpoint: next,
      appliedCodec: codec,
      appliedDeltaBatches: this.#snapshot.AppliedDeltaBatches + 1,
    });
    return true;
  }

  /** Adopts one full snapshot: the resync-required recovery path. */
  public adoptSnapshot(
    payload: Uint8Array,
    codec: SyncV2PayloadCodecName,
    checkpoint: SyncV2Checkpoint,
    materializer?: SyncV2DocumentMaterializer | undefined,
  ): void {
    const documents = materializer?.(payload, codec);
    if (documents !== undefined) {
      this.#documents = new Map(documents);
    }
    this.#commit({
      status: undefined,
      checkpoint,
      appliedCodec: codec,
      adoptedSnapshots: this.#snapshot.AdoptedSnapshots + 1,
    });
  }

  #commit(update: {
    readonly status?: SyncV2ConnectionStatus | undefined;
    readonly checkpoint?: SyncV2Checkpoint | undefined;
    readonly appliedCodec?: SyncV2PayloadCodecName | undefined;
    readonly appliedDeltaBatches?: number | undefined;
    readonly adoptedSnapshots?: number | undefined;
    readonly lastError?: string | undefined;
  }): void {
    const current = this.#snapshot;
    const next = freezeSnapshot({
      Status: update.status ?? current.Status,
      Checkpoint: update.checkpoint ?? current.Checkpoint,
      AppliedPayloadCodec: update.appliedCodec ?? current.AppliedPayloadCodec,
      AppliedDeltaBatches: update.appliedDeltaBatches ?? current.AppliedDeltaBatches,
      AdoptedSnapshots: update.adoptedSnapshots ?? current.AdoptedSnapshots,
      // A present (possibly undefined) lastError key replaces the recorded
      // error; an absent key preserves it — a status transition through
      // setStatus clears a stale error, an application commit keeps it.
      LastError: "lastError" in update ? update.lastError : current.LastError,
    });
    if (next === current) {
      return;
    }
    this.#snapshot = next;
    for (const listener of this.#listeners) {
      listener();
    }
  }
}

function freezeSnapshot(snapshot: SyncV2ClientSnapshot): SyncV2ClientSnapshot {
  return Object.freeze({ ...snapshot, Checkpoint: snapshot.Checkpoint });
}

function advances(current: SyncV2Checkpoint, next: SyncV2Checkpoint): boolean {
  if (next.storage_sequence > current.storage_sequence) {
    return true;
  }
  if (next.storage_sequence < current.storage_sequence) {
    return false;
  }
  return next.ends.length > current.ends.length
    || next.ends.some((pair, index) => {
      const existing = current.ends[index];
      return existing === undefined || existing[0] !== pair[0] || existing[1] !== pair[1];
    });
}

/** The typed failure surface of the session. */
export class SyncV2SessionError extends Error {
  public override readonly name = "SyncV2SessionError";
  public readonly code: string;
  public readonly detail: string | null;
  public readonly resyncRequired: boolean;

  public constructor(code: string, detail: string | null, resyncRequired = false) {
    super(`terrascale.sync.v2 session: ${code}${detail === null ? "" : `: ${detail}`}`);
    this.code = code;
    this.detail = detail;
    this.resyncRequired = resyncRequired;
  }
}

const maxDrainBatches = 4096;
const reconnectDelayDefault = 100;
const maxReconnectAttemptsDefault = 8;
const reconnectDelayCapMilliseconds = 30_000;
/** The stream-stability window: the quantum the ladder measures stability in. */
const reconnectStabilityMilliseconds = 1_000;
/**
 * How long the ladder stays sticky after a stream failure — the sustained
 * stability a stream must outlive before the ladder forgets previous
 * attempts: the delay cap plus one stability window. Two properties pin
 * the value. Being open for a single window is not stability: a stream
 * dying on any cadence just past the window (its delta endpoint healthy)
 * is one sustained outage and must still reach `MaxReconnectAttempts`.
 * And the horizon strictly exceeds every gap the ladder itself can
 * schedule (a single delay is at most the cap), so no late attempt's own
 * backoff gap can masquerade as stability and reset the ladder
 * mid-outage.
 */
const reconnectStabilityHorizonMilliseconds =
  reconnectDelayCapMilliseconds + reconnectStabilityMilliseconds;
const maxSafeInteger = 9_007_199_254_740_991n;

/**
 * The failure codes a reconnect cannot fix: the host session identity is
 * gone (closed on the host side) or the principal/tenant context is
 * refused. Retrying these would spin against a refusal that only a fresh
 * setup (or new credentials) can clear.
 */
const terminalSessionErrorCodes: ReadonlySet<string> = new Set([
  "session_closed",
  "scope_refused",
  "http_401",
  "http_403",
]);

function isTerminalSessionFailure(error: unknown): boolean {
  return error instanceof SyncV2SessionError && terminalSessionErrorCodes.has(error.code);
}

function errorMessageOf(error: unknown): string {
  return error instanceof Error ? error.message : String(error);
}

/**
 * A `terrascale.sync.v2` client session over the API host: HTTP session
 * setup with checkpoint resume, SSE trigger notifications that drive
 * immediate checkpoint-bounded delta pulls, snapshot adoption on
 * resync-required, and server-ack-only idempotent mutations.
 *
 * ## Failure and recovery
 *
 * Background failures (a trigger-driven drain, a reconnect tick, a lost
 * stream) route through one typed surface: transient failures record
 * `LastError` on the store and climb a bounded exponential-backoff ladder
 * (`ReconnectDelayMilliseconds` base, jittered, capped at 30 s, at most
 * `MaxReconnectAttempts` attempts; a recovered tick clears the recorded
 * error, and the ladder forgets previous attempts only after a
 * sustained-stability horizon — no stream failure for longer than every
 * gap the ladder can schedule, the 30 s cap plus the 1 s window — so a
 * stream that dies on any cadence inside the horizon is one sustained
 * outage that still reaches `MaxReconnectAttempts`). Terminal codes —
 * `session_closed`,
 * `scope_refused`, `http_401`, `http_403` — and ladder exhaustion
 * transition the session to
 * the terminal `failed` state with `LastError` recorded and the host
 * session identity dropped. The documented re-setup after a terminal
 * failure is a fresh `connect()` (which performs a new host setup resumed
 * from the surviving client checkpoint) or a new session constructed with
 * the durable `ResumeCheckpoint`; the checkpoint survives the failure.
 */
export class TerraScaleSyncV2Session {
  readonly #options: TerraScaleSyncV2SessionOptions;
  readonly #fetch: SyncV2Fetch;
  readonly #streamFactory: SyncV2StreamFactory;
  readonly #store = new TerraScaleSyncV2Store();
  #sessionId: number | null = null;
  #checkpoint: SyncV2Checkpoint = syncV2CanonicalCheckpoint([], 0n);
  #lastEventId: string | null = null;
  #stream: SyncV2Stream | undefined;
  #drain: Promise<void> = Promise.resolve();
  #disposed = false;
  #status: SyncV2ConnectionStatus = "idle";
  #reconnectTimer: ReturnType<typeof setTimeout> | undefined;
  #reconnectAttempts = 0;
  #lastStreamFailureAt = 0;

  public constructor(options: TerraScaleSyncV2SessionOptions) {
    if (options.Url.length === 0 || !/^https?:/u.test(options.Url)) {
      throw new TypeError("Url must be an absolute http(s) API host base URL.");
    }
    if (options.Collection.length === 0 || options.Collection.length > 128) {
      throw new TypeError("Collection must be a bounded non-empty identifier.");
    }
    if (options.Principal.length === 0 || options.Tenant.length === 0) {
      // The host's local gate refuses a frame without backend-supplied
      // context (PD-2026-08-26-11); the session refuses earlier, client-side.
      throw new TypeError("Principal and Tenant (backend-supplied context) are required.");
    }
    if (options.ResumeCheckpoint !== undefined && options.ResumeCheckpoint.length > 0) {
      this.#checkpoint = syncV2CheckpointFromBytes(decodeBase64(options.ResumeCheckpoint, "the resume checkpoint"));
    }
    this.#options = options;
    this.#fetch = options.Fetch
      ?? (async (input, init) => {
        const request: RequestInit = { method: init?.method ?? "POST", headers: { ...init?.headers } };
        if (init?.body !== undefined) {
          request.body = init.body;
        }
        if (init?.signal !== undefined) {
          request.signal = init.signal;
        }
        return globalThis.fetch(input, request);
      });
    this.#streamFactory = options.StreamFactory ?? sseOverFetch(this.#fetch);
  }

  /** The session's observable store (`useSyncExternalStore` compatible). */
  public getStore(): TerraScaleSyncV2Store {
    return this.#store;
  }

  /** The store snapshot (status, checkpoint, application accounting). */
  public getStoreSnapshot(): SyncV2ClientSnapshot {
    return this.#store.getSnapshot();
  }

  /** The session's connection status. */
  public getStatus(): SyncV2ConnectionStatus {
    return this.#status;
  }

  /** The session's durable checkpoint. */
  public getCheckpoint(): SyncV2Checkpoint {
    return this.#checkpoint;
  }

  /** The session's durable checkpoint, base64-encoded for persistence and resume. */
  public getCheckpointBase64(): string {
    return encodeBase64(syncV2CheckpointToBytes(this.#checkpoint));
  }

  /** Opens the session: setup (resuming when constructed with a checkpoint), initial drain, and the push channel. */
  public async connect(signal?: AbortSignal): Promise<void> {
    this.#throwIfDisposed();
    this.#setStatus("connecting");
    try {
      await this.#setupSession(signal);
      await this.#drainDeltas(signal);
      this.#openStream();
      this.#setStatus("live");
    } catch (error) {
      this.#store.fail(error);
      this.#status = "failed";
      throw error;
    }
  }

  /** Pulls delta batches from the durable checkpoint until current. */
  public async pullNow(signal?: AbortSignal): Promise<void> {
    this.#throwIfDisposed();
    await this.#drainDeltas(signal);
  }

  /** Adopts a full snapshot: the resync-required recovery path. */
  public async resync(signal?: AbortSignal): Promise<void> {
    this.#throwIfDisposed();
    await this.#adoptSnapshot(signal);
  }

  /**
   * Submits one server-ack-only mutation (PD-2026-08-26-12 D2): the
   * durable receipt or typed rejection is the only answer; the document
   * state reaches this session through the journal -> delta path. The
   * idempotency key is the retry identity: a retry with the same key is
   * the same mutation.
   */
  public async executeMutation(
    request: SyncV2MutationRequest,
    idempotencyKey: string,
    signal?: AbortSignal,
  ): Promise<SyncV2MutationResult> {
    this.#throwIfDisposed();
    if (this.#sessionId === null) {
      throw new SyncV2SessionError("session_closed", "the session is not open");
    }
    if (request.Delete !== true && request.Put === undefined) {
      throw new TypeError("a mutation carries exactly one of Put or Delete.");
    }
    validateIdempotencyKey(idempotencyKey);
    const change: Record<string, unknown> = request.Delete === true
      ? { delete: true }
      : { put: Object.entries(request.Put ?? {}).map(([name, value]) => ({ name, value: jsonValueOf(value) })) };
    const response = await this.#postJson(
      `/v1/sync/sessions/${this.#sessionId}/mutations`,
      { document: request.Document, change },
      { "idempotency-key": idempotencyKey },
      signal,
    );
    const body = await readJsonObject(response) as {
      sessionId?: unknown;
      outcome?: { outcome?: unknown; receipt?: { sequence?: unknown; version?: unknown; operationId?: unknown }; reason?: unknown };
    };
    const outcome = body.outcome ?? {};
    if (outcome.outcome === "applied") {
      const receipt = outcome.receipt ?? {};
      return {
        Outcome: "applied",
        Receipt: {
          Sequence: numberField(receipt.sequence),
          Version: numberField(receipt.version),
          OperationId: stringField(receipt.operationId),
        },
      };
    }
    return { Outcome: "rejected", Reason: stringField(outcome.reason) };
  }

  /** Closes the session and its stream; the host session is closed explicitly. */
  public async disconnect(): Promise<void> {
    if (this.#disposed || this.#sessionId === null) {
      this.#closeStream();
      this.#setStatus("closed");
      return;
    }
    this.#closeStream();
    try {
      await this.#postJson(`/v1/sync/sessions/${this.#sessionId}/close`, {}, undefined, undefined);
    } finally {
      this.#sessionId = null;
      this.#setStatus("closed");
    }
  }

  /** Disconnects and marks the session unusable. */
  public async dispose(): Promise<void> {
    this.#disposed = true;
    await this.disconnect();
  }

  // --- Internals -----------------------------------------------------------

  async #setupSession(signal?: AbortSignal): Promise<void> {
    const resume = this.#checkpoint.storage_sequence === 0n && this.#checkpoint.ends.length === 0
      ? undefined
      : encodeBase64(syncV2CheckpointToBytes(this.#checkpoint));
    const response = await this.#postJson(
      "/v1/sync/sessions",
      resume === undefined ? { collection: this.#options.Collection } : { collection: this.#options.Collection, resume },
      undefined,
      signal,
    );
    const body = await readJsonObject(response) as { sessionId?: unknown; checkpoint?: unknown; retainedFloor?: unknown };
    this.#sessionId = numberField(body.sessionId);
    const serverCheckpoint = syncV2CheckpointFromBytes(decodeBase64(stringField(body.checkpoint), "the setup checkpoint"));
    if (resume === undefined && advances(this.#checkpoint, serverCheckpoint)) {
      // A fresh session adopts the full state as one snapshot answer.
      await this.#adoptSnapshot(signal);
    }
  }

  async #drainDeltas(signal?: AbortSignal): Promise<void> {
    if (this.#sessionId === null) {
      return;
    }
    // Serialize concurrent pulls (a trigger during a drain joins it);
    // a rejected drain never poisons the next one.
    const run = this.#drain.then(async () => {
      for (let pulled = 0; pulled < maxDrainBatches; pulled++) {
        if (this.#sessionId === null || this.#disposed) {
          return;
        }
        const served = await this.#pullOne(signal);
        if (!served) {
          return;
        }
      }
    });
    this.#drain = run.then(
      () => undefined,
      () => undefined,
    );
    await run;
  }

  async #pullOne(signal?: AbortSignal): Promise<boolean> {
    if (this.#sessionId === null) {
      return false;
    }
    let response: Response;
    try {
      response = await this.#postJson(
        `/v1/sync/sessions/${this.#sessionId}/delta`,
        { from: encodeBase64(syncV2CheckpointToBytes(this.#checkpoint)) },
        undefined,
        signal,
      );
    } catch (error) {
      if (error instanceof SyncV2SessionError && error.resyncRequired) {
        // A checkpoint behind the retained floor is never served a stale
        // delta: the session adopts a full snapshot and recovers. A
        // resync that does not advance the checkpoint stops the drain
        // (the status stays resync-required) instead of spinning.
        this.#setStatus("resync-required");
        const before = this.#checkpoint;
        await this.#adoptSnapshot(signal);
        if (!advances(before, this.#checkpoint)) {
          return false;
        }
        this.#setStatus("live");
        return true;
      }
      throw error;
    }
    const body = await readJsonObject(response) as {
      payloadCodec?: unknown;
      payload?: unknown;
      next?: unknown;
      commits?: unknown;
      singleCommitOversize?: unknown;
    };
    const payload = decodeBase64(stringField(body.payload), "the delta payload");
    const codec = stringField(body.payloadCodec) as SyncV2PayloadCodecName;
    const next = syncV2CheckpointFromBytes(decodeBase64(stringField(body.next), "the next checkpoint"));
    const commits = numberField(body.commits);
    if (commits === 0 || payload.length === 0) {
      return false;
    }
    const applied = this.#store.applyDeltaBatch(payload, codec, next, this.#options.Materializer);
    if (applied) {
      this.#checkpoint = next;
      await this.#acknowledge(signal);
    }
    return applied;
  }

  async #adoptSnapshot(signal?: AbortSignal): Promise<void> {
    if (this.#sessionId === null) {
      throw new SyncV2SessionError("session_closed", "the session is not open");
    }
    const response = await this.#postJson(
      `/v1/sync/sessions/${this.#sessionId}/snapshot`,
      {},
      undefined,
      signal,
    );
    const body = await readJsonObject(response) as { payloadCodec?: unknown; payload?: unknown; checkpoint?: unknown };
    const payload = decodeBase64(stringField(body.payload), "the snapshot payload");
    const codec = stringField(body.payloadCodec) as SyncV2PayloadCodecName;
    const checkpoint = syncV2CheckpointFromBytes(decodeBase64(stringField(body.checkpoint), "the snapshot checkpoint"));
    this.#store.adoptSnapshot(payload, codec, checkpoint, this.#options.Materializer);
    this.#checkpoint = checkpoint;
    await this.#acknowledge(signal);
  }

  async #acknowledge(signal?: AbortSignal): Promise<void> {
    if (this.#sessionId === null) {
      return;
    }
    const checkpoint = encodeBase64(syncV2CheckpointToBytes(this.#checkpoint));
    await this.#postJson(
      `/v1/sync/sessions/${this.#sessionId}/ack`,
      { checkpoint },
      undefined,
      signal,
    );
    const store = this.#options.CheckpointStore;
    if (store !== undefined) {
      await store.save(checkpoint);
    }
  }

  #openStream(): void {
    if (this.#disposed) {
      return;
    }
    this.#closeStream();
    const headers: Record<string, string> = {
      ...this.#contextHeaders(),
      accept: "text/event-stream",
    };
    if (this.#lastEventId !== null) {
      headers["last-event-id"] = this.#lastEventId;
    }
    const url = `${this.#options.Url.replace(/\/$/u, "")}/v1/streams?collections=${encodeURIComponent(this.#options.Collection)}`;
    this.#stream = this.#streamFactory(
      url,
      headers,
      (event) => {
        if (event.id !== null) {
          this.#lastEventId = event.id;
        }
        if (event.event === "trigger" || event.event === "batch" || event.event === "drop") {
          // Push the hint, pull the delta: the trigger is advisory and
          // the checkpoint-bounded pull is authoritative. A drain failure
          // rides the typed failure surface (and its retry ladder), never
          // a silent status flip.
          void this.#drainDeltas().catch((error: unknown) => {
            this.#handleBackgroundFailure(error);
          });
        }
      },
      (error) => this.#onStreamEnded(error),
    );
  }

  #onStreamEnded(error: unknown): void {
    if (this.#disposed || this.#sessionId === null || this.#status === "failed") {
      return;
    }
    if (isTerminalSessionFailure(error)) {
      this.#failTerminally(error);
      return;
    }
    this.#scheduleStreamReconnect(error === undefined ? undefined : errorMessageOf(error));
  }

  /**
   * The single failure surface of background work (trigger-driven drains,
   * reconnect-tick drains, stream ends). Terminal codes fail the session
   * immediately; transient failures record LastError and climb the bounded
   * exponential-backoff ladder.
   */
  #handleBackgroundFailure(error: unknown): void {
    if (this.#disposed || this.#sessionId === null || this.#status === "failed") {
      return;
    }
    if (isTerminalSessionFailure(error)) {
      this.#failTerminally(error);
      return;
    }
    this.#scheduleStreamReconnect(errorMessageOf(error));
  }

  /**
   * The terminal transition: the failure is recorded on the store
   * (LastError), the stream and any pending reconnect are torn down, and
   * the host session identity is dropped — the documented re-setup is a
   * fresh `connect()` (a new host session resumed from the surviving
   * client checkpoint) or a new session constructed with the durable
   * `ResumeCheckpoint`.
   */
  #failTerminally(error: unknown): void {
    if (this.#status === "failed") {
      return;
    }
    this.#closeStream();
    this.#sessionId = null;
    this.#status = "failed";
    this.#store.fail(error);
  }

  #scheduleStreamReconnect(lastError?: string | undefined): void {
    if (this.#disposed || this.#sessionId === null || this.#status === "failed") {
      return;
    }
    if (this.#reconnectTimer !== undefined) {
      // A tick is already scheduled: failures that arrive together (the
      // stream end and the drain of the same tick) climb one ladder.
      return;
    }
    const failureAt = Date.now();
    if (failureAt - this.#lastStreamFailureAt >= reconnectStabilityHorizonMilliseconds) {
      // No stream failure within the sustained-stability horizon — the
      // stream outlived every backoff gap the ladder itself can schedule
      // (the delay cap plus a window) — so this failure starts a fresh
      // outage on a fresh ladder instead of continuing the last one.
      this.#reconnectAttempts = 0;
    }
    // Otherwise the failure sits inside the horizon and continues the same
    // outage: the ladder keeps climbing, so a stream that dies on any
    // cadence inside the horizon (its delta endpoint healthy) still
    // reaches MaxReconnectAttempts instead of resetting its own ladder.
    this.#lastStreamFailureAt = failureAt;
    const maxAttempts = this.#options.MaxReconnectAttempts ?? maxReconnectAttemptsDefault;
    if (this.#reconnectAttempts >= maxAttempts) {
      this.#failTerminally(new SyncV2SessionError(
        "reconnect_exhausted",
        `the stream did not recover within ${maxAttempts} reconnect attempts`,
      ));
      return;
    }
    this.#setStatus("reconnecting", lastError);
    this.#closeStream();
    const base = this.#options.ReconnectDelayMilliseconds ?? reconnectDelayDefault;
    const attempt = this.#reconnectAttempts;
    this.#reconnectAttempts += 1;
    // Exponential backoff with multiplicative jitter, hard-capped: a
    // sustained transient outage retries a bounded number of times with
    // growing, decorrelated spacing instead of a fixed-interval storm.
    const capped = Math.min(reconnectDelayCapMilliseconds, base * 2 ** attempt);
    const delay = Math.max(0, Math.round(capped * (0.5 + Math.random() / 2)));
    this.#reconnectTimer = setTimeout(() => {
      this.#reconnectTimer = undefined;
      if (this.#disposed || this.#sessionId === null || this.#status === "failed") {
        return;
      }
      this.#openStream();
      void this.#drainDeltas().then(() => {
        if (this.#status === "reconnecting") {
          // A completed reconnect-tick drain clears the recorded transient
          // error; the ladder itself resets only after the
          // sustained-stability horizon (a dying stream with a healthy
          // delta endpoint must not reset its own ladder).
          this.#setStatus("live");
        }
      }).catch((error: unknown) => {
        this.#handleBackgroundFailure(error);
      });
    }, delay);
  }

  #closeStream(): void {
    if (this.#reconnectTimer !== undefined) {
      clearTimeout(this.#reconnectTimer);
      this.#reconnectTimer = undefined;
    }
    // Drop the reference before closing so a factory whose close() reports
    // an end synchronously cannot re-enter the reconnect path.
    const stream = this.#stream;
    this.#stream = undefined;
    stream?.close();
  }

  #contextHeaders(): Record<string, string> {
    return { "x-tb-principal": this.#options.Principal, "x-tb-tenant": this.#options.Tenant };
  }

  async #postJson(
    path: string,
    body: unknown,
    extraHeaders?: Readonly<Record<string, string>> | undefined,
    signal?: AbortSignal | undefined,
  ): Promise<Response> {
    const response = await this.#fetch(`${this.#options.Url.replace(/\/$/u, "")}${path}`, {
      method: "POST",
      headers: {
        ...this.#contextHeaders(),
        ...(extraHeaders ?? {}),
        "content-type": "application/json",
      },
      body: JSON.stringify(body),
      signal,
    });
    if (!response.ok) {
      throw await sessionErrorOf(response);
    }
    return response;
  }

  #setStatus(status: SyncV2ConnectionStatus, lastError?: string | undefined): void {
    this.#status = status;
    this.#store.setStatus(status, lastError);
  }

  #throwIfDisposed(): void {
    if (this.#disposed) {
      throw new SyncV2SessionError("session_closed", "the session is disposed");
    }
  }
}

async function sessionErrorOf(response: Response): Promise<SyncV2SessionError> {
  let code = `http_${response.status}`;
  let detail: string | null = null;
  let resyncRequired = false;
  try {
    const body = await response.json() as { code?: unknown; detail?: unknown; resyncRequired?: unknown };
    if (typeof body.code === "string") {
      code = body.code;
    }
    if (typeof body.detail === "string") {
      detail = body.detail;
    }
    resyncRequired = body.resyncRequired === true;
  } catch {
    // A non-JSON refusal stays an HTTP-status error.
  }
  return new SyncV2SessionError(code, detail, resyncRequired);
}

async function readJsonObject(response: Response): Promise<Record<string, unknown>> {
  let parsed: unknown;
  try {
    parsed = await response.json();
  } catch {
    // A body that is not JSON stays inside the typed rejection
    // vocabulary instead of escaping as a raw SyntaxError.
    throw new SyncV2SessionError("malformed_frame", "the host answer is not valid JSON");
  }
  if (parsed === null || typeof parsed !== "object" || Array.isArray(parsed)) {
    throw new SyncV2SessionError("malformed_frame", "the host answer is not a JSON object");
  }
  return parsed as Record<string, unknown>;
}

function stringField(value: unknown): string {
  if (typeof value !== "string") {
    throw new SyncV2SessionError("malformed_frame", "a required string field is absent");
  }
  return value;
}

function numberField(value: unknown): number {
  if (typeof value === "number" && Number.isFinite(value)) {
    return value;
  }
  throw new SyncV2SessionError("malformed_frame", "a required numeric field is absent");
}

function validateIdempotencyKey(key: string): void {
  if (key.trim().length === 0) {
    throw new SyncV2SessionError("invalid_idempotency_key", "the key is blank");
  }
  const bytes = new TextEncoder().encode(key);
  if (bytes.length > syncV2MaxKeyBytes) {
    throw new SyncV2SessionError("invalid_idempotency_key", "the key exceeds its bound");
  }
  for (const byte of bytes) {
    if (byte < 0x20 || byte === 0x7f) {
      throw new SyncV2SessionError("invalid_idempotency_key", "the key carries control characters");
    }
  }
}

/**
 * Serializes one mutation value to the host's one-discriminator JSON value
 * DTO. A bigint outside the exactly representable integer range is refused
 * fail-closed: routing it through `Number` would silently round the durable
 * write (9223372036854775809n would reach the host as 9223372036854776000).
 */
function jsonValueOf(value: SyncV2Value): Record<string, unknown> {
  if (value === null) {
    return { null: true };
  }
  if (typeof value === "boolean") {
    return { boolean: value };
  }
  if (typeof value === "bigint") {
    if (value > maxSafeInteger || value < -maxSafeInteger) {
      throw new SyncV2SessionError(
        "invalid_mutation_value",
        "an integer mutation value exceeds the exactly representable integer range",
      );
    }
    return { integer: Number(value) };
  }
  if (typeof value === "number") {
    return Number.isInteger(value) ? { integer: value } : { number: value };
  }
  if (typeof value === "string") {
    return { string: value };
  }
  // A composite value rides the host's recursive DTO shape; the engine's
  // serving core rejects it with the typed invalid-body outcome (the
  // scalar value domain is the documented mutation contract).
  const composite = value as unknown;
  if (Array.isArray(composite)) {
    return { array: (composite as SyncV2Value[]).map(jsonValueOf) };
  }
  const fields: Record<string, unknown> = {};
  for (const [field, entry] of Object.entries(composite as Record<string, SyncV2Value>)) {
    fields[field] = jsonValueOf(entry);
  }
  return { object: fields };
}

function encodeBase64(bytes: Uint8Array): string {
  let binary = "";
  for (const byte of bytes) {
    binary += String.fromCharCode(byte);
  }
  return globalThis.btoa(binary);
}

function decodeBase64(value: string, what: string): Uint8Array {
  let binary: string;
  try {
    binary = globalThis.atob(value);
  } catch {
    // Notably the durable ResumeCheckpoint at construction: a bad value is
    // a typed rejection, not a raw DOMException.
    throw new SyncV2SessionError("malformed_frame", `${what} is not valid base64`);
  }
  const bytes = new Uint8Array(binary.length);
  for (let index = 0; index < binary.length; index++) {
    bytes[index] = binary.charCodeAt(index);
  }
  return bytes;
}

/**
 * The SSE line bound of the default stream factory: 16x the trigger frame
 * budget (256 bytes) — a trigger notification's JSON envelope rides far
 * below it, and an undelimited line can never grow the buffer unboundedly.
 */
const sseMaxLineCodeUnits = 4096;

/** The per-event data bound: the joined data lines, 64x the trigger budget. */
const sseMaxDataCodeUnits = 16384;

/** The default push channel: SSE parsed over the session's fetch. */
function sseOverFetch(fetchLike: SyncV2Fetch): SyncV2StreamFactory {
  return (url, headers, onEvent, onEnded) => sseOverFetchWith(fetchLike, url, headers, onEvent, onEnded);
}

function sseOverFetchWith(
  fetchLike: SyncV2Fetch,
  url: string,
  headers: Readonly<Record<string, string>>,
  onEvent: (event: SyncV2StreamEvent) => void,
  onEnded: (error: unknown) => void,
): SyncV2Stream {
  const controller = new AbortController();
  const decoder = new TextDecoder("utf-8", { fatal: false });
  let buffer = "";
  let eventName = "message";
  let eventId: string | null = null;
  let dataLines: string[] = [];
  let dataCodeUnits = 0;
  let closed = false;
  let ended = false;

  /** Reports exactly one end (typed error through onEnded) and aborts the fetch. */
  const endWithError = (error: unknown): void => {
    if (closed || ended) {
      return;
    }
    ended = true;
    controller.abort();
    onEnded(error);
  };

  const dispatch = (): void => {
    if (dataLines.length === 0 && eventName === "message") {
      return;
    }
    const data = dataLines.join("\n");
    let parsed: unknown = data;
    try {
      parsed = JSON.parse(data);
    } catch {
      parsed = data;
    }
    onEvent({ event: eventName, id: eventId, data: parsed });
    eventName = "message";
    dataLines = [];
    dataCodeUnits = 0;
  };

  void (async () => {
    try {
      const response = await fetchLike(url, {
        method: "GET",
        headers: { ...headers, accept: "text/event-stream" },
        signal: controller.signal,
      });
      if (!response.ok) {
        // The refusal rides the same typed surface as every other host
        // answer (body code included), so terminal refusals — an expired
        // host session, an auth/scope rejection — are classified.
        throw await sessionErrorOf(response);
      }
      if (response.body === null) {
        throw new SyncV2SessionError("malformed_frame", "the stream endpoint answered without a body");
      }
      const reader = response.body.getReader();
      for (;;) {
        const { done, value } = await reader.read();
        if (done) {
          break;
        }
        buffer += decoder.decode(value, { stream: true });
        let newline = buffer.indexOf("\n");
        while (newline !== -1) {
          const line = buffer.slice(0, newline).replace(/\r$/u, "");
          buffer = buffer.slice(newline + 1);
          if (line === "") {
            dispatch();
          } else if (line.startsWith(":")) {
            // A comment line; ignored.
          } else if (line.startsWith("event:")) {
            eventName = line.slice(6).trim();
          } else if (line.startsWith("id:")) {
            eventId = line.slice(3).trim();
          } else if (line.startsWith("data:")) {
            const data = line.slice(5).replace(/^ /u, "");
            dataCodeUnits += data.length + 1;
            if (dataCodeUnits > sseMaxDataCodeUnits) {
              endWithError(new SyncV2SessionError(
                "stream_limit",
                `an event-stream event exceeds its ${sseMaxDataCodeUnits}-unit data bound`,
              ));
              return;
            }
            dataLines.push(data);
          }
          newline = buffer.indexOf("\n");
        }
        // Only after every complete line is drained does the bound see the
        // residual buffer — the one line the host has not terminated yet.
        // (Evaluated on the read chunk, the bound measured transport
        // coalescing, not line length: one chunk carrying many complete
        // events — each individually far under the bound — tripped it.)
        if (buffer.length > sseMaxLineCodeUnits) {
          // A pathological host streaming an undelimited line can never
          // grow the buffer (or the read loop) without bound.
          endWithError(new SyncV2SessionError(
            "stream_limit",
            `an event-stream line exceeds its ${sseMaxLineCodeUnits}-unit bound`,
          ));
          return;
        }
      }
      if (!closed && !ended) {
        ended = true;
        onEnded(undefined);
      }
    } catch (error) {
      // A mid-stream failure (including the read aborted above) reports at
      // most one end; aborting the fetch releases the connection.
      endWithError(error);
    }
  })();

  return {
    close(): void {
      closed = true;
      controller.abort();
    },
  };
}
