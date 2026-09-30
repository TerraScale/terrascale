import type {
  DatabaseAuthority, DatabaseCommit, DatabaseCommitReceipt, DatabaseCondition, DatabaseEvent,
  DatabaseField, DatabaseObserved, DatabasePoint, DatabaseQuery, DatabaseQueryPage,
  DatabaseQueryPredicate, DatabaseScope, DatabaseSibling, DatabaseSubscriptionRequest,
  DatabaseSubscriptionResponse, DatabaseTransactionId, DatabaseValue, DatabaseWrite,
  DatabaseTransactionIdentity, DatabaseTransactionStatus,
} from "./database-types.js";

type Json = null | boolean | number | string | readonly Json[] | { readonly [key: string]: Json };
type JsonObject = { readonly [key: string]: Json };
type RecordValue = Record<string, unknown>;
const encoder = new TextEncoder();
const maxU64 = (1n << 64n) - 1n;
const minI64 = -(1n << 63n);
const maxI64 = (1n << 63n) - 1n;
const maximumBodyBytes = 4 * 1024 * 1024;
const maximumValueBytes = 1024 * 1024;
const scalarTypes = new Set(["bool", "i64", "utf8", "bytes"]);

export function requireDatabaseUuid(value: unknown, name = "UUID"): string {
  if (typeof value !== "string" || !/^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/u.test(value)) {
    throw new TypeError(`${name} must be a canonical lowercase UUID.`);
  }
  return value;
}

export function requireDatabaseU64(value: unknown, name = "u64", positive = false): bigint {
  if (typeof value !== "bigint" || value < (positive ? 1n : 0n) || value > maxU64) {
    throw new TypeError(`${name} must be a ${positive ? "positive " : ""}u64 bigint.`);
  }
  return value;
}

function decimal(value: unknown, name: string, positive = false): bigint {
  if (typeof value !== "string" || !/^(0|[1-9][0-9]*)$/u.test(value)) throw new TypeError(`${name} must be canonical decimal.`);
  return requireDatabaseU64(BigInt(value), name, positive);
}

function hex(value: unknown, bytes: number, name: string): string {
  if (typeof value !== "string" || value.length !== bytes * 2 || !/^[0-9a-f]+$/u.test(value)) {
    throw new TypeError(`${name} must be canonical lowercase hexadecimal.`);
  }
  return value;
}

function object(value: unknown, required: readonly string[], optional: readonly string[] = []): RecordValue {
  if (typeof value !== "object" || value === null || Array.isArray(value)) throw new TypeError("Expected a protocol object.");
  const entry = value as RecordValue;
  if (required.some(key => !Object.hasOwn(entry, key)) || Object.keys(entry).some(key => !required.includes(key) && !optional.includes(key))) {
    throw new TypeError("Protocol object has missing or unknown fields.");
  }
  return entry;
}

function array(value: unknown, maximum = 1024, minimum = 0): readonly unknown[] {
  if (!Array.isArray(value) || value.length < minimum || value.length > maximum) throw new TypeError("Protocol array exceeds its bounds.");
  return value;
}

function fieldId(value: unknown): number {
  if (typeof value !== "number" || !Number.isInteger(value) || value < 1 || value > 0xffff_ffff) {
    throw new TypeError("Field ID must be a positive u32.");
  }
  return value;
}

function text(value: unknown): string {
  if (typeof value !== "string") throw new TypeError("UTF-8 value must be text.");
  for (let index = 0; index < value.length; index++) {
    const code = value.charCodeAt(index);
    if (code >= 0xd800 && code <= 0xdbff) {
      const next = value.charCodeAt(++index);
      if (!(next >= 0xdc00 && next <= 0xdfff)) throw new TypeError("Text contains an unpaired surrogate.");
    } else if (code >= 0xdc00 && code <= 0xdfff) throw new TypeError("Text contains an unpaired surrogate.");
  }
  if (encoder.encode(value).length > maximumValueBytes) throw new TypeError("Value exceeds its byte limit.");
  return value;
}

function i64(value: unknown): bigint {
  if (typeof value !== "bigint" || value < minI64 || value > maxI64) throw new TypeError("i64 value must be an in-range bigint.");
  return value;
}

function parseI64(value: unknown): bigint {
  if (typeof value !== "string" || !/^(0|-?[1-9][0-9]*)$/u.test(value)) throw new TypeError("i64 must be canonical decimal.");
  return i64(BigInt(value));
}

