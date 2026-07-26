import type { JsonValue } from "../models.js";
import {
  compareDatabaseRevision,
  equalStateVersion,
  parseSyncUInt64,
  type CompleteTransition,
  type DatabaseRevision,
  type SyncError,
  type SyncStateVersion,
  type SyncUInt64,
} from "./protocol.js";

export type SyncQueryIdentity = {
  readonly Database: string;
  readonly Tenant: string;
  readonly AuthorizationScope: string;
  readonly Query: string;
  readonly Arguments: JsonValue;
  readonly SchemaVersion: SyncUInt64;
  readonly ConsistencyMode: "AuthoritativeSnapshot";
};

export type SyncQuerySnapshot = {
  readonly QueryId: string;
  readonly Value?: JsonValue | undefined;
  readonly Error?: SyncError | undefined;
  readonly ResultHash?: string | undefined;
  readonly ValidatedThroughRevision: DatabaseRevision;
  readonly Optimistic: boolean;
};

export type SyncClientSnapshot = {
  readonly Version: SyncStateVersion;
  readonly Queries: ReadonlyMap<string, SyncQuerySnapshot>;
  readonly IngressRegion?: string | undefined;
  readonly ServingRegion?: string | undefined;
  readonly DatabaseRegion?: string | undefined;
  readonly RouteGeneration?: SyncUInt64 | undefined;
};

export type SyncQueryHandle = {
  readonly QueryId: string;
  subscribe(listener: () => void): () => void;
  getSnapshot(): SyncQuerySnapshot | undefined;
  getServerSnapshot(): SyncQuerySnapshot | undefined;
  release(): void;
};

export type OptimisticPatch = {
  readonly QueryId: string;
  readonly Apply: (current: JsonValue | undefined) => JsonValue;
};

type QueryEntry = {
  readonly QueryId: string;
  References: number;
  readonly Listeners: Set<() => void>;
  readonly ServerSnapshot: SyncQuerySnapshot | undefined;
};

type OptimisticLayer = {
  readonly MutationId: string;
  readonly Patches: readonly OptimisticPatch[];
  CommittedRevision?: DatabaseRevision | undefined;
};

const zeroRevision = Object.freeze({ RevisionEpoch: "0", CommitSequence: "0" } as const);
const zeroVersion = Object.freeze({
  QuerySetVersion: "0",
  DatabaseRevision: zeroRevision,
  IdentityVersion: "0",
} as const);

export class TerraScaleSyncStore {
  readonly #globalListeners = new Set<() => void>();
  readonly #entriesByKey = new Map<string, QueryEntry>();
  readonly #entriesById = new Map<string, QueryEntry>();
  #base: SyncClientSnapshot;
  #observable: SyncClientSnapshot;
  #optimistic: OptimisticLayer[] = [];

  public constructor(initial?: SyncClientSnapshot) {
    this.#base = freezeClientSnapshot(initial ?? { Version: zeroVersion, Queries: new Map() });
    this.#observable = this.#base;
  }

  public subscribe(listener: () => void): () => void {
    this.#globalListeners.add(listener);
    return () => this.#globalListeners.delete(listener);
  }

  public getSnapshot(): SyncClientSnapshot {
    return this.#observable;
  }

  public getServerSnapshot(): SyncClientSnapshot {
    return this.#base;
  }

