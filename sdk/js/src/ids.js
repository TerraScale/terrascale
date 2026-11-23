import { isValid, monotonicFactory } from "ulid";

/** A plain 26-character, uppercase Crockford ULID. @typedef {string} EntityId */

const nextId = monotonicFactory();

/** @returns {EntityId} Monotonic within a millisecond, including clock rollback. */
export function generateId() {
  return nextId();
}

/** @param {unknown} value @returns {value is EntityId} */
export function isValidId(value) {
  return typeof value === "string" && isValid(value) && value === value.toUpperCase() && /^[0-7]/u.test(value);
}

/** @param {unknown} value @param {string} [name] @returns {EntityId} */
export function requireId(value, name = "Entity ID") {
  if (!isValidId(value)) throw new TypeError(`${name} must be a plain 26-character canonical uppercase Crockford ULID.`);
  return value;
}
