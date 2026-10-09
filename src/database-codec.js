import { requireId as requireDatabaseId } from "./ids.js";
export { requireId as requireDatabaseId } from "./ids.js";
/**
 * @import {
 *   DatabaseAuthority, DatabaseCommit, DatabaseCommitReceipt, DatabaseCondition, DatabaseEvent,
 *   DatabaseField, DatabaseObserved, DatabasePoint, DatabaseQuery, DatabaseQueryPage,
 *   DatabaseQueryPredicate, DatabaseScope, DatabaseSibling, DatabaseSubscriptionRequest,
 *   DatabaseSubscriptionResponse, DatabaseTransactionId, DatabaseValue, DatabaseWrite,
 *   DatabaseTransactionIdentity, DatabaseTransactionStatus,
 * } from "./database-types.js"
 */

/** @typedef {null | boolean | number | string | JsonArray | JsonObject} Json */
/** @typedef {readonly Json[]} JsonArray */
/** @typedef {{ readonly [key: string]: Json }} JsonObject */
/** @typedef {Record<string, unknown>} RecordValue */
const encoder = new TextEncoder();
const maxU64 = (1n << 64n) - 1n;
const minI64 = -(1n << 63n);
const maxI64 = (1n << 63n) - 1n;
const maximumBodyBytes = 4 * 1024 * 1024;
const maximumValueBytes = 1024 * 1024;
const scalarTypes = new Set(["bool", "i64", "utf8", "bytes"]);

/**
 * @param {unknown} value
 * @param {string} [name]
 * @param {boolean} [positive]
 * @returns {bigint}
 */
export function requireDatabaseU64(value, name = "u64", positive = false) {
  if (typeof value !== "bigint" || value < (positive ? 1n : 0n) || value > maxU64) {
    throw new TypeError(`${name} must be a ${positive ? "positive " : ""}u64 bigint.`);
  }
  return value;
}

/**
 * @param {unknown} value
 * @param {string} name
 * @param {boolean} [positive]
 * @returns {bigint}
 */
function decimal(value, name, positive = false) {
  if (typeof value !== "string" || !/^(0|[1-9][0-9]*)$/u.test(value)) throw new TypeError(`${name} must be canonical decimal.`);
  return requireDatabaseU64(BigInt(value), name, positive);
}

/**
 * @param {unknown} value
 * @param {number} bytes
 * @param {string} name
 * @returns {string}
 */
function hex(value, bytes, name) {
  if (typeof value !== "string" || value.length !== bytes * 2 || !/^[0-9a-f]+$/u.test(value)) {
    throw new TypeError(`${name} must be canonical lowercase hexadecimal.`);
  }
  return value;
}

/**
 * @param {unknown} value
 * @param {readonly string[]} required
 * @param {readonly string[]} [optional]
 * @returns {RecordValue}
 */
function object(value, required, optional = []) {
  if (typeof value !== "object" || value === null || Array.isArray(value)) throw new TypeError("Expected a protocol object.");
  const entry = /** @type {RecordValue} */ (value);
  if (required.some(key => !Object.hasOwn(entry, key)) || Object.keys(entry).some(key => !required.includes(key) && !optional.includes(key))) {
    throw new TypeError("Protocol object has missing or unknown fields.");
  }
  return entry;
}

/**
 * @param {unknown} value
 * @param {number} [maximum]
 * @param {number} [minimum]
 * @returns {readonly unknown[]}
 */
function array(value, maximum = 1024, minimum = 0) {
  if (!Array.isArray(value) || value.length < minimum || value.length > maximum) throw new TypeError("Protocol array exceeds its bounds.");
  return value;
}

/**
 * @param {unknown} value
 * @returns {number}
 */
function fieldId(value) {
  if (typeof value !== "number" || !Number.isInteger(value) || value < 1 || value > 0xffff_ffff) {
    throw new TypeError("Field ID must be a positive u32.");
  }
  return value;
}

/**
 * @param {unknown} value
 * @returns {string}
 */
