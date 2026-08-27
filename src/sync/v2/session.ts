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
  /** The reconnect delay after a lost stream, in milliseconds (default 100). */
  readonly ReconnectDelayMilliseconds?: number | undefined;
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
      LastError: update.lastError ?? current.LastError,
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
const maxSafeInteger = 9_007_199_254_740_991n;

/**
 * A `terrascale.sync.v2` client session over the API host: HTTP session
 * setup with checkpoint resume, SSE trigger notifications that drive
 * immediate checkpoint-bounded delta pulls, snapshot adoption on
 * resync-required, and server-ack-only idempotent mutations.
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
      this.#checkpoint = syncV2CheckpointFromBytes(decodeBase64(options.ResumeCheckpoint));
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
    const serverCheckpoint = syncV2CheckpointFromBytes(decodeBase64(stringField(body.checkpoint)));
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
    const payload = decodeBase64(stringField(body.payload));
    const codec = stringField(body.payloadCodec) as SyncV2PayloadCodecName;
    const next = syncV2CheckpointFromBytes(decodeBase64(stringField(body.next)));
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
    const payload = decodeBase64(stringField(body.payload));
    const codec = stringField(body.payloadCodec) as SyncV2PayloadCodecName;
    const checkpoint = syncV2CheckpointFromBytes(decodeBase64(stringField(body.checkpoint)));
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
          // the checkpoint-bounded pull is authoritative.
          void this.#drainDeltas().catch(() => {
            this.#setStatus("reconnecting");
          });
        }
      },
      () => this.#scheduleStreamReconnect(),
    );
  }

  #scheduleStreamReconnect(): void {
    if (this.#disposed || this.#sessionId === null) {
      return;
    }
    this.#setStatus("reconnecting");
    this.#stream = undefined;
    const delay = this.#options.ReconnectDelayMilliseconds ?? reconnectDelayDefault;
    this.#reconnectTimer = setTimeout(() => {
      this.#reconnectTimer = undefined;
      if (this.#disposed || this.#sessionId === null) {
        return;
      }
      this.#openStream();
      void this.#drainDeltas().then(() => {
        if (this.#status === "reconnecting") {
          this.#setStatus("live");
        }
      }).catch(() => undefined);
    }, delay);
  }

  #closeStream(): void {
    if (this.#reconnectTimer !== undefined) {
      clearTimeout(this.#reconnectTimer);
      this.#reconnectTimer = undefined;
    }
    this.#stream?.close();
    this.#stream = undefined;
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

  #setStatus(status: SyncV2ConnectionStatus): void {
    this.#status = status;
    this.#store.setStatus(status);
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
  const body = await response.json() as unknown;
  if (body === null || typeof body !== "object" || Array.isArray(body)) {
    throw new SyncV2SessionError("malformed_frame", "the host answer is not a JSON object");
  }
  return body as Record<string, unknown>;
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

function decodeBase64(value: string): Uint8Array {
  const binary = globalThis.atob(value);
  const bytes = new Uint8Array(binary.length);
  for (let index = 0; index < binary.length; index++) {
    bytes[index] = binary.charCodeAt(index);
  }
  return bytes;
}

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
  let closed = false;

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
  };

  void (async () => {
    try {
      const response = await fetchLike(url, {
        method: "GET",
        headers: { ...headers, accept: "text/event-stream" },
        signal: controller.signal,
      });
      if (!response.ok || response.body === null) {
        throw new SyncV2SessionError(`http_${response.status}`, "the stream endpoint refused the subscription");
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
            dataLines.push(line.slice(5).replace(/^ /u, ""));
          }
          newline = buffer.indexOf("\n");
        }
      }
      onEnded(undefined);
    } catch (error) {
      if (!closed) {
        onEnded(error);
      }
    }
  })();

  return {
    close(): void {
      closed = true;
      controller.abort();
    },
  };
}
