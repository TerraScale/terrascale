import { array, boolean, malformed, object, string } from "./decode.js";
import { requireDatabaseSelector, requireStoredId, requireName } from "./ids.js";
/**
 * @import { JsonObject, JsonValue } from "./json.js"
 * @import { V1RequestOptions } from "./terrabase-v1.js"
 * @import { V1Transport } from "./transport.js"
 */

/** @typedef {{ readonly id: string; readonly value: JsonObject }} V1SqlRow */
/**
 * SQL returns one bounded result, without a pagination cursor.
 * @typedef {{ readonly matched: number; readonly truncated: boolean; readonly rows: readonly V1SqlRow[] }} V1SqlResult
 */
/** @typedef {{ readonly field: string; readonly reason: string }} V1SqlIndexSuggestion */
/**
 * @typedef {{
 *   readonly ok: boolean;
 *   readonly error: string | null;
 *   readonly collection: string | null;
 *   readonly hasLimit: boolean;
 *   readonly plan: readonly string[];
 *   readonly indexes: readonly string[];
 *   readonly suggestions: readonly V1SqlIndexSuggestion[];
 * }} V1SqlCheck
 */

/** @param {string} sql @param {V1RequestOptions} options @returns {string} */
function requestBody(sql, options) {
  if (typeof sql !== "string" || sql.length > 16_384)
    throw new TypeError("SQL must be a string of at most 16384 UTF-16 code units.");
  if (options === null || typeof options !== "object" || Array.isArray(options))
    throw new TypeError("SQL request options must be an object.");
  for (const member of Object.keys(options))
    if (member !== "signal") throw new TypeError(`Unknown SQL request option ${member}.`);
  // SQL syntax, including empty statements, stays producer-owned: check returns ok:false.
  return JSON.stringify({ sql });
}

/** @param {JsonValue | undefined} value @returns {V1SqlResult} */
function decodeResult(value) {
  const raw = object(value, "SQL result");
  const rows = array(raw.rows, "SQL rows").map(entry => {
    const row = object(entry, "SQL row");
    return Object.freeze({ id: requireStoredId(row.id), value: object(row.value, "SQL row value") });
  });
  const matched = raw.matched;
  if (typeof matched !== "number" || !Number.isInteger(matched) || matched < 0 || matched > 1000 || matched !== rows.length)
    malformed("SQL matched count");
  if (new Set(rows.map(row => row.id)).size !== rows.length) malformed("SQL row identities");
  return Object.freeze({ matched, truncated: boolean(raw.truncated, "SQL truncation flag"), rows: Object.freeze(rows) });
}

/** @param {JsonValue | undefined} value @param {"checkSql" | "explainSql"} operation @returns {V1SqlCheck} */
function decodeCheck(value, operation) {
  const raw = object(value, "SQL check");
  const ok = boolean(raw.ok, "SQL check flag");
  const error = raw.error === null ? null : string(raw.error, "SQL check error");
  const collection = raw.collection === null ? null : requireName("collection", raw.collection);
  const hasLimit = boolean(raw.hasLimit, "SQL limit flag");
  const plan = array(raw.plan, "SQL plan").map(step => string(step, "SQL plan step"));
  const indexes = array(raw.indexes, "SQL indexes").map(index => string(index, "SQL index"));
  const suggestions = array(raw.suggestions, "SQL index suggestions").map(entry => {
    const suggestion = object(entry, "SQL index suggestion");
    return Object.freeze({ field: string(suggestion.field, "SQL index field"), reason: string(suggestion.reason, "SQL index reason") });
  });
  if (ok ? error !== null || collection === null : error === null || error === "" || collection !== null || hasLimit || plan.length > 0 || indexes.length > 0 || suggestions.length > 0)
    malformed("SQL check outcome");
  if (operation === "explainSql" && !ok) malformed("SQL explain outcome");
  return Object.freeze({ ok, error, collection, hasLimit, plan: Object.freeze(plan), indexes: Object.freeze(indexes), suggestions: Object.freeze(suggestions) });
}

/**
 * Runs the producer's read-only SQL subset; it never converts a legacy SQL builder.
 * @param {V1Transport} transport
 * @param {string} database
 * @param {string} sql
 * @param {V1RequestOptions} [options]
 * @returns {Promise<V1SqlResult>}
 */
export async function runSql(transport, database, sql, options = {}) {
  const body = requestBody(sql, options);
  return decodeResult((await transport.call("runSql", { database: requireDatabaseSelector(database) }, { body, signal: options.signal })).value);
}

/**
 * Check reports invalid SQL in a successful body; explain reports a producer HTTP error.
 * @param {V1Transport} transport
 * @param {string} database
 * @param {string} sql
 * @param {V1RequestOptions} [options]
 * @param {"checkSql" | "explainSql"} [operation]
 * @returns {Promise<V1SqlCheck>}
 */
export async function checkSql(transport, database, sql, options = {}, operation = "checkSql") {
  if (operation !== "checkSql" && operation !== "explainSql") throw new TypeError("SQL checks use checkSql or explainSql.");
  const body = requestBody(sql, options);
  return decodeCheck((await transport.call(operation, { database: requireDatabaseSelector(database) }, { body, signal: options.signal })).value, operation);
}
