// The proposed free-read v1 protocol is separate from the historical shape() contract.
import { malformed, object, string } from "./decode.js";
import { decodeShapePage, encodeShapeRequest, requireShapeHandle, requireShapeOffset } from "./shapes.js";
import { decryptShapeObject } from "./shape-crypto.js";
import { parseExactJson } from "./json.js";
/** @import { V1Transport } from "./transport.js" */
/** @import { V1ShapeRequest, V1ShapePage } from "./shapes.js" */
/** @typedef {{handle: string; offset: string; epoch: string; keyId: string}} V1SealedShapePosition */
/** @param {unknown} value @returns {string} */
function decimal(value) {
  const text = requireShapeOffset(value);
  if (text === "-1" || text.startsWith("snap.")) malformed("shape decimal");
  return text;
}
/** @param {unknown} value @returns {string} */
function resumeToken(value) {
  // Resume envelopes are opaque: validate header safety, never decode or normalize them.
  if (typeof value !== "string" || !/^[A-Za-z0-9_-]+$/u.test(value)) malformed("shape resume cursor");
  return value;
}
/** @param {string} offset @returns {string} */
function snapshotKey(offset) {
  const encoded = offset.split(".")[2] ?? "";
  return new TextDecoder("utf-8", { fatal: true, ignoreBOM: true }).decode(
    Uint8Array.from(atob(encoded.replace(/-/gu, "+").replace(/_/gu, "/")), c => c.charCodeAt(0)),
  );
}
/** @param {import("./json.js").JsonValue | undefined} value @param {Headers} headers @returns {V1ShapePage} */
export function decodeFreeShapePage(value, headers) {
  const page = decodeShapePage(value, headers);
  const raw = object(value, "free shape page");
  const covered = decimal(raw.covered_offset);
  const values = decimal(raw.values_at);
  resumeToken(raw.cursor_resume);
  if (page.offset === "-1" || page.nextOffset === "-1" || BigInt(values) < BigInt(covered)) malformed("shape coverage");
  if (page.offset.startsWith("snap.")) {
    const head = page.offset.split(".")[1];
    if (covered !== head || values !== head || page.upToDate || page.changes.length !== 1 || page.changes[0]?.offset !== head ||
        page.changes[0]?.ops.some(op => op.op !== "upsert") ||
        (page.nextOffset.startsWith("snap.") ? page.nextOffset.split(".")[1] !== head || page.nextOffset === page.offset : page.nextOffset !== head))
      malformed("shape snapshot coverage");
    if (page.nextOffset.startsWith("snap.") && (page.nextOffset.endsWith(".0") ||
        (!page.offset.endsWith(".0") && snapshotKey(page.nextOffset) <= snapshotKey(page.offset)))) malformed("shape snapshot progression");
  } else {
    if (page.nextOffset.startsWith("snap.") || page.nextOffset !== covered || BigInt(covered) < BigInt(page.offset) ||
        (!page.upToDate && covered === page.offset)) malformed("shape log coverage");
    let previous = BigInt(page.offset);
    for (const change of page.changes) {
      const offset = BigInt(decimal(change.offset));
      if (offset <= previous || offset > BigInt(covered)) malformed("shape commit order");
      previous = offset;
    }
  }
  for (const change of page.changes) {
    const ids = new Set();
    for (const op of change.ops) {
      if (ids.has(op.id)) malformed("duplicate shape document");
      ids.add(op.id);
    }
  }
  return page;
}
/**
 * Follows exactly one authorized same-route 307; credentials never follow an arbitrary Location.
 * @param {V1Transport} transport @param {Readonly<Record<string,string>>} params
 * @param {V1ShapeRequest} request @param {AbortSignal | undefined} signal
 * @returns {Promise<V1ShapePage>}
 */