function text(value) {
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

/**
 * @param {unknown} value
 * @returns {bigint}
 */
function i64(value) {
  if (typeof value !== "bigint" || value < minI64 || value > maxI64) throw new TypeError("i64 value must be an in-range bigint.");
  return value;
}

/**
 * @param {unknown} value
 * @returns {bigint}
 */
function parseI64(value) {
  if (typeof value !== "string" || !/^(0|-?[1-9][0-9]*)$/u.test(value)) throw new TypeError("i64 must be canonical decimal.");
  return i64(BigInt(value));
}

/**
 * @param {Uint8Array} bytes
 * @returns {string}
 */
function base64url(bytes) {
  let binary = "";
  for (const byte of bytes) binary += String.fromCharCode(byte);
  return btoa(binary).replaceAll("+", "-").replaceAll("/", "_").replace(/=+$/u, "");
}

/**
 * @param {unknown} value
 * @returns {Uint8Array}
 */
function parseBytes(value) {
  if (typeof value !== "string" || !/^[A-Za-z0-9_-]*$/u.test(value) || value.length > 1_398_102) throw new TypeError("Bytes must be bounded canonical base64url.");
  /** @type {string} */
  let binary;
  try { binary = atob(value.replaceAll("-", "+").replaceAll("_", "/")); }
  catch { throw new TypeError("Bytes must be canonical base64url."); }
  const bytes = Uint8Array.from(binary, character => character.charCodeAt(0));
  if (bytes.length > maximumValueBytes || base64url(bytes) !== value) throw new TypeError("Bytes must be bounded canonical base64url.");
  return bytes;
}

/** @typedef {{ nodes: number }} Budget */

/**
 * @param {number} depth
 * @param {Budget} budget
 * @returns {void}
 */
function takeNode(depth, budget) {
  if (depth > 16 || ++budget.nodes > 4096) throw new TypeError("Document exceeds its depth or node limit.");
}

/**
 * @param {readonly DatabaseField[]} fields
 * @param {number} depth
 * @param {Budget} budget
 * @returns {readonly Json[]}
 */
function fieldsToWire(fields, depth, budget) {
  const entries = array(fields);
  let previous = 0;
  return entries.map(raw => {
    const field = object(raw, ["id", "value"]);
    const id = fieldId(field.id);
    if (id <= previous) throw new TypeError("Field IDs must be strictly increasing.");
    previous = id;
    return { id, value: valueToWire(/** @type {DatabaseValue} */ (field.value), depth, budget) };
  });
}

/**
 * @param {DatabaseValue} value
 * @param {number} depth
 * @param {Budget} budget
 * @returns {JsonObject}
 */
function valueToWire(value, depth, budget) {
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
    case "map": return { type: "map", fields: fieldsToWire(/** @type {readonly DatabaseField[]} */ (object(value, ["type", "fields"]).fields), depth + 1, budget) };
    case "array": return { type: "array", items: array(object(value, ["type", "items"]).items).map(item => valueToWire(/** @type {DatabaseValue} */ (item), depth + 1, budget)) };
    default: throw new TypeError("Unsupported database value type.");
  }
}

/**
 * @param {unknown} raw
 * @param {number} depth
 * @param {Budget} budget
 * @returns {readonly DatabaseField[]}
 */
function fieldsFromWire(raw, depth, budget) {
  let previous = 0;
  return array(raw).map(value => {
    const entry = object(value, ["id", "value"]);
    const id = fieldId(entry.id);
    if (id <= previous) throw new TypeError("Field IDs must be strictly increasing.");
    previous = id;
    return { id, value: valueFromWire(entry.value, depth, budget) };
  });
}

/**
 * @param {unknown} raw
 * @param {number} depth
 * @param {Budget} budget
 * @returns {DatabaseValue}
 */