function base64url(bytes: Uint8Array): string {
  let binary = "";
  for (const byte of bytes) binary += String.fromCharCode(byte);
  return btoa(binary).replaceAll("+", "-").replaceAll("/", "_").replace(/=+$/u, "");
}

function parseBytes(value: unknown): Uint8Array {
  if (typeof value !== "string" || !/^[A-Za-z0-9_-]*$/u.test(value) || value.length > 1_398_102) throw new TypeError("Bytes must be bounded canonical base64url.");
  let binary: string;
  try { binary = atob(value.replaceAll("-", "+").replaceAll("_", "/")); }
  catch { throw new TypeError("Bytes must be canonical base64url."); }
  const bytes = Uint8Array.from(binary, character => character.charCodeAt(0));
  if (bytes.length > maximumValueBytes || base64url(bytes) !== value) throw new TypeError("Bytes must be bounded canonical base64url.");
  return bytes;
}

type Budget = { nodes: number };
function takeNode(depth: number, budget: Budget): void {
  if (depth > 16 || ++budget.nodes > 4096) throw new TypeError("Document exceeds its depth or node limit.");
}

function fieldsToWire(fields: readonly DatabaseField[], depth: number, budget: Budget): readonly Json[] {
  const entries = array(fields);
  let previous = 0;
  return entries.map(raw => {
    const field = object(raw, ["id", "value"]);
    const id = fieldId(field.id);
    if (id <= previous) throw new TypeError("Field IDs must be strictly increasing.");
    previous = id;
    return { id, value: valueToWire(field.value as DatabaseValue, depth, budget) };
  });
}

function valueToWire(value: DatabaseValue, depth: number, budget: Budget): JsonObject {
  takeNode(depth, budget);
  const initial = object(value, ["type"], ["value", "fields", "items"]);
  switch (initial.type) {
    case "null": object(value, ["type"]); return { type: "null" };
    case "bool": {
      const entry = object(value, ["type", "value"]);
      if (typeof entry.value !== "boolean") throw new TypeError("Boolean value must be boolean.");
      return { type: "bool", value: entry.value };
    }
    case "i64": return { type: "i64", value: i64(object(value, ["type", "value"]).value).toString() };
    case "utf8": return { type: "utf8", value: text(object(value, ["type", "value"]).value) };
    case "bytes": {
      const bytes = object(value, ["type", "value"]).value;
      if (!(bytes instanceof Uint8Array) || bytes.length > maximumValueBytes) throw new TypeError("Bytes value must be a bounded Uint8Array.");
      return { type: "bytes", value: base64url(bytes) };
    }
    case "map": return { type: "map", fields: fieldsToWire(object(value, ["type", "fields"]).fields as readonly DatabaseField[], depth + 1, budget) };
    case "array": return { type: "array", items: array(object(value, ["type", "items"]).items).map(item => valueToWire(item as DatabaseValue, depth + 1, budget)) };
    default: throw new TypeError("Unsupported database value type.");
  }
}

function fieldsFromWire(raw: unknown, depth: number, budget: Budget): readonly DatabaseField[] {
  let previous = 0;
  return array(raw).map(value => {
    const entry = object(value, ["id", "value"]);
    const id = fieldId(entry.id);
    if (id <= previous) throw new TypeError("Field IDs must be strictly increasing.");
    previous = id;
    return { id, value: valueFromWire(entry.value, depth, budget) };
  });
}

function valueFromWire(raw: unknown, depth: number, budget: Budget): DatabaseValue {
  takeNode(depth, budget);
  const initial = object(raw, ["type"], ["value", "fields", "items"]);
  switch (initial.type) {
    case "null": object(raw, ["type"]); return { type: "null" };
    case "bool": {
      const entry = object(raw, ["type", "value"]);
      if (typeof entry.value !== "boolean") throw new TypeError("Boolean value must be boolean.");
      return { type: "bool", value: entry.value };
    }
    case "i64": return { type: "i64", value: parseI64(object(raw, ["type", "value"]).value) };
    case "utf8": return { type: "utf8", value: text(object(raw, ["type", "value"]).value) };
    case "bytes": return { type: "bytes", value: parseBytes(object(raw, ["type", "value"]).value) };
    case "map": return { type: "map", fields: fieldsFromWire(object(raw, ["type", "fields"]).fields, depth + 1, budget) };
    case "array": return { type: "array", items: array(object(raw, ["type", "items"]).items).map(value => valueFromWire(value, depth + 1, budget)) };
    default: throw new TypeError("Unsupported database value type.");
  }
}

