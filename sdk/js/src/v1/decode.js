// Typed decoding of TerraBase v1 response bodies.
import { isJsonObject } from "./json.js";
/** @import { JsonObject, JsonValue } from "./json.js" */

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
