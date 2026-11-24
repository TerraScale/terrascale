/** @typedef {"fast" | "strong"} V1Consistency */
/**
 * @typedef {object} V1SqlRequestOptions
 * @property {AbortSignal | undefined} [signal]
 * @property {string | undefined} [bookmark] Merged with the bookmark the client tracks for this database.
 * @property {V1Consistency | undefined} [consistency]
 */
/** @typedef {{ readonly current: () => string; readonly observe: (bookmark: string | null | undefined) => void }} V1BookmarkTracker */

export const bookmarkHeader = "TerraScale-Bookmark";
const consistencyHeader = "TerraScale-Consistency";
const maximumBookmarkLength = 1024;
const maximumRegionBytes = 64;
const minimumEntryBytes = 3;
const maximumRegions = Math.floor((maximumBookmarkLength * 3 / 4 - 2) / minimumEntryBytes);
const maximumSequence = 0x7fff_ffff_ffff_ffffn;

/** @param {unknown} value @returns {V1Consistency | undefined} */
export function requireConsistency(value) {
  if (value === undefined || value === "fast" || value === "strong") return value;
  throw new TypeError("consistency is fast or strong.");
}

/** @param {unknown} value @returns {string | undefined} */
function requireBookmark(value) {
  if (value === undefined) return undefined;
  if (typeof value !== "string") throw new TypeError("bookmark is a string returned by TerraScale.");
  bookmarkEntries(value, () => { throw new TypeError("bookmark is a string returned by TerraScale."); });
  return value;
}

/**
 * @param {string} encoded
 * @param {() => never} refuse
 * @returns {Map<string, bigint>}
 */
function bookmarkEntries(encoded, refuse) {
  /** @type {Map<string, bigint>} */
  const entries = new Map();
  if (encoded === "") return entries;
  if (encoded.length > maximumBookmarkLength || !/^[A-Za-z0-9_-]+$/u.test(encoded) || encoded.length % 4 === 1) refuse();
  const binary = atob(encoded.replaceAll("-", "+").replaceAll("_", "/").padEnd(Math.ceil(encoded.length / 4) * 4, "="));
  const bytes = Uint8Array.from(binary, character => character.charCodeAt(0));
  let at = 1;
  const varuint = () => {
    let value = 0n;
    for (let index = 0; index < 10; index++) {
      const byte = bytes[at++];
      if (byte === undefined || index === 9 && byte > 1) return refuse();
      value |= BigInt(byte & 0x7f) << BigInt(7 * index);
      if (byte < 0x80) return value;
    }
    return refuse();
  };
  if (bytes[0] !== 1) refuse();
  const count = varuint();
  if (count > BigInt(Math.floor((bytes.length - at) / minimumEntryBytes))) refuse();
  let previous = "";
  for (let index = 0n; index < count; index++) {
    const length = Number(varuint());
    if (length === 0 || length > maximumRegionBytes || at + length > bytes.length) refuse();
    /** @type {string} */
    let region;
    try { region = new TextDecoder("utf-8", { fatal: true, ignoreBOM: true }).decode(bytes.subarray(at, at + length)); }
    catch { return refuse(); }
    at += length;
    const sequence = varuint();
    if (sequence === 0n || sequence > maximumSequence || index > 0n && previous >= region) refuse();
    previous = region;
    entries.set(region, sequence);
  }
  if (at !== bytes.length) refuse();
  return entries;
}

/** @param {Map<string, bigint>} entries @returns {string} */
function encodeBookmark(entries) {
  if (entries.size === 0) return "";
  if (entries.size > maximumRegions) throw new RangeError("Merged TerraScale-Bookmark contains too many regions.");
  /** @type {number[]} */
  const bytes = [1];
  /** @param {bigint} value */
  const varuint = value => {
    while (value >= 0x80n) {
      bytes.push(Number(value & 0x7fn) | 0x80);
      value >>= 7n;
    }
    bytes.push(Number(value));
  };
  varuint(BigInt(entries.size));
  for (const [region, sequence] of [...entries].sort(([left], [right]) => left < right ? -1 : left > right ? 1 : 0)) {
    const utf8 = new TextEncoder().encode(region);
    varuint(BigInt(utf8.length));
    bytes.push(...utf8);
    varuint(sequence);
  }
  if (Math.ceil(bytes.length * 4 / 3) > maximumBookmarkLength)
    throw new RangeError("Merged TerraScale-Bookmark exceeds the encoded size limit.");
  return btoa(String.fromCharCode(...bytes)).replaceAll("+", "-").replaceAll("/", "_").replace(/=+$/u, "");
}

/**
 * The newest of two bookmarks: per region, the higher applied sequence.
 * @param {string} left
 * @param {string} right
 * @returns {string}
 */
export function mergeBookmarks(left, right) {
  const refuse = () => { throw new TypeError("TerraBase returned a malformed TerraScale-Bookmark."); };
  const merged = bookmarkEntries(left, refuse);
  if (right === "" || left === right) return left;
  const incoming = bookmarkEntries(right, refuse);
  if (left === "") return right;
  for (const [region, sequence] of incoming)
    if ((merged.get(region) ?? 0n) < sequence) merged.set(region, sequence);
  return encodeBookmark(merged);
}

/** @param {unknown} bookmark @param {V1BookmarkTracker} tracker */
export function requestBookmark(bookmark, tracker) {
  const supplied = requireBookmark(bookmark);
  const tracked = tracker.current();
  return supplied === undefined || supplied === "" ? tracked : mergeBookmarks(tracked, supplied);
}

/**
 * @param {{ readonly bookmark?: unknown; readonly consistency?: unknown }} options
 * @param {V1BookmarkTracker} tracker
 * @returns {Record<string, string>}
 */
export function causalHeaders(options, tracker) {
  const bookmark = requestBookmark(options.bookmark, tracker);
  const consistency = requireConsistency(options.consistency);
  return {
    ...(bookmark === "" ? {} : { [bookmarkHeader]: bookmark }),
    ...(consistency === undefined ? {} : { [consistencyHeader]: consistency }),
  };
}
