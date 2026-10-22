import { array, malformed, object, string, boolean } from "./decode.js";
import { ExactDecimal } from "./json.js";
/** @import { JsonValue } from "./json.js" */
/** @template T @typedef {(value: JsonValue | undefined) => T} Decoder */
export const text = /** @type {Decoder<string>} */ (value => string(value, "string"));
export const flag = /** @type {Decoder<boolean>} */ (value => boolean(value, "boolean"));
/** @type {Decoder<number | ExactDecimal>} */
export const numeric = value => typeof value === "number" && Number.isFinite(value) || value instanceof ExactDecimal ? value : malformed("number");
/** @template T @param {Decoder<T>} decode @returns {Decoder<T | null>} */
export const nullable = decode => value => value === null ? null : decode(value);
/** @template T @param {Decoder<T>} decode @returns {Decoder<T | undefined>} */
export const optional = decode => value => value === undefined ? undefined : decode(value);
/** @template T @param {Decoder<T>} decode @returns {Decoder<readonly T[]>} */
export const list = decode => value => Object.freeze(array(value, "array").map(decode));
/** @template T @param {{ [K in keyof T]: Decoder<T[K]> }} fields @returns {Decoder<Readonly<T>>} */
export function record(fields) {
  return value => {
    const raw = object(value, "management response");
    return Object.freeze(/** @type {T} */ (Object.fromEntries(Object.entries(fields).map(([key, decode]) =>
      [key, /** @type {Decoder<unknown>} */ (decode)(raw[key])]))));
  };
}
/** @template T @param {Decoder<T>} decode @returns {Decoder<Readonly<Record<string, T>>>} */
export const dictionary = decode => value => Object.freeze(Object.fromEntries(Object.entries(object(value, "dictionary")).map(([key, item]) => [key, decode(item)])));
