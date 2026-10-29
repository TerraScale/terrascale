// Current C# GET collection/shape contract (docs/shape-reads.md).
import { array, boolean, documentObject, malformed, object, string, u64 } from "./decode.js";
import { requireFilter } from "./filter.js";
import { parseU64, requireStoredId, requireVersion } from "./ids.js";
import { stringifyExactJson } from "./json.js";
/**
 * @import { JsonObject, JsonValue } from "./json.js"
 * @import { V1Filter } from "./filter.js"
 * @import { V1Transport } from "./transport.js"
 */
/**
 * @typedef {object} V1ShapeRequest
 * @property {V1Filter | undefined} [where] Filter using the current query grammar; projection is unsupported.
 * @property {string | undefined} [offset] -1 (default, server bootstrap only), snap.<head>.<base64url-id>, or a decimal log position.
 * @property {string | undefined} [handle] Required after bootstrap.
 * @property {boolean | undefined} [live] True only for log positions; waits up to 20 seconds.
 * @property {string | undefined} [cursor] Previous live response's TB-Shape-Cursor.
 */
/** @typedef {{ readonly op: "upsert"; readonly id: string; readonly version: string; readonly document: JsonObject } | { readonly op: "delete"; readonly id: string }} V1ShapeOp */
/** @typedef {{ readonly offset: string; readonly ops: readonly V1ShapeOp[] }} V1ShapeChange */
/**
 * One snapshot page or log chunk. Only a fresh open tail/live response can report upToDate.
 * @typedef {object} V1ShapePage
 * @property {string} shapeId
 * @property {string} handle
 * @property {string} offset
 * @property {string} nextOffset
 * @property {boolean} upToDate
 * @property {readonly V1ShapeChange[]} changes Apply commits and operations in their received order.
 * @property {string | undefined} cursor Echo this as cursor on the next live request.
 * @property {string | undefined} etag
 * @property {string | undefined} [coveredOffset] Free-read protocol coverage (feed sequence, not write offset).
 * @property {string | undefined} [valuesAt] Free-read materialization position.
 * @property {string | undefined} [cursorResume] Opaque free-read resume token.
 */
const maximumOffset = (1n << 63n) - 1n;
const handlePattern = /^h1-[0-9a-f]{16}$/u;
/** @param {unknown} value @returns {boolean} */
function isLogOffset(value) {
  const parsed = parseU64(value);
  return parsed !== undefined && parsed <= maximumOffset;
}
/**
 * Validates canonical C# offsets. Snapshot positions use a UTF-8/base64url keyset, not a page number.
 * @param {unknown} value
 * @returns {string}
 */
