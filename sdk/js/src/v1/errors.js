import { isJsonObject, parseExactJson } from "./json.js";
/** @import { JsonObject, JsonValue } from "./json.js" */

/**
 * @typedef {object} TerraBaseV1ErrorInit
 * @property {number} status
 * @property {string} code
 * @property {string | undefined} [message]
 * @property {number | undefined} [op]
 * @property {string | undefined} [handle]
 * @property {number | undefined} [retryAfter]
 * @property {string | undefined} [etag]
 * @property {JsonObject | undefined} [details]
 * @property {JsonValue | undefined} [body]
 * @property {string | undefined} [responseText]
 * @property {Headers | undefined} [headers]
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
  /** @type {string | undefined} New shape handle for `must_refetch`. */
  handle;
  /** @type {number | undefined} Parsed `Retry-After`, in seconds. */
  retryAfter;
  /** @type {string | undefined} Unquoted `ETag`; a deleted document's 404 carries its tombstone version. */
  etag;
  /** @type {JsonObject | undefined} The raw error object, for route-specific metadata. */
  details;
  /** @type {JsonValue | undefined} Complete parsed response body, including unknown fields. */
  body;
  /** @type {string} Original response text; empty when absent or unreadable. */
  responseText;
  /** @type {Headers} Response headers, including authentication challenges and retry metadata. */
  headers;
  /** @type {string | undefined} Diagnostic X-Request-ID, when supplied by the responder. */
  requestId;

  /** @param {TerraBaseV1ErrorInit} init */
  constructor(init) {
    const suffix = [
      init.message === undefined ? "" : `: ${init.message}`,
      init.op === undefined ? "" : ` (op ${init.op})`,
    ].join("");
    super(`TerraBase ${init.status} ${init.code}${suffix}`);
    this.name = "TerraBaseV1Error";
    this.status = init.status;
    this.code = init.code;
    this.op = init.op;
    this.handle = init.handle;
    this.retryAfter = init.retryAfter;
    this.etag = init.etag;
    this.details = init.details;
    this.body = init.body;
    this.responseText = init.responseText ?? "";
    this.headers = new Headers(init.headers);
    this.requestId = this.headers.get("x-request-id") ?? undefined;
  }
}

/** Current document, collection and shape producer codes, plus client not_modified. */
export const V1ErrorCode = Object.freeze({
  invalidRequest: "invalid_request", invalidQuery: "invalid_query", invalidCursor: "invalid_cursor",
  invalidPatch: "invalid_patch", sortRequiresIndex: "sort_requires_index", assertRequiresIndex: "assert_requires_index",
  keyRevoked: "key_revoked", keyExpired: "key_expired", authUnavailable: "auth_unavailable",
  invalidKey: "invalid_key", forbiddenScope: "forbidden_scope", notFound: "not_found",
  unknownDatabase: "unknown_database", unknownCollection: "unknown_collection", unknownRoute: "unknown_route",
  methodNotAllowed: "method_not_allowed", patchConflict: "patch_conflict", nameTaken: "name_taken",
  mustRefetch: "must_refetch", preconditionFailed: "precondition_failed", payloadTooLarge: "payload_too_large",
  unsupportedMediaType: "unsupported_media_type", uriTooLong: "uri_too_long", shapeBusy: "shape_busy",
  internalError: "internal_error", capacity: "capacity", notModified: "not_modified",
  sqlError: "sql_error", sqlConflict: "sql_conflict", sqlTooLarge: "sql_too_large",
});

/** `412 precondition_failed`: an If-Match, If-None-Match, assert or assert_count failed. */
export class V1PreconditionFailedError extends TerraBaseV1Error {
  /** @param {TerraBaseV1ErrorInit} init */
  constructor(init) {
    super(init);
    this.name = "V1PreconditionFailedError";
  }
}

