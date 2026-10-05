import { encodeDatabaseSubscription, requireDatabaseUuid, requireDatabaseU64 } from "./database-codec.js";
/**
 * @import { TerraBaseClient } from "./local/legacy-client.js"
 * @import {
 *   DatabaseField, DatabasePoint, DatabaseQueryPredicate, DatabaseQueryRow,
 *   DatabaseResult, DatabaseScope, DatabaseSubscriptionRequest,
 *   DatabaseSubscriptionResponse, DatabaseValue,
 * } from "./database-types.js"
 * @import { ReactiveReset, ReactiveSubscriptionContext } from "./react/core.js"
 */

/**
 * - `limit`: snapshot page size, between 1 and 128.
 * - `maximumRows`: client memory bound, including all snapshot pages and later inserts.
 * - `retryLimit`: bounded retries of read transport failures with the identical cursor.
 * @typedef {{
 *   readonly collection: string;
 *   readonly predicate: DatabaseQueryPredicate;
 *   readonly limit?: number;
 *   readonly maximumRows?: number;
 *   readonly pollIntervalMs?: number;
 *   readonly retryLimit?: number;
 *   readonly retryDelayMs?: number;
 * }} DatabaseQueryOptions
 */

/**
 * @typedef {{
 *   readonly scope: DatabaseScope;
 *   readonly collection: string;
 *   readonly predicate: DatabaseQueryPredicate;
 *   readonly limit: number;
 * }} DatabaseViewBinding
 */

/**
 * Local generation and server position of one complete authorized cut.
 * @typedef {{
 *   readonly generation: number;
 *   readonly poll_cursor: string;
 *   readonly sequence?: bigint;
 * }} DatabaseViewPosition
 */

/**
 * @typedef {{
 *   readonly rows: readonly DatabaseQueryRow[];
 *   readonly position: DatabaseViewPosition;
 * }} DatabaseViewSnapshot
 */

/**
 * `next`: an async consumer can acknowledge publication before the next poll.
 * @typedef {{
 *   readonly next: (rows: readonly DatabaseQueryRow[], position: DatabaseViewPosition) => void | Promise<void>;
 *   readonly error: (error: unknown) => void;
 *   readonly reset: (reset: ReactiveReset<readonly DatabaseQueryRow[]>) => void;
 * }} DatabaseQueryCallbacks
 */

/**
 * @typedef {{
 *   readonly key?: string | readonly unknown[];
 *   readonly binding: DatabaseViewBinding;
 *   readonly subscribe: (
 *     observer: DatabaseQueryCallbacks,
 *     context: ReactiveSubscriptionContext,
 *   ) => () => void;
 * }} DatabaseQuerySource
 */

export class DatabaseSubscriptionError extends Error {
  /**
   * @override
   * @readonly
   * @type {"DatabaseSubscriptionError"}
   */
  name = "DatabaseSubscriptionError";
  /**
   * @readonly
   * @type {string}
   */
  code;
  /**
   * @readonly
   * @type {Extract<DatabaseResult<never>, { readonly ok: false }>["error"] | undefined}
   */
  detail;
  /**
   * @param {string} code
   * @param {Extract<DatabaseResult<never>, { readonly ok: false }>["error"]} [detail]
   */
  constructor(code, detail) {
    super(`TerraBase subscription: ${code}`, { cause: detail });
    this.code = code;
    this.detail = detail;
  }
}

/**
 * @param {number | undefined} value
 * @param {number} fallback
 * @param {number} maximum
 * @param {string} label
 * @returns {number}
 */
function boundedInteger(value, fallback, maximum, label) {
  const resolved = value ?? fallback;
  if (!Number.isInteger(resolved) || resolved < 1 || resolved > maximum) throw new TypeError(`${label} is outside its bounds.`);
  return resolved;
}

/**
 * @param {DatabaseValue} value
 * @returns {DatabaseValue}
 */
function valueCopy(value) {
  switch (value.type) {
    case "bytes": {
      const bytes = value.value.slice();
      // A typed array cannot be frozen. Keep its backing bytes private and
      // return a copy on every read, including reads by another subscriber.
      return Object.freeze({ type: /** @type {const} */ ("bytes"), get value() { return bytes.slice(); } });
    }
    case "map": return Object.freeze({ type: "map", fields: fieldsCopy(value.fields) });
    case "array": return Object.freeze({ type: "array", items: Object.freeze(value.items.map(valueCopy)) });
    default: return Object.freeze({ ...value });
  }
}

