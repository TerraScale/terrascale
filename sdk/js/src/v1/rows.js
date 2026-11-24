import { RequestTransportError, BodyTransportError } from "./transport.js";
import { ok, err } from "../result.js";
import { TerraBaseV1Error } from "./errors.js";
import { object, string, array } from "./decode.js";
import { stringifyExactJson } from "./json.js";

/** @typedef {"getRow" | "createRow" | "updateRow" | "deleteRow" | "batchRows"} RowOperation */
/** @typedef {{ key: string; row: import("./json.js").JsonObject | null }} RowAnswer */
/** @typedef {{ op: "create"; table: string; row: import("./json.js").JsonInputObject } | { op: "update"; table: string; key: string; row: import("./json.js").JsonInputObject } | { op: "delete"; table: string; key: string }} RowMutation */
/** Row operations return typed results; every peer region accepts them. */
export class V1Rows {
  #transport;
  #params;
  /** @param {import("./transport.js").V1Transport} transport @param {string} database @param {string} table */
  constructor(transport, database, table) {
    if (typeof table !== "string" || !/^[A-Za-z_][A-Za-z0-9_]{0,62}$/u.test(table)) throw new TypeError("Invalid table name.");
    this.#transport = transport;
    this.#params = { database, table };
  }

  /** @param {string} key @param {{ columns?: readonly string[]; signal?: AbortSignal | undefined }} [options] */
  get(key, options = {}) {
    const query = new URLSearchParams();
    if (options.columns !== undefined) {
      if (!options.columns.length || options.columns.some(column => !/^[A-Za-z_][A-Za-z0-9_]*$/u.test(column)))
        throw new TypeError("columns must contain column names.");
      query.set("columns", options.columns.join(","));
    }
    const params = { ...this.#params, key: rowKey(key) };
    return rowResult("getRow", () => this.#transport.call("getRow", params,
      { query, signal: options.signal }), value => ({ row: object(object(value, "row response").row, "row") }));
  }

  /** @param {import("./json.js").JsonInputObject} row @param {import("./terrabase-v1.js").V1RequestOptions} [options] */
  create(row, options = {}) {
    const body = stringifyExactJson({ row }, "row mutation");
    return rowResult("createRow", () => this.#transport.call("createRow", this.#params,
      { body, signal: options.signal }), answer);
  }

  /** @param {string} key @param {import("./json.js").JsonInputObject} row @param {import("./terrabase-v1.js").V1RequestOptions} [options] */
  update(key, row, options = {}) {
    const params = { ...this.#params, key: rowKey(key) };
    const body = stringifyExactJson({ row }, "row mutation");
    return rowResult("updateRow", () => this.#transport.call("updateRow", params,
      { body, signal: options.signal }), answer);
  }

  /** @param {string} key @param {import("./terrabase-v1.js").V1RequestOptions} [options] */
  delete(key, options = {}) {
    const params = { ...this.#params, key: rowKey(key) };
    return rowResult("deleteRow", () => this.#transport.call("deleteRow", params,
      { signal: options.signal }), () => undefined);
  }
}

/** @param {import("./transport.js").V1Transport} transport @param {string} database @param {readonly RowMutation[]} operations @param {import("./terrabase-v1.js").V1RequestOptions} [options] */
export function batchRows(transport, database, operations, options = {}) {
  if (!Array.isArray(operations) || operations.length < 1 || operations.length > 100)
    throw new TypeError("A row batch holds 1 to 100 operations.");
  const body = stringifyExactJson({ operations }, "row batch");
  return rowResult("batchRows", () => transport.call("batchRows", { database },
    { body, signal: options.signal }),
    value => ({ results: array(object(value, "row batch").results, "row batch results").map(answer) }));
}

/** @param {import("./json.js").JsonValue | undefined} value @returns {RowAnswer} */
function answer(value) {
  const raw = object(value, "row answer");
  return { key: rowKey(string(raw.key, "row key")), row: raw.row == null ? null : object(raw.row, "row") };
}

/** @param {string} key */
function rowKey(key) {
  if (typeof key !== "string" || key.length > 20 || !/^(?:0|-?[1-9]\d*)$/u.test(key) ||
      BigInt(key) < -9223372036854775808n || BigInt(key) > 9223372036854775807n)
    throw new TypeError("key must be a row id as a canonical decimal integer.");
  return key;
}

/** @template T @param {RowOperation} operation @param {() => Promise<import("./transport.js").V1CallResult>} call @param {(value: import("./json.js").JsonValue | undefined) => T} decode @returns {Promise<import("../result.js").TerraScaleResult<T>>} */
async function rowResult(operation, call, decode) {
  try {
    const response = await call();
    return ok(operation, response.status, response.headers, decode(response.value));
  } catch (error) {
    if (error instanceof TerraBaseV1Error && error.status !== null) {
      const kind = error.status === 401 ? "authentication" : error.status === 403 ? "authorization" :
        error.status === 404 ? "not-found" : error.status === 409 ? "conflict" :
        error.status === 421 ? "unavailable" : error.status === 429 ? "rate-limit" : error.status >= 500 ? "server" : "validation";
      return err(operation, { kind, code: error.code, message: error.message, status: error.status,
        details: error.details?.details, requestId: error.requestId,
        retryAfter: error.retryAfter === undefined ? undefined : error.retryAfter * 1000 }, error.status, error.headers, error.body);
    }
    const transport = error instanceof RequestTransportError || error instanceof BodyTransportError || !(error instanceof TypeError);
    return err(operation, { kind: transport ? "transport" : "invalid-response",
      code: transport ? "request_failed" : "invalid_response",
      message: "The row request did not complete. A write may have committed; do not replay it." });
  }
}