function valueFromWire(raw, depth, budget) {
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

/**
 * Typed value encoding preserves numeric field IDs, arrays, bytes, and explicit null.
 * @param {DatabaseValue} value
 * @returns {JsonObject}
 */
export function encodeDatabaseValue(value) {
  const wire = valueToWire(value, 1, { nodes: 0 });
  nativeValue(wire);
  return wire;
}

/**
 * @param {unknown} value
 * @returns {DatabaseValue}
 */
export function decodeDatabaseValue(value) {
  const decoded = valueFromWire(value, 1, { nodes: 0 });
  nativeValue(valueToWire(decoded, 1, { nodes: 0 }));
  return decoded;
}

/**
 * @param {readonly DatabaseObserved[]} entries
 * @returns {readonly Json[]}
 */
function observedToWire(entries) {
  let previous = "";
  return array(entries).map(raw => {
    const entry = object(raw, ["replica", "incarnation", "sequence"]);
    const replica = requireDatabaseId(entry.replica, "replica");
    const incarnation = hex(entry.incarnation, 16, "incarnation");
    const order = replica + incarnation;
    if (order <= previous) throw new TypeError("Observed origins must be strictly increasing.");
    previous = order;
    return { replica, incarnation, sequence: requireDatabaseU64(entry.sequence, "sequence", true).toString() };
  });
}

/**
 * @param {unknown} raw
 * @returns {readonly DatabaseObserved[]}
 */
function observedFromWire(raw) {
  const entries = array(raw).map(value => {
    const entry = object(value, ["replica", "incarnation", "sequence"]);
    return { replica: requireDatabaseId(entry.replica, "replica"), incarnation: hex(entry.incarnation, 16, "incarnation"), sequence: decimal(entry.sequence, "sequence", true) };
  });
  observedToWire(entries);
  return entries;
}

/**
 * @param {unknown} raw
 * @returns {DatabaseEvent}
 */
function eventFromWire(raw) {
  const entry = object(raw, ["database", "shard", "replica", "incarnation", "sequence"]);
  return {
    database: requireDatabaseId(entry.database, "database"), shard: decimal(entry.shard, "shard"),
    replica: requireDatabaseId(entry.replica, "replica"), incarnation: hex(entry.incarnation, 16, "incarnation"),
    sequence: decimal(entry.sequence, "sequence", true),
  };
}

/**
 * @param {unknown} raw
 * @returns {DatabasePoint}
 */
export function decodeDatabasePoint(raw) {
  const entry = object(raw, ["version", "state", "state_version", "context", "siblings"]);
  if (entry.version !== 1 || !["absent", "deleted", "value", "conflict"].includes(String(entry.state))) throw new TypeError("Unsupported point state or protocol.");
  const context = observedFromWire(entry.context);
  /** @type {DatabaseSibling[]} */
  const siblings = array(entry.siblings).map(rawSibling => {
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
  const state = /** @type {DatabasePoint["state"]} */ (entry.state);
  const state_version = entry.state_version === null ? null : hex(entry.state_version, 32, "state_version");
  if ((state === "absent" && (context.length !== 0 || siblings.length !== 0 || state_version !== null)) ||
    (state === "deleted" && (siblings.length !== 1 || siblings[0]?.kind !== "tombstone" || state_version === null)) ||
    (state === "value" && (siblings.length !== 1 || siblings[0]?.kind !== "live" || state_version === null)) ||
    (state === "conflict" && (siblings.length < 2 || state_version !== null))) {
    throw new TypeError("Point state contradicts its siblings or condition token.");
  }
  /** @type {Set<string>} */
  const events = new Set();
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

/**
 * @param {unknown} value
 * @returns {string}
 */
function cursor(value) {
  const message = "Cursor must be bounded opaque canonical base64url.";
  if (typeof value !== "string" || value.length > 1024 || !/^[A-Za-z0-9_-]+$/u.test(value)) throw new TypeError(message);
  // Validate only the encoding; the producer-owned cursor contents remain opaque.
  try { parseBytes(value); }
  catch { throw new TypeError(message); }
  return value;
}

/**
 * @param {DatabaseQueryPredicate} predicate
 * @returns {JsonObject}
 */
function predicateToWire(predicate) {
  const entry = object(predicate, ["op", "path"], ["value"]);
  const path = array(entry.path, 16, 1).map(fieldId);
  if (entry.op === "is_null" || entry.op === "is_missing") {
    object(predicate, ["op", "path"]);
    return { op: entry.op, path };
  }
  if (!["equal", "less_than", "less_than_or_equal", "greater_than", "greater_than_or_equal"].includes(String(entry.op))) throw new TypeError("Unsupported query operator.");
  object(predicate, ["op", "path", "value"]);
  const value = encodeDatabaseValue(/** @type {DatabaseValue} */ (entry.value));
  if (!scalarTypes.has(String(value.type))) throw new TypeError("Query values must be supported scalars.");
  return { op: String(entry.op), path, value };
}

/**
 * @param {unknown} value
 * @returns {number}
 */
function queryLimit(value) {
  if (typeof value !== "number" || !Number.isInteger(value) || value < 1 || value > 128) throw new TypeError("Query limit must be between 1 and 128.");
  return value;
}

/**
 * @param {DatabaseQuery} query
 * @returns {string}
 */
export function encodeDatabaseQuery(query) {
  const entry = object(query, ["predicate", "limit"], ["cursor"]);
  return boundedJson({ version: 1, predicate: predicateToWire(/** @type {DatabaseQueryPredicate} */ (entry.predicate)), limit: queryLimit(entry.limit), ...(entry.cursor === undefined ? {} : { cursor: cursor(entry.cursor) }) });
}

/**
 * @param {unknown} raw
 * @returns {DatabaseQueryPage["rows"]}
 */
function rowsFromWire(raw) {
  let previous = "";
  return array(raw, 128).map(rawRow => {
    const entry = object(rawRow, ["document", "point"]);
    const document = requireDatabaseId(entry.document, "document");
    if (document <= previous) throw new TypeError("Query documents must be strictly increasing.");
    previous = document;
    return { document, point: decodeDatabasePoint(entry.point) };
  });
}

/**
 * @param {unknown} raw
 * @returns {DatabaseQueryPage}
 */
export function decodeDatabaseQueryPage(raw) {
  const entry = object(raw, ["version", "rows"], ["next_cursor"]);
  if (entry.version !== 1) throw new TypeError("Unsupported query protocol.");
  return { version: 1, rows: rowsFromWire(entry.rows), ...(entry.next_cursor === undefined ? {} : { next_cursor: cursor(entry.next_cursor) }) };
}

/**
 * @param {DatabaseSubscriptionRequest} request
 * @returns {string}
 */
export function encodeDatabaseSubscription(request) {
  if (request.phase === "snapshot") {
    const entry = object(request, ["phase", "predicate", "limit"], ["snapshot_cursor"]);
    return boundedJson({ version: 1, phase: "snapshot", predicate: predicateToWire(request.predicate), limit: queryLimit(entry.limit), ...(entry.snapshot_cursor === undefined ? {} : { snapshot_cursor: cursor(entry.snapshot_cursor) }) });
  }
  object(request, ["phase", "predicate", "poll_cursor"]);
  if (request.phase !== "poll") throw new TypeError("Unsupported subscription phase.");
  return boundedJson({ version: 1, phase: "poll", predicate: predicateToWire(request.predicate), poll_cursor: cursor(request.poll_cursor) });
}

/**
 * @param {unknown} raw
 * @returns {DatabaseSubscriptionResponse}
 */
export function decodeDatabaseSubscription(raw) {
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
      return { document: requireDatabaseId(effect.document, "document"), point: effect.point === null ? null : decodeDatabasePoint(effect.point) };
    }) };
  });
  return { version: 1, phase: "poll", groups, poll_cursor: cursor(entry.poll_cursor) };
}

