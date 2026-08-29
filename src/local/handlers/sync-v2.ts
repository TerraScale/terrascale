import { matchLocalRoute, jsonResponse, objectBody, type LocalHandler, type LocalRequestContext } from "../context.js";
import type { TerraScaleSqliteStore } from "../store.js";
import {
  syncV2CanonicalCheckpoint,
  syncV2CheckpointFromBytes,
  syncV2CheckpointToBytes,
  syncV2MaxFields,
  syncV2MaxIdentBytes,
  syncV2MaxKeyBytes,
  syncV2MaxFrameBytes,
  type SyncV2Checkpoint,
  type SyncV2Value,
} from "../../sync/v2/frame.js";

type JsonRecord = Record<string, unknown>;
type Session = { readonly sessionId: number; readonly collection: string; readonly principal: string; readonly tenant: string; readonly closed: boolean; readonly acknowledged?: string };
type Change = { readonly sequence: number; readonly document: string; readonly operationId?: string; readonly put?: Readonly<Record<string, SyncV2Value>>; readonly delete?: boolean };
type Replay = { readonly response: JsonRecord; readonly fingerprint: string };
type Document = { readonly document: string; readonly put?: Readonly<Record<string, SyncV2Value>>; readonly delete?: boolean };
type Stream = { readonly collections: ReadonlySet<string>; readonly principal: string; readonly tenant: string; readonly write: (frame: string) => void; readonly close: () => void };

const streamsByStore = new WeakMap<object, Set<Stream>>();
const MAX_SSE_FRAME_BYTES = 64 * 1024;
const PEER = 1n;

export function clearLocalSyncV2State(store: object): void {
  const streams = streamsByStore.get(store);
  if (streams === undefined) return;
  for (const stream of [...streams]) stream.close();
  streamsByStore.delete(store);
}

/** Local HTTP/SSE implementation of the public `terrascale.sync.v2` host surface. */
export const handleLocalSyncV2: LocalHandler = async (context) => {
  const sessionRoute = matchLocalRoute(context, context.method, "/v1/sync/sessions/{sessionId}");
  const childRoute = matchLocalRoute(context, context.method, "/v1/sync/sessions/{sessionId}/{operation}");
  if (context.pathname === "/v1/sync/sessions" && context.method === "POST") return setup(context);
  if (sessionRoute !== undefined || childRoute !== undefined) {
    const identity = syncContext(context);
    if (identity === undefined) return syncError(401, "scope_refused", "X-TB-Principal and X-TB-Tenant are required.");
    const id = sessionRoute?.sessionId ?? childRoute?.sessionId ?? "";
    const operation = childRoute?.operation;
    if (!/^[0-9]+$/u.test(id)) return syncError(400, "malformed_request", "The sync session id is invalid.");
    const session = readSession(context.store, Number(id));
    if (session === undefined || session.closed) return syncError(410, "session_closed", "The sync session is closed.");
    if (identity.principal !== session.principal || identity.tenant !== session.tenant) {
      return syncError(403, "scope_refused", "The session belongs to another principal or tenant.");
    }
    if (operation === undefined) return syncError(405, "method_not_allowed", "A sync session operation is required.");
    switch (operation) {
      case "delta": return delta(context, session);
      case "snapshot": return snapshot(context, session);
      case "ack": return acknowledge(context, session);
      case "mutations": return mutate(context, session);
      case "close": return closeSession(context, session);
      default: return undefined;
    }
  }
  if (context.pathname === "/v1/streams" && context.method === "GET") return stream(context);
  return undefined;
};

function setup(context: LocalRequestContext): Response {
  const identity = syncContext(context);
  if (identity === undefined) return syncError(401, "scope_refused", "X-TB-Principal and X-TB-Tenant are required.");
  const body = objectBody(context);
  const collection = string(body.collection);
  if (!boundedText(collection, 128)) return syncError(400, "scope_refused", "collection is required.");
  const principal = identity.principal;
  const tenant = identity.tenant;
  const id = nextId(context.store);
  const supplied = typeof body.resume === "string" ? safeCheckpoint(body.resume) : initialCheckpoint();
  if (supplied === undefined) return syncError(400, "malformed_frame", "The resume checkpoint is invalid.");
  const current = checkpoint(context.store, principal, tenant, collection);
  if (supplied.storage_sequence > current.storage_sequence) return syncError(409, "checkpoint_refused", "The resume checkpoint is ahead of the local journal.", true);
  writeRecord(context.store, "sync-v2", "sessions", String(id), { sessionId: id, collection, principal, tenant, closed: false });
  const encoded = encodeCheckpoint(current);
  return jsonResponse({ sessionId: id, checkpoint: encoded, retainedFloor: encodeCheckpoint(initialCheckpoint()) });
}

