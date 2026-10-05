// Query v1 filter grammar builders (query-v1.md). Filters are plain wire
// objects, shared by queries, transaction asserts and shapes.
import { ExactDecimal } from "./json.js";

/**
 * A filter or sort path: an RFC 6901 JSON Pointer or "$id".
 * @typedef {string} V1Path
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
  return isJsonPointer(pointer) && pointer.split("/").length - 1 <= 16;
}

/**
 * An RFC 6901 JSON Pointer of at most 4 KiB with at least one segment: `~`
 * only as the escapes `~0` and `~1`.
 * @param {unknown} pointer
 * @returns {pointer is string}
 */
export function isJsonPointer(pointer) {
  return typeof pointer === "string" && pointer.length <= 4096 && pointer.split("/").length - 1 <= 16 && !/[\uD800-\uDFFF]/u.test(pointer) && /^(?:\/(?:[^~/]|~[01])*)+$/u.test(pointer);
}

/**
 * @param {V1Path} path
 * @returns {V1Path}
 */
export function requirePath(path) {
  if (path === "$id" || isPointer(path)) return path;
  throw new TypeError('A TerraBase path is an RFC 6901 JSON Pointer of 1-16 segments, "$id".');
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

const binaryOperators = new Set(["eq", "ne", "lt", "lte", "gt", "gte", "contains"]);

/**
 * Validates a filter against the query-v1 grammar (operators, arity, paths,
 * scalar operands, depth 8 and 256 nodes), so a malformed filter fails locally
 * instead of as a server `invalid_query`. The v1Filter builders always pass.
 * @param {unknown} filter
 * @returns {V1Filter}
 */
export function requireFilter(filter) {
  let nodes = 0;
  /**
   * @param {unknown} node
   * @param {number} depth
   * @returns {void}
   */
  const visit = (node, depth) => {
    if (++nodes > 256) throw new TypeError("A filter has at most 256 nodes.");
    if (depth > 8) throw new TypeError("A filter nests at most 8 levels.");
    if (typeof node !== "object" || node === null || Array.isArray(node) || Object.keys(node).length !== 1)
      throw new TypeError("A filter node is an object with exactly one operator.");
    const [op, args] = /** @type {[string, unknown]} */ (Object.entries(node)[0]);
    if (op === "and" || op === "or") {
      if (!Array.isArray(args) || args.length < 1 || args.length > 64) throw new TypeError(`${op} takes 1-64 filters.`);
      for (const child of args) visit(child, depth + 1);
    } else if (op === "not") visit(args, depth + 1);
    else if (op === "exists") requirePath(/** @type {V1Path} */ (args));
    else if (binaryOperators.has(op) || op === "prefix" || op === "in" || op === "nin") {
      if (!Array.isArray(args) || args.length !== 2) throw new TypeError(`${op} takes [path, value].`);
      requirePath(args[0]);
      if (op === "in" || op === "nin") scalars(op, args[1]);
      else if (op === "prefix") {
        if (typeof args[1] !== "string") throw new TypeError("prefix takes a string.");
      } else {
        requireScalar(args[1]);
        if (op !== "eq" && op !== "ne" && op !== "contains" && args[1] === null) throw new TypeError(`${op} does not compare null.`);
      }
    } else throw new TypeError(`Unknown filter operator ${op}.`);
  };
  visit(filter, 1);
  return /** @type {V1Filter} */ (filter);
}

/**
 * Query v1 filter builders. `v1Filter.id` is the `"$id"` pseudo-path.
 */
export const v1Filter = Object.freeze({
  /** The document ID pseudo-path. */
  id: /** @type {"$id"} */ ("$id"),
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
