import { isJsonObject, parseExactJson } from "./json.js";
/** @import { JsonObject, JsonValue } from "./json.js" */

/**
 * @typedef {object} TerraBaseV1ErrorInit
 * @property {number} status
 * @property {string} code
 * @property {string | undefined} [message]
 * @property {number | undefined} [retryAfter]
 * @property {JsonObject | undefined} [details]
 * @property {JsonValue | undefined} [body]
 * @property {string | undefined} [responseText]
 * @property {Headers | undefined} [headers]
 */

/**
 * A non-2xx TerraBase v1 response. `code` is the contract's `error.code`, or
 * `http_<status>` when the body carried no code.
 */
export class TerraBaseV1Error extends Error {
  /** @type {number} HTTP status. */
  status;
  /** @type {string} */
  code;
  /** @type {number | undefined} Parsed `Retry-After`, in seconds. */
  retryAfter;
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
    super(`TerraBase ${init.status} ${init.code}${init.message === undefined ? "" : `: ${init.message}`}`);
    this.name = "TerraBaseV1Error";
    this.status = init.status;
    this.code = init.code;
    this.retryAfter = init.retryAfter;
    this.details = init.details;
    this.body = init.body;
    this.responseText = init.responseText ?? "";
    this.headers = new Headers(init.headers);
    this.requestId = this.headers.get("x-request-id") ?? undefined;
  }
}

/** Current producer codes outside SQL statement errors. */
export const V1ErrorCode = Object.freeze({
  invalidRequest: "invalid_request", invalidCursor: "invalid_cursor",
  keyRevoked: "key_revoked", keyExpired: "key_expired", authUnavailable: "auth_unavailable",
  invalidKey: "invalid_key", forbiddenScope: "forbidden_scope", notFound: "not_found",
  unknownDatabase: "unknown_database", unknownTable: "unknown_table", unknownRoute: "unknown_route",
  methodNotAllowed: "method_not_allowed", nameTaken: "name_taken",
  mustRefetch: "must_refetch", payloadTooLarge: "payload_too_large",
  unsupportedMediaType: "unsupported_media_type", uriTooLong: "uri_too_long", shapeBusy: "shape_busy",
  internalError: "internal_error", capacity: "capacity",
  replicaNotReady: "replica_not_ready", moveInProgress: "move_in_progress", moveUnresolved: "move_unresolved", wrongRegion: "wrong_region",
});

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

/** `404 not_found`: the addressed resource does not exist. */
export class V1NotFoundError extends TerraBaseV1Error {
  /** @param {TerraBaseV1ErrorInit} init */
  constructor(init) {
    super(init);
    this.name = "V1NotFoundError";
  }
}

/** TerraScale SQL error codes, the same on REST, the SQL WebSocket and the PostgreSQL wire. */
export const SqlErrorCode = Object.freeze({
  syntaxError: "syntax_error", undefinedTable: "undefined_table", undefinedColumn: "undefined_column",
  duplicateObject: "duplicate_object", primaryKeyViolation: "primary_key_violation", uniqueViolation: "unique_violation",
  notNullViolation: "not_null_violation", checkViolation: "check_violation", foreignKeyViolation: "foreign_key_violation",
  constraintViolation: "constraint_violation", forbiddenScope: "forbidden_scope", sqlSandbox: "sql_sandbox", serializationConflict: "serialization_conflict",
  rowConflict: "row_conflict",
  tooLarge: "too_large", queryCanceled: "query_canceled", transactionRolledBack: "transaction_rolled_back",
  transactionAborted: "transaction_aborted", readOnlyTransaction: "read_only_transaction", transactionState: "transaction_state",
  invalidRequest: "invalid_request", quotaExceeded: "quota_exceeded", tenantBusy: "tenant_busy",
  unauthorized: "unauthorized", unavailable: "unavailable", wrongRegion: "wrong_region", internalError: "internal_error",
  sqlError: "sql_error",
});

const retryableSqlCodes = new Set(["serialization_conflict", "tenant_busy", "unavailable"]);

/**
 * A refused SQL statement. `code` is a TerraScale code (see SqlErrorCode) and `sqlstate` its PostgreSQL SQLSTATE.
 * Over REST and outside a WebSocket transaction the whole request rolled back.
 */
export class SqlError extends TerraBaseV1Error {
  /** @type {number} Zero-based index of the failing statement. */
  statement;
  /** @type {string} The PostgreSQL SQLSTATE, such as `23505`. */
  sqlstate;
  /** @type {boolean} True for serialization_conflict, tenant_busy and unavailable: the whole request may be sent again. */
  retryable;

  /** @param {TerraBaseV1ErrorInit & { statement: number; sqlstate: string }} init */
  constructor(init) {
    super(init);
    this.name = "SqlError";
    this.statement = init.statement;
    this.sqlstate = init.sqlstate;
    this.retryable = retryableSqlCodes.has(init.code);
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
    const sqlstate = init.details?.sqlstate;
    if (typeof sqlstate === "string" && /^[0-9A-Z]{5}$/u.test(sqlstate)) return new SqlError({ ...init, statement, sqlstate });
  }
  switch (init.code) {
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
    else if (isJsonObject(body) && typeof body.error === "string") details = {
      code: body.error, ...(typeof body.error_description === "string" ? { message: body.error_description } : {}),
    };
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
  const code = text_("code") ?? `http_${status}`;
  return typedError({
    status,
    code,
    message: text_("message"),
    retryAfter: parseRetryAfter(headers),
    details,
    body,
    responseText: text,
    headers,
  });
}
