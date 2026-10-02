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
 * Every `error.code` in the api-v1.md error table, plus the client-side
 * `not_modified` (304). The v1 route manifest check compares this list with
 * the pinned contract table.
 */
export const V1ErrorCode = Object.freeze({
  invalidRequest: "invalid_request",
  invalidQuery: "invalid_query",
  invalidCursor: "invalid_cursor",
  invalidPatch: "invalid_patch",
  ttlInvalid: "ttl_invalid",
  sortRequiresIndex: "sort_requires_index",
  assertRequiresIndex: "assert_requires_index",
  invalidKey: "invalid_key",
  invalidToken: "invalid_token",
  forbiddenScope: "forbidden_scope",
  notFound: "not_found",
  unknownDatabase: "unknown_database",
  unknownCollection: "unknown_collection",
  unknownRoute: "unknown_route",
  methodNotAllowed: "method_not_allowed",
  conflictUnresolved: "conflict_unresolved",
  uniqueViolation: "unique_violation",
  patchConflict: "patch_conflict",
  indexLimitExceeded: "index_limit_exceeded",
  idempotencyConflict: "idempotency_conflict",
  idempotencyExpired: "idempotency_expired",
  schemaChanged: "schema_changed",
  mustRefetch: "must_refetch",
  preconditionFailed: "precondition_failed",
  payloadTooLarge: "payload_too_large",
  transactionTooLarge: "transaction_too_large",
  unsupportedMediaType: "unsupported_media_type",
  homeRegionRequired: "home_region_required",
  rateLimited: "rate_limited",
  internalError: "internal_error",
  upstreamUnavailable: "upstream_unavailable",
  capacity: "capacity",
  storageUnavailable: "storage_unavailable",
  storedCorruption: "stored_corruption",
  commitUncertain: "commit_uncertain",
  dependenciesUnavailable: "dependencies_unavailable",
  notModified: "not_modified",
});

/** `412 precondition_failed`: an If-Match, If-None-Match, assert or assert_count failed. */
export class V1PreconditionFailedError extends TerraBaseV1Error {
  /** @param {TerraBaseV1ErrorInit} init */
  constructor(init) {
    super(init);
    this.name = "V1PreconditionFailedError";
  }
}

/** `409 unique_violation`; `constraint` names the violated unique constraint. */
export class V1UniqueViolationError extends TerraBaseV1Error {
  /** @param {TerraBaseV1ErrorInit} init */
  constructor(init) {
    super(init);
    this.name = "V1UniqueViolationError";
  }
}

/** `401 invalid_key`: the data key is unknown or revoked. */
export class V1InvalidKeyError extends TerraBaseV1Error {
  /** @param {TerraBaseV1ErrorInit} init */
  constructor(init) {
    super(init);
    this.name = "V1InvalidKeyError";
  }
}

/** `403 forbidden_scope`: the key's grants do not cover the request. */
export class V1ForbiddenScopeError extends TerraBaseV1Error {
  /** @param {TerraBaseV1ErrorInit} init */
  constructor(init) {
    super(init);
    this.name = "V1ForbiddenScopeError";
  }
}

/** `404 not_found`: absent, deleted or expired; `etag` carries a tombstone version. */
export class V1NotFoundError extends TerraBaseV1Error {
  /** @param {TerraBaseV1ErrorInit} init */
  constructor(init) {
    super(init);
    this.name = "V1NotFoundError";
  }
}

/**
 * @param {TerraBaseV1ErrorInit} init
 * @returns {TerraBaseV1Error}
 */
function typedError(init) {
  switch (init.code) {
    case "precondition_failed": return new V1PreconditionFailedError(init);
    case "unique_violation": return new V1UniqueViolationError(init);
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
  return typedError({
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
