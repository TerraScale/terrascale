import { documentObject, malformed, object, u64 } from "./decode.js";
import { parseV1Error } from "./errors.js";
import { requireStoredId, requireName } from "./ids.js";
import { parseExactJson } from "./json.js";
import { BodyTransportError } from "./transport.js";
/** @import { JsonObject } from "./json.js" */
/** @import { V1Transport } from "./transport.js" */

/**
 * Cursors are opaque and must be resumed with the identical collection filter.
 * @typedef {object} V1ChangeStreamRequest
 * @property {string | undefined} [cursor] An issued cursor, or `now`; omitted starts at retention.
 * @property {readonly string[] | undefined} [collections] One to 64 collection names.
 * @property {string | undefined} [lastEventId] Overrides cursor, as the producer's Last-Event-ID header.
 */
/**
 * At-least-once delivery: callers deduplicate by origin, version, collection and id.
 * The document is its current serving value, including null, rather than a historical value.
 * Only the last change of an origin/version carries a resumable cursor.
 * @typedef {{ readonly type: "change"; readonly cursor: string | undefined; readonly collection: string; readonly id: string;
 *   readonly op: "upsert" | "delete"; readonly version: string; readonly origin: string;
 *   readonly document: JsonObject | null }} V1DatabaseChange
 */
/** @typedef {{ readonly type: "cursor"; readonly cursor: string }} V1DatabaseChangeCursor */
/** @typedef {{ type: string | undefined; id: string | undefined; data: string }} WireEvent */

// A 1 MiB document may require six bytes per character when JSON escapes it.
const maximumEventBytes = 8 * 1024 * 1024;

/** @param {unknown} value @returns {string} */
function requireCursor(value) {
  if (typeof value !== "string" || value === "" || /[\u0000-\u0020\u007f-\u009f\ud800-\udfff]/u.test(value))
    throw new TypeError("A changes cursor is a non-empty opaque string without whitespace or control characters.");
  return value;
}

/** @param {V1ChangeStreamRequest} request */
function encodeRequest(request) {
  if (typeof request !== "object" || request === null || Array.isArray(request)) throw new TypeError("A changes request is an object.");
  for (const key of Object.keys(request))
    if (!["cursor", "collections", "lastEventId"].includes(key)) throw new TypeError(`Unknown changes member ${key}.`);
  const query = new URLSearchParams();
  if (request.cursor !== undefined) query.set("cursor", requireCursor(request.cursor));
  if (request.collections !== undefined) {
    if (!Array.isArray(request.collections) || request.collections.length < 1 || request.collections.length > 64)
      throw new TypeError("A changes filter has one to 64 collections.");
    query.set("collections", request.collections.map(name => requireName("collection", name)).join(","));
  }
  return { query, lastEventId: request.lastEventId === undefined ? undefined : requireCursor(request.lastEventId) };
}

/**
 * Parses lines from bytes so split UTF-8 and CRLF never change a checkpoint.
 * @param {Response} response
 * @param {AbortSignal | undefined} signal
 * @returns {AsyncGenerator<WireEvent, void, undefined>}
 */
async function* events(response, signal) {
  const reader = response.body?.getReader();
  if (reader === undefined) malformed("empty changes stream");
  const abort = () => { void reader.cancel(signal?.reason).catch(() => undefined); };
  signal?.addEventListener("abort", abort, { once: true });
  const decoder = new TextDecoder("utf-8", { fatal: true, ignoreBOM: true });
  /** @type {Uint8Array[]} */
  let lineParts = [];
  let lineBytes = 0;
  let eventBytes = 0;
  let skipLf = false;
  let firstLine = true;
  /** @type {string | undefined} */
  let type;
  /** @type {string | undefined} */
  let id;
  /** @type {string[]} */
  let data = [];
  let hasFields = false;
  /** @param {Uint8Array} bytes */
  const append = bytes => {
    eventBytes += bytes.byteLength;
    if (eventBytes > maximumEventBytes) malformed("changes event exceeding 8 MiB");
    if (bytes.byteLength > 0) { lineParts.push(bytes); lineBytes += bytes.byteLength; }
  };
  try {
    while (true) {
      signal?.throwIfAborted();
      /** @type {ReadableStreamReadResult<Uint8Array>} */
      let part;
      try { part = await reader.read(); }
      catch (error) { signal?.throwIfAborted(); throw new BodyTransportError(error); }
      signal?.throwIfAborted();
      if (part.done) break;
      let start = 0;
      for (let index = 0; index < part.value.byteLength; index++) {
        const byte = part.value[index];
        if (skipLf) {
          skipLf = false;
          if (byte === 10) { start = index + 1; continue; }
        }
        if (byte !== 10 && byte !== 13) continue;
        append(part.value.subarray(start, index));
        if (++eventBytes > maximumEventBytes) malformed("changes event exceeding 8 MiB");
        const bytes = new Uint8Array(lineBytes);
        let at = 0;
        for (const fragment of lineParts) { bytes.set(fragment, at); at += fragment.byteLength; }
        let line = decoder.decode(bytes);
        if (firstLine) { firstLine = false; if (line.startsWith("\ufeff")) line = line.slice(1); }
        lineParts = [];
        lineBytes = 0;
        start = index + 1;
        skipLf = byte === 13;
        if (line === "") {
          const event = hasFields ? { type, id, data: data.join("\n") } : undefined;
          type = undefined; id = undefined; data = []; hasFields = false; eventBytes = 0;
          if (event !== undefined) { signal?.throwIfAborted(); yield event; signal?.throwIfAborted(); }
          continue;
        }
        if (line.startsWith(":")) continue;
        const colon = line.indexOf(":");
        const field = colon === -1 ? line : line.slice(0, colon);
        let value = colon === -1 ? "" : line.slice(colon + 1);
        if (value.startsWith(" ")) value = value.slice(1);
        if (field === "event") { type = value; hasFields = true; }
        else if (field === "id") { id = value; hasFields = true; }
        else if (field === "data") { data.push(value); hasFields = true; }
      }
      append(part.value.subarray(start));
    }
    if (lineBytes !== 0 || hasFields) malformed("incomplete changes event");
  } finally {
    signal?.removeEventListener("abort", abort);
    await reader.cancel().catch(() => undefined);
    reader.releaseLock();
  }
}