function delta(context: LocalRequestContext, session: Session): Response {
  const body = objectBody(context);
  const from = typeof body.from === "string" ? safeCheckpoint(body.from) : initialCheckpoint();
  if (from === undefined) return syncError(400, "malformed_frame", "The delta checkpoint is invalid.");
  const current = checkpoint(context.store, session.principal, session.tenant, session.collection);
  if (from.storage_sequence > current.storage_sequence) return syncError(409, "checkpoint_refused", "The requested checkpoint is not known.", true);
  const pending = listChanges(context.store, session.principal, session.tenant, session.collection).filter((item) => BigInt(item.sequence) > from.storage_sequence);
  const changes: Change[] = [];
  let payloadLength = 0;
  for (const candidate of pending) {
    const candidateLength = utf8(JSON.stringify({ collection: session.collection, changes: [...changes, candidate] })).length;
    if (changes.length > 0 && candidateLength > 65_536) break;
    changes.push(candidate);
    payloadLength = candidateLength;
    if (candidateLength > 65_536) break;
  }
  if (changes.length === 0) {
    return jsonResponse({ sessionId: session.sessionId, payloadCodec: "terrabase-native/1", payload: "", next: encodeCheckpoint(from), commits: 0, singleCommitOversize: false });
  }
  const payload = utf8(JSON.stringify({ collection: session.collection, changes }));
  return jsonResponse({ sessionId: session.sessionId, payloadCodec: "terrabase-native/1", payload: base64(payload), next: encodeCheckpoint(syncV2CanonicalCheckpoint([[PEER, changes.at(-1)!.sequence]], BigInt(changes.at(-1)!.sequence))), commits: changes.length, singleCommitOversize: changes.length === 1 && payloadLength > 65_536 });
}

function snapshot(context: LocalRequestContext, session: Session): Response {
  const current = checkpoint(context.store, session.principal, session.tenant, session.collection);
  const documents = listDocuments(context.store, session.principal, session.tenant, session.collection).filter((item) => item.delete !== true);
  const payload = utf8(JSON.stringify({ collection: session.collection, documents }));
  if (payload.length > syncV2MaxFrameBytes) return syncError(413, "overloaded", "The local snapshot exceeds the frame budget.");
  return jsonResponse({ sessionId: session.sessionId, payloadCodec: "terrabase-snapshot/1", payload: base64(payload), checkpoint: encodeCheckpoint(current) });
}

function acknowledge(context: LocalRequestContext, session: Session): Response {
  const body = objectBody(context);
  if (typeof body.checkpoint !== "string") return syncError(400, "malformed_request", "checkpoint is required.");
  const checkpointValue = safeCheckpoint(body.checkpoint);
  if (checkpointValue === undefined) return syncError(400, "malformed_frame", "The acknowledgement checkpoint is invalid.");
  const current = checkpoint(context.store, session.principal, session.tenant, session.collection);
  if (checkpointValue.storage_sequence > current.storage_sequence) return syncError(409, "checkpoint_refused", "The acknowledgement is ahead of the local journal.");
  if (session.acknowledged !== undefined) {
    const previous = safeCheckpoint(session.acknowledged);
    if (previous !== undefined && checkpointValue.storage_sequence < previous.storage_sequence) return syncError(409, "checkpoint_refused", "Acknowledgements must advance monotonically.");
  }
  writeRecord(context.store, "sync-v2", "sessions", String(session.sessionId), { ...session, acknowledged: body.checkpoint });
  return jsonResponse({ sessionId: session.sessionId });
}

