// The proposed free-read v1 protocol is separate from the historical shape() contract.
import { requireId } from "../ids.js";
import { malformed, object, string } from "./decode.js";
import { decodeShapePage, encodeShapeRequest, requireShapeHandle, requireShapeOffset } from "./shapes.js";
import { decryptShapeObject } from "./shape-crypto.js";
import { parseExactJson } from "./json.js";
import { TerraBaseV1Error } from "./errors.js";
import { OpaqueRedirectError } from "./transport.js";
/** @import { V1Transport } from "./transport.js" */
/** @import { V1ShapeRequest, V1ShapePage } from "./shapes.js" */
/** @typedef {{handle: string; offset: string; epoch: string; keyId: string}} V1SealedShapePosition */
/** @typedef {{ latestEpoch: bigint; keys: Map<string, { epoch: bigint; key: Uint8Array }> }} ShapeKeys */
/** @type {WeakMap<V1Transport, Map<string, ShapeKeys>>} Only immutable credentials retain keys across decrypts. */
const shapeKeys = new WeakMap();
/** @param {Readonly<Record<string, string>>} params */
const keyScope = params => JSON.stringify([params.database, params.collection]);
/**
 * A refetch invalidates both the materialized state and any epoch keys for that collection.
 * @param {V1Transport} transport @param {Readonly<Record<string, string>>} params
 * @param {"readFreeShape" | "getShapeKey"} operation @param {import("./transport.js").V1CallOptions} options
 */
async function shapeCall(transport, params, operation, options) {
  try { return await transport.call(operation, params, options); }
  catch (error) {
    if (error instanceof TerraBaseV1Error && error.status === 409 && error.code === "must_refetch")
      shapeKeys.get(transport)?.delete(keyScope(params));
    throw error;
  }
}
/** @param {V1Transport} transport @param {Readonly<Record<string, string>>} params @returns {ShapeKeys | undefined} */
function currentKeys(transport, params) {
  // Callback tokens can change users; authorize every decrypt without retaining their keys.
  if (transport.hasTokenProvider) return undefined;
  let scopes = shapeKeys.get(transport);
  if (scopes === undefined) { scopes = new Map(); shapeKeys.set(transport, scopes); }
  const scope = keyScope(params);
  let cache = scopes.get(scope);
  if (cache === undefined) { cache = { latestEpoch: 0n, keys: new Map() }; scopes.set(scope, cache); }
  // Expiry is lazy; a higher served epoch also protects against a regressed client clock.
  const epoch = BigInt(Math.floor(Date.now() / 600_000));
  if (epoch > cache.latestEpoch) cache.latestEpoch = epoch;
  for (const [id, entry] of cache.keys) if (entry.epoch < cache.latestEpoch) cache.keys.delete(id);
  return cache;
}
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
    let previousId;
    for (const op of change.ops) {
      if (previousId !== undefined && op.id <= previousId) malformed("shape document order");
      previousId = op.id;
    }
  }
  return page;
}
/**
 * Follows exactly one authorized same-route 307; credentials never follow an arbitrary Location.
 * @param {V1Transport} transport @param {Readonly<Record<string,string>>} params
 * @param {V1ShapeRequest} request @param {AbortSignal | undefined} signal
 * @param {string | undefined} [ifNoneMatch] A complete entity-tag list or *; 304 throws not_modified with response headers.
 * @returns {Promise<V1ShapePage>}
 */
