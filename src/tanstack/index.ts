import {
  TerraScaleSyncV2Client,
  type SyncV2Message,
  type SyncV2Envelope,
} from "../sync/v2/client.js";
import { decodeTerrascaleSyncV2MsgpackEnvelope } from "../msgpack/generated/terrascale-sync-v2-msgpack.js";
import {
  SyncV2ResultViewStore,
  SyncV2ViewCheckpoint,
  type SyncV2ResultDelta,
  type SyncV2ResultItem,
  type SyncV2ResultOperation,
} from "../sync/v2/result-view.js";

/** A TanStack DB change message, kept structural so the optional adapter does not
 * make `@tanstack/db` a runtime or package dependency. */
export type TerraScaleTanStackChange<T extends object, TKey extends string | number> =
  | { readonly type: "insert"; readonly value: T; readonly metadata?: Record<string, unknown> }
  | { readonly type: "update"; readonly value: T; readonly metadata?: Record<string, unknown> }
  | { readonly type: "delete"; readonly key: TKey; readonly metadata?: Record<string, unknown> };

export type TerraScaleTanStackDeleteMessage<TKey extends string | number> = {
  readonly type: "delete";
  readonly key: TKey;
  readonly metadata?: Record<string, unknown>;
};

export type TerraScaleTanStackSyncParams<T extends object, TKey extends string | number> = {
  readonly collection: unknown;
  readonly begin: (options?: { readonly immediate?: boolean }) => void;
  readonly write: (message: TerraScaleTanStackChange<T, TKey>) => void;
  readonly commit: (signal?: AbortSignal) => true | Promise<void>;
  readonly markReady: () => void;
  readonly markError: (error?: unknown) => void;
  readonly truncate: () => void;
};

export type TerraScaleTanStackMutation = {
  readonly mutationId?: string;
  readonly key: string | number;
  readonly original?: Record<string, unknown>;
  readonly modified: Record<string, unknown>;
  readonly changes?: Record<string, unknown>;
  readonly type: "insert" | "update" | "delete";
  readonly metadata?: unknown;
};

export type TerraScaleTanStackTransaction<T extends object = Record<string, unknown>> = {
  readonly id?: string;
  readonly mutations: readonly (TerraScaleTanStackMutation & {
    readonly modified: T;
    readonly original?: T;
  })[];
  readonly metadata?: Record<string, unknown>;
};

export type TerraScaleTanStackMutationRequest<T extends object, TKey extends string | number> = {
  readonly operation: "insert" | "update" | "delete";
  readonly idempotencyKey: string;
  readonly transactionId: string;
  readonly mutations: readonly TerraScaleTanStackMutation[];
  readonly signal: AbortSignal;
  readonly keyOf: (item: T) => TKey;
};

export type TerraScaleTanStackMutationResponse =
  | SyncV2ResultDelta
  | { readonly delta: SyncV2ResultDelta }
  | { readonly status: "accepted"; readonly delta?: SyncV2ResultDelta }
  | { readonly status: "rejected"; readonly error?: unknown };

export type TerraScaleTanStackTransport = {
  /** Sends an already encoded public sync-v2 frame. */
  readonly send: (frame: Uint8Array, signal: AbortSignal) => void | Promise<void>;
};

export type TerraScaleTanStackCollectionOptions<T extends object, TKey extends string | number = string> = {
  readonly id?: string;
  readonly getKey: (item: T) => TKey;
  readonly sync: {
    readonly rowUpdateMode: "full";
    readonly sync: (params: TerraScaleTanStackSyncParams<T, TKey>) => (() => void) | void;
  };
  readonly onInsert: (params: { readonly transaction: TerraScaleTanStackTransaction<T> }) => Promise<unknown>;
  readonly onUpdate: (params: { readonly transaction: TerraScaleTanStackTransaction<T> }) => Promise<unknown>;
  readonly onDelete: (params: { readonly transaction: TerraScaleTanStackTransaction<T> }) => Promise<unknown>;
};