/** Typed value encoding preserves numeric field IDs, arrays, bytes, and explicit null. */
export function encodeDatabaseValue(value: DatabaseValue): JsonObject {
  const wire = valueToWire(value, 1, { nodes: 0 });
  nativeValue(wire);
  return wire;
}

export function decodeDatabaseValue(value: unknown): DatabaseValue {
  const decoded = valueFromWire(value, 1, { nodes: 0 });
  nativeValue(valueToWire(decoded, 1, { nodes: 0 }));
  return decoded;
}

function observedToWire(entries: readonly DatabaseObserved[]): readonly Json[] {
  let previous = "";
  return array(entries).map(raw => {
    const entry = object(raw, ["replica", "incarnation", "sequence"]);
    const replica = requireDatabaseUuid(entry.replica, "replica");
    const incarnation = hex(entry.incarnation, 16, "incarnation");
    const order = replica + incarnation;
    if (order <= previous) throw new TypeError("Observed origins must be strictly increasing.");
    previous = order;
    return { replica, incarnation, sequence: requireDatabaseU64(entry.sequence, "sequence", true).toString() };
  });
}

function observedFromWire(raw: unknown): readonly DatabaseObserved[] {
  const entries = array(raw).map(value => {
    const entry = object(value, ["replica", "incarnation", "sequence"]);
    return { replica: requireDatabaseUuid(entry.replica, "replica"), incarnation: hex(entry.incarnation, 16, "incarnation"), sequence: decimal(entry.sequence, "sequence", true) };
  });
  observedToWire(entries);
  return entries;
}

function eventFromWire(raw: unknown): DatabaseEvent {
  const entry = object(raw, ["database", "shard", "replica", "incarnation", "sequence"]);
  return {
    database: requireDatabaseUuid(entry.database, "database"), shard: decimal(entry.shard, "shard"),
    replica: requireDatabaseUuid(entry.replica, "replica"), incarnation: hex(entry.incarnation, 16, "incarnation"),
    sequence: decimal(entry.sequence, "sequence", true),
  };
}

export function decodeDatabasePoint(raw: unknown): DatabasePoint {
  const entry = object(raw, ["version", "state", "state_version", "context", "siblings"]);
  if (entry.version !== 1 || !["absent", "deleted", "value", "conflict"].includes(String(entry.state))) throw new TypeError("Unsupported point state or protocol.");
  const context = observedFromWire(entry.context);
  const siblings: DatabaseSibling[] = array(entry.siblings).map(rawSibling => {
    const sibling = object(rawSibling, ["event", "schema_generation", "schema_digest", "kind"], ["fields"]);
    const base = { event: eventFromWire(sibling.event), schema_generation: decimal(sibling.schema_generation, "schema_generation", true), schema_digest: hex(sibling.schema_digest, 32, "schema_digest") };
    if (sibling.kind === "tombstone") {
      object(rawSibling, ["event", "schema_generation", "schema_digest", "kind"]);
      return { ...base, kind: "tombstone" };
    }
    if (sibling.kind !== "live") throw new TypeError("Unsupported sibling kind.");
    object(rawSibling, ["event", "schema_generation", "schema_digest", "kind", "fields"]);
    const fields = fieldsFromWire(sibling.fields, 1, { nodes: 0 });
    validateNativeDocument(fieldsToWire(fields, 1, { nodes: 0 }));
    return { ...base, kind: "live", fields };
  });
  const state = entry.state as DatabasePoint["state"];
  const state_version = entry.state_version === null ? null : hex(entry.state_version, 32, "state_version");
  if ((state === "absent" && (context.length !== 0 || siblings.length !== 0 || state_version !== null)) ||
    (state === "deleted" && (siblings.length !== 1 || siblings[0]?.kind !== "tombstone" || state_version === null)) ||
    (state === "value" && (siblings.length !== 1 || siblings[0]?.kind !== "live" || state_version === null)) ||
    (state === "conflict" && (siblings.length < 2 || state_version !== null))) {
    throw new TypeError("Point state contradicts its siblings or condition token.");
  }
  const events = new Set<string>();
  for (const sibling of siblings) {
    const event = sibling.event;
    const key = event.replica + event.incarnation + ":" + event.sequence;
    if (events.has(key) || !context.some(origin => origin.replica === event.replica && origin.incarnation === event.incarnation && origin.sequence >= event.sequence)) {
      throw new TypeError("Point causal context is incomplete or contains duplicate siblings.");
    }
    events.add(key);
  }
  return { version: 1, state, state_version, context, siblings };
}