export async function readShapePage(transport, params, request, signal, ifNoneMatch) {
  const isResume = (request.offset ?? "-1") === "-1" && request.cursor !== undefined;
  if (isResume && (request.handle !== undefined || request.live !== undefined)) throw new TypeError("Shape resume requires offset=-1 without handle or live.");
  const query = encodeShapeRequest(isResume ? { ...request, cursor: undefined } : request);
  // Canonical filter size is checked by the producer; check our exact wire spelling here.
  if (new TextEncoder().encode(query.get("where") ?? "").byteLength > 8192) throw new TypeError("Shape filters are limited to 8 KiB of UTF-8.");
  if (query.get("offset") === "-1" && query.has("handle")) throw new TypeError("Shape bootstrap must omit handle.");
  /** @type {Record<string,string>} */
  const headers = {};
  if (ifNoneMatch !== undefined) {
    if (typeof ifNoneMatch !== "string" || !/^[ \t]*(?:\*|(?:W\/)?"[\x21\x23-\x7e\x80-\xff]*"(?:[ \t]*,[ \t]*(?:W\/)?"[\x21\x23-\x7e\x80-\xff]*")*)[ \t]*$/u.test(ifNoneMatch))
      throw new TypeError("ifNoneMatch must be * or a list of quoted entity tags.");
    headers["If-None-Match"] = ifNoneMatch;
  }
  const source = new URL(await transport.url("readFreeShape", params, signal));
  if (isResume) {
    query.set("cursor", resumeToken(request.cursor));
    // The producer reserves 64 bytes for request-line framing in its 8 KiB ceiling.
    if (new TextEncoder().encode(`${source.pathname}?${query}`).length > 8192 - 64) {
      headers["TB-Shape-Resume"] = resumeToken(request.cursor);
      query.delete("cursor");
    }
  }
  const bootstrap = query.get("offset") === "-1";
  /** @param {string} location */
  const redirectTarget = location => {
    const target = new URL(location, source);
    if (target.origin !== source.origin || target.pathname !== source.pathname || target.username !== "" || target.password !== "" || target.hash !== "") malformed("shape redirect target");
    for (const key of target.searchParams.keys()) {
      if (!["where", "handle", "offset"].includes(key) || target.searchParams.getAll(key).length !== 1) malformed("shape redirect query");
    }
    const handle = requireShapeHandle(target.searchParams.get("handle"));
    const offset = requireShapeOffset(target.searchParams.get("offset"));
    if (offset === "-1" || (!isResume && !/^snap\.[0-9]+\.0$/u.test(offset)) || (isResume && offset.startsWith("snap."))) malformed("shape redirect offset");
    if (target.searchParams.has("where") !== query.has("where")) malformed("shape redirect filter");
    // Keep the caller's validated filter: the server's canonical spelling must not broaden it.
    query.delete("cursor");
    delete headers["TB-Shape-Resume"];
    query.set("handle", handle);
    query.set("offset", offset);
  };
  let response;
  try { response = await shapeCall(transport, params, "readFreeShape", { query, headers, signal }); }
  catch (error) {
    if (!(error instanceof OpaqueRedirectError) || !bootstrap) throw error;
    // Browsers hide a manual 307; follow it natively, then hold the final URL to the Location rules.
    response = await shapeCall(transport, params, "readFreeShape", { query: new URLSearchParams(query), headers: { ...headers }, signal, redirect: "follow" });
    if (response.redirectedTo === undefined || response.status !== 200) malformed("shape bootstrap requires redirect");
    redirectTarget(response.redirectedTo);
  }
  if (response.status === 307) {
    if (!bootstrap) malformed("unexpected shape redirect");
    const location = response.headers.get("location");
    if (location === null) malformed("shape redirect Location");
    redirectTarget(location);
    response = await shapeCall(transport, params, "readFreeShape", { query, headers, signal });
    if (response.status !== 200) malformed("repeated shape redirect");
  } else if (query.get("offset") === "-1") malformed("shape bootstrap requires redirect");
  const page = decodeFreeShapePage(response.value, response.headers);
  if (page.handle !== query.get("handle") || page.offset !== query.get("offset")) malformed("shape response position");
  const inputCursor = query.get("cursor");
  if (request.live === true && (page.cursor === undefined || (inputCursor !== null && BigInt(page.cursor) <= BigInt(inputCursor)))) malformed("shape live cursor advance");
  return page;
}
/**
 * Reuse keys only for static credentials; callback tokens authorize every decrypt with no-store.
 * @param {V1Transport} transport @param {Readonly<Record<string,string>>} params
 * @param {Uint8Array} bytes @param {V1SealedShapePosition} position @param {AbortSignal | undefined} signal
 * @returns {Promise<V1ShapePage>}
 */