function mutate(context: LocalRequestContext, session: Session): Response {
  const key = context.request.headers.get("idempotency-key")?.trim() ?? "";
  const body = objectBody(context);
  const document = typeof body.document === "string" ? body.document : "";
  const change = record(body.change);
  if (!boundedText(key, syncV2MaxKeyBytes) || !boundedText(document, syncV2MaxIdentBytes)) return jsonResponse({ sessionId: session.sessionId, outcome: { outcome: "rejected", reason: "invalid_body" } });
  if (utf8(JSON.stringify({ document, change })).length > syncV2MaxFrameBytes) return jsonResponse({ sessionId: session.sessionId, outcome: { outcome: "rejected", reason: "invalid_body" } });
  const fingerprint = JSON.stringify({ document, change });
  const replayScope = `replay:${durableScope(session.principal, session.tenant, session.collection)}`;
  const previous = readRecord<Replay>(context.store, "sync-v2", replayScope, key);
  if (previous !== undefined) {
    if (previous.fingerprint !== fingerprint) return syncError(409, "idempotency_key_reused", "The idempotency key was already used for another mutation.");
    return jsonResponse({ ...previous.response, sessionId: session.sessionId });
  }
  const put = decodePut(change.put);
  const hasPut = Object.prototype.hasOwnProperty.call(change, "put");
  const hasDelete = Object.prototype.hasOwnProperty.call(change, "delete");
  const deleting = change.delete === true;
  if (hasPut === hasDelete || (hasDelete && !deleting) || (hasPut && put === undefined)) return jsonResponse({ sessionId: session.sessionId, outcome: { outcome: "rejected", reason: "invalid_body" } });
  const estimated: Change = deleting
    ? { sequence: 0, document, operationId: key, delete: true }
    : { sequence: 0, document, operationId: key, ...(put === undefined ? {} : { put }) };
  if (utf8(JSON.stringify(estimated)).length > syncV2MaxFrameBytes) return jsonResponse({ sessionId: session.sessionId, outcome: { outcome: "rejected", reason: "invalid_body" } });
  const sequence = nextSequence(context.store);
  const applied: Change = deleting
    ? { sequence, document, operationId: key, delete: true }
    : { sequence, document, operationId: key, ...(put === undefined ? {} : { put }) };
  const scope = durableScope(session.principal, session.tenant, session.collection);
  writeRecord(context.store, "sync-v2-documents", scope, document, applied);
  writeRecord(context.store, "sync-v2-changes", scope, changeId(sequence), applied);
  const response = { sessionId: session.sessionId, outcome: { outcome: "applied", receipt: { sequence, version: sequence, operationId: key } } };
  writeRecord(context.store, "sync-v2", replayScope, key, { response, fingerprint });
  broadcast(context.store, session.collection, sequence, key, session.principal, session.tenant);
  return jsonResponse(response);
}

function closeSession(context: LocalRequestContext, session: Session): Response {
  writeRecord(context.store, "sync-v2", "sessions", String(session.sessionId), { ...session, closed: true });
  return jsonResponse({ sessionId: session.sessionId });
}