  public acquire(
    queryId: string,
    identity: SyncQueryIdentity,
    serverSnapshot?: SyncQuerySnapshot,
  ): SyncQueryHandle {
    const key = canonicalQueryKey(identity);
    let entry = this.#entriesByKey.get(key);
    if (entry === undefined) {
      if (this.#entriesById.has(queryId)) {
        throw new TypeError(`Query ID '${queryId}' is already assigned to another query.`);
      }
      entry = {
        QueryId: queryId,
        References: 0,
        Listeners: new Set(),
        ServerSnapshot: serverSnapshot === undefined ? undefined : freezeQuerySnapshot(serverSnapshot),
      };
      this.#entriesByKey.set(key, entry);
      this.#entriesById.set(queryId, entry);
    }
    entry.References++;

    let released = false;
    return Object.freeze({
      QueryId: entry.QueryId,
      subscribe: (listener: () => void) => {
        entry!.Listeners.add(listener);
        return () => entry!.Listeners.delete(listener);
      },
      getSnapshot: () => this.#observable.Queries.get(entry!.QueryId) ?? entry!.ServerSnapshot,
      getServerSnapshot: () => entry!.ServerSnapshot ?? this.#base.Queries.get(entry!.QueryId),
      release: () => {
        if (released) {
          return;
        }
        released = true;
        entry!.References--;
        if (entry!.References === 0) {
          this.#entriesByKey.delete(key);
          this.#entriesById.delete(entry!.QueryId);
          entry!.Listeners.clear();
        }
      },
    });
  }

  public applyTransition(transition: CompleteTransition): void {
    if (!equalStateVersion(this.#base.Version, transition.StartVersion)) {
      throw new SyncVersionMismatchError(this.#base.Version, transition.StartVersion);
    }
    if (
      compareDatabaseRevision(
        transition.EndVersion.DatabaseRevision,
        transition.StartVersion.DatabaseRevision,
      ) < 0
      || parseSyncUInt64(transition.EndVersion.QuerySetVersion)
        < parseSyncUInt64(transition.StartVersion.QuerySetVersion)
      || parseSyncUInt64(transition.EndVersion.IdentityVersion)
        < parseSyncUInt64(transition.StartVersion.IdentityVersion)
    ) {
      throw new TypeError("A transition cannot move state backward.");
    }

    const queries = new Map(this.#base.Queries);
    const changed = new Set<string>();

    for (const removed of transition.QueryRemoved) {
      if (queries.delete(removed.QueryId)) {
        changed.add(removed.QueryId);
      }
    }
    for (const failed of transition.QueryFailed) {
      const next = freezeQuerySnapshot({
        QueryId: failed.QueryId,
        Error: failed.Error,
        ValidatedThroughRevision: failed.ValidatedThroughRevision,
        Optimistic: false,
      });
      if (!sameObservable(queries.get(failed.QueryId), next)) {
        changed.add(failed.QueryId);
      }
      queries.set(failed.QueryId, next);
    }
    for (const updated of transition.QueryUpdated) {
      const next = freezeQuerySnapshot({
        QueryId: updated.QueryId,
        Value: updated.Result,
        ResultHash: updated.ResultHash,
        ValidatedThroughRevision: updated.ValidatedThroughRevision,
        Optimistic: false,
      });
      if (!sameObservable(queries.get(updated.QueryId), next)) {
        changed.add(updated.QueryId);
      }
      queries.set(updated.QueryId, next);
    }
    for (const [queryId, query] of queries) {
      if (
        compareDatabaseRevision(
          query.ValidatedThroughRevision,
          transition.EndVersion.DatabaseRevision,
        ) < 0
      ) {
        queries.set(queryId, freezeQuerySnapshot({
          ...query,
          ValidatedThroughRevision: transition.EndVersion.DatabaseRevision,
        }));
      }
    }

    this.#base = freezeClientSnapshot({
      Version: transition.EndVersion,
      Queries: queries,
      IngressRegion: transition.IngressRegion,
      ServingRegion: transition.ServingRegion,
      DatabaseRegion: transition.DatabaseRegion,
      RouteGeneration: transition.RouteGeneration,
    });
    for (const queryId of this.#removeCaughtUpOptimisticLayers()) {
      changed.add(queryId);
    }
    this.#rebuildObservable();
    this.#notify(changed);
  }

  public beginOptimisticMutation(
    mutationId: string,
    patches: readonly OptimisticPatch[],
  ): void {
    if (this.#optimistic.some(layer => layer.MutationId === mutationId)) {
      throw new TypeError(`Optimistic mutation '${mutationId}' already exists.`);
    }
    const layer: OptimisticLayer = {
      MutationId: mutationId,
      Patches: Object.freeze([...patches]),
    };
    this.#optimistic = [...this.#optimistic, layer];
    this.#rebuildObservable();
    this.#notify(new Set(patches.map(patch => patch.QueryId)));
  }

  public markMutationCommitted(mutationId: string, revision: DatabaseRevision): void {
    const index = this.#optimistic.findIndex(candidate => candidate.MutationId === mutationId);
    if (index < 0) {
      throw new TypeError(`Optimistic mutation '${mutationId}' does not exist.`);
    }
    const layer = this.#optimistic[index]!;
    this.#optimistic = this.#optimistic.map((candidate, candidateIndex) =>
      candidateIndex === index ? { ...candidate, CommittedRevision: revision } : candidate);
    if (this.#removeCaughtUpOptimisticLayers().size > 0) {
      this.#rebuildObservable();
      this.#notify(new Set(layer.Patches.map(patch => patch.QueryId)));
    }
  }

