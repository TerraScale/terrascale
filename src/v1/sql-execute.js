import { array, malformed, object, string } from "./decode.js";
import { requireDatabaseSelector } from "./ids.js";
import { ExactDecimal, stringifyExactJson } from "./json.js";
/**
 * @import { JsonValue } from "./json.js"
 * @import { V1RequestOptions } from "./terrabase-v1.js"
 * @import { V1Transport } from "./transport.js"
 */

/**
 * An argument: null, a finite number, an int64 `bigint` or ExactDecimal, a boolean (stored as 1 or 0) or a string.
 * @typedef {null | boolean | number | bigint | ExactDecimal | string} V1SqlArgument
 */
/** @typedef {readonly V1SqlArgument[] | Readonly<Record<string, V1SqlArgument>>} V1SqlArguments Positional array, or named values keyed without their `:`, `@` or `$` prefix. */
/**
 * @typedef {object} V1SqlStatement
 * @property {string} sql
 * @property {readonly V1SqlArgument[] | undefined} [args] Positional arguments.
 * @property {Readonly<Record<string, V1SqlArgument>> | undefined} [named] Named arguments, keys without their prefix.
 */
/**
 * A result value. Integers beyond 2^53 decode as ExactDecimal, keeping every digit; `BigInt(String(value))` converts one.
 * @typedef {null | number | ExactDecimal | string} V1SqlValue
 */
/** @typedef {{ readonly name: string; readonly decltype: string | null }} V1SqlColumn A column; `decltype` is null for an expression. */
/**
 * @typedef {object} V1SqlExecuteResult
 * @property {readonly V1SqlColumn[]} columns
 * @property {readonly (readonly V1SqlValue[])[]} rows One value per column.
 * @property {number} rowsAffected
 * @property {number | ExactDecimal | null} lastInsertRowid
 */

const maximumStatements = 256;
const maximumSqlLength = 16_384;

/** @param {unknown} value @param {string} where */
function requireArgument(value, where) {
  if (value === null || typeof value === "boolean" || typeof value === "string" || value instanceof ExactDecimal) return;
  if (typeof value === "number" && Number.isFinite(value)) return;
  if (typeof value === "bigint" && BigInt.asIntN(64, value) === value) return;
  throw new TypeError(`SQL ${where} is null, a finite number, an int64 bigint, a boolean or a string.`);
}

/** @param {unknown} statement @param {number} index */
function encodeStatement(statement, index) {
  if (typeof statement !== "string" && (typeof statement !== "object" || statement === null || Array.isArray(statement)))
    throw new TypeError(`SQL statement ${index} is a string or { sql, args, named }.`);
  const { sql, args, named, ...extra } = /** @type {V1SqlStatement} */ (typeof statement === "string" ? { sql: statement } : statement);
  for (const member of Object.keys(extra)) throw new TypeError(`Unknown SQL statement member ${member}.`);
  if (typeof sql !== "string" || sql.length === 0 || sql.length > maximumSqlLength)
    throw new TypeError(`SQL statement ${index} is a non-empty string of at most ${maximumSqlLength} UTF-16 code units.`);
  /** @type {{ sql: string; args?: V1SqlArgument[]; named?: Record<string, V1SqlArgument> }} */
  const encoded = { sql };
  if (args !== undefined) {
    if (!Array.isArray(args)) throw new TypeError(`SQL statement ${index} args is an array.`);
    args.forEach((arg, position) => requireArgument(arg, `argument ${position}`));
    encoded.args = [...args];
  }
  if (named !== undefined) {
    if (named === null || typeof named !== "object" || Array.isArray(named)) throw new TypeError(`SQL statement ${index} named is an object.`);
    for (const [name, value] of Object.entries(named)) {
      if (/^[:@$]/u.test(name)) throw new TypeError(`SQL named argument ${name} is keyed without its prefix.`);
      requireArgument(value, `named argument ${name}`);
    }
    encoded.named = { ...named };
  }
  return encoded;
}

/** @param {string} sql @param {V1SqlArguments | undefined} args @returns {V1SqlStatement} */
export function sqlStatement(sql, args) {
  if (args === undefined) return { sql };
  return Array.isArray(args) ? { sql, args } : { sql, named: /** @type {Readonly<Record<string, V1SqlArgument>>} */ (args) };
}

/** @param {JsonValue | undefined} value @returns {V1SqlValue} */
function decodeValue(value) {
  if (value === null || typeof value === "number" || typeof value === "string" || value instanceof ExactDecimal) return value;
  return malformed("SQL value");
}

/** @param {JsonValue | undefined} value @returns {V1SqlExecuteResult} */
function decodeResult(value) {
  const raw = object(value, "SQL statement result");
  const columns = array(raw.columns, "SQL columns").map(entry => {
    const column = object(entry, "SQL column");
    const decltype = column.decltype === null || column.decltype === undefined ? null : string(column.decltype, "SQL column decltype");
    return Object.freeze({ name: string(column.name, "SQL column name"), decltype });
  });
  const rows = array(raw.rows, "SQL rows").map(entry => {
    const row = array(entry, "SQL row").map(decodeValue);
    if (row.length !== columns.length) malformed("SQL row width");
    return Object.freeze(row);
  });
  const affected = raw.rows_affected;
  if (typeof affected !== "number" || !Number.isSafeInteger(affected) || affected < 0) malformed("SQL rows_affected");
  const rowid = raw.last_insert_rowid ?? null;
  if (rowid !== null && !(rowid instanceof ExactDecimal) && !(typeof rowid === "number" && Number.isInteger(rowid))) malformed("SQL last_insert_rowid");
  return Object.freeze({ columns: Object.freeze(columns), rows: Object.freeze(rows), rowsAffected: affected, lastInsertRowid: rowid });
}

/**
 * Runs 1 to 256 statements as one transaction: all commit or none do. BEGIN, COMMIT and ROLLBACK are refused.
 * A refused statement throws SqlError with its index.
 * @param {V1Transport} transport
 * @param {string} database
 * @param {readonly (string | V1SqlStatement)[]} statements
 * @param {V1RequestOptions} [options]
 * @returns {Promise<readonly V1SqlExecuteResult[]>}
 */
export async function executeSql(transport, database, statements, options = {}) {
  if (!Array.isArray(statements) || statements.length === 0 || statements.length > maximumStatements)
    throw new TypeError(`SQL batches hold 1 to ${maximumStatements} statements.`);
  if (options === null || typeof options !== "object" || Array.isArray(options)) throw new TypeError("SQL request options must be an object.");
  for (const member of Object.keys(options)) if (member !== "signal") throw new TypeError(`Unknown SQL request option ${member}.`);
  const body = stringifyExactJson({ statements: statements.map(encodeStatement) }, "SQL statements");
  const response = await transport.call("executeSql", { database: requireDatabaseSelector(database) }, { body, signal: options.signal });
  const results = array(object(response.value, "SQL execute response").results, "SQL results").map(decodeResult);
  if (results.length !== statements.length) malformed("SQL result count");
  return Object.freeze(results);
}

/**
 * Rows as objects keyed by column name; a later duplicate column name wins.
 * @template [T=Record<string, V1SqlValue>]
 * @param {V1SqlExecuteResult} result
 * @returns {T[]}
 */
export function sqlRowObjects(result) {
  return result.rows.map(row => /** @type {T} */ (Object.fromEntries(result.columns.map((column, index) => [column.name, row[index] ?? null]))));
}