/** `401 invalid_key`: the API key is unknown or revoked. */
export class V1InvalidKeyError extends TerraBaseV1Error {
  /** @param {TerraBaseV1ErrorInit} init */
  constructor(init) {
    super(init);
    this.name = "V1InvalidKeyError";
  }
}

/** `401 key_revoked`: permanent; replace the key without retrying or exchanging it. */
export class V1KeyRevokedError extends V1InvalidKeyError {
  /** @param {TerraBaseV1ErrorInit} init */
  constructor(init) { super(init); this.name = "V1KeyRevokedError"; }
}

/** `401 key_expired`: permanent; replace the key without retrying or exchanging it. */
export class V1KeyExpiredError extends V1InvalidKeyError {
  /** @param {TerraBaseV1ErrorInit} init */
  constructor(init) { super(init); this.name = "V1KeyExpiredError"; }
}

/** `403 forbidden_scope`: the key's scopes do not cover the request. */
export class V1ForbiddenScopeError extends TerraBaseV1Error {
  /** @param {TerraBaseV1ErrorInit} init */
  constructor(init) {
    super(init);
    this.name = "V1ForbiddenScopeError";
  }
}

/** `404 not_found`: absent or deleted; `etag` carries a tombstone version. */
export class V1NotFoundError extends TerraBaseV1Error {
  /** @param {TerraBaseV1ErrorInit} init */
  constructor(init) {
    super(init);
    this.name = "V1NotFoundError";
  }
}

/**
 * A refused relational SQL statement: `sql_error` (400), `forbidden_scope` (403, writes and DDL need
 * documents:write), `sql_conflict` (409) or `sql_too_large` (413). The whole request rolled back.
 */
export class SqlError extends TerraBaseV1Error {
  /** @type {number} Zero-based index of the failing statement. */
  statement;
  /** @type {string | undefined} The SQLite error code, such as `SQLITE_CONSTRAINT_PRIMARYKEY`. */
  sqliteCode;

  /** @param {TerraBaseV1ErrorInit & { statement: number; sqliteCode?: string | undefined }} init */
  constructor(init) {
    super(init);
    this.name = "SqlError";
    this.statement = init.statement;
    this.sqliteCode = init.sqliteCode;
  }
}

/**
 * @param {TerraBaseV1ErrorInit} init
 * @returns {TerraBaseV1Error}
 */
function typedError(init) {
  // Only the SQL execute route reports a statement index.
  const statement = init.details?.statement;
  if (typeof statement === "number" && Number.isInteger(statement) && statement >= 0) {
    const sqliteCode = init.details?.sqlite_code;
    return new SqlError({ ...init, statement, sqliteCode: typeof sqliteCode === "string" ? sqliteCode : undefined });
  }
  switch (init.code) {
    case "precondition_failed": return new V1PreconditionFailedError(init);
    case "key_revoked": return new V1KeyRevokedError(init);
    case "key_expired": return new V1KeyExpiredError(init);
    case "invalid_key": return new V1InvalidKeyError(init);
    case "forbidden_scope": return new V1ForbiddenScopeError(init);
    case "not_found": return new V1NotFoundError(init);
    default: return new TerraBaseV1Error(init);
  }
}

/**
 * @param {unknown} error
 * @param {string} [code] Also require this `error.code`.
 * @returns {error is TerraBaseV1Error}
 */
export function isTerraBaseV1Error(error, code) {
  return error instanceof TerraBaseV1Error && (code === undefined || error.code === code);
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
  /** @type {JsonValue | undefined} */
  let body;
  /** @type {JsonObject | undefined} */
  let details;
  try {
    body = text === "" ? undefined : parseExactJson(text);
    if (isJsonObject(body) && isJsonObject(body.error)) details = body.error;
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
  return typedError({
    status,
    code,
    message: text_("message"),
    op: typeof op === "number" && Number.isInteger(op) ? op : undefined,
    handle: text_("handle"),
    retryAfter: parseRetryAfter(headers),
    etag: unquoteEtag(headers.get("etag")),
    details,
    body,
    responseText: text,
    headers,
  });
}
