// Typed decoding of TerraBase v1 response bodies.
import { parseU64, requireDocumentId, requireStoredId, requireVersion } from "./ids.js";
import { isJsonObject } from "./json.js";
/** @import { JsonObject, JsonValue } from "./json.js" */

/** Unknown future labels pass through. @typedef {"local_durable" | (string & {})} V1Durability */
/**
 * `document` is the value, or the LWW winner when a causal read answers `"conflict"` with `siblings`.
 * Unknown future states pass through.
 * @typedef {object} V1Document
 * @property {string} id Caller-supplied key or server-generated ULID.
 * @property {string} version
 * @property {"value" | "conflict" | (string & {})} state
 * @property {JsonObject} document
 * @property {readonly JsonValue[]} [siblings] Live versions and tombstones on a causal conflict.
 * @property {number} [distance] Distance to the query vector, or meters to a geo `near` point; present only on `near` query results.
 * @property {number} [score] Full-text relevance score on results sorted by `$score`.
 */

/**
 * The result of a document write.
 * @typedef {object} V1WriteResult
 * @property {string} id Caller-supplied key or server-generated ULID.
 * @property {string} version For a delete, the tombstone's version.
 * @property {string} offset The database commit offset (decimal u64), usable with shape `waitFor`.
 * @property {V1Durability} durability
 * @property {boolean} created True when a PUT or POST answered 201.
 * @property {string | undefined} location The Location header of a POST create.
 * @property {JsonObject} [document] Stored document returned by PATCH.
 */

/**
 * @param {string} what
 * @returns {never}
 */
export function malformed(what) {
  throw new TypeError(`TerraBase returned a malformed ${what}.`);
}

/**
 * @param {JsonValue | undefined} value
 * @param {string} what
 * @returns {JsonObject}
 */
export function object(value, what) {
  return isJsonObject(value) ? value : malformed(what);
}

/** @param {JsonValue | undefined} value @returns {JsonObject} */
export function documentObject(value) {
  return object(value, "document body");
}

/**
 * @param {JsonValue | undefined} value
 * @param {string} what
 * @returns {string}
 */
export function string(value, what) {
  return typeof value === "string" ? value : malformed(what);
}

/**
 * @param {JsonValue | undefined} value
 * @param {string} what
 * @returns {string}
 */
export function u64(value, what) {
  return parseU64(value) === undefined ? malformed(what) : /** @type {string} */ (value);
}

/**
 * @param {JsonValue | undefined} value
 * @param {string} what
 * @returns {boolean}
 */
export function boolean(value, what) {
  return typeof value === "boolean" ? value : malformed(what);
}

/**
 * @param {JsonValue | undefined} value
 * @param {string} what
 * @returns {JsonValue[]}
 */
export function array(value, what) {
  return Array.isArray(value) ? value : malformed(what);
}

/**
 * @param {JsonValue | undefined} value
 * @returns {V1Durability}
 */
export function durability(value) {
  return string(value, "durability");
}

/**
 * @param {JsonValue | undefined} value
 * @returns {V1Document}
 */
export function decodeDocument(value) {
  const raw = object(value, "document");
  const id = requireStoredId(raw.id);
  const version = requireVersion(raw.version);
  const state = string(raw.state, "document state");
  const document = documentObject(raw.document);
  if (raw.siblings === undefined) return Object.freeze({ id, version, state, document });
  const siblings = array(raw.siblings, "document siblings");
  for (const sibling of siblings) {
    const entry = object(sibling, "document sibling");
    if (entry.deleted !== true || entry.document !== undefined) documentObject(entry.document);
  }
  return Object.freeze({ id, version, state, document, siblings: Object.freeze(siblings) });
}

/**
 * @param {{ status: number; headers: Headers; value: JsonValue | undefined }} response
 * @returns {V1WriteResult}
 */
export function decodeWrite(response) {
  const raw = object(response.value, "write result");
  return Object.freeze({
    id: requireDocumentId(raw.id),
    version: requireVersion(raw.version),
    offset: u64(raw.offset, "write offset"),
    durability: durability(raw.durability),
    created: response.status === 201,
    location: response.headers.get("location") ?? undefined,
    ...(raw.document === undefined ? {} : { document: documentObject(raw.document) }),
  });
}
