import { documentObject, malformed, object } from "./decode.js";
import { requireStoredId, requireName } from "./ids.js";
import { parseExactJson } from "./json.js";
import { BodyTransportError } from "./transport.js";
/** @import { JsonObject } from "./json.js" */
/** @import { V1Transport } from "./transport.js" */

/** @typedef {{ readonly collection: string; readonly id: string; readonly document: JsonObject }} V1ExportedDocument */
/**
 * @typedef {object} V1ExportOptions
 * @property {{ readonly collection: string; readonly id: string } | undefined} [after] Resume after this document; the resumed part reads a new snapshot.
 * @property {AbortSignal | undefined} [signal]
 */

// A 1 MiB document may require six characters per character when JSON escapes it.
const maximumLineLength = 8 * 1024 * 1024;

/** @param {string} line @returns {V1ExportedDocument} */
function decodeLine(line) {
  /** @type {import("./json.js").JsonValue} */
  let value;
  try { value = parseExactJson(line); }
  catch { malformed("export line JSON"); }
  const raw = object(value, "export line");
  return Object.freeze({ collection: requireName("collection", raw.collection), id: requireStoredId(raw.id), document: documentObject(raw.document) });
}

/**
 * Streams every live document of a database as NDJSON, in collection then id order, from one read snapshot.
 * A stream that ends inside a line is incomplete and throws; resume with `after` set to the last document received.
 * @param {V1Transport} transport
 * @param {string} database
 * @param {V1ExportOptions} [options]
 * @returns {AsyncGenerator<V1ExportedDocument, void, undefined>}
 */
export async function* exportDocuments(transport, database, options = {}) {
  const { after, signal } = options;
  const query = new URLSearchParams();
  if (after !== undefined) {
    if (typeof after !== "object" || after === null) throw new TypeError("export after is { collection, id }.");
    query.set("after_collection", requireName("collection", after.collection));
    query.set("after_id", requireStoredId(after.id));
  }
  const response = await transport.openStream("exportDatabase", { database }, { query, signal });
  const body = /** @type {ReadableStream<Uint8Array>} */ (response.body);
  const decoder = /** @type {ReadableWritablePair<string, Uint8Array>} */ (new TextDecoderStream("utf-8", { fatal: true, ignoreBOM: true }));
  const reader = body.pipeThrough(decoder).getReader();
  let pending = "";
  try {
    while (true) {
      /** @type {ReadableStreamReadResult<string>} */
      let part;
      try { part = await reader.read(); }
      catch (error) { signal?.throwIfAborted(); throw new BodyTransportError(error); }
      if (part.done) break;
      // Split only the new text so a long line arriving in many chunks is scanned once.
      const lines = part.value.split("\n");
      lines[0] = pending + lines[0];
      pending = /** @type {string} */ (lines.pop());
      if (pending.length > maximumLineLength) malformed("export line exceeding 8 MiB");
      for (const line of lines) yield decodeLine(line);
    }
    if (pending !== "") malformed("incomplete export line");
  } finally {
    await reader.cancel().catch(() => undefined);
    reader.releaseLock();
  }
}
