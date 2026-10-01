import { createDatabaseQuery, DatabaseSubscriptionError } from "../database-view.js";
/** @import { DatabaseQueryOptions, DatabaseViewPosition, DatabaseViewSnapshot } from "../database-view.js" */
/** @import { PreparedDatabaseCommit, TerraBaseClient } from "../database.js" */
/** @import { DatabaseCommitReceipt, DatabaseQueryRow, DatabaseResult, DatabaseScope } from "../database-types.js" */

/**
 * Structural TanStack DB callbacks keep this optional integration dependency free.
 *
 * @template {object} T
 * @template {string | number} TKey
 * @typedef {{ readonly type: "insert" | "update"; readonly value: T }
 *   | { readonly type: "delete"; readonly key: TKey }} TerraScaleTanStackChange
 */

/**
 * @template {object} T
 * @template {string | number} TKey
 * @typedef {{
 *   readonly collection: unknown;
 *   readonly begin: (options?: { readonly immediate?: boolean }) => void;
 *   readonly write: (message: TerraScaleTanStackChange<T, TKey>) => void;
 *   readonly commit: (signal?: AbortSignal) => true | Promise<void>;
 *   readonly markReady: () => void;
 *   readonly markError: (error?: unknown) => void;
 *   readonly truncate: () => void;
 * }} TerraScaleTanStackSyncParams
 */

/**
 * @template {object} [T=Record<string, unknown>]
 * @typedef {{
 *   readonly mutationId?: string;
 *   readonly key: string | number;
 *   readonly original?: Partial<T>;
 *   readonly modified: T;
 *   readonly changes?: Partial<T>;
 *   readonly type: "insert" | "update" | "delete";
 *   readonly metadata?: unknown;
 * }} TerraScaleTanStackMutation
 */

/**
 * @template {object} [T=Record<string, unknown>]
 * @typedef {{
 *   readonly id?: string;
 *   readonly mutations: readonly TerraScaleTanStackMutation<T>[];
 *   readonly metadata?: Record<string, unknown>;
 * }} TerraScaleTanStackTransaction
 */

/**
 * @template {object} T
 * @template {string | number} TKey
 * @typedef {{
 *   readonly operation: "insert" | "update" | "delete";
 *   readonly idempotencyKey: string;
 *   readonly transactionId: string;
 *   readonly transaction: TerraScaleTanStackTransaction<T>;
 *   readonly scope: DatabaseScope;
 *   readonly collection: string;
 *   readonly signal: AbortSignal;
 *   readonly keyOf: (item: T) => TKey;
 * }} TerraScaleTanStackMutationRequest
 */

/**
 * @template {object} T
 * @template {string | number} [TKey=string]
 * @typedef {{
 *   readonly id?: string;
 *   readonly getKey: (item: T) => TKey;
 *   readonly sync: {
 *     readonly rowUpdateMode: "full";
 *     readonly sync: (params: TerraScaleTanStackSyncParams<T, TKey>) => () => void;
 *   };
 *   readonly onInsert: (params: { readonly transaction: TerraScaleTanStackTransaction<T> }) => Promise<DatabaseCommitReceipt>;
 *   readonly onUpdate: (params: { readonly transaction: TerraScaleTanStackTransaction<T> }) => Promise<DatabaseCommitReceipt>;
 *   readonly onDelete: (params: { readonly transaction: TerraScaleTanStackTransaction<T> }) => Promise<DatabaseCommitReceipt>;
 * }} TerraScaleTanStackCollectionOptions
 */

/**
 * `mapRow` maps the complete native point explicitly; conflicts are never
 * flattened. `prepareMutation` prepares through this same client. The adapter
 * sends the resulting commit to the server once; TanStack owns its reversible
 * optimistic presentation.
 *
 * @template {object} T
 * @template {string | number} [TKey=string]
 * @typedef {DatabaseQueryOptions & {
 *   readonly client: TerraBaseClient;
 *   readonly mapRow: (row: DatabaseQueryRow) => T;
 *   readonly getKey: (item: T) => TKey;
 *   readonly id?: string;
 *   readonly prepareMutation?: (request: TerraScaleTanStackMutationRequest<T, TKey>) => Promise<PreparedDatabaseCommit>;
 *   readonly idempotencyKey?: (transaction: TerraScaleTanStackTransaction<T>) => string;
 *   readonly signal?: AbortSignal;
 * }} TerraScaleTanStackAdapterOptions
 */