/**
 * @param {DatabaseAuthority} authority
 * @returns {JsonObject}
 */
export function encodeDatabaseAuthority(authority) {
  if (authority.mode === "leaderless") { object(authority, ["mode"]); return { mode: "leaderless" }; }
  object(authority, ["mode", "generation", "writer"]);
  if (authority.mode !== "single_writer") throw new TypeError("Unsupported authority mode.");
  const writer = object(authority.writer, ["replica", "incarnation"]);
  return { mode: "single_writer", generation: requireDatabaseU64(authority.generation, "authority generation", true).toString(), writer: { replica: requireDatabaseId(writer.replica, "writer"), incarnation: hex(writer.incarnation, 16, "incarnation") } };
}

/**
 * @param {unknown} raw
 * @returns {DatabaseAuthority}
 */
export function decodeDatabaseAuthority(raw) {
  const initial = object(raw, ["mode"], ["generation", "writer"]);
  if (initial.mode === "leaderless") { object(raw, ["mode"]); return { mode: "leaderless" }; }
  const entry = object(raw, ["mode", "generation", "writer"]);
  if (entry.mode !== "single_writer") throw new TypeError("Unsupported authority mode.");
  const writer = object(entry.writer, ["replica", "incarnation"]);
  return { mode: "single_writer", generation: decimal(entry.generation, "authority generation", true), writer: { replica: requireDatabaseId(writer.replica, "writer"), incarnation: hex(writer.incarnation, 16, "incarnation") } };
}