export async function decryptSealedShapePage(transport, params, bytes, position, signal) {
  const handle = requireShapeHandle(position.handle);
  const offset = requireShapeOffset(position.offset);
  if (offset === "-1") throw new TypeError("Sealed shapes require a snapshot or log offset.");
  if (typeof position.epoch !== "string" || !/^(?:0|[1-9][0-9]*)$/u.test(position.epoch) || BigInt(position.epoch) > (1n << 64n) - 1n) throw new TypeError("Shape epoch must be a canonical uint64.");
  const epoch = position.epoch;
  const keyId = requireId(position.keyId, "Shape key ID");
  signal?.throwIfAborted();
  const cache = currentKeys(transport, params);
  const cacheId = JSON.stringify([handle, epoch, keyId]);
  let raw = cache?.keys.get(cacheId)?.key;
  if (raw === undefined) {
    const query = new URLSearchParams({ handle, epoch, key_id: keyId });
    const response = await shapeCall(transport, params, "getShapeKey", { query, cache: "no-store", signal });
    const key = object(response.value, "shape epoch key");
    if (key.handle !== handle || key.epoch !== epoch || key.key_id !== keyId) malformed("shape epoch key binding");
    const encoded = string(key.key, "shape epoch key");
    if (!/^[A-Za-z0-9_-]{43}$/u.test(encoded)) malformed("shape epoch key encoding");
    raw = Uint8Array.from(atob(encoded.replace(/-/gu, "+").replace(/_/gu, "/")), c => c.charCodeAt(0));
    if (btoa(String.fromCharCode(...raw)).replace(/\+/gu, "-").replace(/\//gu, "_").replace(/=+$/u, "") !== encoded) malformed("shape epoch key encoding");
    const servedEpoch = BigInt(epoch);
    const active = currentKeys(transport, params);
    if (cache !== undefined && active === cache && servedEpoch >= cache.latestEpoch) {
      cache.latestEpoch = servedEpoch;
      for (const [id, entry] of cache.keys) if (entry.epoch < servedEpoch) cache.keys.delete(id);
      cache.keys.set(cacheId, { epoch: servedEpoch, key: raw });
    }
  }
  const plaintext = await decryptShapeObject(bytes, { handle, offset, epoch, keyId, key: raw });
  signal?.throwIfAborted();
  const page = decodeFreeShapePage(parseExactJson(new TextDecoder("utf-8", { fatal: true, ignoreBOM: true }).decode(plaintext)), new Headers());
  if (page.handle !== handle || page.offset !== offset || page.upToDate) malformed("sealed shape position");
  return page;
}

/** @typedef {{readonly handle: string; readonly epoch: string; readonly keyId: string}} V1ShapeKeyRequest */
/** @typedef {V1ShapeKeyRequest & {readonly key: Uint8Array}} V1ShapeKey */
/** @param {V1Transport} transport @param {Readonly<Record<string, string>>} params @param {V1ShapeKeyRequest} request @param {AbortSignal | undefined} signal @returns {Promise<V1ShapeKey>} */
export async function readShapeKey(transport, params, request, signal) {
  const handle = requireShapeHandle(request.handle);
  const epoch = decimal(request.epoch);
  const keyId = requireId(request.keyId);
  const response = await shapeCall(transport, params, "getShapeKey", {
    query: new URLSearchParams({ handle, epoch, key_id: keyId }), cache: "no-store", signal,
  });
  const key = object(response.value, "shape epoch key");
  if (key.handle !== handle || key.epoch !== epoch || key.key_id !== keyId) malformed("shape epoch key binding");
  const encoded = string(key.key, "shape key");
  if (!/^[A-Za-z0-9_-]{43}$/u.test(encoded)) malformed("shape key encoding");
  const bytes = Uint8Array.from(atob(encoded.replace(/-/gu, "+").replace(/_/gu, "/")), c => c.charCodeAt(0));
  if (btoa(String.fromCharCode(...bytes)).replace(/\+/gu, "-").replace(/\//gu, "_").replace(/=+$/u, "") !== encoded) malformed("shape key encoding");
  return Object.freeze({ handle, epoch, keyId, key: bytes });
}
