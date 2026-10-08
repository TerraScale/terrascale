// Identifiers for the TerraBase v1 Documents API.
import { requireId } from "../ids.js";

const namePattern = /^[a-z][a-z0-9_-]{0,62}$/u;
// Version tokens are opaque (tbv1-<32 hex>, tbv2-...): only RFC 9110 opaque-tag characters, safe to quote in If-Match.
const versionPattern = /^[\x21\x23-\x7e]{1,256}$/u;
const offsetPattern = /^(?:0|[1-9]\d{0,19})$/u;
const maximumU64 = (1n << 64n) - 1n;

/**
 * @param {"database" | "collection"} kind
 * @param {unknown} name
 * @returns {string}
 */
export function requireName(kind, name) {
  if (typeof name !== "string" || !namePattern.test(name))
    throw new TypeError(`TerraBase ${kind} names must match ^[a-z][a-z0-9_-]{0,62}$.`);
  return name;
}

/**
 * The catalog project ID is a canonical ULID; database and collection routes use names.
 * @param {unknown} project
 * @returns {import("../ids.js").EntityId}
 */
export function requireProject(project) {
  return requireId(project);
}

/** @param {unknown} id @returns {import("../ids.js").EntityId} */
export function requireDocumentId(id) {
  return requireId(id);
}

/**
 * @param {unknown} version
 * @returns {string}
 */
export function requireVersion(version) {
  if (typeof version !== "string" || !versionPattern.test(version))
    throw new TypeError("TerraBase versions are opaque tokens of 1-256 visible ASCII characters without quotes.");
  return version;
}

/**
 * Parses a canonical decimal u64 (an offset), or returns undefined.
 * @param {unknown} value
 * @returns {bigint | undefined}
 */
export function parseU64(value) {
  if (typeof value !== "string" || !offsetPattern.test(value)) return undefined;
  const parsed = BigInt(value);
  return parsed <= maximumU64 ? parsed : undefined;
}

/**
 * A caller-supplied u64 (a decimal string or bigint), as a bigint.
 * @param {unknown} value
 * @param {string} what
 * @returns {bigint}
 */
export function requireU64(value, what) {
  const parsed = parseU64(typeof value === "bigint" ? value.toString() : value);
  if (parsed === undefined) throw new TypeError(`${what} must be a u64: a canonical decimal string or bigint.`);
  return parsed;
}
