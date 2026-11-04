import { array, boolean, malformed, object, string } from "./decode.js";
import { isTerraBaseV1Error } from "./errors.js";
import { decodeSqlValue } from "./sql-execute.js";
/**
 * @import { JsonObject } from "./json.js"
 * @import { V1SqlValue } from "./sql-execute.js"
 * @import { V1Transport } from "./transport.js"
 */

/** @typedef {Readonly<Record<string, V1SqlValue>>} V1TableRow */
/**
 * @template {V1TableRow} [T=V1TableRow]
 * @typedef {object} V1TableShapeState
 * @property {ReadonlyMap<string, T>} rows Every row of the table, keyed by row id as a decimal string; a live view that later changes update.
 * @property {boolean} upToDate True once the shape reached the table's head.
 * @property {string} offset The commit boundary the rows are at.
 */
/**
 * @typedef {object} V1TableShapeSubscribeOptions
 * @property {AbortSignal | undefined} [signal] Closes the subscription when aborted.
 */

const tablePattern = /^(?!_tb_|turso_|sqlite_)[A-Za-z_][A-Za-z0-9_]{0,62}$/u;
const retryable = new Set([408, 429, 500, 502, 503, 504]);
const terminal = new Set(["invalid_request", "unsupported_table", "unknown_database", "unknown_table", "invalid_key", "key_revoked", "key_expired", "forbidden_scope"]);

/** @param {number} attempt @param {number | undefined} retryAfter */
function delay(attempt, retryAfter) {
  return retryAfter !== undefined ? retryAfter * 1000 : Math.min(30_000, 250 * 2 ** attempt) * (0.5 + Math.random() / 2);
}

/** @param {number} ms @param {AbortSignal} signal */
function sleep(ms, signal) {
  return new Promise((resolve, reject) => {
    const done = () => { clearTimeout(timer); reject(signal.reason); };
    const timer = setTimeout(() => { signal.removeEventListener("abort", done); resolve(undefined); }, ms);
    signal.addEventListener("abort", done, { once: true });
  });
}

/** One SQL table's free-read shape: a snapshot, then live changes. */
export class V1TableShape {
  /** @type {V1Transport} */
  #transport;
  /** @type {Readonly<Record<string, string>>} */
  #params;

  /** @param {V1Transport} transport @param {string} database @param {string} table */
  constructor(transport, database, table) {
    if (typeof table !== "string" || !tablePattern.test(table)) throw new TypeError("A table name is 1 to 63 letters, digits and underscores, not starting with a digit, _tb_, turso_ or sqlite_.");
    this.#transport = transport;
    this.#params = Object.freeze({ database, table });
  }

  /**
   * Calls `listener` with the whole table after the snapshot and after every change, until closed.
   * @template {V1TableRow} [T=V1TableRow]
   * @param {(state: V1TableShapeState<T>) => void} listener
   * @param {V1TableShapeSubscribeOptions} [options]
   * @returns {{ close: () => void; done: Promise<void> }}
   */
  subscribe(listener, options = {}) {
    if (typeof listener !== "function") throw new TypeError("subscribe takes a listener function.");
    const controller = new AbortController();
    const external = options.signal;
    const close = () => controller.abort(new DOMException("The shape subscription was closed.", "AbortError"));
    external?.addEventListener("abort", close, { once: true });
    if (external?.aborted) close();
    const done = this.#run(/** @type {(state: V1TableShapeState) => void} */ (listener), controller.signal)
      .catch(error => { if (!controller.signal.aborted) throw error; })
      .finally(() => external?.removeEventListener("abort", close));
    return { close, done };
  }

  /** @param {(state: V1TableShapeState) => void} listener @param {AbortSignal} signal */
  async #run(listener, signal) {
    /** @type {Map<string, V1TableRow>} */
    let rows = new Map();
    let handle = "";
    let offset = "-1";
    let upToDate = false;
    /** @type {string | undefined} */
    let cursor;
    for (let attempt = 0; !signal.aborted;) {
      const query = new URLSearchParams(handle === "" ? { offset } : { handle, offset });
      if (upToDate && !offset.startsWith("snap.")) {
        query.set("live", "true");
        if (cursor !== undefined) query.set("cursor", cursor);
      }
      /** @type {import("./transport.js").V1CallResult} */
      let response;
      try {
        response = await this.#transport.call("readTableShape", this.#params, { query, signal, redirect: "follow" });
      } catch (error) {
        if (signal.aborted) return;
        if (isTerraBaseV1Error(error, "must_refetch")) {
          rows = new Map();
          handle = "";
          offset = "-1";
          upToDate = false;
          cursor = undefined;
          continue;
        }
        if (isTerraBaseV1Error(error) && (terminal.has(error.code) || !retryable.has(error.status))) throw error;
        await sleep(delay(attempt++, isTerraBaseV1Error(error) ? error.retryAfter : undefined), signal);
        continue;
      }
      attempt = 0;
      const page = object(response.value, "shape page");
      const pageHandle = string(page.handle, "shape handle");
      if (handle !== "" && pageHandle !== handle) malformed("shape handle");
      handle = pageHandle;
      for (const change of array(page.changes, "shape changes")) {
        for (const entry of array(object(change, "shape change").ops, "shape ops")) {
          const op = object(entry, "shape op");
          const key = string(op.key, "shape key");
          if (op.op === "delete") rows.delete(key);
          else if (op.op === "insert" || op.op === "update") rows.set(key, decodeRow(object(op.row, "shape row")));
          else malformed("shape op");
        }
      }
      offset = string(page.next_offset, "shape next_offset");
      upToDate = boolean(page.up_to_date, "shape up_to_date");
      cursor = response.headers.get("tb-shape-cursor") ?? cursor;
      if (!offset.startsWith("snap.")) listener(Object.freeze({ rows, upToDate, offset }));
    }
  }
}

/** @param {JsonObject} row @returns {V1TableRow} */
function decodeRow(row) {
  return Object.freeze(Object.fromEntries(Object.entries(row).map(([name, value]) => [name, decodeSqlValue(value)])));
}
