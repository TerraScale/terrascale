// Typed decoding of TerraBase v1 response bodies.
import { parseU64 } from "./ids.js";
import { isJsonObject } from "./json.js";
/** @import { JsonObject, JsonValue } from "./json.js" */

/** @typedef {"local_durable" | "object_durable"} V1Durability */

/**
 * @typedef {object} V1SiblingEvent
 * @property {string} replica Lowercase hyphenated UUID.
 * @property {string} incarnation 32 lowercase hex characters.
 * @property {string} sequence Decimal u64.
 */

/**
 * One concurrent version of a conflicted document.
 * @typedef {object} V1Sibling
 * @property {V1SiblingEvent} event
 * @property {boolean} deleted
 * @property {JsonObject | undefined} document Absent for deleted siblings.
 */

/**
 * A document from GET, a query or a shape. `state:"value"` carries
 * `document`; `state:"conflict"` carries every sibling.
 * @typedef {{ readonly id: string; readonly version: string; readonly state: "value"; readonly document: JsonObject }
 *   | { readonly id: string; readonly version: string; readonly state: "conflict"; readonly siblings: readonly V1Sibling[] }} V1Document
 */

/**
 * The result of a document write.
 * @typedef {object} V1WriteResult
 * @property {string} id
 * @property {string} version For a delete, the tombstone's version.
 * @property {string} offset The database commit offset (decimal u64), usable with shape `waitFor`.
 * @property {V1Durability} durability
 * @property {boolean} created True when a PUT or POST answered 201.
 * @property {string | undefined} location The Location header of a POST create.
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
  return value === "local_durable" || value === "object_durable" ? value : malformed("durability");
}

/**
 * @param {JsonValue | undefined} value
 * @returns {V1Sibling}
 */
function sibling(value) {
  const raw = object(value, "sibling");
  const event = object(raw.event, "sibling event");
  return Object.freeze({
    event: Object.freeze({
      replica: string(event.replica, "sibling replica"),
      incarnation: string(event.incarnation, "sibling incarnation"),
      sequence: u64(event.sequence, "sibling sequence"),
    }),
    deleted: boolean(raw.deleted, "sibling deleted flag"),
    document: raw.document === undefined ? undefined : object(raw.document, "sibling document"),
  });
}

/**
 * @param {JsonValue | undefined} value
 * @returns {V1Document}
 */
export function decodeDocument(value) {
  const raw = object(value, "document");
  const id = string(raw.id, "document id");
  const version = string(raw.version, "document version");
  if (raw.state === "conflict")
    return Object.freeze({ id, version, state: "conflict", siblings: Object.freeze(array(raw.siblings, "siblings").map(sibling)) });
  if (raw.state !== "value") return malformed("document state");
  return Object.freeze({ id, version, state: "value", document: object(raw.document, "document body") });
}

/**
 * @param {{ status: number; headers: Headers; value: JsonValue | undefined }} response
 * @returns {V1WriteResult}
 */
export function decodeWrite(response) {
  const raw = object(response.value, "write result");
  return Object.freeze({
    id: string(raw.id, "write id"),
    version: string(raw.version, "write version"),
    offset: u64(raw.offset, "write offset"),
    durability: durability(raw.durability),
    created: response.status === 201,
    location: response.headers.get("location") ?? undefined,
  });
}
