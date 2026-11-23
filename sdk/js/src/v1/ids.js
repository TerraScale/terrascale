import { isValidId, requireId } from "../ids.js";

const namePattern = /^[a-z][a-z0-9_-]{0,62}$/u;

/**
 * @param {"database" | "function"} kind
 * @param {unknown} name
 * @returns {string}
 */
export function requireName(kind, name) {
  if (typeof name !== "string" || !namePattern.test(name))
    throw new TypeError(`TerraBase ${kind} names must match ^[a-z][a-z0-9_-]{0,62}$.`);
  return name;
}

/** A database route selector is an immutable ULID or current name.
 * @param {unknown} selector @returns {string}
 */
export function requireDatabaseSelector(selector) {
  return isValidId(selector) ? requireId(selector) : requireName("database", selector);
}