  public failMutation(mutationId: string): void {
    const layer = this.#optimistic.find(candidate => candidate.MutationId === mutationId);
    if (layer === undefined) {
      return;
    }
    this.#optimistic = this.#optimistic.filter(candidate => candidate !== layer);
    this.#rebuildObservable();
    this.#notify(new Set(layer.Patches.map(patch => patch.QueryId)));
  }

  public getReferenceCount(identity: SyncQueryIdentity): number {
    return this.#entriesByKey.get(canonicalQueryKey(identity))?.References ?? 0;
  }

  #removeCaughtUpOptimisticLayers(): ReadonlySet<string> {
    const removedQueryIds = new Set<string>();
    const remaining = this.#optimistic.filter(layer =>
    {
      const retain = layer.CommittedRevision === undefined
        || compareDatabaseRevision(
          this.#base.Version.DatabaseRevision,
          layer.CommittedRevision,
        ) < 0;
      if (!retain) {
        for (const patch of layer.Patches) {
          removedQueryIds.add(patch.QueryId);
        }
      }
      return retain;
    });
    if (remaining.length === this.#optimistic.length) {
      return removedQueryIds;
    }
    this.#optimistic = remaining;
    return removedQueryIds;
  }

  #rebuildObservable(): void {
    if (this.#optimistic.length === 0) {
      this.#observable = this.#base;
      return;
    }

    const queries = new Map(this.#base.Queries);
    for (const layer of this.#optimistic) {
      for (const patch of layer.Patches) {
        const current = queries.get(patch.QueryId);
        const nextValue = freezeJson(patch.Apply(current?.Value));
        queries.set(patch.QueryId, freezeQuerySnapshot({
          QueryId: patch.QueryId,
          Value: nextValue,
          Error: current?.Error,
          ResultHash: current?.ResultHash,
          ValidatedThroughRevision:
            current?.ValidatedThroughRevision ?? this.#base.Version.DatabaseRevision,
          Optimistic: true,
        }));
      }
    }
    this.#observable = freezeClientSnapshot({ ...this.#base, Queries: queries });
  }

  #notify(queryIds: ReadonlySet<string>): void {
    for (const listener of this.#globalListeners) {
      listener();
    }
    for (const queryId of queryIds) {
      for (const listener of this.#entriesById.get(queryId)?.Listeners ?? []) {
        listener();
      }
    }
  }
}

export class SyncVersionMismatchError extends Error {
  public constructor(
    public readonly Current: SyncStateVersion,
    public readonly TransitionStart: SyncStateVersion,
  ) {
    super("The transition StartVersion does not match the current Sync state.");
    this.name = "SyncVersionMismatchError";
  }
}

export function applyCompleteTransition(
  snapshot: SyncClientSnapshot,
  transition: CompleteTransition,
): SyncClientSnapshot {
  const store = new TerraScaleSyncStore(snapshot);
  store.applyTransition(transition);
  return store.getSnapshot();
}