function cursor(value: unknown): string {
  if (typeof value !== "string" || value.length > 1024 || !/^[A-Za-z0-9_-]+$/u.test(value)) throw new TypeError("Cursor must be bounded opaque base64url.");
  return value;
}

function predicateToWire(predicate: DatabaseQueryPredicate): JsonObject {
  const entry = object(predicate, ["op", "path"], ["value"]);
  const path = array(entry.path, 16, 1).map(fieldId);
  if (entry.op === "is_null" || entry.op === "is_missing") {
    object(predicate, ["op", "path"]);
    return { op: entry.op, path };
  }
  if (!["equal", "less_than", "less_than_or_equal", "greater_than", "greater_than_or_equal"].includes(String(entry.op))) throw new TypeError("Unsupported query operator.");
  object(predicate, ["op", "path", "value"]);
  const value = encodeDatabaseValue(entry.value as DatabaseValue);
  if (!scalarTypes.has(String(value.type))) throw new TypeError("Query values must be supported scalars.");
  return { op: String(entry.op), path, value };
}

function queryLimit(value: unknown): number {
  if (typeof value !== "number" || !Number.isInteger(value) || value < 1 || value > 128) throw new TypeError("Query limit must be between 1 and 128.");
  return value;
}

export function encodeDatabaseQuery(query: DatabaseQuery): string {
  const entry = object(query, ["predicate", "limit"], ["cursor"]);
  return boundedJson({ version: 1, predicate: predicateToWire(entry.predicate as DatabaseQueryPredicate), limit: queryLimit(entry.limit), ...(entry.cursor === undefined ? {} : { cursor: cursor(entry.cursor) }) });
}

function rowsFromWire(raw: unknown): DatabaseQueryPage["rows"] {
  let previous = "";
  return array(raw, 128).map(rawRow => {
    const entry = object(rawRow, ["document", "point"]);
    const document = requireDatabaseUuid(entry.document, "document");
    if (document <= previous) throw new TypeError("Query documents must be strictly increasing.");
    previous = document;
    return { document, point: decodeDatabasePoint(entry.point) };
  });
}

export function decodeDatabaseQueryPage(raw: unknown): DatabaseQueryPage {
  const entry = object(raw, ["version", "rows"], ["next_cursor"]);
  if (entry.version !== 1) throw new TypeError("Unsupported query protocol.");
  return { version: 1, rows: rowsFromWire(entry.rows), ...(entry.next_cursor === undefined ? {} : { next_cursor: cursor(entry.next_cursor) }) };
}

export function encodeDatabaseSubscription(request: DatabaseSubscriptionRequest): string {
  if (request.phase === "snapshot") {
    const entry = object(request, ["phase", "predicate", "limit"], ["snapshot_cursor"]);
    return boundedJson({ version: 1, phase: "snapshot", predicate: predicateToWire(request.predicate), limit: queryLimit(entry.limit), ...(entry.snapshot_cursor === undefined ? {} : { snapshot_cursor: cursor(entry.snapshot_cursor) }) });
  }
  object(request, ["phase", "predicate", "poll_cursor"]);
  if (request.phase !== "poll") throw new TypeError("Unsupported subscription phase.");
  return boundedJson({ version: 1, phase: "poll", predicate: predicateToWire(request.predicate), poll_cursor: cursor(request.poll_cursor) });
}