export type TerraScaleTanStackAdapterOptions<T extends object, TKey extends string | number = string> = {
  readonly client: TerraScaleSyncV2Client;
  readonly subscription: string;
  readonly query: string;
  readonly parameters?: Uint8Array;
  readonly checkpoint?: SyncV2ViewCheckpoint | Uint8Array;
  readonly getKey: (item: T) => TKey;
  readonly keyFromIdentity?: (identity: string) => TKey;
  readonly id?: string;
  readonly transport: TerraScaleTanStackTransport;
  /** The server write operation. It must return the authoritative delta before
   * the TanStack transaction is considered persisted. */
  readonly mutate: (
    request: TerraScaleTanStackMutationRequest<T, TKey>,
  ) => Promise<TerraScaleTanStackMutationResponse>;
  readonly idempotencyKey?: (transaction: TerraScaleTanStackTransaction<T>) => string;
  readonly signal?: AbortSignal;
};

export type TerraScaleTanStackSyncMeta = {
  readonly checkpoint?: Uint8Array;
  readonly subscription: string;
};

/**
 * An official TanStack DB collection-sync adapter over the public sync-v2
 * client. It owns no local durable writes. Incoming snapshots and deltas are
 * the only source of collection state; optimistic rows live in the reactive
 * view store until their idempotent server write is accepted or rejected.
 */
export class TerraScaleTanStackCollectionSync<T extends object, TKey extends string | number = string> {
  readonly #options: TerraScaleTanStackAdapterOptions<T, TKey>;
  readonly #collectionOptions: TerraScaleTanStackCollectionOptions<T, TKey>;
  readonly #abort = new AbortController();
  readonly #mutationPromises = new Map<string, Promise<unknown>>();
  readonly #mutationIdentities = new Map<string, string>();
  #session: { readonly id: string; readonly view: SyncV2ResultViewStore } | undefined;
  #removeViewListener: (() => void) | undefined;
  #syncParams: TerraScaleTanStackSyncParams<T, TKey> | undefined;
  #published = new Map<string, T>();
  #publishedOrder: string[] = [];
  #ready = false;
  #disposed = false;
  #correlation = 1n;
  #checkpoint: SyncV2ViewCheckpoint | undefined;
  #forceReset = false;