/**
 * @param {unknown} value
 * @param {Set<object>} [seen]
 * @returns {unknown}
 */
function canonical(value, seen = new Set()) {
  if (typeof value === "bigint") return ["bigint", value.toString()];
  if (value === undefined) return ["undefined"];
  if (value === null || typeof value === "string" || typeof value === "boolean") return [typeof value, value];
  if (typeof value === "number" && Number.isFinite(value)) return ["number", value];
  if (value instanceof Uint8Array) return ["bytes", [...value]];
  if (typeof value !== "object" || value === null || seen.has(value)) throw new TypeError("TanStack values must be acyclic data.");
  seen.add(value);
  try {
    if (Array.isArray(value)) return ["array", value.map(entry => canonical(entry, seen))];
    if (Object.getPrototypeOf(value) !== Object.prototype && Object.getPrototypeOf(value) !== null) throw new TypeError("TanStack values must contain plain objects.");
    return ["object", Object.entries(value).sort(([left], [right]) => left < right ? -1 : left > right ? 1 : 0).map(([key, entry]) => [key, canonical(entry, seen)])];
  } finally { seen.delete(value); }
}

/**
 * @param {unknown} value
 * @returns {string}
 */
function fingerprint(value) { return JSON.stringify(canonical(value)); }

/**
 * @param {Promise<void>} pending
 * @param {AbortSignal} signal
 * @returns {Promise<void>}
 */
async function acknowledged(pending, signal) {
  signal.throwIfAborted();
  return new Promise((resolve, reject) => {
    /** @returns {void} */
    const abort = () => reject(signal.reason);
    signal.addEventListener("abort", abort, { once: true });
    pending.then(resolve, reject).finally(() => signal.removeEventListener("abort", abort));
  });
}

/**
 * Every complete native cut owns its mapped values until collection acknowledgment.
 *
 * @template {object} T
 * @template {string | number} [TKey=string]
 */
export class TerraScaleTanStackCollectionPublisher {
  /** @readonly @type {TerraScaleTanStackSyncParams<T, TKey>} */
  #params;
  /** @readonly @type {(row: DatabaseQueryRow) => T} */
  #mapRow;
  /** @readonly @type {(item: T) => TKey} */
  #getKey;
  /** @readonly */
  #abort = new AbortController();
  /** @type {Map<string, { readonly value: T; readonly key: TKey; readonly fingerprint: string }>} */
  #published = new Map();
  /** @type {DatabaseViewPosition | undefined} */
  #position;
  #ready = false;
  #needsReset = true;
  #epoch = 0;
  /** @type {Promise<void>} */
  #tail = Promise.resolve();

  /**
   * @param {TerraScaleTanStackSyncParams<T, TKey>} params
   * @param {(row: DatabaseQueryRow) => T} mapRow
   * @param {(item: T) => TKey} getKey
   */
  constructor(params, mapRow, getKey) {
    this.#params = params;
    this.#mapRow = mapRow;
    this.#getKey = getKey;
  }