/**
 * @param {DatabaseCondition} condition
 * @returns {JsonObject}
 */
function conditionToWire(condition) {
  if (condition.kind === "none" || condition.kind === "absent") { object(condition, ["kind"]); return { kind: condition.kind }; }
  object(condition, ["kind", "version"]);
  if (condition.kind !== "version") throw new TypeError("Unsupported condition.");
  return { kind: "version", version: hex(condition.version, 32, "condition version") };
}

/**
 * @param {DatabaseTransactionId} transaction
 * @returns {JsonObject}
 */
function transactionToWire(transaction) {
  object(transaction, ["expires_at_unix_ms", "nonce"]);
  return { expires_at_unix_ms: requireDatabaseU64(transaction.expires_at_unix_ms, "expiry").toString(), nonce: hex(transaction.nonce, 16, "nonce") };
}

/**
 * @param {unknown} raw
 * @returns {DatabaseTransactionId}
 */
function transactionFromWire(raw) {
  const entry = object(raw, ["expires_at_unix_ms", "nonce"]);
  return { expires_at_unix_ms: decimal(entry.expires_at_unix_ms, "expiry"), nonce: hex(entry.nonce, 16, "nonce") };
}

/**
 * @param {unknown} raw
 * @returns {DatabaseCommitReceipt}
 */
export function decodeDatabaseCommitReceipt(raw) {
  const entry = object(raw, ["version", "durability", "transaction_id", "event", "commit_sequence", "effect_digest", "authority"]);
  if (entry.version !== 1 || (entry.durability !== "local_durable" && entry.durability !== "object_durable")) throw new TypeError("Unsupported durability or protocol.");
  return { version: 1, durability: entry.durability, transaction_id: transactionFromWire(entry.transaction_id), event: eventFromWire(entry.event), commit_sequence: decimal(entry.commit_sequence, "commit_sequence", true), effect_digest: hex(entry.effect_digest, 32, "effect digest"), authority: decodeDatabaseAuthority(entry.authority) };
}

/**
 * @param {DatabaseWrite} write
 * @param {DatabaseAuthority} authority
 * @returns {JsonObject}
 */
function writeToWire(write, authority) {
  object(write, ["collection", "document", "schema_generation", "schema_digest", "condition", "observed", "value"]);
  if (authority.mode === "single_writer" && write.condition.kind === "none") throw new TypeError("Single-writer mutations require an atomic condition.");
  const value = write.value.kind === "tombstone"
    ? (object(write.value, ["kind"]), { kind: "tombstone" })
    : (object(write.value, ["kind", "fields"]), { kind: "live", fields: fieldsToWire(write.value.fields, 1, { nodes: 0 }) });
  if (write.value.kind !== "live" && write.value.kind !== "tombstone") throw new TypeError("Unsupported write kind.");
  if (value.kind === "live") validateNativeDocument(/** @type {readonly Json[]} */ (value.fields));
  return { collection: requireDatabaseId(write.collection, "collection"), document: requireDatabaseId(write.document, "document"), schema_generation: requireDatabaseU64(write.schema_generation, "schema generation", true).toString(), schema_digest: hex(write.schema_digest, 32, "schema digest"), condition: conditionToWire(write.condition), observed: observedToWire(write.observed), value };
}

class Bytes {
  /**
   * @readonly
   * @type {Uint8Array[]}
   */
  chunks = [];
  length = 0;
  /**
   * @param {Uint8Array} bytes
   * @returns {void}
   */
  put(bytes) { this.length += bytes.length; if (this.length > maximumBodyBytes * 2) throw new TypeError("Canonical effect exceeds its byte limit."); this.chunks.push(bytes); }
  /**
   * @param {bigint} value
   * @param {number} size
   * @returns {void}
   */
  integer(value, size) {
    const bytes = new Uint8Array(size);
    for (let index = 0; index < size; index++) { bytes[index] = Number(value & 255n); value >>= 8n; }
    this.put(bytes);
  }
  /**
   * @param {string} value
   * @returns {void}
   */
  hex(value) { this.put(Uint8Array.from(value.match(/../gu) ?? [], byte => Number.parseInt(byte, 16))); }
  /**
   * @param {string} value
   * @returns {void}
   */
  id(value) {
    const alphabet = "0123456789ABCDEFGHJKMNPQRSTVWXYZ";
    let bits = 0n;
    for (const char of requireDatabaseId(value)) bits = (bits << 5n) | BigInt(alphabet.indexOf(char));
    this.hex(bits.toString(16).padStart(32, "0"));
  }
  /** @returns {Uint8Array} */
  finish() { const bytes = new Uint8Array(this.length); let offset = 0; for (const chunk of this.chunks) { bytes.set(chunk, offset); offset += chunk.length; } return bytes; }
}