function stream(context: LocalRequestContext): Response {
  const identity = syncContext(context);
  if (identity === undefined) return syncError(401, "scope_refused", "X-TB-Principal and X-TB-Tenant are required.");
  const values = (context.url.searchParams.get("collections") ?? "").split(",").map((item) => item.trim()).filter(Boolean);
  if (values.length === 0 || values.length > 256 || values.some((item) => !boundedText(item, 128))) return syncError(400, "scope_refused", "At least one bounded collection is required.");
  const lastEvent = parseEventId(context.request.headers.get("last-event-id"));
  if (lastEvent === undefined) return syncError(400, "malformed_request", "Last-Event-ID must be a non-negative integer.");
  const collections = new Set(values);
  let controller: ReadableStreamDefaultController<Uint8Array> | undefined;
  let closed = false;
  const encoder = new TextEncoder();
  const frames = new Set<Stream>();
  const write = (frame: string): void => {
    if (closed || frame.length > MAX_SSE_FRAME_BYTES || controller === undefined) return;
    // A disconnected/slow reader must not turn a local trigger source into
    // an unbounded queue. ReadableStream's desiredSize is measured in queued
    // chunks; leave a fixed headroom for already-enqueued frames.
    if (controller.desiredSize !== null && controller.desiredSize < -128) return;
    try { controller.enqueue(encoder.encode(frame)); } catch { closed = true; cleanup(); }
  };
  const close = (): void => {
    if (closed) return;
    closed = true;
    try { controller?.close(); } catch { /* cancellation already closed it */ }
    cleanup();
  };
  const streamRecord: Stream = { collections, principal: identity.principal, tenant: identity.tenant, write, close };
  frames.add(streamRecord);
  const known = streamsByStore.get(context.store) ?? new Set<Stream>();
  known.add(streamRecord);
  streamsByStore.set(context.store, known);
  const cleanup = (): void => { frames.delete(streamRecord); known.delete(streamRecord); context.request.signal.removeEventListener("abort", close); };
  const body = new ReadableStream<Uint8Array>({
    start(nextController) {
      controller = nextController;
      context.request.signal.addEventListener("abort", close, { once: true });
      write(`id: ${lastEvent}\nevent: opened\ndata: ${JSON.stringify({ subscriptionId: 1, startSequence: lastEvent, collections: values })}\n\n`);
      for (const item of values) {
        for (const change of listChanges(context.store, identity.principal, identity.tenant, item)) {
          if (change.sequence > lastEvent) write(triggerFrame(change, 1, item));
        }
      }
      if (context.request.signal.aborted) close();
    },
    pull() { /* The stream's native queue supplies backpressure and remains bounded by write(). */ },
    cancel() { close(); },
  });
  return new Response(body, { headers: { "content-type": "text/event-stream; charset=utf-8", "cache-control": "no-cache", connection: "keep-alive", "x-terrascale-edge-region": "local", "x-terrascale-serving-region": "local", "x-terrascale-route-mode": "global" } });
}

function broadcast(store: TerraScaleSqliteStore, collection: string, sequence: number, operationId: string, principal: string, tenant: string): void {
  const streams = streamsByStore.get(store);
  if (streams === undefined) return;
  const frame = triggerFrame({ sequence, document: "", operationId }, 1, collection);
  for (const item of [...streams]) if (item.collections.has(collection) && item.principal === principal && item.tenant === tenant) item.write(frame);
}

function triggerFrame(change: Change, subscriptionId: number, collection: string): string {
  const data = JSON.stringify({ subscriptionId, collection: collection ?? "", sequence: change.sequence, operationId: change.operationId ?? "local" });
  return `id: ${change.sequence}\nevent: trigger\ndata: ${data}\n\n`;
}