export function decodeDatabaseSubscription(raw: unknown): DatabaseSubscriptionResponse {
  const initial = object(raw, ["version", "phase"], ["rows", "next_snapshot_cursor", "poll_cursor", "groups"]);
  if (initial.version !== 1) throw new TypeError("Unsupported subscription protocol.");
  if (initial.phase === "snapshot") {
    const continuation = Object.hasOwn(initial, "next_snapshot_cursor") ? "next_snapshot_cursor" : "poll_cursor";
    const entry = object(raw, ["version", "phase", "rows", continuation]);
    const rows = rowsFromWire(entry.rows);
    return continuation === "next_snapshot_cursor"
      ? { version: 1, phase: "snapshot", rows, next_snapshot_cursor: cursor(entry.next_snapshot_cursor) }
      : { version: 1, phase: "snapshot", rows, poll_cursor: cursor(entry.poll_cursor) };
  }
  const entry = object(raw, ["version", "phase", "groups", "poll_cursor"]);
  if (entry.phase !== "poll") throw new TypeError("Unsupported subscription phase.");
  let previous = 0n;
  const groups = array(entry.groups, 8).map(rawGroup => {
    const group = object(rawGroup, ["sequence", "effects"]);
    const sequence = decimal(group.sequence, "sequence", true);
    if (sequence <= previous) throw new TypeError("Subscription sequences must increase.");
    previous = sequence;
    return { sequence, effects: array(group.effects, 128).map(rawEffect => {
      const effect = object(rawEffect, ["document", "point"]);
      return { document: requireDatabaseUuid(effect.document, "document"), point: effect.point === null ? null : decodeDatabasePoint(effect.point) };
    }) };
  });
  return { version: 1, phase: "poll", groups, poll_cursor: cursor(entry.poll_cursor) };
}

export function encodeDatabaseAuthority(authority: DatabaseAuthority): JsonObject {
  if (authority.mode === "leaderless") { object(authority, ["mode"]); return { mode: "leaderless" }; }
  object(authority, ["mode", "generation", "writer"]);
  if (authority.mode !== "single_writer") throw new TypeError("Unsupported authority mode.");
  const writer = object(authority.writer, ["replica", "incarnation"]);
  return { mode: "single_writer", generation: requireDatabaseU64(authority.generation, "authority generation", true).toString(), writer: { replica: requireDatabaseUuid(writer.replica, "writer"), incarnation: hex(writer.incarnation, 16, "incarnation") } };
}

export function decodeDatabaseAuthority(raw: unknown): DatabaseAuthority {
  const initial = object(raw, ["mode"], ["generation", "writer"]);
  if (initial.mode === "leaderless") { object(raw, ["mode"]); return { mode: "leaderless" }; }
  const entry = object(raw, ["mode", "generation", "writer"]);
  if (entry.mode !== "single_writer") throw new TypeError("Unsupported authority mode.");
  const writer = object(entry.writer, ["replica", "incarnation"]);
  return { mode: "single_writer", generation: decimal(entry.generation, "authority generation", true), writer: { replica: requireDatabaseUuid(writer.replica, "writer"), incarnation: hex(writer.incarnation, 16, "incarnation") } };
}

function conditionToWire(condition: DatabaseCondition): JsonObject {
  if (condition.kind === "none" || condition.kind === "absent") { object(condition, ["kind"]); return { kind: condition.kind }; }
  object(condition, ["kind", "version"]);
  if (condition.kind !== "version") throw new TypeError("Unsupported condition.");
  return { kind: "version", version: hex(condition.version, 32, "condition version") };
}

function transactionToWire(transaction: DatabaseTransactionId): JsonObject {
  object(transaction, ["expires_at_unix_ms", "nonce"]);
  return { expires_at_unix_ms: requireDatabaseU64(transaction.expires_at_unix_ms, "expiry").toString(), nonce: hex(transaction.nonce, 16, "nonce") };
}

function transactionFromWire(raw: unknown): DatabaseTransactionId {
  const entry = object(raw, ["expires_at_unix_ms", "nonce"]);
  return { expires_at_unix_ms: decimal(entry.expires_at_unix_ms, "expiry"), nonce: hex(entry.nonce, 16, "nonce") };
}

export function decodeDatabaseCommitReceipt(raw: unknown): DatabaseCommitReceipt {
  const entry = object(raw, ["version", "durability", "transaction_id", "event", "commit_sequence", "effect_digest", "authority"]);
  if (entry.version !== 1 || (entry.durability !== "local_durable" && entry.durability !== "object_durable")) throw new TypeError("Unsupported durability or protocol.");
  return { version: 1, durability: entry.durability, transaction_id: transactionFromWire(entry.transaction_id), event: eventFromWire(entry.event), commit_sequence: decimal(entry.commit_sequence, "commit_sequence", true), effect_digest: hex(entry.effect_digest, 32, "effect digest"), authority: decodeDatabaseAuthority(entry.authority) };
}