/**
 * Exact tb-format nested containers: u16 count, optional u32 field ID, u8 type, u32 byte length, bytes.
 * @param {JsonObject} value
 * @param {boolean} [insideArray]
 * @returns {{ type: number; bytes: Uint8Array }}
 */
function nativeValue(value, insideArray = false) {
  const bytes = new Bytes();
  /** @type {number} */
  let type;
  switch (value.type) {
    case "null": type = 1; break;
    case "bool": type = 2; bytes.integer(value.value === true ? 1n : 0n, 1); break;
    case "i64": type = 3; bytes.integer(BigInt(/** @type {string} */ (value.value)), 8); break;
    case "utf8": type = 4; bytes.put(encoder.encode(/** @type {string} */ (value.value))); break;
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
        const native = nativeValue(/** @type {JsonObject} */ (field?.value ?? entry), insideArray || !map);
        bytes.integer(BigInt(native.type), 1); bytes.integer(BigInt(native.bytes.length), 4); bytes.put(native.bytes);
      }
      break;
    }
    default: throw new TypeError("Unsupported database value type.");
  }
  if (bytes.length > maximumValueBytes) throw new TypeError("Native value exceeds its byte limit.");
  return { type, bytes: bytes.finish() };
}

/**
 * @param {readonly Json[]} fields
 * @returns {void}
 */
function validateNativeDocument(fields) {
  // tb-format document header + checksum, directory entries, and native payloads.
  let size = 98 + 13 * fields.length;
  for (const raw of fields) {
    size += nativeValue(/** @type {JsonObject} */ (object(raw, ["id", "value"]).value)).bytes.length;
    if (size > maximumValueBytes) throw new TypeError("Encoded document exceeds its 1 MiB limit.");
  }
}

/**
 * @param {DatabaseScope} scope
 * @param {JsonObject} authority
 * @param {readonly JsonObject[]} writes
 * @returns {Uint8Array}
 */
function effectBytes(scope, authority, writes) {
  const bytes = new Bytes();
  bytes.put(encoder.encode("TB-DOC-EFFECT-v1\0"));
  bytes.id(requireDatabaseId(scope.tenant, "tenant")); bytes.id(requireDatabaseId(scope.database, "database")); bytes.integer(requireDatabaseU64(scope.shard, "shard"), 8);
  const singleWriter = authority.mode === "single_writer";
  bytes.integer(singleWriter ? 1n : 0n, 1);
  if (singleWriter) {
    const writer = object(authority.writer, ["replica", "incarnation"]);
    bytes.integer(BigInt(/** @type {string} */ (authority.generation)), 8); bytes.id(/** @type {string} */ (writer.replica)); bytes.hex(/** @type {string} */ (writer.incarnation));
  }
  bytes.integer(BigInt(writes.length), 8);
  for (const write of writes) {
    bytes.id(/** @type {string} */ (write.collection)); bytes.id(/** @type {string} */ (write.document)); bytes.integer(BigInt(/** @type {string} */ (write.schema_generation)), 8); bytes.hex(/** @type {string} */ (write.schema_digest));
    const condition = object(write.condition, ["kind"], ["version"]);
    bytes.integer(condition.kind === "none" ? 0n : condition.kind === "absent" ? 1n : 2n, 1);
    if (condition.kind === "version") bytes.hex(/** @type {string} */ (condition.version));
    const observed = array(write.observed);
    bytes.integer(BigInt(observed.length), 8);
    for (const raw of observed) { const entry = object(raw, ["replica", "incarnation", "sequence"]); bytes.id(/** @type {string} */ (entry.replica)); bytes.hex(/** @type {string} */ (entry.incarnation)); bytes.integer(BigInt(/** @type {string} */ (entry.sequence)), 8); }
    const value = object(write.value, ["kind"], ["fields"]);
    bytes.integer(value.kind === "tombstone" ? 0n : 1n, 1);
    if (value.kind === "live") {
      const fields = array(value.fields); bytes.integer(BigInt(fields.length), 8);
      for (const raw of fields) { const field = object(raw, ["id", "value"]); const native = nativeValue(/** @type {JsonObject} */ (field.value)); bytes.integer(BigInt(/** @type {number} */ (field.id)), 4); bytes.integer(BigInt(native.type), 1); bytes.integer(BigInt(native.bytes.length), 8); bytes.put(native.bytes); }
    }
  }
  return bytes.finish();
}

