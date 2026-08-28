import { createHash } from "node:crypto";

import type { SyncWebSocket, SyncWebSocketFactory } from "../sync/client.js";
import { syncProtocolVersion, type DatabaseRevision, type SyncStateVersion } from "../sync/protocol.js";
import type { TerraScaleSqliteStore } from "./store.js";

/**
 * A small, in-process implementation of the public v1 wire contract.  It is
 * intentionally transport-only: callers still exercise the real client
 * framing, sequence checks, query-set transitions, and mutation replay, but
 * no TCP listener (or WebSocket package) is needed for local tests.
 */
export function createLocalSyncWebSocketFactory(store: TerraScaleSqliteStore): SyncWebSocketFactory {
  if (store === undefined || store === null) {
    throw new TypeError("A SQLite store is required.");
  }
  return (_url, protocols) => {
    const sockets = socketsByStore.get(store) ?? new Set<LocalSyncWebSocket>();
    let socket!: LocalSyncWebSocket;
    socket = new LocalSyncWebSocket(store, protocols, () => sockets.delete(socket));
    sockets.add(socket);
    socketsByStore.set(store, sockets);
    return socket;
  };
}

/** Closes all in-process v1 sockets owned by a local runtime (reset/close). */
export function clearLocalSyncV1State(store: object, code = 1000, reason = "local reset"): void {
  const sockets = socketsByStore.get(store);
  if (sockets === undefined) return;
  for (const socket of [...sockets]) socket.close(code, reason);
  socketsByStore.delete(store);
}

export const closeLocalSyncWebSockets = clearLocalSyncV1State;

const OPEN = 1;
const CLOSING = 2;
const CLOSED = 3;
const MAX_MESSAGE_BYTES = 1024 * 1024;
const ZERO_REVISION: DatabaseRevision = Object.freeze({ RevisionEpoch: "0", CommitSequence: "0" });
const socketsByStore = new WeakMap<object, Set<LocalSyncWebSocket>>();

type JsonRecord = Record<string, unknown>;
type QueryWire = { QueryId?: unknown; Query?: unknown; Arguments?: unknown; SchemaVersion?: unknown; Journal?: unknown };
type MutationReplay = { readonly clientRequestId: string; readonly result?: unknown; readonly revision: DatabaseRevision; readonly fingerprint?: string };
type PendingTransition = { readonly transition: JsonRecord; readonly queryIds: readonly string[] };
type SessionState = { readonly version: SyncStateVersion; readonly queryIds: readonly string[]; readonly pending?: PendingTransition };

class LocalSyncWebSocket implements SyncWebSocket {
  public binaryType: BinaryType = "arraybuffer";
  public onopen: ((event: Event) => void) | null = null;
  public onmessage: ((event: MessageEvent<unknown>) => void) | null = null;
  public onerror: ((event: Event) => void) | null = null;
  public onclose: ((event: CloseEvent) => void) | null = null;
  public readonly protocol: string;
  public readyState = 0;
  public bufferedAmount = 0;

  readonly #store: TerraScaleSqliteStore;
  #connectionSequence = "0";
  #incomingSequence = 0n;
  #outgoingSequence = 0n;
  #version: SyncStateVersion = version("0", ZERO_REVISION, "0");
  #queries = new Map<string, QueryWire>();
  #sessionId = "";
  #authenticated = false;
  readonly #onClosed: (() => void) | undefined;

  public constructor(store: TerraScaleSqliteStore, protocols: readonly string[], onClosed?: () => void) {
    this.#store = store;
    this.#onClosed = onClosed;
    this.protocol = protocols.includes("terrascale-sync-v1") ? "terrascale-sync-v1" : "";
    this.#authenticated = protocols.some((value) => /^ticket\.[A-Za-z0-9_-]+$/u.test(value));
    queueMicrotask(() => {
      if (this.readyState !== 0) return;
      if (this.protocol === "") {
        this.readyState = CLOSED;
        this.#onClosed?.();
        this.onerror?.(new Event("error"));
        this.onclose?.(closeEvent(1002, "terrascale-sync-v1 is required"));
        return;
      }
      this.readyState = OPEN;
      this.onopen?.(new Event("open"));
    });
  }

