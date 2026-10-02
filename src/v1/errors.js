import { isJsonObject, parseExactJson } from "./json.js";
/** @import { JsonObject } from "./json.js" */

/**
 * @typedef {object} TerraBaseV1ErrorInit
 * @property {number} status
 * @property {string} code
 * @property {string | undefined} [message]
 * @property {number | undefined} [op]
 * @property {string | undefined} [constraint]
 * @property {string | undefined} [handle]
 * @property {number | undefined} [retryAfter]
 * @property {string | undefined} [etag]
 * @property {JsonObject | undefined} [details]
 */

/**
 * A non-2xx TerraBase v1 response. `code` is the contract's `error.code`, or
 * `not_modified` for a 304 and `http_<status>` when the body carried no code.
 */
export class TerraBaseV1Error extends Error {
  /** @type {number} HTTP status. */
  status;
  /** @type {string} */
  code;
  /** @type {number | undefined} Failing transaction op index. */
  op;
  /** @type {string | undefined} Unique constraint name for `unique_violation`. */
  constraint;
  /** @type {string | undefined} New shape handle for `must_refetch`. */
  handle;
  /** @type {number | undefined} Parsed `Retry-After`, in seconds. */
  retryAfter;
  /** @type {string | undefined} Unquoted `ETag`; a deleted document's 404 carries its tombstone version. */
  etag;
  /** @type {JsonObject | undefined} The raw error object, for route-specific members such as `suggested_index`. */
  details;

  /** @param {TerraBaseV1ErrorInit} init */
  constructor(init) {
    const suffix = [
      init.message === undefined ? "" : `: ${init.message}`,
      init.op === undefined ? "" : ` (op ${init.op})`,
      init.constraint === undefined ? "" : ` (constraint ${init.constraint})`,
    ].join("");
    super(`TerraBase ${init.status} ${init.code}${suffix}`);
    this.name = "TerraBaseV1Error";
    this.status = init.status;
    this.code = init.code;
    this.op = init.op;
    this.constraint = init.constraint;
    this.handle = init.handle;
    this.retryAfter = init.retryAfter;
    this.etag = init.etag;
    this.details = init.details;
  }
}

/**
 * @param {string | null} value
 * @returns {string | undefined}
 */
export function unquoteEtag(value) {
  if (value === null) return undefined;
  const trimmed = value.trim().replace(/^W\//u, "");
  return trimmed.length >= 2 && trimmed.startsWith('"') && trimmed.endsWith('"')
    ? trimmed.slice(1, -1)
    : trimmed;
}

/**
 * @param {Headers} headers
 * @returns {number | undefined} Seconds.
 */
export function parseRetryAfter(headers) {
  const value = headers.get("retry-after")?.trim();
  if (value === undefined || value === "") return undefined;
  if (/^\d{1,9}$/u.test(value)) return Number(value);
  const date = Date.parse(value);
  return Number.isNaN(date) ? undefined : Math.max(0, (date - Date.now()) / 1000);
}

/**
 * @param {number} status
 * @param {Headers} headers
 * @param {string} text
 * @returns {TerraBaseV1Error}
 */
export function parseV1Error(status, headers, text) {
  /** @type {JsonObject | undefined} */
  let details;
  try {
    const parsed = text === "" ? undefined : parseExactJson(text);
    if (isJsonObject(parsed) && isJsonObject(parsed.error)) details = parsed.error;
  } catch {
    details = undefined;
  }
  /**
   * @param {string} name
   * @returns {string | undefined}
   */
  const text_ = (name) => {
    const value = details?.[name];
    return typeof value === "string" ? value : undefined;
  };
  const op = details?.op;
  const code =
    text_("code") ?? (status === 304 ? "not_modified" : `http_${status}`);
  return new TerraBaseV1Error({
    status,
    code,
    message: text_("message"),
    op: typeof op === "number" && Number.isInteger(op) ? op : undefined,
    constraint: text_("constraint"),
    handle: text_("handle"),
    retryAfter: parseRetryAfter(headers),
    etag: unquoteEtag(headers.get("etag")),
    details,
  });
}