/**
 * @param {readonly DatabaseField[]} fields
 * @returns {readonly DatabaseField[]}
 */
function fieldsCopy(fields) {
  return Object.freeze(fields.map(field => Object.freeze({ id: field.id, value: valueCopy(field.value) })));
}

/**
 * @param {DatabaseQueryPredicate} predicate
 * @returns {DatabaseQueryPredicate}
 */
function predicateCopy(predicate) {
  const path = Object.freeze([...predicate.path]);
  if (!("value" in predicate)) return Object.freeze({ op: predicate.op, path });
  return Object.freeze({ op: predicate.op, path, value: /** @type {typeof predicate.value} */ (valueCopy(predicate.value)) });
}

/**
 * @param {DatabasePoint} point
 * @param {DatabaseScope} scope
 * @returns {DatabasePoint}
 */
function pointCopy(point, scope) {
  if (point.siblings.some(sibling => sibling.event.database !== scope.database || sibling.event.shard !== scope.shard)) {
    throw new DatabaseSubscriptionError("scope_mismatch");
  }
  return Object.freeze({
    ...point,
    context: Object.freeze(point.context.map(observed => Object.freeze({ ...observed }))),
    siblings: Object.freeze(point.siblings.map(sibling => Object.freeze({
      ...sibling,
      event: Object.freeze({ ...sibling.event }),
      ...(sibling.kind === "live" ? { fields: fieldsCopy(sibling.fields) } : {}),
    }))),
  });
}

/**
 * @param {string} value
 * @returns {string}
 */
function cursor(value) {
  if (typeof value !== "string" || value.length === 0 || value.length > 4096) throw new DatabaseSubscriptionError("invalid_cursor");
  return value;
}

/**
 * Pure native result reducer. A paged snapshot stays private until its
 * terminal poll cursor. Poll groups are staged in order and published as one
 * complete response; invalid input cannot partially replace the prior cut.
 */
export class DatabaseSubscriptionView {
  /**
   * @readonly
   * @type {DatabaseViewBinding}
   */
  binding;
  /**
   * @readonly
   * @type {number}
   */
  #maximumRows;
  /** @type {Map<string, DatabaseQueryRow>} */
  #rows = new Map();
  /** @type {DatabaseQueryRow[]} */
  #snapshotRows = [];
  /** @type {string | undefined} */
  #snapshotCursor;
  /** @type {Set<string>} */
  #seenSnapshotCursors = new Set();
  /** @type {string | undefined} */
  #pollCursor;
  /** @type {bigint | undefined} */
  #sequence;
  #generation = 0;
  /** @type {DatabaseViewSnapshot | undefined} */
  #snapshot;

  /**
   * @param {DatabaseScope} scope
   * @param {Pick<DatabaseQueryOptions, "collection" | "predicate" | "limit" | "maximumRows">} options
   */
  constructor(scope, options) {
    const capturedScope = Object.freeze({
      tenant: requireDatabaseUuid(scope.tenant, "tenant"),
      database: requireDatabaseUuid(scope.database, "database"),
      shard: requireDatabaseU64(scope.shard, "shard"),
    });
    const predicate = predicateCopy(options.predicate);
    const limit = boundedInteger(options.limit, 128, 128, "Snapshot page size");
    encodeDatabaseSubscription({ phase: "snapshot", predicate, limit });
    this.binding = Object.freeze({ scope: capturedScope, collection: requireDatabaseUuid(options.collection, "collection"), predicate, limit });
    this.#maximumRows = boundedInteger(options.maximumRows, 10_000, 1_000_000, "Maximum rows");
  }