function checkpoint(store: TerraScaleSqliteStore, principal: string, tenant: string, collection: string): SyncV2Checkpoint {
  const sequence = listChanges(store, principal, tenant, collection).at(-1)?.sequence ?? 0;
  return syncV2CanonicalCheckpoint(sequence === 0 ? [] : [[PEER, sequence]], BigInt(sequence));
}
function listChanges(store: TerraScaleSqliteStore, principal: string, tenant: string, collection: string): Change[] { return listRecords<Change>(store, "sync-v2-changes", durableScope(principal, tenant, collection)).sort((left, right) => left.sequence - right.sequence); }
function listDocuments(store: TerraScaleSqliteStore, principal: string, tenant: string, collection: string): Document[] { return listRecords<Document>(store, "sync-v2-documents", durableScope(principal, tenant, collection)); }
function initialCheckpoint(): SyncV2Checkpoint { return syncV2CanonicalCheckpoint([], 0n); }
function parseCheckpoint(value: string): SyncV2Checkpoint { return syncV2CheckpointFromBytes(unbase64(value)); }
function safeCheckpoint(value: string): SyncV2Checkpoint | undefined { try { return parseCheckpoint(value); } catch { return undefined; } }
function encodeCheckpoint(value: SyncV2Checkpoint): string { return base64(syncV2CheckpointToBytes(value)); }
/** Encodes identity components so SQLite's scope key cannot collide on separators. */
function durableScope(principal: string, tenant: string, collection: string): string {
  return [principal, tenant, collection].map((value) => encodeURIComponent(value)).join(":");
}
function nextId(store: TerraScaleSqliteStore): number { const value = getMeta<number>(store, "sync-v2.next-session"); const id = typeof value === "number" && Number.isSafeInteger(value) ? value + 1 : 1; setMeta(store, "sync-v2.next-session", id); return id; }
function nextSequence(store: TerraScaleSqliteStore): number { const current = getMeta<number>(store, "sync-v2.sequence"); const value = typeof current === "number" && Number.isSafeInteger(current) ? current + 1 : 1; setMeta(store, "sync-v2.sequence", value); return value; }
function readSession(store: TerraScaleSqliteStore, id: number): Session | undefined { return readRecord<Session>(store, "sync-v2", "sessions", String(id)); }
function readRecord<T>(store: TerraScaleSqliteStore, namespace: string, scope: string, id: string): T | undefined { return store.get<T>(namespace, scope, id); }
function writeRecord<T>(store: TerraScaleSqliteStore, namespace: string, scope: string, id: string, value: T): void { store.put(namespace, scope, id, value); }
function listRecords<T>(store: TerraScaleSqliteStore, namespace: string, scope: string): T[] { return store.list<T>(namespace, scope); }
function getMeta<T>(store: TerraScaleSqliteStore, name: string): T | undefined { return store.getMeta<T>(name); }
function setMeta<T>(store: TerraScaleSqliteStore, name: string, value: T): void { store.setMeta(name, value); }
function record(value: unknown): JsonRecord { return value !== null && typeof value === "object" && !Array.isArray(value) ? value as JsonRecord : {}; }
function string(value: unknown): string { return typeof value === "string" ? value.trim() : ""; }
function changeId(sequence: number): string { return sequence.toString().padStart(20, "0"); }
function decodePut(value: unknown): Readonly<Record<string, SyncV2Value>> | undefined {
  if (!Array.isArray(value)) return undefined;
  if (value.length > syncV2MaxFields) return undefined;
  const result: Record<string, SyncV2Value> = {};
  const names = new Set<string>();
  for (const field of value) {
    const item = record(field);
    const scalar = decodeScalar(item.value);
    if (typeof item.name !== "string" || !boundedText(item.name, syncV2MaxIdentBytes) || names.has(item.name) || scalar === undefined) return undefined;
    names.add(item.name);
    result[item.name] = scalar;
  }
  return result;
}
function isScalar(value: unknown): value is SyncV2Value { return value === null || typeof value === "string" || typeof value === "boolean" || typeof value === "number" && Number.isFinite(value) && (!Number.isInteger(value) || Number.isSafeInteger(value)); }
function decodeScalar(value: unknown): SyncV2Value | undefined {
  if (isScalar(value)) return value;
  const encoded = record(value);
  const keys = Object.keys(encoded);
  if (keys.length !== 1) return undefined;
  const kind = keys[0];
  if (kind === "null" && encoded.null === true) return null;
  if (kind === "string" && typeof encoded.string === "string") return encoded.string;
  if (kind === "boolean" && typeof encoded.boolean === "boolean") return encoded.boolean;
  if (kind === "integer" && typeof encoded.integer === "number" && Number.isSafeInteger(encoded.integer)) return encoded.integer;
  if (kind === "number" && typeof encoded.number === "number" && Number.isFinite(encoded.number)) return encoded.number;
  return undefined;
}
function utf8(value: string): Uint8Array { return new TextEncoder().encode(value); }
function base64(value: Uint8Array): string { return Buffer.from(value).toString("base64"); }
function unbase64(value: string): Uint8Array { return new Uint8Array(Buffer.from(value, "base64")); }
function syncError(status: number, code: string, detail: string, resyncRequired = false): Response {
  return jsonResponse({ code, detail, ...(resyncRequired ? { resyncRequired: true } : {}) }, status);
}
function syncContext(context: LocalRequestContext): { readonly principal: string; readonly tenant: string } | undefined {
  const principal = context.request.headers.get("x-tb-principal")?.trim() ?? "";
  const tenant = context.request.headers.get("x-tb-tenant")?.trim() ?? "";
  return boundedText(principal, 256) && boundedText(tenant, 256) ? { principal, tenant } : undefined;
}
function boundedText(value: string, maxBytes: number): boolean {
  return value.length > 0 && new TextEncoder().encode(value).length <= maxBytes && !/[\u0000\u000a\u000d]/u.test(value);
}
function parseEventId(value: string | null): number | undefined {
  if (value === null || value.trim() === "") return 0;
  const parsed = Number(value);
  return Number.isSafeInteger(parsed) && parsed >= 0 ? parsed : undefined;
}