export function canonicalQueryKey(identity: SyncQueryIdentity): string {
  return canonicalJson(identity);
}

function canonicalJson(value: unknown): string {
  if (value === null || typeof value === "boolean" || typeof value === "string") {
    return JSON.stringify(value);
  }
  if (typeof value === "number") {
    if (!Number.isFinite(value)) {
      throw new TypeError("Query keys cannot contain non-finite numbers.");
    }
    return JSON.stringify(value);
  }
  if (Array.isArray(value)) {
    return `[${value.map(canonicalJson).join(",")}]`;
  }
  if (typeof value === "object") {
    const record = value as Record<string, unknown>;
    return `{${Object.keys(record)
      .sort()
      .map(key => `${JSON.stringify(key)}:${canonicalJson(record[key])}`)
      .join(",")}}`;
  }
  throw new TypeError("Query keys can contain only JSON values.");
}

function freezeClientSnapshot(snapshot: SyncClientSnapshot): SyncClientSnapshot {
  return Object.freeze({
    ...snapshot,
    Version: Object.freeze({
      ...snapshot.Version,
      DatabaseRevision: Object.freeze({ ...snapshot.Version.DatabaseRevision }),
    }),
    Queries: readonlyMap(new Map(
      [...snapshot.Queries].map(([queryId, query]) => [queryId, freezeQuerySnapshot(query)]),
    )),
  });
}

function freezeQuerySnapshot(snapshot: SyncQuerySnapshot): SyncQuerySnapshot {
  return Object.freeze({
    ...snapshot,
    Value: snapshot.Value === undefined ? undefined : freezeJson(snapshot.Value),
    Error: snapshot.Error === undefined ? undefined : Object.freeze({ ...snapshot.Error }),
    ValidatedThroughRevision: Object.freeze({ ...snapshot.ValidatedThroughRevision }),
  });
}

function freezeJson(value: JsonValue): JsonValue {
  if (value === null || typeof value !== "object") {
    return value;
  }
  if (Array.isArray(value)) {
    return Object.freeze(value.map(freezeJson));
  }
  const result: Record<string, JsonValue> = {};
  for (const [key, item] of Object.entries(value)) {
    result[key] = freezeJson(item);
  }
  return Object.freeze(result);
}

function readonlyMap<T>(source: ReadonlyMap<string, T>): ReadonlyMap<string, T> {
  return new ImmutableMap(source);
}

function sameObservable(
  current: SyncQuerySnapshot | undefined,
  next: SyncQuerySnapshot,
): boolean {
  return current?.ResultHash === next.ResultHash
    && current?.Error?.Code === next.Error?.Code
    && current?.Error?.Message === next.Error?.Message
    && current?.Error?.Retryable === next.Error?.Retryable;
}

class ImmutableMap<T> implements ReadonlyMap<string, T> {
  readonly #map: Map<string, T>;

  public constructor(source: ReadonlyMap<string, T>) {
    this.#map = new Map(source);
  }

  public get size(): number {
    return this.#map.size;
  }

  public get(key: string): T | undefined {
    return this.#map.get(key);
  }

  public has(key: string): boolean {
    return this.#map.has(key);
  }

  public entries(): MapIterator<[string, T]> {
    return this.#map.entries();
  }

  public keys(): MapIterator<string> {
    return this.#map.keys();
  }

  public values(): MapIterator<T> {
    return this.#map.values();
  }

  public forEach(
    callbackfn: (value: T, key: string, map: ReadonlyMap<string, T>) => void,
    thisArg?: unknown,
  ): void {
    for (const [key, value] of this.#map) {
      callbackfn.call(thisArg, value, key, this);
    }
  }

  public [Symbol.iterator](): MapIterator<[string, T]> {
    return this.#map[Symbol.iterator]();
  }
}