  public send(data: string): void {
    if (this.readyState !== OPEN) throw new Error("The local Sync socket is not open.");
    if (typeof data !== "string" || new TextEncoder().encode(data).length > MAX_MESSAGE_BYTES) {
      throw new RangeError("The Sync message exceeds 1 MiB.");
    }
    queueMicrotask(() => this.#receive(data));
  }

  public close(code = 1000, reason = ""): void {
    if (this.readyState === CLOSED || this.readyState === CLOSING) return;
    this.readyState = CLOSING;
    queueMicrotask(() => {
      this.readyState = CLOSED;
      this.#onClosed?.();
      this.onclose?.(closeEvent(code, reason));
    });
  }

  #receive(text: string): void {
    if (this.readyState !== OPEN) return;
    try {
      const envelope = JSON.parse(text) as JsonRecord;
      if (envelope.Protocol !== syncProtocolVersion) throw new Error("unsupported protocol");
      const connection = stringValue(envelope.ConnectionSequence);
      const sequence = BigInt(stringValue(envelope.MessageSequence));
      if (sequence !== this.#incomingSequence + 1n) throw new Error("non-contiguous client message sequence");
      this.#incomingSequence = sequence;
      if (this.#connectionSequence !== "0" && connection !== this.#connectionSequence) {
        throw new Error("stale connection sequence");
      }
      this.#connectionSequence = connection;
      const type = typeof envelope.MessageType === "string" ? envelope.MessageType : "";
      const payload = record(envelope.Payload);
      switch (type) {
        case "Authenticate":
          if (typeof payload.BearerToken !== "string" || payload.BearerToken.trim().length === 0 || payload.BearerToken.length > 16 * 1024) throw new Error("BearerToken is required.");
          this.#authenticated = true;
          return;
        case "Connect":
          return this.#connect(payload);
        case "ModifyQuerySet":
          return this.#modifyQuerySet(payload);
        case "ExecuteMutation":
          return this.#mutation(payload);
        case "AcknowledgeTransition":
          return this.#acknowledge(payload);
        case "ClientTelemetry":
          return;
        case "Ping":
          return this.#reply("Ping", { Nonce: stringValue(payload.Nonce), SentAt: stringValue(payload.SentAt) });
        default:
          throw new Error(`unsupported message type ${type}`);
      }
    } catch (error) {
      this.#reply("FatalProtocolError", {
        Error: { Code: "malformed_frame", Message: error instanceof Error ? error.message : "Malformed Sync message." },
      });
    }
  }

  #connect(payload: JsonRecord): void {
    if (!this.#authenticated) {
      this.#reply("AuthenticationError", { Error: syncError("authentication_required", "A Sync ticket or bearer token is required.") });
      return;
    }
    const session = typeof payload.SessionId === "string" ? payload.SessionId : "local-session";
    this.#sessionId = session;
    const saved = getRecord<SessionState>(this.#store, "sync-v1", "sessions", session);
    if (saved !== undefined) this.#version = saved.version;
    const previousIds = saved?.queryIds ?? [];
    this.#queries.clear();
    for (const item of array(payload.ActiveQueries)) {
      const query = record(item);
      const id = typeof query.QueryId === "string" ? query.QueryId : undefined;
      if (id !== undefined) this.#queries.set(id, query);
    }
    if (saved?.pending !== undefined) {
      this.#reply("Transition", saved.pending.transition);
      return;
    }
    if (saved !== undefined && sameStrings(previousIds, [...this.#queries.keys()])
      && BigInt(saved.version.DatabaseRevision.CommitSequence) >= BigInt(this.#currentRevision().CommitSequence)) return;
    const nextSet = increment(this.#version.QuerySetVersion);
    this.#transition(nextSet, this.#queries, new Set());
  }

  #modifyQuerySet(payload: JsonRecord): void {
    for (const item of array(payload.AddQuery)) {
      const query = record(item);
      if (typeof query.QueryId === "string") this.#queries.set(query.QueryId, query);
    }
    const removed = new Set<string>();
    for (const item of array(payload.RemoveQuery)) {
      const id = record(item).QueryId;
      if (typeof id === "string") {
        this.#queries.delete(id);
        removed.add(id);
      }
    }
    this.#transition(increment(this.#version.QuerySetVersion), this.#queries, removed);
  }

  #transition(querySetVersion: string, queries: ReadonlyMap<string, QueryWire>, removed: ReadonlySet<string>): void {
    const start = this.#version;
    const end = version(querySetVersion, this.#currentRevision(), start.IdentityVersion);
    const updated = [...queries.values()].map((query) => this.#queryUpdated(query, end.DatabaseRevision));
    this.#version = end;
    const transition = {
      FrameType: "Complete", TransitionId: this.#store.nextUlid(), StartVersion: start, EndVersion: end,
      QueryUpdated: updated, QueryFailed: [], QueryRemoved: [...removed].map((QueryId) => ({ QueryId })),
      IngressRegion: "local", ServingRegion: "local", DatabaseRegion: "local", RouteGeneration: "0",
    };
    if (this.#sessionId !== "") putRecord(this.#store, "sync-v1", "sessions", this.#sessionId, { version: end, queryIds: [...queries.keys()], pending: { transition, queryIds: [...queries.keys()] } });
    this.#reply("Transition", transition);
  }

  #acknowledge(payload: JsonRecord): void {
    const saved = this.#sessionId === "" ? undefined : getRecord<SessionState>(this.#store, "sync-v1", "sessions", this.#sessionId);
    const pending = saved?.pending;
    if (saved === undefined || pending === undefined) return;
    const id = record(pending.transition).TransitionId;
    if (typeof payload.TransitionId !== "string" || payload.TransitionId !== id) throw new Error("Transition acknowledgement does not match the pending transition.");
    if (this.#sessionId !== "") putRecord(this.#store, "sync-v1", "sessions", this.#sessionId, { version: saved.version, queryIds: saved.queryIds });
  }

  #queryUpdated(query: QueryWire, revision: DatabaseRevision): JsonRecord {
    const queryId = typeof query.QueryId === "string" ? query.QueryId : this.#store.nextUlid();
    const queryText = typeof query.Query === "string" ? query.Query : "";
    const result = queryResult(this.#store, queryText, query.Arguments);
    return {
      QueryId: queryId, QueryFingerprint: sha256(JSON.stringify({ Query: queryText, Arguments: query.Arguments ?? null })),
      Result: result, ResultHash: sha256(JSON.stringify(result)), ExecutionRevision: revision,
      ValidatedThroughRevision: revision, ...(query.Journal === undefined ? {} : { Journal: query.Journal }),
    };
  }

  #mutation(payload: JsonRecord): void {
    const key = typeof payload.IdempotencyKey === "string" ? payload.IdempotencyKey : "";
    const clientRequestId = typeof payload.ClientRequestId === "string" ? payload.ClientRequestId : this.#store.nextUlid();
    if (key.trim().length === 0 || key.length > 128 || key.includes("\u0000")) {
      return this.#reply("MutationResult", { ClientRequestId: clientRequestId, Succeeded: false, Error: syncError("invalid_idempotency_key", "IdempotencyKey is required.") });
    }
    const replay = getRecord<MutationReplay>(this.#store, "sync-v1", "replay", key);
    if (replay !== undefined) {
      const fingerprint = sha256(JSON.stringify({ Mutation: payload.Mutation, Arguments: payload.Arguments ?? null }));
      if (replay.fingerprint !== undefined && replay.fingerprint !== fingerprint) {
        return this.#reply("MutationResult", { ClientRequestId: clientRequestId, Succeeded: false, Error: syncError("idempotency_key_reused", "The idempotency key was already used for another mutation.") });
      }
      return this.#reply("MutationResult", { ClientRequestId: clientRequestId, Succeeded: true, Result: replay.result, CommittedRevision: replay.revision });
    }
    const args = record(payload.Arguments);
    const mutation = applyMutation(this.#store, args);
    if (mutation === undefined) {
      return this.#reply("MutationResult", { ClientRequestId: clientRequestId, Succeeded: false, Error: syncError("invalid_mutation", "The mutation must identify a document and exactly one valid change.") });
    }
    const revision = nextRevision(this.#store);
    const result = mutationResult(payload.Arguments);
    const fingerprint = sha256(JSON.stringify({ Mutation: payload.Mutation, Arguments: payload.Arguments ?? null }));
    putRecord(this.#store, "sync-v1", "replay", key, { clientRequestId, result, revision, fingerprint });
    this.#reply("MutationResult", { ClientRequestId: clientRequestId, Succeeded: true, Result: result, CommittedRevision: revision });
    // The mutation result is only the receipt.  The query state reaches the
    // client through the normal journal/transition path, just like a remote
    // host.  This also makes replaying a key safe: the transition is emitted
    // only for the first application.
    if (mutation !== undefined) {
      this.#transition(this.#version.QuerySetVersion, this.#queries, new Set());
    }
  }

  #currentRevision(): DatabaseRevision {
    return readRevision(this.#store);
  }

  #reply(messageType: string, payload: JsonRecord): void {
    if (this.readyState !== OPEN) return;
    const sequence = ++this.#outgoingSequence;
    const value = JSON.stringify({ Protocol: syncProtocolVersion, MessageType: messageType, ConnectionSequence: this.#connectionSequence, MessageSequence: sequence.toString(), Payload: payload });
    queueMicrotask(() => this.onmessage?.(new MessageEvent("message", { data: value })));
  }
}

function version(querySetVersion: string, databaseRevision: DatabaseRevision, identityVersion: string): SyncStateVersion {
  return Object.freeze({ QuerySetVersion: querySetVersion as `${bigint}`, DatabaseRevision: databaseRevision, IdentityVersion: identityVersion as `${bigint}` });
}

function nextRevision(store: TerraScaleSqliteStore): DatabaseRevision {
  const current = readRevision(store);
  const next: DatabaseRevision = { RevisionEpoch: current.RevisionEpoch, CommitSequence: increment(current.CommitSequence) as `${bigint}` };
  setMeta(store, "sync-v1.revision", next);
  return next;
}

function readRevision(store: TerraScaleSqliteStore): DatabaseRevision {
  const value = getMeta<DatabaseRevision>(store, "sync-v1.revision");
  return value?.RevisionEpoch !== undefined && value.CommitSequence !== undefined ? value : ZERO_REVISION;
}

function queryResult(store: TerraScaleSqliteStore, query: string, argumentsValue: unknown): unknown {
  const collectionMatch = /\bfrom\s+["`']?([A-Za-z0-9_.-]+)/iu.exec(query);
  const collection = collectionMatch?.[1] ?? (record(argumentsValue).collection as string | undefined) ?? "default";
  const records = ["sync-v1-documents", "sync", "sync-records", "sync-v1", "__sync_v1", "documents", "local-documents"]
    .flatMap((namespace) => listRecords<unknown>(store, namespace, collection));
  const seen = new Set<string>();
  return records.filter((entry) => {
    const value = record(entry);
    const id = typeof value.id === "string" ? value.id : JSON.stringify(value);
    if (seen.has(id)) return false;
    seen.add(id);
    return value.deleted !== true;
  }).map((entry) => {
    const value = record(entry);
    return value.data === undefined ? entry : value.data;
  });
}

function applyMutation(store: TerraScaleSqliteStore, args: JsonRecord): boolean | undefined {
  const document = typeof args.Document === "string" ? args.Document : typeof args.document === "string" ? args.document : typeof args.Id === "string" ? args.Id : undefined;
  if (document === undefined || document.length === 0) return undefined;
  const collection = typeof args.Collection === "string" ? args.Collection : typeof args.collection === "string" ? args.collection : "default";
  if (collection.trim().length === 0 || new TextEncoder().encode(collection).length > 128) return undefined;
  const hasDelete = Object.prototype.hasOwnProperty.call(args, "Delete") || Object.prototype.hasOwnProperty.call(args, "delete");
  const hasPut = Object.prototype.hasOwnProperty.call(args, "Put") || Object.prototype.hasOwnProperty.call(args, "put") || Object.prototype.hasOwnProperty.call(args, "Data") || Object.prototype.hasOwnProperty.call(args, "data");
  if (hasDelete === hasPut) return undefined;
  const deleting = args.Delete === true || args.delete === true;
  if (hasDelete && !deleting) return undefined;
  if (deleting) {
    putRecord(store, "sync-v1-documents", collection, document, { id: document, collection, deleted: true });
    return true;
  }
  const data = args.Put ?? args.put ?? args.Data ?? args.data;
  if (data === undefined) return undefined;
  putRecord(store, "sync-v1-documents", collection, document, { id: document, collection, data });
  return true;
}

function mutationResult(value: unknown): unknown {
  const args = record(value);
  if (args.Result !== undefined) return args.Result;
  if (args.result !== undefined) return args.result;
  return { applied: true };
}

function syncError(code: string, message: string): JsonRecord {
  return { Code: code, Message: message, Retryable: false };
}

function getRecord<T>(store: TerraScaleSqliteStore, namespace: string, scope: string, id: string): T | undefined {
  return store.get<T>(namespace, scope, id);
}

function putRecord<T>(store: TerraScaleSqliteStore, namespace: string, scope: string, id: string, value: T): void {
  store.put(namespace, scope, id, value);
}

function listRecords<T>(store: TerraScaleSqliteStore, namespace: string, scope: string): T[] {
  return store.list<T>(namespace, scope);
}
function getMeta<T>(store: TerraScaleSqliteStore, key: string): T | undefined {
  return store.getMeta<T>(key);
}
function setMeta<T>(store: TerraScaleSqliteStore, key: string, value: T): void {
  store.setMeta(key, value);
}

function record(value: unknown): JsonRecord {
  return value !== null && typeof value === "object" && !Array.isArray(value) ? value as JsonRecord : {};
}

function array(value: unknown): readonly unknown[] {
  return Array.isArray(value) ? value : [];
}

function stringValue(value: unknown): string {
  return typeof value === "string" ? value : String(value ?? "0");
}

function increment(value: string): string {
  try { return (BigInt(value) + 1n).toString(); } catch { return "1"; }
}

function sameStrings(left: readonly string[], right: readonly string[]): boolean {
  return left.length === right.length && left.every((value, index) => value === right[index]);
}

function sha256(value: string): string {
  return createHash("sha256").update(value).digest("hex");
}

function closeEvent(code: number, reason: string): CloseEvent {
  return new CloseEvent("close", { code, reason, wasClean: code === 1000 });
}