/** @param {WireEvent} event @param {Headers} headers @returns {V1DatabaseChange | V1DatabaseChangeCursor} */
function decodeEvent(event, headers) {
  /** @type {import("./json.js").JsonValue} */
  let value;
  try { value = parseExactJson(event.data); }
  catch { malformed("changes event JSON"); }
  const raw = object(value, "changes event");
  if (event.type === "error") {
    const detail = object(raw.error, "changes error");
    if (typeof detail.code !== "string" || detail.code === "") malformed("changes error code");
    // Headers have already committed: retain the actual HTTP 200 and the producer's terminal code.
    throw parseV1Error(200, headers, event.data);
  }
  if (event.type === "cursor") {
    const cursor = requireCursor(event.id);
    if (raw.cursor !== cursor) malformed("changes cursor position");
    return Object.freeze({ type: "cursor", cursor });
  }
  if (event.type !== "change") malformed("changes event kind");
  const cursor = event.id === undefined ? undefined : requireCursor(event.id);
  if (raw.op !== "upsert" && raw.op !== "delete") malformed("changes operation");
  if (typeof raw.origin !== "string" || !/^[0-9a-f]{16}$/u.test(raw.origin)) malformed("changes origin");
  return Object.freeze({ type: "change", cursor, collection: requireName("collection", raw.collection),
    id: requireStoredId(raw.id), op: raw.op, version: u64(raw.version, "changes version"), origin: raw.origin,
    document: raw.document === null ? null : documentObject(raw.document),
  });
}

/** @param {AbortSignal | undefined} signal @returns {Promise<void>} */
function reconnectDelay(signal) {
  signal?.throwIfAborted();
  return new Promise((resolve, reject) => {
    const abort = () => { clearTimeout(timer); signal?.removeEventListener("abort", abort); reject(signal?.reason); };
    const timer = setTimeout(() => { signal?.removeEventListener("abort", abort); resolve(); }, 250);
    signal?.addEventListener("abort", abort, { once: true });
  });
}

/**
 * Reconnects only a normally completed, valid stream, with the latest issued cursor.
 * Denials, terminal errors, malformed data and lost connections are returned to the caller.
 * @param {V1Transport} transport
 * @param {string} database
 * @param {V1ChangeStreamRequest} [request]
 * @param {AbortSignal | undefined} [signal]
 * @returns {AsyncGenerator<V1DatabaseChange | V1DatabaseChangeCursor, void, undefined>}
 */
export function streamChanges(transport, database, request = {}, signal) {
  const controller = new AbortController();
  const combined = signal === undefined ? controller.signal : AbortSignal.any([signal, controller.signal]);
  const iterator = readChanges(transport, database, request, combined);
  const next = iterator.next.bind(iterator);
  const finish = iterator.return.bind(iterator);
  const closed = new DOMException("The changes stream was closed.", "AbortError");
  iterator.next = async () => {
    try { return await next(); }
    catch (error) { if (error === closed) return { done: true, value: undefined }; throw error; }
  };
  // Native generator return queues behind a pending read; interrupt its private signal first.
  iterator.return = value => { controller.abort(closed); return finish(value); };
  return iterator;
}

/**
 * @param {V1Transport} transport
 * @param {string} database
 * @param {V1ChangeStreamRequest} request
 * @param {AbortSignal} signal
 * @returns {AsyncGenerator<V1DatabaseChange | V1DatabaseChangeCursor, void, undefined>}
 */
async function* readChanges(transport, database, request, signal) {
  const encoded = encodeRequest(request);
  let lastEventId = encoded.lastEventId;
  while (true) {
    signal?.throwIfAborted();
    const response = await transport.openStream("streamDatabaseChanges", { database }, {
      query: encoded.query, ...(lastEventId === undefined ? {} : { headers: { "last-event-id": lastEventId } }), signal,
    });
    let count = 0;
    for await (const wire of events(response, signal)) {
      const event = decodeEvent(wire, response.headers);
      if (event.cursor !== undefined) lastEventId = event.cursor;
      count++;
      yield event;
      signal?.throwIfAborted();
    }
    if (count === 0 || lastEventId === undefined) malformed("empty changes stream");
    await reconnectDelay(signal);
  }
}
