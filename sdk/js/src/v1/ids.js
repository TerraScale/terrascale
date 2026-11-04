// Identifiers for the TerraBase v1 API.
import { isValidId, requireId } from "../ids.js";

const namePattern = /^[a-z][a-z0-9_-]{0,62}$/u;

/**
 * @param {"database"} kind
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

/**
 * The catalog project ID is a canonical ULID; database selectors accept IDs or names.
 * @param {unknown} project
 * @returns {import("../ids.js").EntityId}
 */
export function requireProject(project) {
  return requireId(project);
}