  /** @returns {DatabaseViewSnapshot | undefined} */
  getSnapshot() { return this.#snapshot; }

  /** @returns {DatabaseSubscriptionRequest} */
  getRequest() {
    const { predicate, limit } = this.binding;
    return this.#pollCursor === undefined
      ? Object.freeze({ phase: "snapshot", predicate, limit, ...(this.#snapshotCursor === undefined ? {} : { snapshot_cursor: this.#snapshotCursor }) })
      : Object.freeze({ phase: "poll", predicate, poll_cursor: this.#pollCursor });
  }

  /**
   * Discard every opaque position; the next request starts a fresh cut.
   * @returns {void}
   */
  restart() {
    this.#snapshotRows = [];
    this.#snapshotCursor = undefined;
    this.#seenSnapshotCursors.clear();
    this.#pollCursor = undefined;
    this.#sequence = undefined;
    this.#snapshot = undefined;
  }

  /**
   * @param {DatabaseSubscriptionResponse} response
   * @returns {DatabaseViewSnapshot | undefined}
   */
  apply(response) {
    if (this.#pollCursor === undefined) {
      if (response.phase !== "snapshot") throw new DatabaseSubscriptionError("phase_mismatch");
      if (response.rows.length > this.binding.limit) throw new DatabaseSubscriptionError("page_limit_exceeded");
      let previous = this.#snapshotRows.at(-1)?.document ?? "";
      const page = response.rows.map(row => {
        const document = requireDatabaseUuid(row.document, "document");
        if (document <= previous) throw new DatabaseSubscriptionError("snapshot_order_invalid");
        previous = document;
        return Object.freeze({ document, point: pointCopy(row.point, this.binding.scope) });
      });
      if (this.#snapshotRows.length + page.length > this.#maximumRows) throw new DatabaseSubscriptionError("view_limit_exceeded");
      if ("next_snapshot_cursor" in response) {
        const next = cursor(response.next_snapshot_cursor);
        if (this.#seenSnapshotCursors.has(next)) throw new DatabaseSubscriptionError("snapshot_cursor_cycle");
        this.#snapshotRows.push(...page);
        this.#seenSnapshotCursors.add(next);
        this.#snapshotCursor = next;
        return undefined;
      }
      const pollCursor = cursor(response.poll_cursor);
      this.#rows = new Map([...this.#snapshotRows, ...page].map(row => [row.document, row]));
      this.#snapshotRows = [];
      this.#snapshotCursor = undefined;
      this.#seenSnapshotCursors.clear();
      this.#pollCursor = pollCursor;
      this.#generation += 1;
      return this.#publish();
    }
    if (response.phase !== "poll") throw new DatabaseSubscriptionError("phase_mismatch");
    const pollCursor = cursor(response.poll_cursor);
    const unchanged = response.groups.every(group => group.effects.length === 0);
    const rows = unchanged ? this.#rows : new Map(this.#rows);
    let sequence = this.#sequence ?? 0n;
    for (const group of response.groups) {
      if (typeof group.sequence !== "bigint" || group.sequence <= sequence || group.sequence > 18_446_744_073_709_551_615n) {
        throw new DatabaseSubscriptionError("group_order_invalid");
      }
      /** @type {Set<string>} */
      const documents = new Set();
      for (const effect of group.effects) {
        const document = requireDatabaseUuid(effect.document, "document");
        if (documents.has(document)) throw new DatabaseSubscriptionError("duplicate_group_document");
        documents.add(document);
        if (effect.point === null) rows.delete(document);
        else rows.set(document, Object.freeze({ document, point: pointCopy(effect.point, this.binding.scope) }));
      }
      if (rows.size > this.#maximumRows) throw new DatabaseSubscriptionError("view_limit_exceeded");
      // A filtered group may have no matching effects. Its sequence is still
      // a valid advance; the signed response cursor must also be committed.
      sequence = group.sequence;
    }
    this.#rows = rows;
    this.#pollCursor = pollCursor;
    if (response.groups.length > 0) this.#sequence = sequence;
    return this.#publish(unchanged ? this.#snapshot?.rows : undefined);
  }

  /**
   * @param {readonly DatabaseQueryRow[]} [rows]
   * @returns {DatabaseViewSnapshot}
   */
  #publish(rows) {
    this.#snapshot = Object.freeze({
      rows: rows ?? Object.freeze([...this.#rows.values()].sort((left, right) => left.document < right.document ? -1 : left.document > right.document ? 1 : 0)),
      position: Object.freeze({ generation: this.#generation, poll_cursor: /** @type {string} */ (this.#pollCursor), ...(this.#sequence === undefined ? {} : { sequence: this.#sequence }) }),
    });
    return this.#snapshot;
  }
}

/**
 * @template T
 * @param {Promise<T>} pending
 * @param {AbortSignal} signal
 * @returns {Promise<T>}
 */
async function waitFor(pending, signal) {
  signal.throwIfAborted();
  return new Promise((resolve, reject) => {
    /** @returns {void} */
    const abort = () => reject(signal.reason);
    signal.addEventListener("abort", abort, { once: true });
    pending.then(resolve, reject).finally(() => signal.removeEventListener("abort", abort));
  });
}

/**
 * @param {number} milliseconds
 * @param {AbortSignal} signal
 * @returns {Promise<void>}
 */
async function delay(milliseconds, signal) {
  signal.throwIfAborted();
  return new Promise((resolve, reject) => {
    /** @returns {void} */
    const abort = () => { clearTimeout(timer); reject(signal.reason); };
    const timer = setTimeout(() => { signal.removeEventListener("abort", abort); resolve(); }, milliseconds);
    signal.addEventListener("abort", abort, { once: true });
  });
}

/**
 * @deprecated Retired SQLite/Rust subscriptions; use the C# client.
 * Native snapshot/poll source shared by React, Svelte and TanStack.
 * @param {TerraBaseClient} client
 * @param {DatabaseQueryOptions} options
 * @returns {DatabaseQuerySource}
 */
export function createDatabaseQuery(client, options) {
  const template = new DatabaseSubscriptionView(client.scope, options);
  const pollIntervalMs = boundedInteger(options.pollIntervalMs, 1_000, 60_000, "Poll interval");
  const retryDelayMs = boundedInteger(options.retryDelayMs, 250, 60_000, "Retry delay");
  const retryLimit = options.retryLimit ?? 2;
  if (!Number.isInteger(retryLimit) || retryLimit < 0 || retryLimit > 10) throw new TypeError("Retry limit is outside its bounds.");
  const capturedOptions = Object.freeze({ collection: template.binding.collection, predicate: template.binding.predicate, limit: template.binding.limit, ...(options.maximumRows === undefined ? {} : { maximumRows: options.maximumRows }) });
  const binding = template.binding;
  const requestKey = encodeDatabaseSubscription({ phase: "snapshot", predicate: binding.predicate, limit: binding.limit });
  return Object.freeze({
    key: Object.freeze(["terrabase-subscription", binding.scope.tenant, binding.scope.database, binding.scope.shard.toString(), binding.collection, requestKey]),
    binding,
    /**
     * @param {DatabaseQueryCallbacks} observer
     * @param {ReactiveSubscriptionContext} context
     * @returns {() => void}
     */
    subscribe: (observer, context) => {
      const abort = new AbortController();
      const signal = AbortSignal.any([context.signal, abort.signal]);
      const view = new DatabaseSubscriptionView(binding.scope, capturedOptions);
      /** @returns {Promise<void>} */
      const run = async () => {
        while (!signal.aborted) {
          const request = view.getRequest();
          /** @type {DatabaseResult<DatabaseSubscriptionResponse>} */
          let result;
          let retries = 0;
          while (true) {
            result = await waitFor(client.subscription(binding.collection, request, { signal }), signal);
            signal.throwIfAborted();
            if (result.ok || !["transport_unavailable", "cancelled"].includes(result.error.code) || retries >= retryLimit) break;
            retries += 1;
            await delay(retryDelayMs * retries, signal);
          }
          if (!result.ok) {
            if (result.error.status === 409 && result.error.code === "resync_required") {
              view.restart();
              observer.reset(Object.freeze({ reason: "resync_required", code: result.error.code }));
              await delay(pollIntervalMs, signal);
              continue;
            }
            throw new DatabaseSubscriptionError(result.error.code, result.error);
          }
          const snapshot = view.apply(result.value);
          if (snapshot !== undefined) {
            await waitFor(Promise.resolve(observer.next(snapshot.rows, snapshot.position)), signal);
            await delay(pollIntervalMs, signal);
          }
        }
      };
      void run().catch(error => {
        if (!signal.aborted) {
          try { observer.error(error); } catch { /* consumer callback failed */ } finally { abort.abort(); }
        }
      });
      return () => abort.abort();
    },
  });
}
