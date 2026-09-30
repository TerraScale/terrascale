import { encodeDatabaseSubscription, requireDatabaseUuid, requireDatabaseU64 } from "./database-codec.js";
import type { TerraBaseClient } from "./database.js";
import type {
  DatabaseField, DatabasePoint, DatabaseQueryPredicate, DatabaseQueryRow,
  DatabaseResult, DatabaseScope, DatabaseSubscriptionRequest,
  DatabaseSubscriptionResponse, DatabaseValue,
} from "./database-types.js";
import type { ReactiveReset, ReactiveSubscriptionContext } from "./react/core.js";

export type DatabaseQueryOptions = {
  readonly collection: string;
  readonly predicate: DatabaseQueryPredicate;
  /** Snapshot page size, between 1 and 128. */
  readonly limit?: number;
  /** Client memory bound, including all snapshot pages and later inserts. */
  readonly maximumRows?: number;
  readonly pollIntervalMs?: number;
  /** Bounded retries of read transport failures with the identical cursor. */
  readonly retryLimit?: number;
  readonly retryDelayMs?: number;
};

export type DatabaseViewBinding = {
  readonly scope: DatabaseScope;
  readonly collection: string;
  readonly predicate: DatabaseQueryPredicate;
  readonly limit: number;
};

/** Local generation and server position of one complete authorized cut. */
export type DatabaseViewPosition = {
  readonly generation: number;
  readonly poll_cursor: string;
  readonly sequence?: bigint;
};

export type DatabaseViewSnapshot = {
  readonly rows: readonly DatabaseQueryRow[];
  readonly position: DatabaseViewPosition;
};

export type DatabaseQueryCallbacks = {
  /** An async consumer can acknowledge publication before the next poll. */
  readonly next: (rows: readonly DatabaseQueryRow[], position: DatabaseViewPosition) => void | Promise<void>;
  readonly error: (error: unknown) => void;
  readonly reset: (reset: ReactiveReset<readonly DatabaseQueryRow[]>) => void;
};

export type DatabaseQuerySource = {
  readonly key?: string | readonly unknown[];
  readonly binding: DatabaseViewBinding;
  readonly subscribe: (
    observer: DatabaseQueryCallbacks,
    context: ReactiveSubscriptionContext,
  ) => () => void;
};

type DatabaseFailure = Extract<DatabaseResult<never>, { readonly ok: false }>;

export class DatabaseSubscriptionError extends Error {
  public override readonly name = "DatabaseSubscriptionError";
  public constructor(public readonly code: string, public readonly detail?: DatabaseFailure["error"]) {
    super(`TerraBase subscription: ${code}`, { cause: detail });
  }
}

function boundedInteger(value: number | undefined, fallback: number, maximum: number, label: string): number {
  const resolved = value ?? fallback;
  if (!Number.isInteger(resolved) || resolved < 1 || resolved > maximum) throw new TypeError(`${label} is outside its bounds.`);
  return resolved;
}

function valueCopy(value: DatabaseValue): DatabaseValue {
  switch (value.type) {
    case "bytes": {
      const bytes = value.value.slice();
      // A typed array cannot be frozen. Keep its backing bytes private and
      // return a copy on every read, including reads by another subscriber.
      return Object.freeze({ type: "bytes" as const, get value() { return bytes.slice(); } });
    }
    case "map": return Object.freeze({ type: "map", fields: fieldsCopy(value.fields) });
    case "array": return Object.freeze({ type: "array", items: Object.freeze(value.items.map(valueCopy)) });
    default: return Object.freeze({ ...value });
  }
}

function fieldsCopy(fields: readonly DatabaseField[]): readonly DatabaseField[] {
  return Object.freeze(fields.map(field => Object.freeze({ id: field.id, value: valueCopy(field.value) })));
}

function predicateCopy(predicate: DatabaseQueryPredicate): DatabaseQueryPredicate {
  const path = Object.freeze([...predicate.path]);
  if (!("value" in predicate)) return Object.freeze({ op: predicate.op, path });
  return Object.freeze({ op: predicate.op, path, value: valueCopy(predicate.value) as typeof predicate.value });
}

