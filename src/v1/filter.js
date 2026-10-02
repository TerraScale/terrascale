// Query v1 filter grammar builders (query-v1.md). Filters are plain wire
// objects, shared by queries, transaction asserts and shapes.
import { ExactDecimal } from "./json.js";

/**
 * A filter or sort path: an RFC 6901 JSON Pointer, `"$id"`, or
 * `{ lower: pointer }` for Unicode case-folded comparison.
 * @typedef {string | { readonly lower: string }} V1Path
 */
/**
 * A filter operand: string, exact number, boolean or null.
 * @typedef {null | boolean | string | number | bigint | ExactDecimal} V1Scalar
 */
/**
 * @typedef {{ readonly and: readonly V1Filter[] }
 *   | { readonly or: readonly V1Filter[] }
 *   | { readonly not: V1Filter }
 *   | { readonly eq: readonly [V1Path, V1Scalar] }
 *   | { readonly ne: readonly [V1Path, V1Scalar] }
 *   | { readonly lt: readonly [V1Path, V1Scalar] }
 *   | { readonly lte: readonly [V1Path, V1Scalar] }
 *   | { readonly gt: readonly [V1Path, V1Scalar] }
 *   | { readonly gte: readonly [V1Path, V1Scalar] }
 *   | { readonly in: readonly [V1Path, readonly V1Scalar[]] }
 *   | { readonly nin: readonly [V1Path, readonly V1Scalar[]] }
 *   | { readonly exists: V1Path }
 *   | { readonly prefix: readonly [V1Path, string] }
 *   | { readonly contains: readonly [V1Path, V1Scalar] }} V1Filter
 */

/**
 * @param {unknown} pointer
 * @returns {pointer is string}
 */
function isPointer(pointer) {
  return typeof pointer === "string" && pointer.startsWith("/") && pointer.length <= 4096;
}

/**
 * @param {V1Path} path
 * @returns {V1Path}
 */
export function requirePath(path) {
  if (path === "$id" || isPointer(path)) return path;
  if (typeof path === "object" && path !== null && isPointer(path.lower) && Object.keys(path).length === 1)
    return Object.freeze({ lower: path.lower });
  throw new TypeError('A TerraBase path is a JSON Pointer starting with "/", "$id", or { lower: pointer }.');
}

/**
 * @param {V1Scalar} value
 * @returns {V1Scalar}
 */
function requireScalar(value) {
  if (
    value === null ||
    typeof value === "boolean" ||
    typeof value === "string" ||
    typeof value === "bigint" ||
    value instanceof ExactDecimal ||
    (typeof value === "number" && Number.isFinite(value))
  )
    return value;
  throw new TypeError("Filter values are JSON scalars: string, finite number, bigint, ExactDecimal, boolean or null.");
}

/**
 * @param {"and" | "or"} op
 * @param {readonly V1Filter[]} filters
 * @returns {readonly V1Filter[]}
 */
function children(op, filters) {
  if (filters.length < 1 || filters.length > 64) throw new TypeError(`${op} takes 1-64 filters.`);
  return Object.freeze([...filters]);
}

/**
 * @param {"in" | "nin"} op
 * @param {readonly V1Scalar[]} values
 * @returns {readonly V1Scalar[]}
 */
function scalars(op, values) {
  if (!Array.isArray(values) || values.length < 1 || values.length > 256)
    throw new TypeError(`${op} takes 1-256 values.`);
  return Object.freeze(values.map(requireScalar));
}

/**
 * Query v1 filter builders. `v1Filter.id` is the `"$id"` pseudo-path.
 */
export const v1Filter = Object.freeze({
  /** The document ID pseudo-path. */
  id: /** @type {"$id"} */ ("$id"),
  /**
   * Compares the path's string values after Unicode default case folding.
   * @param {string} pointer
   * @returns {{ readonly lower: string }}
   */
  lower: (pointer) => /** @type {{ readonly lower: string }} */ (requirePath({ lower: pointer })),
  /**
   * @param {...V1Filter} filters
   * @returns {V1Filter}
   */
  and: (...filters) => Object.freeze({ and: children("and", filters) }),
  /**
   * @param {...V1Filter} filters
   * @returns {V1Filter}
   */
  or: (...filters) => Object.freeze({ or: children("or", filters) }),
  /**
   * @param {V1Filter} filter
   * @returns {V1Filter}
   */
  not: (filter) => Object.freeze({ not: filter }),
  /**
   * @param {V1Path} path
   * @param {V1Scalar} value
   * @returns {V1Filter}
   */
  eq: (path, value) => Object.freeze({ eq: Object.freeze(/** @type {const} */ ([requirePath(path), requireScalar(value)])) }),
  /**
   * Not equal; also matches a missing member.
   * @param {V1Path} path
   * @param {V1Scalar} value
   * @returns {V1Filter}
   */
  ne: (path, value) => Object.freeze({ ne: Object.freeze(/** @type {const} */ ([requirePath(path), requireScalar(value)])) }),
  /**
   * @param {V1Path} path
   * @param {V1Scalar} value
   * @returns {V1Filter}
   */
  lt: (path, value) => Object.freeze({ lt: Object.freeze(/** @type {const} */ ([requirePath(path), requireScalar(value)])) }),
  /**
   * @param {V1Path} path
   * @param {V1Scalar} value
   * @returns {V1Filter}
   */
  lte: (path, value) => Object.freeze({ lte: Object.freeze(/** @type {const} */ ([requirePath(path), requireScalar(value)])) }),
  /**
   * @param {V1Path} path
   * @param {V1Scalar} value
   * @returns {V1Filter}
   */
  gt: (path, value) => Object.freeze({ gt: Object.freeze(/** @type {const} */ ([requirePath(path), requireScalar(value)])) }),
  /**
   * @param {V1Path} path
   * @param {V1Scalar} value
   * @returns {V1Filter}
   */
  gte: (path, value) => Object.freeze({ gte: Object.freeze(/** @type {const} */ ([requirePath(path), requireScalar(value)])) }),
  /**
   * @param {V1Path} path
   * @param {readonly V1Scalar[]} values 1-256 values.
   * @returns {V1Filter}
   */
  in: (path, values) => Object.freeze({ in: Object.freeze(/** @type {const} */ ([requirePath(path), scalars("in", values)])) }),
  /**
   * @param {V1Path} path
   * @param {readonly V1Scalar[]} values 1-256 values.
   * @returns {V1Filter}
   */
  nin: (path, values) => Object.freeze({ nin: Object.freeze(/** @type {const} */ ([requirePath(path), scalars("nin", values)])) }),
  /**
   * The member is present (including null).
   * @param {V1Path} path
   * @returns {V1Filter}
   */
  exists: (path) => Object.freeze({ exists: requirePath(path) }),
  /**
   * @param {V1Path} path
   * @param {string} prefix
   * @returns {V1Filter}
   */
  prefix: (path, prefix) => {
    if (typeof prefix !== "string") throw new TypeError("prefix takes a string.");
    return Object.freeze({ prefix: Object.freeze(/** @type {const} */ ([requirePath(path), prefix])) });
  },
  /**
   * The value is an array containing the scalar.
   * @param {V1Path} path
   * @param {V1Scalar} value
   * @returns {V1Filter}
   */
  contains: (path, value) => Object.freeze({ contains: Object.freeze(/** @type {const} */ ([requirePath(path), requireScalar(value)])) }),
});