function writeToWire(write: DatabaseWrite, authority: DatabaseAuthority): JsonObject {
  object(write, ["collection", "document", "schema_generation", "schema_digest", "condition", "observed", "value"]);
  if (authority.mode === "single_writer" && write.condition.kind === "none") throw new TypeError("Single-writer mutations require an atomic condition.");
  const value = write.value.kind === "tombstone"
    ? (object(write.value, ["kind"]), { kind: "tombstone" })
    : (object(write.value, ["kind", "fields"]), { kind: "live", fields: fieldsToWire(write.value.fields, 1, { nodes: 0 }) });
  if (write.value.kind !== "live" && write.value.kind !== "tombstone") throw new TypeError("Unsupported write kind.");
  if (value.kind === "live") validateNativeDocument(value.fields as readonly Json[]);
  return { collection: requireDatabaseUuid(write.collection, "collection"), document: requireDatabaseUuid(write.document, "document"), schema_generation: requireDatabaseU64(write.schema_generation, "schema generation", true).toString(), schema_digest: hex(write.schema_digest, 32, "schema digest"), condition: conditionToWire(write.condition), observed: observedToWire(write.observed), value };
}

class Bytes {
  readonly chunks: Uint8Array[] = [];
  length = 0;
  put(bytes: Uint8Array): void { this.length += bytes.length; if (this.length > maximumBodyBytes * 2) throw new TypeError("Canonical effect exceeds its byte limit."); this.chunks.push(bytes); }
  integer(value: bigint, size: number): void {
    const bytes = new Uint8Array(size);
    for (let index = 0; index < size; index++) { bytes[index] = Number(value & 255n); value >>= 8n; }
    this.put(bytes);
  }
  hex(value: string): void { this.put(Uint8Array.from(value.match(/../gu) ?? [], byte => Number.parseInt(byte, 16))); }
  uuid(value: string): void { this.hex(value.replaceAll("-", "")); }
  finish(): Uint8Array { const bytes = new Uint8Array(this.length); let offset = 0; for (const chunk of this.chunks) { bytes.set(chunk, offset); offset += chunk.length; } return bytes; }
}

/** Exact tb-format nested containers: u16 count, optional u32 field ID, u8 type, u32 byte length, bytes. */
function nativeValue(value: JsonObject, insideArray = false): { type: number; bytes: Uint8Array } {
  const bytes = new Bytes();
  let type: number;
  switch (value.type) {
    case "null": type = 1; break;
    case "bool": type = 2; bytes.integer(value.value === true ? 1n : 0n, 1); break;
    case "i64": type = 3; bytes.integer(BigInt(value.value as string), 8); break;
    case "utf8": type = 4; bytes.put(encoder.encode(value.value as string)); break;
    case "bytes": type = 5; bytes.put(parseBytes(value.value)); break;
    case "map":
    case "array": {
      const map = value.type === "map";
      if (!map && insideArray) throw new TypeError("Arrays cannot contain descendant arrays, including through maps.");
      type = map ? 6 : 7;
      const entries = array(map ? value.fields : value.items);
      bytes.integer(BigInt(entries.length), 2);
      for (const entry of entries) {
        const field = map ? object(entry, ["id", "value"]) : undefined;
        if (field !== undefined) bytes.integer(BigInt(fieldId(field.id)), 4);
        const native = nativeValue((field?.value ?? entry) as JsonObject, insideArray || !map);
        bytes.integer(BigInt(native.type), 1); bytes.integer(BigInt(native.bytes.length), 4); bytes.put(native.bytes);
      }
      break;
    }
    default: throw new TypeError("Unsupported database value type.");
  }
  if (bytes.length > maximumValueBytes) throw new TypeError("Native value exceeds its byte limit.");
  return { type, bytes: bytes.finish() };
}

function validateNativeDocument(fields: readonly Json[]): void {
  // tb-format document header + checksum, directory entries, and native payloads.
  let size = 98 + 13 * fields.length;
  for (const raw of fields) {
    size += nativeValue(object(raw, ["id", "value"]).value as JsonObject).bytes.length;
    if (size > maximumValueBytes) throw new TypeError("Encoded document exceeds its 1 MiB limit.");
  }
}