export function requireShapeOffset(value) {
  if (value === "-1" || isLogOffset(value)) return /** @type {string} */ (value);
  if (typeof value !== "string") throw new TypeError("A shape offset is a string.");
  const match = /^snap\.([0-9]+)\.(0|[A-Za-z0-9_-]{2,})$/u.exec(value);
  if (match !== null && isLogOffset(match[1])) {
    const encoded = match[2] ?? "";
    if (encoded === "0") return value;
    try {
      const bytes = Uint8Array.from(atob(encoded.replace(/-/gu, "+").replace(/_/gu, "/")), c => c.charCodeAt(0));
      const id = new TextDecoder("utf-8", { fatal: true, ignoreBOM: true }).decode(bytes);
      const canonical = btoa(String.fromCharCode(...new TextEncoder().encode(id))).replace(/\+/gu, "-").replace(/\//gu, "_").replace(/=+$/u, "");
      if (canonical === encoded) { requireStoredId(id); return value; }
    } catch { /* Invalid base64url or UTF-8. */ }
  }
  throw new TypeError("A shape offset is -1, a signed-64-bit decimal log position, or snap.<head>.<canonical-base64url-id>.");
}
/** @param {unknown} value @returns {string} */
export function requireShapeHandle(value) {
  if (typeof value !== "string" || !handlePattern.test(value)) throw new TypeError("A shape handle is h1- followed by 16 lowercase hex characters.");
  return value;
}
/** @param {V1ShapeRequest} request @returns {URLSearchParams} */
export function encodeShapeRequest(request) {
  if (typeof request !== "object" || request === null || Array.isArray(request)) throw new TypeError("A shape request is an object.");
  for (const key of Object.keys(request))
    if (!["where", "offset", "handle", "live", "cursor"].includes(key)) throw new TypeError(`Unknown shape member ${key}.`);
  const query = new URLSearchParams();
  if (request.where !== undefined) query.set("where", stringifyExactJson(requireFilter(request.where), "shape filter"));
  const offset = requireShapeOffset(request.offset ?? "-1");
  query.set("offset", offset);
  if (request.handle !== undefined) query.set("handle", requireShapeHandle(request.handle));
  else if (offset !== "-1") throw new TypeError("A shape handle is required after bootstrap.");
  if (request.live !== undefined && typeof request.live !== "boolean") throw new TypeError("live is a boolean.");
  if (request.live === true) {
    if (!isLogOffset(offset)) throw new TypeError("live requires a log offset.");
    query.set("live", "true");
  }
  if (request.cursor !== undefined) {
    if (request.live !== true || !isLogOffset(request.cursor) || BigInt(request.cursor) === maximumOffset)
      throw new TypeError("cursor requires live=true and a decimal below Int64.MaxValue.");
    query.set("cursor", request.cursor);
  }
  return query;
}
/** @param {JsonValue | undefined} value @param {Headers} headers @returns {V1ShapePage} */
export function decodeShapePage(value, headers) {
  const raw = object(value, "shape page");
  const shapeId = string(raw.shape_id, "shape_id");
  if (!/^[0-9a-f]{32}$/u.test(shapeId)) malformed("shape_id");
  const cursor = headers.get("tb-shape-cursor") ?? undefined;
  if (cursor !== undefined && !isLogOffset(cursor)) malformed("shape cursor");
  return Object.freeze({
    shapeId, handle: requireShapeHandle(raw.handle),
    offset: requireShapeOffset(raw.offset), nextOffset: requireShapeOffset(raw.next_offset),
    upToDate: boolean(raw.up_to_date, "shape up_to_date"), cursor,
    etag: headers.get("etag") ?? undefined,
    ...(raw.covered_offset === undefined ? {} : { coveredOffset: requireShapeOffset(raw.covered_offset) }),
    ...(raw.values_at === undefined ? {} : { valuesAt: requireShapeOffset(raw.values_at) }),
    ...(raw.cursor_resume === undefined ? {} : { cursorResume: string(raw.cursor_resume, "shape resume cursor") }),
    changes: Object.freeze(array(raw.changes, "shape changes").map(value => {
      const change = object(value, "shape change");
      return Object.freeze({ offset: u64(change.offset, "shape change offset"), ops: Object.freeze(array(change.ops, "shape operations").map(value => {
        const op = object(value, "shape operation");
        const id = requireStoredId(op.id);
        if (op.op === "delete") return Object.freeze({ op: /** @type {const} */ ("delete"), id });
        if (op.op !== "upsert") malformed("shape operation kind");
        return Object.freeze({ op: /** @type {const} */ ("upsert"), id, version: requireVersion(op.version), document: documentObject(op.document) });
      })) });
    })),
  });
}
/**
 * Bootstrap extracts continuation metadata from 307 but never forwards credentials to Location.
 * The next request is rebuilt against the same named route and configured endpoint.
 * @param {V1Transport} transport
 * @param {Readonly<Record<string, string>>} params
 * @param {V1ShapeRequest} request
 * @param {AbortSignal | undefined} signal
 * @returns {Promise<V1ShapePage>}
 */
export async function readShape(transport, params, request, signal) {
  const query = encodeShapeRequest(request);
  let response = await transport.call("readShape", params, { query, signal });
  if (response.status === 307) {
    if (query.get("offset") !== "-1") malformed("unexpected shape redirect");
    const source = new URL(await transport.url("readShape", params, signal));
    const location = response.headers.get("location");
    if (location === null) malformed("shape redirect Location");
    const target = new URL(location, source);
    if (target.origin !== source.origin || target.pathname !== source.pathname || target.username !== "" || target.password !== "" || target.hash !== "")
      malformed("shape redirect target");
    const handle = requireShapeHandle(target.searchParams.get("handle"));
    const offset = requireShapeOffset(target.searchParams.get("offset"));
    if (!offset.startsWith("snap.")) malformed("shape bootstrap offset");
    query.set("handle", handle);
    query.set("offset", offset);
    response = await transport.call("readShape", params, { query, signal });
    if (response.status !== 200) malformed("repeated shape redirect");
  }
  const page = decodeShapePage(response.value, response.headers);
  if (page.offset !== query.get("offset") || (query.has("handle") && page.handle !== query.get("handle"))) malformed("shape response position");
  return page;
}