/**
 * @param {Json} value
 * @returns {string}
 */
function boundedJson(value) {
  const encoded = JSON.stringify(value);
  if (encoder.encode(encoded).length > maximumBodyBytes) throw new TypeError("Request exceeds its 4 MiB limit.");
  return encoded;
}

/**
 * @param {DatabaseTransactionIdentity} identity
 * @returns {string}
 */
export function encodeDatabaseStatus(identity) {
  const entry = object(identity, ["transaction_id", "authority", "effect_digest"]);
  return boundedJson({ version: 1, transaction_id: transactionToWire(/** @type {DatabaseTransactionId} */ (entry.transaction_id)), authority: encodeDatabaseAuthority(/** @type {DatabaseAuthority} */ (entry.authority)), effect_digest: hex(entry.effect_digest, 32, "effect digest") });
}

/**
 * @param {unknown} raw
 * @returns {DatabaseTransactionStatus}
 */
export function decodeDatabaseStatus(raw) {
  const entry = object(raw, ["version", "state", "transaction_id", "authority", "effect_digest", "outcome"]);
  if (entry.version !== 1) throw new TypeError("Unsupported transaction status protocol.");
  const base = { version: /** @type {const} */ (1), transaction_id: transactionFromWire(entry.transaction_id), authority: decodeDatabaseAuthority(entry.authority), effect_digest: hex(entry.effect_digest, 32, "effect digest") };
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

/**
 * Reject duplicate JSON members and unbounded syntax before typed response decoding.
 * @param {Uint8Array} bytes
 * @returns {unknown}
 */
export function parseDatabaseJson(bytes) {
  if (bytes.length < 1 || bytes.length > maximumBodyBytes) throw new TypeError("JSON response exceeds its byte limit.");
  const input = new TextDecoder("utf-8", { fatal: true }).decode(bytes);
  let offset = 0;
  /** @type {() => void} */
  const whitespace = () => { while (/[ \t\r\n]/u.test(input[offset] ?? "x")) offset++; };
  /** @type {() => never} */
  const fail = () => { throw new TypeError("Response contains malformed or duplicate JSON members."); };
  /** @type {() => string} */
  const string = () => {
    const start = offset++;
    while (offset < input.length) {
      const code = input.charCodeAt(offset);
      if (input[offset] === '"') {
        offset++;
        /** @type {unknown} */
        const parsed = JSON.parse(input.slice(start, offset));
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
  /** @type {(depth: number) => void} */
  const value = (depth) => {
    if (depth > 64) fail();
    whitespace();
    if (input[offset] === '"') { string(); return; }
    if (input[offset] === "{" || input[offset] === "[") {
      const map = input[offset++] === "{";
      const end = map ? "}" : "]";
      /** @type {Set<string>} */
      const keys = new Set();
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
  return /** @type {unknown} */ (JSON.parse(input));
}

/**
 * Build and snapshot the exact logical effect before awaiting WebCrypto.
 * @param {DatabaseScope} scope
 * @param {DatabaseCommit} commit
 * @returns {Promise<{ readonly body: string; readonly effect_digest: string }>}
 */
export async function encodeDatabaseCommit(scope, commit) {
  object(scope, ["tenant", "database", "shard"]);
  object(commit, ["transaction_id", "authority", "writes"]);
  const authority = encodeDatabaseAuthority(commit.authority);
  const transaction = transactionToWire(commit.transaction_id);
  let previous = "";
  const writes = array(commit.writes, 1024, 1).map(raw => {
    const write = writeToWire(/** @type {DatabaseWrite} */ (raw), commit.authority);
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