  /** @returns {DatabaseViewPosition | undefined} */
  getPosition() { return this.#position; }

  /** @returns {void} */
  reset() {
    this.#epoch += 1;
    this.#position = undefined;
    this.#ready = false;
    this.#needsReset = true;
  }

  /** @returns {void} */
  dispose() { this.#abort.abort(); }

  /**
   * @param {DatabaseViewSnapshot} snapshot
   * @param {AbortSignal} [externalSignal]
   * @returns {Promise<void>}
   */
  publish(snapshot, externalSignal) {
    const signal = AbortSignal.any([this.#abort.signal, ...(externalSignal === undefined ? [] : [externalSignal])]);
    /** @type {Map<string, { readonly value: T; readonly key: TKey; readonly fingerprint: string }>} */
    let values;
    const epoch = this.#epoch;
    const position = Object.freeze({ ...snapshot.position });
    try {
      signal.throwIfAborted();
      values = new Map();
      /** @type {Set<TKey>} */
      const keys = new Set();
      for (const row of snapshot.rows) {
        const value = structuredClone(this.#mapRow(row));
        const key = this.#getKey(structuredClone(value));
        if ((typeof key !== "string" && typeof key !== "number") || (typeof key === "number" && !Number.isFinite(key)) || keys.has(key) || values.has(row.document)) {
          throw new TypeError("TanStack rows require distinct string or finite number keys.");
        }
        keys.add(key);
        values.set(row.document, { value, key, fingerprint: fingerprint(value) });
      }
    } catch (error) { return Promise.reject(error); }
    const run = this.#tail.then(async () => {
      signal.throwIfAborted();
      if (epoch !== this.#epoch) throw new DOMException("The native publication was reset", "AbortError");
      const params = this.#params;
      try {
        params.begin();
        if (this.#needsReset || this.#position?.generation !== position.generation) {
          params.truncate();
          for (const row of values.values()) params.write({ type: "insert", value: structuredClone(row.value) });
        } else {
          for (const [document, previous] of this.#published) {
            const next = values.get(document);
            if (next === undefined || next.key !== previous.key) params.write({ type: "delete", key: previous.key });
          }
          for (const [document, row] of values) {
            const previous = this.#published.get(document);
            if (previous?.fingerprint === row.fingerprint && previous.key === row.key) continue;
            params.write({ type: previous === undefined || previous.key !== row.key ? "insert" : "update", value: structuredClone(row.value) });
          }
        }
        const receipt = params.commit(signal);
        if (receipt !== true) await acknowledged(receipt, signal);
        signal.throwIfAborted();
        if (epoch !== this.#epoch) throw new DOMException("The native publication was reset", "AbortError");
        this.#published = values;
        this.#position = position;
        this.#needsReset = false;
        if (!this.#ready) { this.#ready = true; params.markReady(); }
      } catch (error) {
        this.#needsReset = true;
        throw error;
      }
    });
    this.#tail = run.then(() => undefined, () => undefined);
    return run;
  }
}

export class TerraScaleTanStackMutationError extends Error {
  /** @override @readonly */
  name = "TerraScaleTanStackMutationError";
  /** @readonly @type {Extract<DatabaseResult<DatabaseCommitReceipt>, { readonly ok: false }>["error"]} */
  detail;
  /** @returns {string} */
  get code() { return this.detail.code; }
  /**
   * @param {Extract<DatabaseResult<DatabaseCommitReceipt>, { readonly ok: false }>["error"]} detail
   */
  constructor(detail) {
    super(`TerraBase mutation: ${detail.code}; outcome ${detail.mutation_outcome ?? "unknown"}`, { cause: detail });
    this.detail = detail;
  }
}

/**
 * Native HTTP snapshot/poll collection adapter. All durable writes reach the server.
 *
 * @template {object} T
 * @template {string | number} [TKey=string]
 */
export class TerraScaleTanStackCollectionSync {
  /** @readonly @type {TerraScaleTanStackAdapterOptions<T, TKey>} */
  #options;
  /** @readonly @type {ReturnType<typeof createDatabaseQuery>} */
  #query;
  /** @readonly */
  #abort = new AbortController();
  /** @readonly @type {Map<string, { readonly fingerprint: string; readonly promise: Promise<DatabaseCommitReceipt> }>} */
  #mutations = new Map();
  /** @readonly @type {TerraScaleTanStackCollectionOptions<T, TKey>} */
  #collectionOptions;
  /** @type {TerraScaleTanStackCollectionPublisher<T, TKey> | undefined} */
  #publisher;
  /** @type {AbortController | undefined} */
  #sessionAbort;
  /** @type {AbortController | undefined} */
  #deliveryAbort;
  /** @type {(() => void) | undefined} */
  #stopDelivery;
  /** @type {(() => void) | undefined} */
  #removeExternalAbort;
  /** @type {Promise<void>} */
  #mutationTail = Promise.resolve();
  #online = false;
  /** @type {(error: unknown) => void} */
  #paramsError = () => undefined;

  /**
   * @param {TerraScaleTanStackAdapterOptions<T, TKey>} options
   */
  constructor(options) {
    this.#options = { ...options };
    this.#query = createDatabaseQuery(options.client, options);
    this.#collectionOptions = Object.freeze({
      ...(options.id === undefined ? {} : { id: options.id }),
      getKey: options.getKey,
      sync: Object.freeze({ rowUpdateMode: /** @type {const} */ ("full"), sync: /** @param {TerraScaleTanStackSyncParams<T, TKey>} params */ params => this.#start(params) }),
      onInsert: /** @param {{ readonly transaction: TerraScaleTanStackTransaction<T> }} params */ params => this.#mutate("insert", params.transaction),
      onUpdate: /** @param {{ readonly transaction: TerraScaleTanStackTransaction<T> }} params */ params => this.#mutate("update", params.transaction),
      onDelete: /** @param {{ readonly transaction: TerraScaleTanStackTransaction<T> }} params */ params => this.#mutate("delete", params.transaction),
    });
    if (options.signal !== undefined) {
      if (options.signal.aborted) this.dispose();
      else {
        /** @returns {void} */
        const abort = () => this.dispose();
        options.signal.addEventListener("abort", abort, { once: true });
        this.#removeExternalAbort = () => options.signal?.removeEventListener("abort", abort);
      }
    }
  }

  /** @returns {TerraScaleTanStackCollectionOptions<T, TKey>} */
  get options() { return this.#collectionOptions; }
  /** @returns {string | undefined} */
  getCheckpoint() { return this.#publisher?.getPosition()?.poll_cursor; }

  /**
   * Reconnect correctness starts with a fresh, complete authorized snapshot.
   *
   * @returns {void}
   */
  resubscribe() {
    this.#assertActive();
    const publisher = this.#publisher;
    if (publisher === undefined) throw new Error("The collection sync callback has not started.");
    this.#stop();
    if (this.#publisher !== publisher || this.#deliveryAbort !== undefined || this.#abort.signal.aborted) return;
    publisher.reset();
    this.#open();
  }

  /** @returns {void} */
  dispose() {
    if (this.#abort.signal.aborted) return;
    this.#abort.abort();
    this.#endSession();
    this.#removeExternalAbort?.();
    this.#removeExternalAbort = undefined;
  }

  /**
   * @param {TerraScaleTanStackSyncParams<T, TKey>} params
   * @returns {() => void}
   */
  #start(params) {
    this.#assertActive();
    if (this.#publisher !== undefined) throw new Error("The collection sync callback already started.");
    const publisher = new TerraScaleTanStackCollectionPublisher(params, this.#options.mapRow, this.#options.getKey);
    this.#publisher = publisher;
    this.#sessionAbort = new AbortController();
    this.#paramsError = error => params.markError(error);
    this.#open();
    // TanStack cleanup releases one sync session; the collection may restart.
    return () => { if (this.#publisher === publisher) this.#endSession(); };
  }

  /** @returns {void} */
  #endSession() {
    const publisher = this.#publisher;
    const session = this.#sessionAbort;
    this.#publisher = undefined;
    this.#sessionAbort = undefined;
    this.#paramsError = () => undefined;
    this.#stop();
    session?.abort();
    publisher?.dispose();
  }

  /** @returns {void} */
  #open() {
    const publisher = this.#publisher;
    const session = this.#sessionAbort;
    if (publisher === undefined || session === undefined) throw new Error("The collection sync callback has not started.");
    const delivery = new AbortController();
    this.#deliveryAbort = delivery;
    const signal = AbortSignal.any([this.#abort.signal, session.signal, delivery.signal]);
    this.#stopDelivery = this.#query.subscribe({
      next: async (rows, position) => {
        await publisher.publish({ rows, position }, signal);
        if (!signal.aborted) this.#online = true;
      },
      reset: reset => {
        if (signal.aborted) return;
        this.#online = false;
        publisher.reset();
        this.#reportError(new DatabaseSubscriptionError(reset.code ?? reset.reason));
      },
      error: error => {
        if (signal.aborted) return;
        this.#online = false;
        this.#reportError(error);
      },
    }, { signal });
  }

  /** @returns {void} */
  #stop() {
    this.#online = false;
    const delivery = this.#deliveryAbort;
    const stop = this.#stopDelivery;
    this.#deliveryAbort = undefined;
    this.#stopDelivery = undefined;
    delivery?.abort();
    stop?.();
  }

  /**
   * @param {unknown} error
   * @returns {void}
   */
  #reportError(error) {
    try { this.#paramsError(error); } catch { /* consumer callback failed */ }
  }

  /**
   * @param {"insert" | "update" | "delete"} operation
   * @param {TerraScaleTanStackTransaction<T>} transaction
   * @returns {Promise<DatabaseCommitReceipt>}
   */
  async #mutate(operation, transaction) {
    this.#assertActive();
    const transactionId = transaction.id ?? transaction.mutations[0]?.mutationId;
    if (transactionId === undefined || transactionId.length === 0 || transactionId.length > 128) throw new TypeError("TanStack transaction requires a bounded stable identity.");
    if (transaction.mutations.length === 0 || transaction.mutations.length > 128 || transaction.mutations.some(mutation => mutation.type !== operation)) throw new TypeError("TanStack transaction operations are empty, mixed, or outside their bound.");
    // TanStack's real Transaction carries functions, collection instances and
    // deferred receipts. Capture only the declared mutation data, never the
    // framework transaction object or its runtime ownership graph.
    /** @type {TerraScaleTanStackTransaction<T>} */
    const captured = structuredClone({
      ...(transaction.id === undefined ? {} : { id: transaction.id }),
      ...(transaction.metadata === undefined ? {} : { metadata: transaction.metadata }),
      mutations: transaction.mutations.map(mutation => ({
        key: mutation.key, type: mutation.type, modified: mutation.modified,
        ...(mutation.mutationId === undefined ? {} : { mutationId: mutation.mutationId }),
        ...(mutation.original === undefined ? {} : { original: mutation.original }),
        ...(mutation.changes === undefined ? {} : { changes: mutation.changes }),
        ...(mutation.metadata === undefined ? {} : { metadata: mutation.metadata }),
      })),
    });
    const digest = fingerprint({ operation, transaction: captured });
    const existing = this.#mutations.get(transactionId);
    if (existing !== undefined) {
      if (existing.fingerprint !== digest) throw new TypeError("The transaction identity was reused with different effects.");
      return existing.promise;
    }
    if (!this.#online) throw new Error("Durable mutations require a current native subscription.");
    const prepareMutation = this.#options.prepareMutation;
    if (prepareMutation === undefined) throw new Error("This collection is read only.");
    if (this.#mutations.size >= 1024) throw new Error("The adapter's retained mutation identity limit was reached.");
    const idempotencyKey = this.#options.idempotencyKey?.(structuredClone(captured)) ?? transactionId;
    if (idempotencyKey.length === 0 || idempotencyKey.length > 128) throw new TypeError("Idempotency identity is outside its bound.");
    const session = this.#sessionAbort;
    if (session === undefined) throw new Error("Durable mutations require a current native subscription.");
    const signal = AbortSignal.any([this.#abort.signal, session.signal]);
    const run = this.#mutationTail.then(async () => {
      this.#assertActive();
      signal.throwIfAborted();
      if (!this.#online) throw new Error("Durable mutations require a current native subscription.");
      const prepared = await prepareMutation({
        operation, idempotencyKey, transactionId, transaction: captured,
        scope: this.#query.binding.scope, collection: this.#query.binding.collection,
        signal, keyOf: this.#options.getKey,
      });
      signal.throwIfAborted();
      const result = await this.#options.client.commit(prepared, { signal });
      if (!result.ok) throw new TerraScaleTanStackMutationError(result.error);
      this.#assertActive();
      signal.throwIfAborted();
      return result.value;
    });
    this.#mutations.set(transactionId, { fingerprint: digest, promise: run });
    this.#mutationTail = run.then(() => undefined, () => undefined);
    return run;
  }

  /** @returns {void} */
  #assertActive() {
    if (this.#abort.signal.aborted) throw new DOMException("The TerraScale TanStack adapter was disposed", "AbortError");
  }
}

/**
 * Options for `createCollection(terrascaleCollectionOptions(...))`.
 *
 * @template {object} T
 * @template {string | number} [TKey=string]
 * @param {TerraScaleTanStackAdapterOptions<T, TKey>} options
 * @returns {TerraScaleTanStackCollectionOptions<T, TKey> & { readonly adapter: TerraScaleTanStackCollectionSync<T, TKey> }}
 */
export function terrascaleCollectionOptions(options) {
  const adapter = new TerraScaleTanStackCollectionSync(options);
  return Object.freeze({ ...adapter.options, adapter });
}