function pointCopy(point: DatabasePoint, scope: DatabaseScope): DatabasePoint {
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

function cursor(value: string): string {
  if (typeof value !== "string" || value.length === 0 || value.length > 4096) throw new DatabaseSubscriptionError("invalid_cursor");
  return value;
}

/**
 * Pure native result reducer. A paged snapshot stays private until its
 * terminal poll cursor. Poll groups are staged in order and published as one
 * complete response; invalid input cannot partially replace the prior cut.
 */
export class DatabaseSubscriptionView {
  public readonly binding: DatabaseViewBinding;
  readonly #maximumRows: number;
  #rows = new Map<string, DatabaseQueryRow>();
  #snapshotRows: DatabaseQueryRow[] = [];
  #snapshotCursor: string | undefined;
  #seenSnapshotCursors = new Set<string>();
  #pollCursor: string | undefined;
  #sequence: bigint | undefined;
  #generation = 0;
  #snapshot: DatabaseViewSnapshot | undefined;

  public constructor(scope: DatabaseScope, options: Pick<DatabaseQueryOptions, "collection" | "predicate" | "limit" | "maximumRows">) {
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

  public getSnapshot(): DatabaseViewSnapshot | undefined { return this.#snapshot; }

  public getRequest(): DatabaseSubscriptionRequest {
    const { predicate, limit } = this.binding;
    return this.#pollCursor === undefined
      ? Object.freeze({ phase: "snapshot", predicate, limit, ...(this.#snapshotCursor === undefined ? {} : { snapshot_cursor: this.#snapshotCursor }) })
      : Object.freeze({ phase: "poll", predicate, poll_cursor: this.#pollCursor });
  }

  /** Discard every opaque position; the next request starts a fresh cut. */
  public restart(): void {
    this.#snapshotRows = [];
    this.#snapshotCursor = undefined;
    this.#seenSnapshotCursors.clear();
    this.#pollCursor = undefined;
    this.#sequence = undefined;
    this.#snapshot = undefined;
  }

  public apply(response: DatabaseSubscriptionResponse): DatabaseViewSnapshot | undefined {
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
      const documents = new Set<string>();
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

  #publish(rows?: readonly DatabaseQueryRow[]): DatabaseViewSnapshot {
    this.#snapshot = Object.freeze({
      rows: rows ?? Object.freeze([...this.#rows.values()].sort((left, right) => left.document < right.document ? -1 : left.document > right.document ? 1 : 0)),
      position: Object.freeze({ generation: this.#generation, poll_cursor: this.#pollCursor!, ...(this.#sequence === undefined ? {} : { sequence: this.#sequence }) }),
    });
    return this.#snapshot;
  }
}

async function waitFor<T>(pending: Promise<T>, signal: AbortSignal): Promise<T> {
  signal.throwIfAborted();
  return new Promise<T>((resolve, reject) => {
    const abort = (): void => reject(signal.reason);
    signal.addEventListener("abort", abort, { once: true });
    pending.then(resolve, reject).finally(() => signal.removeEventListener("abort", abort));
  });
}

async function delay(milliseconds: number, signal: AbortSignal): Promise<void> {
  signal.throwIfAborted();
  return new Promise<void>((resolve, reject) => {
    const abort = (): void => { clearTimeout(timer); reject(signal.reason); };
    const timer = setTimeout(() => { signal.removeEventListener("abort", abort); resolve(); }, milliseconds);
    signal.addEventListener("abort", abort, { once: true });
  });
}

/** Native snapshot/poll source shared by React, Svelte and TanStack. */
export function createDatabaseQuery(client: TerraBaseClient, options: DatabaseQueryOptions): DatabaseQuerySource {
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
    subscribe: (observer: DatabaseQueryCallbacks, context: ReactiveSubscriptionContext): (() => void) => {
      const abort = new AbortController();
      const signal = AbortSignal.any([context.signal, abort.signal]);
      const view = new DatabaseSubscriptionView(binding.scope, capturedOptions);
      const run = async (): Promise<void> => {
        while (!signal.aborted) {
          const request = view.getRequest();
          let result: DatabaseResult<DatabaseSubscriptionResponse>;
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
