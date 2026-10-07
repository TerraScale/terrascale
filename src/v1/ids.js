// Identifiers for the TerraBase v1 Documents API (api-v1.md).

const namePattern = /^[a-z][a-z0-9_-]{0,62}$/u;
// Version tokens are opaque (tbv1-<32 hex>, tbv2-...): only RFC 9110 opaque-tag characters, safe to quote in If-Match.
const versionPattern = /^[\x21\x23-\x7e]{1,256}$/u;
const offsetPattern = /^(?:0|[1-9]\d{0,19})$/u;
const maximumU64 = (1n << 64n) - 1n;

/** RFC 9562 NAMESPACE_URL, 6ba7b811-9dad-11d1-80b4-00c04fd430c8. */
const namespaceUrl = Uint8Array.of(
  0x6b, 0xa7, 0xb8, 0x11, 0x9d, 0xad, 0x11, 0xd1,
  0x80, 0xb4, 0x00, 0xc0, 0x4f, 0xd4, 0x30, 0xc8,
);

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
 * The producer resolves this opaque catalog project id (normally a UUID), not its display name.
 * @param {unknown} project
 * @returns {string}
 */
export function requireProject(project) {
  if (typeof project !== "string" || project === "" || project === "." || project === ".." || /[\p{Cc}\uD800-\uDFFF]/u.test(project))
    throw new TypeError("project must be a non-empty catalog project ID without control characters or dot path segments.");
  return project;
}

/**
 * @param {unknown} id
 * @returns {string}
 */
export function requireDocumentId(id) {
  if (typeof id !== "string" || id.length < 1 || id.length > 256 || /[\p{Cc}]/u.test(id) || /[\uD800-\uDFFF]/u.test(id) || id === "." || id === "..")
    throw new TypeError("Document IDs must be 1-256 characters without control characters or dot path segments; new IDs must be lowercase UUIDs.");
  return id;
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

/**
 * @param {Uint8Array} namespace
 * @param {string} name
 * @returns {Promise<Uint8Array>}
 */
async function uuidV5(namespace, name) {
  const encoded = new TextEncoder().encode(name);
  const input = new Uint8Array(namespace.length + encoded.length);
  input.set(namespace);
  input.set(encoded, namespace.length);
  const digest = new Uint8Array(await globalThis.crypto.subtle.digest("SHA-1", input));
  const uuid = digest.slice(0, 16);
  uuid[6] = ((uuid[6] ?? 0) & 0x0f) | 0x50;
  uuid[8] = ((uuid[8] ?? 0) & 0x3f) | 0x80;
  return uuid;
}

/**
 * @param {Uint8Array} bytes
 * @returns {string}
 */
function formatUuid(bytes) {
  const hex = Array.from(bytes, (byte) => byte.toString(16).padStart(2, "0")).join("");
  return `${hex.slice(0, 8)}-${hex.slice(8, 12)}-${hex.slice(12, 16)}-${hex.slice(16, 20)}-${hex.slice(20)}`;
}

/**
 * The deterministic document ID for a natural key in a collection:
 * `uuidv5(uuidv5(NAMESPACE_URL, "terrabase:collection:" + collection), key)`.
 * Uses WebCrypto, so it works in Node.js, browsers and workers.
 * @param {string} collection
 * @param {string} key
 * @returns {Promise<string>}
 */
export async function documentIdFor(collection, key) {
  requireName("collection", collection);
  if (typeof key !== "string") throw new TypeError("The natural key must be a string.");
  const namespace = await uuidV5(namespaceUrl, `terrabase:collection:${collection}`);
  return formatUuid(await uuidV5(namespace, key));
}