function effectBytes(scope: DatabaseScope, authority: JsonObject, writes: readonly JsonObject[]): Uint8Array {
  const bytes = new Bytes();
  bytes.put(encoder.encode("TB-DOC-EFFECT-v1\0"));
  bytes.uuid(requireDatabaseUuid(scope.tenant, "tenant")); bytes.uuid(requireDatabaseUuid(scope.database, "database")); bytes.integer(requireDatabaseU64(scope.shard, "shard"), 8);
  const singleWriter = authority.mode === "single_writer";
  bytes.integer(singleWriter ? 1n : 0n, 1);
  if (singleWriter) {
    const writer = object(authority.writer, ["replica", "incarnation"]);
    bytes.integer(BigInt(authority.generation as string), 8); bytes.uuid(writer.replica as string); bytes.hex(writer.incarnation as string);
  }
  bytes.integer(BigInt(writes.length), 8);
  for (const write of writes) {
    bytes.uuid(write.collection as string); bytes.uuid(write.document as string); bytes.integer(BigInt(write.schema_generation as string), 8); bytes.hex(write.schema_digest as string);
    const condition = object(write.condition, ["kind"], ["version"]);
    bytes.integer(condition.kind === "none" ? 0n : condition.kind === "absent" ? 1n : 2n, 1);
    if (condition.kind === "version") bytes.hex(condition.version as string);
    const observed = array(write.observed);
    bytes.integer(BigInt(observed.length), 8);
    for (const raw of observed) { const entry = object(raw, ["replica", "incarnation", "sequence"]); bytes.uuid(entry.replica as string); bytes.hex(entry.incarnation as string); bytes.integer(BigInt(entry.sequence as string), 8); }
    const value = object(write.value, ["kind"], ["fields"]);
    bytes.integer(value.kind === "tombstone" ? 0n : 1n, 1);
    if (value.kind === "live") {
      const fields = array(value.fields); bytes.integer(BigInt(fields.length), 8);
      for (const raw of fields) { const field = object(raw, ["id", "value"]); const native = nativeValue(field.value as JsonObject); bytes.integer(BigInt(field.id as number), 4); bytes.integer(BigInt(native.type), 1); bytes.integer(BigInt(native.bytes.length), 8); bytes.put(native.bytes); }
    }
  }
  return bytes.finish();
}

function boundedJson(value: Json): string {
  const encoded = JSON.stringify(value);
  if (encoder.encode(encoded).length > maximumBodyBytes) throw new TypeError("Request exceeds its 4 MiB limit.");
  return encoded;
}

export function encodeDatabaseStatus(identity: DatabaseTransactionIdentity): string {
  const entry = object(identity, ["transaction_id", "authority", "effect_digest"]);
  return boundedJson({ version: 1, transaction_id: transactionToWire(entry.transaction_id as DatabaseTransactionId), authority: encodeDatabaseAuthority(entry.authority as DatabaseAuthority), effect_digest: hex(entry.effect_digest, 32, "effect digest") });
}

export function decodeDatabaseStatus(raw: unknown): DatabaseTransactionStatus {
  const entry = object(raw, ["version", "state", "transaction_id", "authority", "effect_digest", "outcome"]);
  if (entry.version !== 1) throw new TypeError("Unsupported transaction status protocol.");
  const base = { version: 1 as const, transaction_id: transactionFromWire(entry.transaction_id), authority: decodeDatabaseAuthority(entry.authority), effect_digest: hex(entry.effect_digest, 32, "effect digest") };
  if (entry.state === "pending" || entry.state === "unknown" || entry.state === "expired") {
    if (entry.outcome !== null) throw new TypeError("Unresolved transaction status must have no terminal outcome.");
    return { ...base, state: entry.state, outcome: null };
  }
  if (entry.state === "committed") {
    const outcome = object(entry.outcome, ["receipt"]);
    const receipt = decodeDatabaseCommitReceipt(outcome.receipt);
    if (receipt.transaction_id.nonce !== base.transaction_id.nonce || receipt.transaction_id.expires_at_unix_ms !== base.transaction_id.expires_at_unix_ms || receipt.effect_digest !== base.effect_digest || JSON.stringify(encodeDatabaseAuthority(receipt.authority)) !== JSON.stringify(encodeDatabaseAuthority(base.authority))) {
      throw new TypeError("Status receipt contradicts its operation identity.");
    }
    return { ...base, state: "committed", outcome: { receipt } };
  }
  if (entry.state === "rejected") {
    const outcome = object(entry.outcome, ["error"]);
    const error = object(outcome.error, ["code"]);
    if (error.code !== "precondition_failed") throw new TypeError("Unsupported rejection outcome.");
    return { ...base, state: "rejected", outcome: { error: { code: "precondition_failed" } } };
  }
  throw new TypeError("Unsupported transaction status.");
}