export async function readShapePage(transport, params, request, signal) {
  const isResume = (request.offset ?? "-1") === "-1" && request.cursor !== undefined;
  if (isResume && (request.handle !== undefined || request.live !== undefined)) throw new TypeError("Shape resume requires offset=-1 without handle or live.");
  const query = encodeShapeRequest(isResume ? { ...request, cursor: undefined } : request);
  if (query.get("offset") === "-1" && query.has("handle")) throw new TypeError("Shape bootstrap must omit handle.");
  /** @type {Record<string,string>} */
  const headers = {};
  const source = new URL(await transport.url("readFreeShape", params, signal));
  if (isResume) {
    query.set("cursor", resumeToken(request.cursor));
    // The producer reserves 64 bytes for request-line framing in its 8 KiB ceiling.
    if (new TextEncoder().encode(`${source.pathname}?${query}`).length > 8192 - 64) {
      headers["TB-Shape-Resume"] = resumeToken(request.cursor);
      query.delete("cursor");
    }
  }
  let response = await transport.call("readFreeShape", params, { query, headers, signal });
  if (response.status === 307) {
    if (query.get("offset") !== "-1") malformed("unexpected shape redirect");
    const location = response.headers.get("location");
    if (location === null) malformed("shape redirect Location");
    const target = new URL(location, source);
    if (target.origin !== source.origin || target.pathname !== source.pathname || target.username !== "" || target.password !== "" || target.hash !== "") malformed("shape redirect target");
    for (const key of target.searchParams.keys()) {
      if (!["where", "handle", "offset"].includes(key) || target.searchParams.getAll(key).length !== 1) malformed("shape redirect query");
    }
    const handle = requireShapeHandle(target.searchParams.get("handle"));
    const offset = requireShapeOffset(target.searchParams.get("offset"));
    if (offset === "-1" || (!isResume && !offset.startsWith("snap.")) || (isResume && offset.startsWith("snap."))) malformed("shape redirect offset");
    if (target.searchParams.has("where") !== query.has("where")) malformed("shape redirect filter");
    // Keep the caller's validated filter: the server's canonical spelling must not broaden it.
    query.delete("cursor");
    query.set("handle", handle);
    query.set("offset", offset);
    response = await transport.call("readFreeShape", params, { query, signal });
    if (response.status !== 200) malformed("repeated shape redirect");
  } else if (query.get("offset") === "-1") malformed("shape bootstrap requires redirect");
  const page = decodeFreeShapePage(response.value, response.headers);
  if (page.handle !== query.get("handle") || page.offset !== query.get("offset")) malformed("shape response position");
  const inputCursor = query.get("cursor");
  if (request.live === true && (page.cursor === undefined || (inputCursor !== null && BigInt(page.cursor) <= BigInt(inputCursor)))) malformed("shape live cursor advance");
  return page;
}
/**
 * Fetch a key for exactly this authorized handle/epoch/KID, with no browser or HTTP cache reuse.
 * @param {V1Transport} transport @param {Readonly<Record<string,string>>} params
 * @param {Uint8Array} bytes @param {V1SealedShapePosition} position @param {AbortSignal | undefined} signal
 * @returns {Promise<V1ShapePage>}
 */
export async function decryptSealedShapePage(transport, params, bytes, position, signal) {
  const handle = requireShapeHandle(position.handle);
  const offset = requireShapeOffset(position.offset);
  if (offset === "-1") throw new TypeError("Sealed shapes require a snapshot or log offset.");
  if (typeof position.epoch !== "string" || !/^(?:0|[1-9][0-9]*)$/u.test(position.epoch) || BigInt(position.epoch) > (1n << 64n) - 1n) throw new TypeError("Shape epoch must be a canonical uint64.");
  if (typeof position.keyId !== "string" || !/^[0-7][0-9A-HJKMNP-TV-Z]{25}$/u.test(position.keyId)) throw new TypeError("Shape key id must be a canonical ULID.");
  const epoch = position.epoch;
  const keyId = position.keyId;
  const query = new URLSearchParams({ handle, epoch, key_id: keyId });
  const response = await transport.call("getShapeKey", params, { query, cache: "no-store", signal });
  const key = object(response.value, "shape epoch key");
  if (key.handle !== handle || key.epoch !== epoch || key.key_id !== keyId) malformed("shape epoch key binding");
  const encoded = string(key.key, "shape epoch key");
  if (!/^[A-Za-z0-9_-]{43}$/u.test(encoded)) malformed("shape epoch key encoding");
  const raw = Uint8Array.from(atob(encoded.replace(/-/gu, "+").replace(/_/gu, "/")), c => c.charCodeAt(0));
  if (btoa(String.fromCharCode(...raw)).replace(/\+/gu, "-").replace(/\//gu, "_").replace(/=+$/u, "") !== encoded) malformed("shape epoch key encoding");
  const plaintext = await decryptShapeObject(bytes, { handle, offset, epoch, keyId, key: raw });
  signal?.throwIfAborted();
  const page = decodeFreeShapePage(parseExactJson(new TextDecoder("utf-8", { fatal: true, ignoreBOM: true }).decode(plaintext)), new Headers());
  if (page.handle !== handle || page.offset !== offset || page.upToDate) malformed("sealed shape position");
  return page;
}