  public constructor(options: TerraScaleTanStackAdapterOptions<T, TKey>) {
    validateOptions(options);
    this.#options = options;
    this.#checkpoint = options.checkpoint instanceof Uint8Array
      ? new SyncV2ViewCheckpoint(options.checkpoint)
      : options.checkpoint;
    if (options.signal !== undefined) {
      if (options.signal.aborted) this.dispose();
      else options.signal.addEventListener("abort", () => this.dispose(), { once: true });
    }

    this.#collectionOptions = Object.freeze({
      ...(options.id === undefined ? {} : { id: options.id }),
      getKey: options.getKey,
      sync: Object.freeze({
        rowUpdateMode: "full" as const,
        sync: (params: TerraScaleTanStackSyncParams<T, TKey>) => this.#start(params),
      }),
      onInsert: (params: { readonly transaction: TerraScaleTanStackTransaction<T> }) => this.#mutate("insert", params.transaction),
      onUpdate: (params: { readonly transaction: TerraScaleTanStackTransaction<T> }) => this.#mutate("update", params.transaction),
      onDelete: (params: { readonly transaction: TerraScaleTanStackTransaction<T> }) => this.#mutate("delete", params.transaction),
    });
  }

  /** Options to pass directly to `createCollection(...)`. */
  public get options(): TerraScaleTanStackCollectionOptions<T, TKey> {
    return this.#collectionOptions;
  }

  /** Feeds one encoded server frame into the existing public reactive client. */
  public receive(frame: Uint8Array): SyncV2Envelope {
    this.#assertActive();
    let messageKind: SyncV2Message["kind"] | undefined;
    try {
      messageKind = decodeTerrascaleSyncV2MsgpackEnvelope(frame).message.kind;
    } catch {
      // The reactive client performs the authoritative protocol validation and
      // reports its typed protocol error below.
    }
    this.#forceReset = messageKind === "snapshot" || messageKind === "reset" || messageKind === "authorization_changed";
    try {
      const envelope = this.#options.client.receive(frame);
      this.#saveCheckpoint();
      return envelope;
    } finally {
      this.#forceReset = false;
    }
  }

  /** Opens the view when a transport is managed outside TanStack. */
  public open(): void {
    if (this.#syncParams === undefined) {
      throw new Error("TerraScale TanStack sync: open() requires the collection sync callback");
    }
    this.#openSession();
  }

  /** Encodes a checkpoint acknowledgement for the active view. */
  public checkpointAcknowledgement(): Extract<SyncV2Message, { readonly kind: "checkpoint_ack" }> {
    this.#assertActive();
    if (this.#session === undefined) throw new Error("TerraScale TanStack sync: no active subscription");
    return this.#options.client.checkpointAcknowledgement(this.#session.id);
  }

  /** Sends the latest checkpoint acknowledgement over the configured transport. */
  public async sendCheckpointAcknowledgement(): Promise<void> {
    const message = this.checkpointAcknowledgement();
    const frame = this.#options.client.encode(message, this.#nextCorrelation());
    await this.#options.transport.send(frame, this.#abort.signal);
  }

  /** Propagates connection state to the existing public reactive client. */
  public setOnline(online: boolean): void {
    this.#assertActive();
    this.#options.client.setOnline(online);
  }

  /** Returns the latest opaque checkpoint as a defensive copy. */
  public getCheckpoint(): Uint8Array | undefined {
    return this.#checkpoint?.toBytes();
  }

  /** Metadata is process-local unless the caller explicitly persists it. */
  public exportSyncMeta(): TerraScaleTanStackSyncMeta {
    return Object.freeze({
      subscription: this.#options.subscription,
      ...(this.#checkpoint === undefined ? {} : { checkpoint: this.#checkpoint.toBytes() }),
    });
  }

  public importSyncMeta(meta: unknown): void {
    this.#assertActive();
    if (!isRecord(meta) || meta.subscription !== this.#options.subscription) return;
    if (meta.checkpoint !== undefined) {
      if (!(meta.checkpoint instanceof Uint8Array)) throw new TypeError("TerraScale TanStack sync metadata checkpoint must be bytes");
      this.#checkpoint = new SyncV2ViewCheckpoint(meta.checkpoint);
    }
  }

  /** Stops delivery, aborts in-flight requests, and closes the public view. */
  public dispose(): void {
    if (this.#disposed) return;
    this.#disposed = true;
    this.#abort.abort();
    this.#removeViewListener?.();
    this.#removeViewListener = undefined;
    const session = this.#session;
    const view = session?.view;
    this.#session = undefined;
    if (session !== undefined) {
      try {
        const unsubscribe = this.#options.client.closeSubscription(session.id);
        if (this.#options.client.getSnapshot().online) {
          const frame = this.#options.client.encode(unsubscribe, this.#nextCorrelation());
          void Promise.resolve(this.#options.transport.send(frame, this.#abort.signal)).catch(() => undefined);
        }
      } catch {
        // A closed transport cannot be sent an unsubscribe. Local disposal is
        // still complete and the server's session retention handles recovery.
      }
    }
    for (const transactionId of this.#mutationPromises.keys()) {
      const identity = this.#mutationIdentities.get(transactionId) ?? transactionId;
      try { view?.rejectMutation(identity); } catch { /* already settled */ }
    }
  }

  #start(params: TerraScaleTanStackSyncParams<T, TKey>): () => void {
    this.#assertActive();
    this.#syncParams = params;
    this.#openSession();
    return () => {
      this.dispose();
    };
  }

  #openSession(): void {
    if (this.#session !== undefined || this.#disposed) return;
    this.#options.client.setOnline(true);
    const opened = this.#options.client.openSubscription({
      subscription: this.#options.subscription,
      query: this.#options.query,
      parameters: new Uint8Array(this.#options.parameters ?? new Uint8Array()),
      ...(this.#checkpoint === undefined ? {} : { checkpoint: this.#checkpoint }),
    });
    this.#session = opened.subscription;
    this.#removeViewListener = opened.subscription.view.subscribe(() => this.#publish());
    try {
      const frame = this.#options.client.encode(opened.message, this.#nextCorrelation());
      const sent = this.#options.transport.send(frame, this.#abort.signal);
      if (sent !== undefined) {
        this.#options.client.setOnline(false);
        void Promise.resolve(sent)
          .then(() => { if (!this.#disposed) this.#options.client.setOnline(true); })
          .catch((error: unknown) => this.#transportFailed(error));
      }
    } catch (error) {
      this.#transportFailed(error);
    }
  }

  #publish(): void {
    const params = this.#syncParams;
    const session = this.#session;
    if (this.#disposed || params === undefined || session === undefined) return;
    if (session.view.getCheckpoint() === undefined) return;
    const visible = session.view.getVisibleItems();
    const next = new Map<string, T>();
    try {
      for (const item of visible) next.set(item.identity, this.#decodeItem(item));
    } catch (error) {
      params.markError(error);
      return;
    }

    try {
      params.begin();
      if (!this.#ready || this.#forceReset || session.view.getViewGeneration() !== this.#publishedGeneration) {
        params.truncate();
        for (const value of next.values()) params.write({ type: "insert", value });
      } else {
        for (const identity of this.#publishedOrder) {
          if (!next.has(identity)) params.write({ type: "delete", key: this.#key(identity) });
        }
        for (const [identity, value] of next) {
          const previous = this.#published.get(identity);
          if (previous !== undefined && sameValue(previous, value)) continue;
          params.write(previous === undefined
            ? { type: "insert", value }
            : { type: "update", value });
        }
      }
      const receipt = params.commit(this.#abort.signal);
      this.#published = next;
      this.#publishedOrder = [...next.keys()];
      this.#publishedGeneration = session.view.getViewGeneration();
      this.#checkpoint = session.view.getCheckpoint();
      const markReady = !this.#ready;
      this.#ready = true;
      if (markReady) {
        if (receipt === true) params.markReady();
        else void receipt.then(() => { if (!this.#disposed) params.markReady(); }).catch(error => { if (!this.#disposed) params.markError(error); });
      } else if (receipt !== true) {
        void receipt.catch((error: unknown) => { if (!this.#disposed) params.markError(error); });
      }
    } catch (error) {
      params.markError(error);
    }
  }

  #publishedGeneration = 0n;

  #decodeItem(item: SyncV2ResultItem): T {
    const value: Record<string, unknown> = Object.create(null) as Record<string, unknown>;
    for (const [path, bytes] of Object.entries(item.value)) {
      let decoded: unknown;
      try { decoded = JSON.parse(new TextDecoder().decode(bytes)); }
      catch (error) { throw new Error(`invalid projected JSON at '${path}'`, { cause: error }); }
      value[path] = decoded;
    }
    return value as T;
  }

  #key(identity: string): TKey {
    return (this.#options.keyFromIdentity?.(identity) ?? identity as unknown as TKey);
  }

  async #mutate(operation: "insert" | "update" | "delete", transaction: TerraScaleTanStackTransaction<T>): Promise<unknown> {
    this.#assertActive();
    const transactionId = transaction.id ?? transaction.mutations[0]?.mutationId;
    if (transactionId === undefined || transactionId.length === 0) throw new TypeError("TanStack transaction requires a stable id");
    const existing = this.#mutationPromises.get(transactionId);
    if (existing !== undefined) return existing;
    const promise = this.#performMutation(operation, transaction, transactionId);
    this.#mutationPromises.set(transactionId, promise);
    promise.finally(() => {
      if (this.#mutationPromises.size > 1024) this.#mutationPromises.delete(this.#mutationPromises.keys().next().value as string);
    }).catch(() => undefined);
    return promise;
  }

  async #performMutation(operation: "insert" | "update" | "delete", transaction: TerraScaleTanStackTransaction<T>, transactionId: string): Promise<unknown> {
    const session = this.#session;
    if (session === undefined || !session.view.isOnline()) throw new Error("durable mutations require an online TanStack sync session");
    const identity = this.#options.idempotencyKey?.(transaction) ?? transactionId;
    if (identity.length === 0 || identity.length > 128) throw new TypeError("TanStack idempotency identity is outside its bound");
    this.#mutationIdentities.set(transactionId, identity);
    const operations = this.#optimisticOperations(operation, transaction.mutations);
    session.view.beginMutation(identity, operations);
    try {
      const response = await this.#options.mutate({
        operation,
        idempotencyKey: identity,
        transactionId,
        mutations: transaction.mutations,
        signal: this.#abort.signal,
        keyOf: this.#options.getKey,
      });
      if (isRejected(response)) {
        session.view.rejectMutation(identity);
        throw response.error ?? new Error("the TerraScale mutation was rejected");
      }
      const delta = isDelta(response) ? response : "delta" in response ? response.delta : undefined;
      if (delta === undefined) throw new Error("the mutation must return its authoritative reactive delta");
      session.view.acceptMutation(identity, delta);
      return response;
    } catch (error) {
      try { session.view.rejectMutation(identity); } catch { /* accepted or reset during the request */ }
      throw error;
    }
  }

  #optimisticOperations(
    operation: "insert" | "update" | "delete",
    mutations: readonly TerraScaleTanStackMutation[],
  ): readonly SyncV2ResultOperation[] {
    const items = this.#session?.view.getVisibleItems() ?? [];
    const operations: SyncV2ResultOperation[] = [];
    let length = items.length;
    for (const mutation of mutations) {
      const identity = String(mutation.key);
      if (operation === "delete") {
        operations.push({ kind: "Remove", identity });
        length = Math.max(0, length - 1);
      }
      else if (operation === "insert") {
        operations.push({ kind: "Insert", identity, position: length, value: encodeValue(mutation.modified) });
        length += 1;
      } else operations.push({ kind: "Update", identity, value: encodeValue(mutation.modified) });
    }
    return operations;
  }

  #saveCheckpoint(): void {
    this.#checkpoint = this.#session?.view.getCheckpoint() ?? this.#checkpoint;
  }

  #transportFailed(error: unknown): void {
    if (this.#disposed) return;
    this.#options.client.setOnline(false);
    if (!this.#ready) this.#syncParams?.markError(error);
  }

  #nextCorrelation(): bigint { return this.#correlation++; }

  #assertActive(): void {
    if (this.#disposed) throw abortError();
    if (this.#abort.signal.aborted) throw abortError();
  }
}

/** Build options for `createCollection(terrascaleCollectionOptions(...))`. */
export function terrascaleCollectionOptions<T extends object, TKey extends string | number = string>(
  options: TerraScaleTanStackAdapterOptions<T, TKey>,
): TerraScaleTanStackCollectionOptions<T, TKey> & { readonly adapter: TerraScaleTanStackCollectionSync<T, TKey> } {
  const adapter = new TerraScaleTanStackCollectionSync(options);
  return Object.freeze({ ...adapter.options, adapter });
}

export const createTerraScaleCollectionOptions = terrascaleCollectionOptions;
export const terraScaleCollectionOptions = terrascaleCollectionOptions;
export const tanstackCollectionOptions = terrascaleCollectionOptions;
export const TerraScaleCollectionSyncAdapter = TerraScaleTanStackCollectionSync;
export const createTerraScaleTanStackCollectionSync = <T extends object, TKey extends string | number = string>(
  options: TerraScaleTanStackAdapterOptions<T, TKey>,
): TerraScaleTanStackCollectionSync<T, TKey> => new TerraScaleTanStackCollectionSync(options);

function encodeValue(value: Record<string, unknown>): Readonly<Record<string, Uint8Array>> {
  const fields: Record<string, Uint8Array> = Object.create(null) as Record<string, Uint8Array>;
  for (const [path, field] of Object.entries(value)) {
    const json = JSON.stringify(field);
    if (json === undefined) throw new TypeError(`cannot optimistically encode field '${path}'`);
    fields[path] = new TextEncoder().encode(json);
  }
  return fields;
}

function sameValue(left: object, right: object): boolean {
  return JSON.stringify(left) === JSON.stringify(right);
}

function isRecord(value: unknown): value is Record<string, any> {
  return typeof value === "object" && value !== null;
}

function isDelta(value: TerraScaleTanStackMutationResponse): value is SyncV2ResultDelta {
  return isRecord(value) && "from_revision" in value && "to_revision" in value
    && typeof value.from_revision === "bigint" && typeof value.to_revision === "bigint";
}

function isRejected(value: TerraScaleTanStackMutationResponse): value is { readonly status: "rejected"; readonly error?: unknown } {
  return isRecord(value) && "status" in value && value.status === "rejected";
}

function validateOptions<T extends object, TKey extends string | number>(options: TerraScaleTanStackAdapterOptions<T, TKey>): void {
  if (options.subscription.trim().length === 0) throw new TypeError("TanStack subscription cannot be empty");
  if (options.query.trim().length === 0) throw new TypeError("TanStack collection query cannot be empty");
}

function abortError(): DOMException {
  return new DOMException("The TerraScale TanStack sync was disposed", "AbortError");
}