/** Reject duplicate JSON members and unbounded syntax before typed response decoding. */
export function parseDatabaseJson(bytes: Uint8Array): unknown {
  if (bytes.length < 1 || bytes.length > maximumBodyBytes) throw new TypeError("JSON response exceeds its byte limit.");
  const input = new TextDecoder("utf-8", { fatal: true }).decode(bytes);
  let offset = 0;
  const whitespace = (): void => { while (/[ \t\r\n]/u.test(input[offset] ?? "x")) offset++; };
  const fail = (): never => { throw new TypeError("Response contains malformed or duplicate JSON members."); };
  const string = (): string => {
    const start = offset++;
    while (offset < input.length) {
      const code = input.charCodeAt(offset);
      if (input[offset] === '"') {
        offset++;
        const parsed: unknown = JSON.parse(input.slice(start, offset));
        if (typeof parsed !== "string") return fail();
        return parsed;
      }
      if (code < 32) return fail();
      if (input[offset++] === "\\") {
        const escaped = input[offset++];
        if (escaped === "u") {
          if (!/^[0-9a-fA-F]{4}$/u.test(input.slice(offset, offset + 4))) return fail();
          offset += 4;
        } else if (escaped === undefined || !'"\\/bfnrt'.includes(escaped)) return fail();
      }
    }
    return fail();
  };
  const value = (depth: number): void => {
    if (depth > 64) fail();
    whitespace();
    if (input[offset] === '"') { string(); return; }
    if (input[offset] === "{" || input[offset] === "[") {
      const map = input[offset++] === "{";
      const end = map ? "}" : "]";
      const keys = new Set<string>();
      whitespace();
      if (input[offset] === end) { offset++; return; }
      while (offset < input.length) {
        if (map) {
          whitespace();
          if (input[offset] !== '"') fail();
          const key = string();
          if (keys.has(key)) fail();
          keys.add(key);
          whitespace();
          if (input[offset++] !== ":") fail();
        }
        value(depth + 1);
        whitespace();
        if (input[offset] === end) { offset++; return; }
        if (input[offset++] !== ",") fail();
      }
      fail();
    }
    for (const literal of ["true", "false", "null"]) {
      if (input.slice(offset, offset + literal.length) === literal) { offset += literal.length; return; }
    }
    const number = /-?(?:0|[1-9][0-9]*)(?:\.[0-9]+)?(?:[eE][+-]?[0-9]+)?/uy;
    number.lastIndex = offset;
    if (number.exec(input) === null) fail();
    offset = number.lastIndex;
  };
  value(0); whitespace();
  if (offset !== input.length) fail();
  return JSON.parse(input) as unknown;
}

/** Build and snapshot the exact logical effect before awaiting WebCrypto. */
export async function encodeDatabaseCommit(scope: DatabaseScope, commit: DatabaseCommit): Promise<{ readonly body: string; readonly effect_digest: string }> {
  object(scope, ["tenant", "database", "shard"]);
  object(commit, ["transaction_id", "authority", "writes"]);
  const authority = encodeDatabaseAuthority(commit.authority);
  const transaction = transactionToWire(commit.transaction_id);
  let previous = "";
  const writes = array(commit.writes, 1024, 1).map(raw => {
    const write = writeToWire(raw as DatabaseWrite, commit.authority);
    const order = String(write.collection) + String(write.document);
    if (order <= previous) throw new TypeError("Writes must be sorted by collection and document.");
    previous = order;
    return write;
  });
  const canonical = effectBytes(scope, authority, writes);
  const digest = new Uint8Array(await crypto.subtle.digest("SHA-256", new Uint8Array(canonical).buffer));
  const effect_digest = Array.from(digest, byte => byte.toString(16).padStart(2, "0")).join("");
  return { body: boundedJson({ version: 1, transaction_id: transaction, authority, effect_digest, writes }), effect_digest };
}
